#!/usr/bin/env python3
"""M02 — bekçilerin KIRMIZI yanabildiğinin ölçümü (CLAUDE.md §6).

Yordam dört adımlı ve son ikisi bu depoda pahalıya öğrenildi:

    kusuru yaz → dosyayı OKU ve kusurun orada olduğunu İDDİA ET → koştur
    → geri al → GERİ ALMANIN DERLEMEYE ULAŞTIĞINI GÖR

İddia adımı olmazsa yeşil bir sonuç *"kusur etkisiz"* ile *"kusur hiç
uygulanmadı"* arasında ayrım yapamıyor. Son adım da ayrı bir turda ödendi:
`shutil.copy2` zaman damgasını geri yüklediği için düzeltilmiş dosya derlenmiş
ikiliden ESKİ göründü, MSBuild projeyi atladı ve `dotnet build` "0 hata" derken
ikili hâlâ kusurluydu. Burada `copy` + `touch` kullanılıyor.

Her ölçümün bir de KONTROL testi var: kusurdan etkilenmemesi gereken bir test.
Yalnızca "kırmızı yandı" ölçülseydi, her şeyi düşüren bir kusur da aynı çıktıyı
verirdi — kapının NEYİ tuttuğu değil, yalnızca tuttuğu ölçülmüş olurdu.
"""

import os
import pathlib
import shutil
import subprocess
import sys

KOK = pathlib.Path(__file__).resolve().parent.parent
DOTNET = str(pathlib.Path.home() / ".dotnet" / "dotnet")

CEKIRDEK = KOK / "src/Bizigo.Commands/ParserCommands.cs"
KATALOG = KOK / "src/Bizigo.Commands/CommandCatalog.cs"
KATALOG_TESTI = KOK / "tests/Bizigo.UnitTests/CommandCatalogTests.cs"
ARAC = KOK / "src/Bizigo.Commands.Mcp/CommandTool.cs"

OLCUMLER = [
    {
        "ad": "A · Komut çekirdeği konsola yazarsa",
        "dosya": CEKIRDEK,
        "eski": "        var expanded = Expand(files);\n\n        if (expanded.Count == 0)\n        {\n            return CommandOutcome<ParserLintOutcome>.Failed(",
        "yeni": "        var expanded = Expand(files);\n        Console.WriteLine($\"KIRMIZI-A {expanded.Count}\");\n\n        if (expanded.Count == 0)\n        {\n            return CommandOutcome<ParserLintOutcome>.Failed(",
        "isaret": "KIRMIZI-A",
        "kirmizi": "CommandCoreConsoleTests.Komut_cekirdeginde_Console_kullanimi_yok",
        # Kapsama bekçisi konsoldan etkilenmiyor: kusur DAR, yalnızca stdout
        # disiplinini bozuyor.
        "kontrol": "CommandCatalogTests.Araclar_katalogla_birebir",
    },
    {
        "ad": "B · Bir araç katalogda muaf gösterilirse",
        "dosya": KATALOG,
        "eski": '''        new("parser.lint", "parser lint",
            "Parser doğrulaması",
            "Parser YAML'ının şemasını doğrular ve ReDoS taraması yapar.",
            CommandExposure.Tool.Instance),''',
        "yeni": '''        new("parser.lint", "parser lint",
            "Parser doğrulaması",
            "Parser YAML'ının şemasını doğrular ve ReDoS taraması yapar.",
            new CommandExposure.Exempt("KIRMIZI-B: aracı olduğu hâlde muaf gösteriliyor, gerekçe uydurma.")),''',
        "isaret": "KIRMIZI-B",
        "kirmizi": "CommandCatalogTests.Araclar_katalogla_birebir",
        # Komut SAYISI değişmiyor — yalnızca sınıfı. Sayıya bakan bekçi yeşil
        # kalıyor ve bu, iki bekçinin farklı şeyler tuttuğunun kanıtı.
        "kontrol": "CommandCatalogTests.Katalog_CLI_yaprak_komutlariyla_ayni_sayida",
    },
    {
        "ad": "C · Muaf sayısı sabitle tutulmazsa",
        "dosya": KATALOG_TESTI,
        "eski": "    private const int ExpectedExemptCount = 5;",
        "yeni": "    private const int ExpectedExemptCount = 6; // KIRMIZI-C",
        "isaret": "KIRMIZI-C",
        "kirmizi": "CommandCatalogTests.Muaf_sayisi_sabitle_tutuluyor",
        "kontrol": "CommandCatalogTests.Ucuncu_bir_hal_yok",
    },
    {
        "ad": "D · Bir aracın bağlam maliyeti tavanı aşarsa",
        "dosya": ARAC,
        "eski": "    public sealed override string ToolDescription => descriptor.Summary;",
        "yeni": (
            "    public sealed override string ToolDescription => descriptor.Summary\n"
            "        // KIRMIZI-D: açıklama şişiriliyor — bağlam bütçesinin en pahalı kalemi.\n"
            "        + string.Concat(Enumerable.Repeat(\" bağlam bütçesini şişiren gereksiz açıklama metni\", 40));"
        ),
        "isaret": "KIRMIZI-D",
        "kirmizi": "McpSchemaBudgetTests.Arac_semalarinin_baglam_maliyeti",
        # Şişen açıklama araç ADINI değiştirmiyor: kapsama bekçisi yeşil.
        "kontrol": "CommandCatalogTests.Araclar_katalogla_birebir",
    },
]


def kosum(filtre):
    """Tek bir testi koşturur; (gecti, ozet) döndürür."""
    ortam = {
        **os.environ,
        "DOTNET_ROOT": str(pathlib.Path.home() / ".dotnet"),
        "PATH": f"{pathlib.Path.home() / '.dotnet'}:{os.environ['PATH']}",
    }

    sonuc = subprocess.run(
        [DOTNET, "test", "tests/Bizigo.UnitTests", "--nologo", "-v", "q",
         "--filter", f"FullyQualifiedName~{filtre}"],
        cwd=KOK, capture_output=True, text=True, env=ortam,
    )
    ciktı = sonuc.stdout + sonuc.stderr

    # "Hiç test koşmadı" yeşille aynı çıkış kodunu veriyor — bir kapı geçişi
    # değil, kapının hiç sorulmaması. Ayrı ayırt ediliyor.
    if "Toplam:     0" in ciktı or "Total:     0" in ciktı:
        return None, "HİÇ TEST KOŞMADI (filtre eşleşmedi)"

    ozet = next((s.strip() for s in ciktı.splitlines()
                 if "Başarısız:" in s or "Failed:" in s), "özet okunamadı")
    return sonuc.returncode == 0, ozet


def main():
    rapor = []

    for olcum in OLCUMLER:
        dosya = olcum["dosya"]
        yedek = dosya.with_suffix(dosya.suffix + ".yedek")
        shutil.copy2(dosya, yedek)

        try:
            metin = dosya.read_text(encoding="utf-8")

            if olcum["eski"] not in metin:
                print(f"[{olcum['ad']}] ÇAPA BULUNAMADI — ölçüm yapılamadı.")
                rapor.append((olcum["ad"], "ÇAPA YOK", "-"))
                continue

            dosya.write_text(metin.replace(olcum["eski"], olcum["yeni"], 1), encoding="utf-8")

            # Kusurun dosyada OLDUĞUNU İDDİA ET.
            tazeden = dosya.read_text(encoding="utf-8")
            assert olcum["isaret"] in tazeden, f"{olcum['ad']}: kusur dosyada YOK"
            assert olcum["eski"] not in tazeden, f"{olcum['ad']}: eski satır hâlâ duruyor"
            print(f"[{olcum['ad']}] kusur dosyada doğrulandı ({olcum['isaret']}).")

            kirmizi, k_ozet = kosum(olcum["kirmizi"])
            kontrol, n_ozet = kosum(olcum["kontrol"])

            print(f"  hedef   {olcum['kirmizi']}: {'KIRMIZI ✓' if kirmizi is False else 'YEŞİL ✗'} — {k_ozet}")
            print(f"  kontrol {olcum['kontrol']}: {'yeşil ✓' if kontrol is True else 'KIRMIZI ✗'} — {n_ozet}")

            rapor.append((
                olcum["ad"],
                "KIRMIZI ✓" if kirmizi is False else f"YEŞİL ✗ ({k_ozet})",
                "yeşil ✓" if kontrol is True else f"KIRMIZI ✗ ({n_ozet})",
            ))
        finally:
            # `copy2` DEĞİL: zaman damgasını geri yüklerse MSBuild düzeltmeyi
            # görmüyor ve bir sonraki koşum kusurlu ikiliyi ölçüyor.
            shutil.copy(yedek, dosya)
            dosya.touch()
            yedek.unlink()

    print("\n=== ÖZET ===")
    for ad, hedef, kontrol in rapor:
        print(f"{ad}\n    hedef: {hedef}\n    kontrol: {kontrol}")

    return 0 if all(h.startswith("KIRMIZI ✓") and k.startswith("yeşil ✓") for _, h, k in rapor) else 1


if __name__ == "__main__":
    sys.exit(main())
