#!/usr/bin/env python3
"""T63 · Gerçek RustFS kesintisinde ingest ve ham arşiv toparlanması.

Bu ölçüm compose yığının çalışan ``api``, ``rustfs``, ``keycloak`` ve
``postgres`` servislerini kullanır. RustFS gerçekten durdurulur; bu sırada
OTLP batch'leri gerçek HTTP ingest ucundan gönderilir ve ack/sayaç farkları
okunur. API daha sonra durdurularak açık WAL segmenti mühürlenir. RustFS ile
API geri geldiğinde aynı segmentin ``raw_manifest.verified_at`` alanıyla
doğrulandığı beklenir.

API'yi RustFS kapalıyken yeniden başlatmıyoruz: ``RawArchiveService`` ilk
kovayı oluşturma denemesini kalkışta yapar. O hâl farklı bir soruyu — host
kalkış politikasını — ölçerdi. Buradaki soru çalışan ingest'in depo
kesintisine davranışıdır.

Koşum bir hata alsa da ``finally`` bloğu iki container'ı geri kaldırır.
Volume silinmez ve compose yeniden yaratılmaz.
"""

from __future__ import annotations

import base64
import json
import re
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib
from dataclasses import dataclass
from pathlib import Path


KOK = Path(__file__).resolve().parent.parent
API = "bizigo-api-1"
RUSTFS = "bizigo-rustfs-1"
POSTGRES = "bizigo-postgres-1"

API_ORIGIN = "http://127.0.0.1:5080"
KEYCLOAK_TOKEN_URL = (
    "http://127.0.0.1:8180/realms/bizigo/protocol/openid-connect/token"
)

BATCH_SAYISI = 3
BATCH_BASINA = 4
WAL_MAGIC = 0x425A4731
WAL_HEADER_BYTES = 12


@dataclass(frozen=True)
class ManifestOzeti:
    segment_sayisi: int
    satir_sayisi: int
    dogrulanmis: int
    dogrulanmamis: int
    kayit_sayisi: int


def komut(*args: str, check: bool = True) -> str:
    sonuc = subprocess.run(
        args,
        cwd=KOK,
        text=True,
        capture_output=True,
        check=False,
    )

    if check and sonuc.returncode != 0:
        ayrinti = (sonuc.stderr or sonuc.stdout).strip()
        raise RuntimeError(f"Komut düştü ({sonuc.returncode}): {' '.join(args)}\n{ayrinti}")

    return sonuc.stdout.strip()


def http_json(url: str, *, method: str = "GET", body: object | bytes | None = None,
              headers: dict[str, str] | None = None, timeout: float = 10) -> tuple[int, object]:
    veri: bytes | None
    istek_basliklari = dict(headers or {})

    if isinstance(body, bytes):
        veri = body
    elif body is None:
        veri = None
    else:
        veri = json.dumps(body, ensure_ascii=False).encode("utf-8")
        istek_basliklari.setdefault("Content-Type", "application/json")

    istek = urllib.request.Request(
        url,
        data=veri,
        headers=istek_basliklari,
        method=method,
    )

    try:
        with urllib.request.urlopen(istek, timeout=timeout) as yanit:
            ham = yanit.read()
            return yanit.status, json.loads(ham) if ham else {}
    except urllib.error.HTTPError as hata:
        ham = hata.read()
        try:
            yuk = json.loads(ham) if ham else {}
        except json.JSONDecodeError:
            yuk = ham.decode("utf-8", errors="replace")
        return hata.code, yuk


def belirtec() -> str:
    govde = urllib.parse.urlencode({
        "grant_type": "client_credentials",
        "client_id": "bizigo-collector",
        "client_secret": "bizigo-collector-dev-secret",
    }).encode("ascii")

    durum, yuk = http_json(
        KEYCLOAK_TOKEN_URL,
        method="POST",
        body=govde,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )

    if durum != 200 or not isinstance(yuk, dict) or not yuk.get("access_token"):
        raise RuntimeError(f"Collector belirteci alınamadı: HTTP {durum} {yuk}")

    return str(yuk["access_token"])


def istatistik() -> dict[str, object]:
    durum, yuk = http_json(f"{API_ORIGIN}/internal/ingest/stats")
    if durum != 200 or not isinstance(yuk, dict):
        raise RuntimeError(f"Ingest sayacı okunamadı: HTTP {durum} {yuk}")
    return yuk


def manifest_ozeti(segmentler: list[str]) -> ManifestOzeti:
    if not segmentler:
        return ManifestOzeti(0, 0, 0, 0, 0)

    for segment in segmentler:
        if re.fullmatch(r"wal-\d+\.log", segment) is None:
            raise ValueError(f"Geçersiz WAL segment adı: {segment!r}")

    adlar = ", ".join("'" + segment.replace("'", "''") + "'" for segment in segmentler)
    ham = komut(
        "docker", "exec", POSTGRES,
        "psql", "-U", "bizigo", "-d", "bizigo", "-Atc",
        "select count(distinct wal_segment), count(*), count(verified_at), "
        "count(*) filter (where verified_at is null), coalesce(sum(event_count), 0) "
        "from raw_manifest where regexp_replace(wal_segment, '^.*/', '') "
        f"in ({adlar});",
    )
    parcalar = ham.split("|")
    if len(parcalar) != 5:
        raise RuntimeError(f"Manifest özeti çözülemedi: {ham!r}")
    return ManifestOzeti(*(int(parca) for parca in parcalar))


def saglik_bekle(container: str, timeout: float = 90) -> None:
    son = ""
    baslangic = time.monotonic()

    while time.monotonic() - baslangic < timeout:
        son = komut(
            "docker", "inspect", "--format", "{{.State.Health.Status}}", container,
            check=False,
        ).strip()
        if son == "healthy":
            return
        time.sleep(1)

    raise TimeoutError(f"{container} {timeout:.0f} sn içinde healthy olmadı; son={son!r}")


def otlp_batch(batch: int, run_id: str) -> dict[str, object]:
    nanos = int(time.time() * 1_000_000_000)
    kayitlar = [
        {
            "timeUnixNano": str(nanos + sira),
            "body": {
                "stringValue":
                    f"t63-rustfs-outage run={run_id} batch={batch} record={sira}",
            },
            "attributes": [
                {
                    "key": "t63.run_id",
                    "value": {"stringValue": run_id},
                },
                {
                    "key": "t63.batch",
                    "value": {"intValue": str(batch)},
                },
            ],
        }
        for sira in range(BATCH_BASINA)
    ]

    return {
        "resourceLogs": [{
            "resource": {"attributes": [{
                "key": "service.name",
                "value": {"stringValue": "t63-rustfs-outage"},
            }]},
            "scopeLogs": [{
                "scope": {"name": "t63.measurement"},
                "logRecords": kayitlar,
            }],
        }],
    }


def wal_baglama() -> tuple[str, str]:
    imaj = komut("docker", "inspect", "--format", "{{.Config.Image}}", API)
    volume = komut(
        "docker", "inspect", "--format",
        "{{range .Mounts}}{{if eq .Destination \"/var/lib/bizigo/wal\"}}{{.Name}}{{end}}{{end}}",
        API,
    )
    if not volume:
        raise RuntimeError("API WAL volume adı bulunamadı")
    return imaj, volume


def wal_dosyalari() -> dict[str, int]:
    imaj, volume = wal_baglama()

    ham = komut(
        "docker", "run", "--rm", "--entrypoint", "bash",
        "--volume", f"{volume}:/wal:ro", imaj,
        "-lc", "find /wal -maxdepth 1 -type f -name 'wal-*.log' -printf '%f %s\\n' | sort",
    )
    sonuc: dict[str, int] = {}
    for satir in ham.splitlines():
        if not satir.strip():
            continue
        ad, boyut = satir.rsplit(" ", 1)
        sonuc[ad] = int(boyut)
    return sonuc


def degisen_segmentler(once: dict[str, int], sonra: dict[str, int]) -> list[str]:
    return sorted(
        ad for ad, boyut in sonra.items()
        if boyut > once.get(ad, 0)
    )


def wal_segment_verisi(segment: str) -> bytes:
    if re.fullmatch(r"wal-\d+\.log", segment) is None:
        raise ValueError(f"Geçersiz WAL segment adı: {segment!r}")

    imaj, volume = wal_baglama()
    ham = komut(
        "docker", "run", "--rm", "--entrypoint", "base64",
        "--volume", f"{volume}:/wal:ro", imaj,
        "-w0", f"/wal/{segment}",
    )
    return base64.b64decode(ham, validate=True)


def wal_verisi_kayit_sayisi(veri: bytes) -> int:
    konum = 0
    kayit = 0

    while konum < len(veri):
        if len(veri) - konum < WAL_HEADER_BYTES:
            raise ValueError(f"Yarım WAL başlığı: offset={konum}")

        magic, uzunluk, crc = struct.unpack_from(">III", veri, konum)
        if magic != WAL_MAGIC:
            raise ValueError(f"WAL magic bozuk: offset={konum}, magic=0x{magic:08x}")

        govde_baslangici = konum + WAL_HEADER_BYTES
        govde_sonu = govde_baslangici + uzunluk
        if govde_sonu > len(veri):
            raise ValueError(f"Yarım WAL gövdesi: offset={konum}, uzunluk={uzunluk}")

        govde = veri[govde_baslangici:govde_sonu]
        if zlib.crc32(govde) & 0xFFFFFFFF != crc:
            raise ValueError(f"WAL CRC bozuk: offset={konum}")

        kayit += sum(1 for satir in govde.splitlines() if satir)
        konum = govde_sonu

    return kayit


def toparlanma_sorunlari(
    segmentler: list[str],
    wal_kayit_sayisi: int,
    manifest: ManifestOzeti,
) -> list[str]:
    sorunlar: list[str] = []

    if manifest.segment_sayisi != len(segmentler):
        sorunlar.append(
            f"segment eşleşmesi {manifest.segment_sayisi}/{len(segmentler)}"
        )
    if manifest.satir_sayisi < len(segmentler):
        sorunlar.append(
            f"manifest satırı {manifest.satir_sayisi}, en az {len(segmentler)} bekleniyor"
        )
    if manifest.dogrulanmis != manifest.satir_sayisi:
        sorunlar.append(
            f"doğrulanmış {manifest.dogrulanmis}/{manifest.satir_sayisi}"
        )
    if manifest.dogrulanmamis != 0:
        sorunlar.append(f"doğrulanmamış manifest {manifest.dogrulanmamis}")
    if manifest.kayit_sayisi != wal_kayit_sayisi:
        sorunlar.append(
            f"manifest kayıt sayısı {manifest.kayit_sayisi}, WAL {wal_kayit_sayisi}"
        )

    return sorunlar


def main() -> int:
    print("=== T63 · gerçek RustFS kesintisi ===")
    saglik_bekle(API)
    saglik_bekle(RUSTFS)

    token = belirtec()
    once = istatistik()
    wal_once = wal_dosyalari()
    run_id = f"t63-{time.time_ns()}-{uuid.uuid4().hex[:8]}"
    api_durdu = False
    rustfs_durdu = False

    try:
        komut("docker", "stop", RUSTFS)
        rustfs_durdu = True
        print("· RustFS durduruldu")

        ack_toplam = 0
        for batch in range(BATCH_SAYISI):
            durum, yuk = http_json(
                f"{API_ORIGIN}/v1/logs",
                method="POST",
                body=otlp_batch(batch, run_id),
                headers={"Authorization": f"Bearer {token}"},
            )
            if durum != 200 or not isinstance(yuk, dict):
                raise RuntimeError(f"Batch {batch} ack almadı: HTTP {durum} {yuk}")
            ack_toplam += int(yuk.get("accepted", -1))

        sonra = istatistik()
        batch_farki = int(sonra["accepted_batches"]) - int(once["accepted_batches"])
        kayit_farki = int(sonra["accepted_records"]) - int(once["accepted_records"])
        full_farki = int(sonra["rejected_full"]) - int(once["rejected_full"])

        beklenen_kayit = BATCH_SAYISI * BATCH_BASINA
        if (ack_toplam, batch_farki, kayit_farki, full_farki) != (
            beklenen_kayit, BATCH_SAYISI, beklenen_kayit, 0,
        ):
            raise AssertionError(
                "Sayaç/ack farkı beklenenden saptı: "
                f"ack={ack_toplam}, batches=+{batch_farki}, records=+{kayit_farki}, "
                f"rejected_full=+{full_farki}"
            )

        print(
            f"· depo kapalıyken ack={ack_toplam}, accepted_batches=+{batch_farki}, "
            f"accepted_records=+{kayit_farki}, rejected_full=+{full_farki}, run={run_id}"
        )

        komut("docker", "stop", API)
        api_durdu = True
        wal_sonra = wal_dosyalari()
        kosum_segmentleri = degisen_segmentler(wal_once, wal_sonra)
        if not kosum_segmentleri:
            raise AssertionError("Ack alındı ama bu koşumda büyüyen WAL segmenti bulunamadı")

        segment_kayitlari = sum(
            wal_verisi_kayit_sayisi(wal_segment_verisi(segment))
            for segment in kosum_segmentleri
        )
        if segment_kayitlari < beklenen_kayit:
            raise AssertionError(
                "Ack alınan kayıtlar WAL'da yok: "
                f"WAL={segment_kayitlari}, bu koşum en az={beklenen_kayit}"
            )

        kapali_manifest = manifest_ozeti(kosum_segmentleri)
        if kapali_manifest.satir_sayisi != 0:
            raise AssertionError(
                "RustFS kapalıyken bu koşumun segmenti manifest'e girdi: "
                f"{kapali_manifest}"
            )

        print(
            "· bu koşumun manifestsiz segmenti volume'da duruyor: "
            f"{', '.join(kosum_segmentleri)}; segment_kayıtları={segment_kayitlari}"
        )

        komut("docker", "start", RUSTFS)
        rustfs_durdu = False
        saglik_bekle(RUSTFS)
        komut("docker", "start", API)
        api_durdu = False
        saglik_bekle(API)

        son_manifest = ManifestOzeti(0, 0, 0, 0, 0)
        son_sorunlar = ["manifest henüz yok"]
        son_tarih = time.monotonic() + 90
        while time.monotonic() < son_tarih:
            son_manifest = manifest_ozeti(kosum_segmentleri)
            son_sorunlar = toparlanma_sorunlari(
                kosum_segmentleri,
                segment_kayitlari,
                son_manifest,
            )
            if not son_sorunlar:
                break
            time.sleep(2)

        if son_sorunlar:
            raise AssertionError(
                "RustFS döndü ama bu koşumun segmentleri birebir doğrulanmadı: "
                + "; ".join(son_sorunlar)
            )

        print(
            f"· toparlanma: segment={son_manifest.segment_sayisi}, "
            f"manifest={son_manifest.satir_sayisi}, "
            f"verified_at={son_manifest.dogrulanmis}, "
            f"unverified={son_manifest.dogrulanmamis}, "
            f"event_count={son_manifest.kayit_sayisi}/{segment_kayitlari}"
        )
        print("KRİTER GEÇTİ ✓")
        return 0
    finally:
        if rustfs_durdu:
            komut("docker", "start", RUSTFS, check=False)
            try:
                saglik_bekle(RUSTFS)
            except Exception as hata:  # Kurtarma hatası asıl sonucu gizlemesin.
                print(f"UYARI: RustFS geri kaldırılamadı: {hata}", file=sys.stderr)

        if api_durdu:
            komut("docker", "start", API, check=False)
            try:
                saglik_bekle(API)
            except Exception as hata:
                print(f"UYARI: API geri kaldırılamadı: {hata}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
