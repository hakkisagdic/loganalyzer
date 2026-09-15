#!/usr/bin/env python3
"""T63 · WAL yarısı — GERÇEK süreç, GERÇEK `SIGKILL`.

`WriteAheadLogTests` yarım çerçeveyi taklit ediyor ve o dosyanın ŞEKLİNİ ölçüyor.
Bu program farklı bir şeyi ölçüyor: **ack verildikten sonra süreç gerçekten
öldürülürse veri diskte kalıyor mu.** Aradaki fark fsync'in tuttuğu yer.

Neden container gerekmiyor: `WriteAheadLog` yerel bir dizine yazıyor ve ack ham
batch fsync edildikten SONRA veriliyor — yani dayanıklılık sınırı ClickHouse'un
berisinde. Ürünün kendi cümlesi bu ("dayanıklılık sınırı WAL'dır, depo değil").

§6 buraya GÖMÜLÜ: `WalOptions.FlushToDisk` bir bayrak, yani fsync'i kapatmak
üretim kodunu değiştirmeden mümkün. Kapalıyken bu ölçüm KIRMIZI yanmalı —
yanmıyorsa ölçüm `kill -9`'u değil başka bir şeyi ölçüyor.

Protokolün uyduğu kararlar (ticket'ta yazılı):
  - `SIGKILL`, `SIGTERM` DEĞİL: `SIGTERM` düzgün kapanışı tetikler ve o yol zaten
    fsync eder; o hâlde ölçüm graceful shutdown'ı ölçer.
  - Öldürme ack'ten HEMEN SONRA, hiç beklemeden.
  - Veri, yeniden AÇILARAK ve SAYILARAK doğrulanır; "dosya var" yeterli değil.
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
YAZAR = KOK / "tools/t63-wal-yazar"

BATCH_SAYISI = 8
KAYIT_BASINA = 4


def yazar_projesi_kur(hedef: Path) -> None:
    """Ack veren ve öldürülmeyi bekleyen küçük bir konak.

    Neden ayrı bir süreç: `SIGKILL` yakalanamaz ve test koşucusunu da öldürür.
    Ölçüm kodu öldürülen sürecin DIŞINDA durmak zorunda.
    """
    hedef.mkdir(parents=True, exist_ok=True)

    (hedef / "t63-wal-yazar.csproj").write_text(
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);CA1305;CA1307</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Bizigo.Ingest/Bizigo.Ingest.csproj" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )

    (hedef / "Program.cs").write_text(
        """using System.Text;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// T63 ölçüm konağı. ÜRÜN KODU DEĞİL — `WriteAheadLog`'u üretimdeki gibi kurup
// ack veriyor, sonra öldürülmeyi bekliyor.
//
// argv: <dizin> <batchSayisi> <kayitBasina> <flushToDisk>

var directory = args[0];
var batches = int.Parse(args[1]);
var perBatch = int.Parse(args[2]);
var flush = bool.Parse(args[3]);

var options = new WalOptions
{
    Directory = directory,
    FlushToDisk = flush,
};

using var wal = new WriteAheadLog(
    Options.Create(options),
    NullLogger<WriteAheadLog>.Instance);

var acked = 0;

for (var b = 0; b < batches; b++)
{
    var payloads = new List<ReadOnlyMemory<byte>>();

    for (var r = 0; r < perBatch; r++)
    {
        payloads.Add(Encoding.UTF8.GetBytes($"t63-batch{b}-kayit{r}"));
    }

    // ÜRÜNÜN SIRASI: yaz + fsync, SONRA ack. `AppendAsync` dönmesi ack'in
    // verilebildiği an demek.
    await wal.AppendAsync(payloads, CancellationToken.None);

    acked += perBatch;
}

// Ack'lerin hepsi verildi. Ölçüm bu satırı okuyup HEMEN öldürüyor.
Console.Out.WriteLine($"ACKED {acked}");
await Console.Out.FlushAsync();

// Öldürülmeyi bekle. Kendi başına çıkmamalı: düzgün çıkış `Dispose`'u çağırır
// ve o da fsync eder — yani ölçüm fsync'i değil kapanışı ölçmüş olurdu.
await Task.Delay(Timeout.Infinite);
""",
        encoding="utf-8",
    )


def sayaci_oku(dizin: Path) -> int:
    """WAL'ı YENİDEN AÇIP çerçeveleri sayar — 'dosya var' yeterli değil."""
    okuyucu = KOK / "tools/t63-wal-okuyucu"
    okuyucu.mkdir(parents=True, exist_ok=True)

    (okuyucu / "t63-wal-okuyucu.csproj").write_text(
        (YAZAR / "t63-wal-yazar.csproj").read_text(encoding="utf-8"),
        encoding="utf-8",
    )

    (okuyucu / "Program.cs").write_text(
        """using Bizigo.Ingest.Wal;

// Segmentleri DİSKTEN sayıyor: öldürülmüş süreç son segmenti mühürlemedi, yani
// `ListSealedSegments()` onu göstermez — ve tam o segment ölçümün konusu.
var count = 0;

foreach (var path in Directory.EnumerateFiles(args[0], "wal-*.log").Order(StringComparer.Ordinal))
{
    foreach (var _ in WriteAheadLog.ReadFrames(path))
    {
        count++;
    }
}

Console.Out.WriteLine(count);
""",
        encoding="utf-8",
    )

    sonuc = subprocess.run(
        ["dotnet", "run", "--project", str(okuyucu), "-c", "Debug", "--", str(dizin)],
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
            ["dotnet", "run", "--project", str(YAZAR), "-c", "Debug", "--",
             str(dizin), str(BATCH_SAYISI), str(KAYIT_BASINA), str(flush).lower()],
            cwd=KOK, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            # ZORUNLU: kendi oturumu olmadan `os.killpg` ÖLÇÜMÜ KOŞTURAN süreç
            # grubunu da öldürürdü — yani bu satır olmadan ölçüm kendini
            # öldürerek "sonuç yok" verirdi.
            start_new_session=True,
        )

        acked = -1
        basladi = time.monotonic()

        # Ack satırını bekle. Zaman aşımı ölçümün kendisi değil, takılma kapısı.
        while time.monotonic() - basladi < 180:
            satir = proc.stdout.readline() if proc.stdout else ""

            if not satir and proc.poll() is not None:
                break

            if satir.startswith("ACKED "):
                acked = int(satir.split()[1])
                break

        if acked < 0:
            proc.kill()
            return {"hata": "ack satırı hiç gelmedi", "stderr": (proc.stderr.read() if proc.stderr else "")[:600]}

        # SIGKILL — ACK'TEN HEMEN SONRA, HİÇ BEKLEMEDEN.
        # `dotnet run` bir sarmalayıcı süreç: çocuk gerçek konak. Süreç GRUBUNU
        # öldürmek gerekiyor, yoksa konak yaşamaya devam eder ve `Dispose`
        # çağrılırsa fsync olur — ölçüm bozulur.
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            proc.kill()

        proc.wait(timeout=30)

        diskte = sayaci_oku(dizin)

        return {"ack": acked, "diskte": diskte, "kayip": acked - diskte}
    finally:
        shutil.rmtree(dizin, ignore_errors=True)


def main() -> int:
    print("=== T63 · WAL yarısı — gerçek süreç, gerçek SIGKILL\n")

    YAZAR.parent.mkdir(parents=True, exist_ok=True)
    yazar_projesi_kur(YAZAR)

    print(f"ack'lenecek kayıt: {BATCH_SAYISI * KAYIT_BASINA} "
          f"({BATCH_SAYISI} batch × {KAYIT_BASINA})\n")

    # 1 · ÜRETİM AYARI: fsync açık. Ack'lenen hiçbir kayıt kaybolmamalı.
    uretim = bir_kosum(flush=True)
    print(f"fsync AÇIK   (üretim)  → {json.dumps(uretim, ensure_ascii=False)}")

    # 2 · §6 KUSURU: fsync kapalı. Ölçüm burada KIRMIZI yanmalı.
    kusurlu = bir_kosum(flush=False)
    print(f"fsync KAPALI (kusur)   → {json.dumps(kusurlu, ensure_ascii=False)}")

    print()

    if "hata" in uretim:
        print(f"ÖLÇÜM YAPILAMADI: {uretim['hata']}")
        print(uretim.get("stderr", ""))
        return 1

    gecti = uretim.get("kayip") == 0
    print(f"KRİTER (ack'lenen kayıp yok): {'GEÇTİ ✓' if gecti else 'DÜŞTÜ ✗'}")

    if "hata" not in kusurlu:
        ayirt = kusurlu.get("kayip", 0) > 0
        print(f"§6 (fsync kapalıyken kayıp GÖRÜLÜYOR): {'EVET ✓' if ayirt else 'HAYIR ✗ — ölçüm fsync-duyarsız'}")

        if not ayirt:
            print("      Bu, ölçümün `kill -9`'u ölçmediği anlamına gelir: fsync'siz de")
            print("      veri kalıyorsa işletim sistemi tamponu bizim yerimize dayanıklılık")
            print("      sağlıyor demek, ve o garanti değil.")

    return 0 if gecti else 1


if __name__ == "__main__":
    sys.exit(main())
