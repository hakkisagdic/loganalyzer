#!/usr/bin/env python3
"""M21 · §6 — T63'ün okuyacağı sayaç bekçileri kırmızı yanabiliyor mu.

Bu turun özel sorusu: bekçilerin öznesi bir SAYAÇ, ve sayaç T63'ün depo
yarısının GİRDİSİ. Yani bu bekçiler yanlış yeşil verirse, T63 yanlış hüküm verir
ve kimse fark etmez — kusurlar bu yüzden `IngestGateway`in sayaç çağrılarına
uygulanıyor, teste değil.

Desen M10–M19'dan: `shutil.copy` (copy2 DEĞİL), `finally`de geri yükleme, sonda
ağaçta `KIRMIZI-` grep'i, ölçüt çıkış kodu.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent

BEKCI = "IngestGatewayTests"

GECIT = KOK / "src/Bizigo.Ingest/Pipeline/IngestGateway.cs"
SAYAC = KOK / "src/Bizigo.Ingest/Pipeline/IngestStats.cs"


@dataclass
class Kusur:
    ad: str
    dosya: Path
    eski: str
    yeni: str
    kirmizi_bekleniyor: bool = True
    not_: str = ""


KUSURLAR = [
    Kusur(
        ad="KIRMIZI-RejectFull-cagrisi-dustu",
        dosya=GECIT,
        eski="            _stats.RejectFull();",
        yeni="            /* KIRMIZI-RejectFull-cagrisi-dustu */",
        not_="T63'ün okuyacağı sayaç artmazsa yanmalı. DAVRANIŞ aynı kalıyor "
        "(istemci yine 503 alıyor), yani eski testler yeşil kalır — bu kusurun "
        "ölçtüğü şey tam olarak o boşluk: davranış ölçülüyordu, sayaç değil.",
    ),
    Kusur(
        ad="KIRMIZI-dolu-batch-kabul-de-sayiliyor",
        dosya=GECIT,
        eski="            _stats.RejectFull();",
        yeni="            _stats.RejectFull();\n"
        "            _stats.Accepted(0);  // KIRMIZI-dolu-batch-kabul-de-sayiliyor",
        not_="Reddedilen batch kabul sayılırsa yanmalı. Bu kusur T63 için en "
        "tehlikelisi: `AcceptedBatches` artmaya devam eder ve depo yarısı "
        "'ingest devam ediyor' der, oysa hiçbir veri yazılmamıştır.",
    ),
    Kusur(
        ad="KIRMIZI-kabul-sayaci-hic-artmiyor",
        dosya=SAYAC,
        eski="        Interlocked.Increment(ref _acceptedBatches);",
        yeni="        /* KIRMIZI-kabul-sayaci-hic-artmiyor */",
        not_="Taban çizgisi testi yanmalı: T63 `AcceptedBatches`in ARTMASINI "
        "bekliyor, yani hiç artmayan bir sayaç ölçümü sessizce ters çevirir.",
    ),
    Kusur(
        ad="KIRMIZI-kayit-sayaci-batch-sayiyor",
        dosya=SAYAC,
        eski="        Interlocked.Increment(ref _acceptedBatches);\n        Interlocked.Add(ref _acceptedRecords, recordCount);",
        yeni="        Interlocked.Increment(ref _acceptedBatches);\n"
        "        Interlocked.Increment(ref _acceptedRecords);  // KIRMIZI-kayit-sayaci-batch-sayiyor",
        not_="`+(N×M)` beklentisi `+N`e dönerse yanmalı. İki sayacın AYRI "
        "olmasının sebebi bu: batch sayısı eşitken kayıt kaybı yalnızca ikinci "
        "sayaçta görünüyor.",
    ),
    Kusur(
        ad="KIRMIZI-retry-ipucu-sabitlendi",
        dosya=GECIT,
        eski="                _walOptions.RetryAfterSeconds);",
        yeni="                60);  // KIRMIZI-retry-ipucu-sabitlendi",
        not_="İpucu yapılandırmadan gelmezse yanmalı — operatörün ayarladığı "
        "değerin yok sayılması, T63'ün 'ack duruyor ve istemciye ne kadar "
        "bekleyeceği söyleniyor' iddiasını boşa çıkarır.",
    ),
]


def kos() -> int:
    return subprocess.run(
        ["dotnet", "test", "tests/Bizigo.UnitTests", "--filter", f"FullyQualifiedName~{BEKCI}"],
        cwd=KOK, capture_output=True, text=True,
    ).returncode


def main() -> int:
    print(f"=== M21 §6 ölçümü · {len(KUSURLAR)} kusur\n")

    taban = kos()
    if taban != 0:
        print(f"DUR: taban zaten kırmızı (çıkış {taban}).")
        return 1
    print("taban: YEŞİL ✓\n")

    basarisiz: list[str] = []

    for kusur in KUSURLAR:
        yedek = kusur.dosya.with_suffix(kusur.dosya.suffix + ".m21yedek")
        shutil.copy(kusur.dosya, yedek)

        try:
            metin = kusur.dosya.read_text(encoding="utf-8")

            if kusur.eski not in metin:
                print(f"  ATLANDI (çapa yok): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: çapa bulunamadı")
                continue

            kusur.dosya.write_text(metin.replace(kusur.eski, kusur.yeni, 1), encoding="utf-8")

            # §6: kusurun DOSYADA olduğunu geri okuyarak doğrula.
            if kusur.ad not in kusur.dosya.read_text(encoding="utf-8"):
                print(f"  ATLANDI (kusur yazılamadı): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: kusur dosyada değil")
                continue

            cikis = kos()
            kirmizi = cikis != 0

            if kirmizi == kusur.kirmizi_bekleniyor:
                print(f"  {'KIRMIZI ✓' if kirmizi else 'YEŞİL ✓ (beklendiği gibi)'}  {kusur.ad}")
            else:
                beklenen = "kırmızı" if kusur.kirmizi_bekleniyor else "yeşil"
                print(f"  BAŞARISIZ  {kusur.ad} — {beklenen} beklendi, çıkış {cikis}")
                basarisiz.append(kusur.ad)

            if kusur.not_:
                print(f"      {kusur.not_}")
        finally:
            shutil.copy(yedek, kusur.dosya)
            yedek.unlink()

    son = kos()
    print(f"\ngeri yükleme sonrası: {'YEŞİL ✓' if son == 0 else f'KIRMIZI ✗ ({son})'}")

    kalinti = subprocess.run(
        ["grep", "-rn", "KIRMIZI-", "src/", "tests/"], cwd=KOK, capture_output=True, text=True,
    ).stdout.strip()
    print(f"ağaçta KIRMIZI- kalıntısı: {'YOK ✓' if not kalinti else 'VAR ✗'}")

    if basarisiz:
        print(f"\n{len(basarisiz)} kalem başarısız:")
        for ad in basarisiz:
            print(f"  - {ad}")
        return 1

    print(f"\nTÜMÜ GEÇTİ · {len(KUSURLAR)}/{len(KUSURLAR)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
