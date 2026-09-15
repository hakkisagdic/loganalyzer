#!/usr/bin/env python3
"""T63 · WAL yarısı — GERÇEK süreç, GERÇEK `SIGKILL`.

`WriteAheadLogTests` yarım çerçeveyi taklit ediyor ve dosyanın ŞEKLİNİ ölçüyor.
Bu program farklı bir şey ölçüyor: **ack verildikten sonra süreç gerçekten
öldürülürse veri diskte kalıyor mu.** Aradaki fark fsync'in tuttuğu yer.

Container gerekmiyor: `WriteAheadLog` yerel bir dizine yazıyor ve ack ham batch
fsync edildikten SONRA veriliyor — dayanıklılık sınırı ClickHouse'un berisinde.
Ürünün kendi cümlesi bu ("dayanıklılık sınırı WAL'dır, depo değil").

§6 GÖMÜLÜ: `WalOptions.FlushToDisk` bir bayrak, yani fsync'i kapatmak üretim
kodunu değiştirmeden mümkün. Kapalıyken bu ölçüm KIRMIZI yanmalı — yanmıyorsa
ölçüm `kill -9`'u değil başka bir şeyi ölçüyor.

ÜÇ KEZ ÖLÇÜM YAPILAMADI ve üçü de ölçümün kendisiyle ilgiliydi, ürünle değil:
  1. `AppendAsync` tek payload alıyor, liste değil (üretim de batch'i tek çerçeve
     yazıyor — `IngestGateway.cs:101`).
  2. Depo DIŞINA kurulan konak, `Bizigo.Ingest`in göreli ProjectReference'larını
     bozdu (MSB3202). Konak depo içinde olmak zorunda.
  3. C# kaynağını Python dizgesinden ÜRETMEK kaçış karakterlerini bozdu
     (CS1039). Çözüm: konak artık ÜRETİLMİYOR, `tools/t63-wal-harness/` altında
     commit edilmiş gerçek dosyalar.
Üçü de "kusur derlenmezse benim söz dizimimi ölçmüş olurum" tuzağının aynısı.
"""

from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent

YAZAR = KOK / "tools/t63-wal-harness/yazar"
OKUYUCU = KOK / "tools/t63-wal-harness/okuyucu"

BATCH_SAYISI = 8
BATCH_BASINA = 4

ACK_ZAMAN_ASIMI_SN = 120


def derle(proje: Path) -> str | None:
    """Ayrı derleme adımı.

    Gerekçesi ölçüldü: `dotnet run` içindeki derleme hatası, ölçümde *"ack satırı
    hiç gelmedi"* gibi görünüyor ve sebebi YANLIŞ raporluyor — yani ölçüm
    başarısızlığını ürün davranışı sanabilirdim.
    """
    sonuc = subprocess.run(
        ["dotnet", "build", str(proje), "-v", "q", "--nologo"],
        cwd=KOK, capture_output=True, text=True,
    )

    if sonuc.returncode != 0:
        satirlar = [l for l in (sonuc.stdout + sonuc.stderr).splitlines() if "error" in l]
        return "\n".join(satirlar[:5]) or (sonuc.stdout + sonuc.stderr)[-800:]

    return None


def diskteki_cerceveler(dizin: Path) -> int:
    sonuc = subprocess.run(
        ["dotnet", "run", "--project", str(OKUYUCU), "--no-build", "--", str(dizin)],
        cwd=KOK, capture_output=True, text=True,
    )

    if sonuc.returncode != 0:
        return -1

    for satir in reversed(sonuc.stdout.strip().splitlines()):
        if satir.strip().isdigit():
            return int(satir.strip())

    return -1


def bir_kosum(flush: bool) -> dict:
    dizin = Path(tempfile.mkdtemp(prefix="t63-wal-"))

    try:
        proc = subprocess.Popen(
            ["dotnet", "run", "--project", str(YAZAR), "--no-build", "--",
             str(dizin), str(BATCH_SAYISI), str(BATCH_BASINA), str(flush).lower()],
            cwd=KOK, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            # ZORUNLU: kendi oturumu olmadan `os.killpg` ÖLÇÜMÜ KOŞTURAN süreç
            # grubunu da öldürürdü — ölçüm kendini öldürüp "sonuç yok" verirdi.
            start_new_session=True,
        )

        acked = -1
        basladi = time.monotonic()

        while time.monotonic() - basladi < ACK_ZAMAN_ASIMI_SN:
            satir = proc.stdout.readline() if proc.stdout else ""

            if not satir and proc.poll() is not None:
                break

            if satir.startswith("ACKED "):
                acked = int(satir.split()[1])
                break

        if acked < 0:
            try:
                os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                proc.kill()

            return {
                "hata": "ack satırı gelmedi",
                "stderr": (proc.stderr.read() if proc.stderr else "")[:500],
            }

        # SIGKILL — ACK'TEN HEMEN SONRA, HİÇ BEKLEMEDEN.
        #
        # Süreç GRUBU öldürülüyor: `dotnet run` bir sarmalayıcı, gerçek konak onun
        # çocuğu. Yalnızca sarmalayıcıyı öldürmek konağı yaşatır ve `Dispose`
        # çağrılırsa fsync olur — ölçüm bozulur.
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            proc.kill()

        proc.wait(timeout=60)

        diskte = diskteki_cerceveler(dizin)

        return {"ack": acked, "diskte": diskte, "kayip": acked - diskte}
    finally:
        shutil.rmtree(dizin, ignore_errors=True)


def main() -> int:
    print("=== T63 · WAL yarısı — gerçek süreç, gerçek SIGKILL\n")

    for proje in (YAZAR, OKUYUCU):
        hata = derle(proje)

        if hata:
            print(f"KONAK DERLENMEDİ ({proje.name}) — bu bir ÖLÇÜM SONUCU DEĞİL:")
            print(hata)
            return 1

    print(f"ack'lenecek ÇERÇEVE: {BATCH_SAYISI} "
          f"(her biri {BATCH_BASINA} satırlık bir batch)\n")

    uretim = bir_kosum(flush=True)
    print(f"fsync AÇIK   (üretim) → {json.dumps(uretim, ensure_ascii=False)}")

    kusurlu = bir_kosum(flush=False)
    print(f"fsync KAPALI (kusur)  → {json.dumps(kusurlu, ensure_ascii=False)}")

    print()

    if "hata" in uretim:
        print(f"ÖLÇÜM YAPILAMADI: {uretim['hata']}")
        print(uretim.get("stderr", ""))
        return 1

    gecti = uretim.get("kayip") == 0
    print(f"KRİTER — ack'lenen çerçeve kaybı YOK: {'GEÇTİ ✓' if gecti else 'DÜŞTÜ ✗'}")

    if "hata" not in kusurlu:
        ayirt = kusurlu.get("kayip", 0) > 0
        print(f"§6 — fsync KAPALIYKEN kayıp görülüyor: "
              f"{'EVET ✓' if ayirt else 'HAYIR ✗'}")

        if not ayirt:
            print()
            print("      ⚠️ Ölçüm fsync'e DUYARSIZ. Yani `kill -9`'u değil başka bir")
            print("      şeyi ölçüyor: fsync'siz de veri kalıyorsa dayanıklılığı")
            print("      işletim sistemi tamponu sağlıyor demek — ve o bir garanti")
            print("      değil, yalnızca bu makinenin bugünkü davranışı.")

    return 0 if gecti else 1


if __name__ == "__main__":
    sys.exit(main())
