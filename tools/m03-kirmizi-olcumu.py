#!/usr/bin/env python3
"""M03 — bekçilerin KIRMIZI yanabildiğinin ölçümü (CLAUDE.md §6).

Yordam:

    kusuru yaz → dosyayı OKU ve kusurun orada olduğunu İDDİA ET → koştur →
    yedekten geri al → EN SONDA tam paketi bir kez daha koştur

**İddia adımı** olmazsa ölçüm kendi başarısızlığını sessizce başarı diye
raporluyor: kırmızı beklenen bir koşumda yeşil sonuç iki şey anlatabiliyor —
*"kusur etkisiz"* ya da **"kusur hiç uygulanmadı"** — ve ikisi aynı çıktıyı
veriyor.

**Kontrol testi** olmazsa "kırmızı yandı" ölçümü kapının neyi tuttuğunu
söylemiyor: her şeyi düşüren bir kusur da aynı çıktıyı verirdi.

**Geri alma `git checkout` ile DEĞİL yedek dosyayla.** `git checkout <dosya>`
çalışma ağacının tamamına bakan bir araç ve commit edilmemiş işin üstüne yazıyor;
bu depoda bir kez bir ajanın kendi çalışmasını sildi.

**Yedekten dönerken `shutil.copy2` YOK.** `copy2` zaman damgasını da geri
yüklüyor: düzeltilmiş kaynak derlenmiş ikiliden eski görünüyor, MSBuild projeyi
atlıyor, `dotnet build` *"0 hata"* diyor ve koşan ikili hâlâ kusurlu. Bu depoda
bir kez ölçümü sessizce yalancı yaptı. `copy` + `touch` kullanılıyor.

**A ve B çifti bu ölçümün kalbi.** Tek yön ölçmek yetmez: *"her şeye
`wrong_surface` de"* diyen bir uygulama A'yı geçerdi. B onu düşürüyor.

⚠ **ÖLDÜRÜLEN BİR KOŞUM KUSURU AĞAÇTA BIRAKIR.** `finally` bloğu yalnızca süreç
normal ya da istisnayla biterse koşuyor; `SIGKILL` onu atlıyor. Bu bir kez
yaşandı: koşum öldürüldü ve `SimulatorStateStore.cs` **kilitsiz hâlde** çalışma
ağacında kaldı — derlenen, testleri geçen, ve sessizce yanlış bir dosya.

Bu yüzden koşum kesilirse **önce ağacı ölç, sonra devam et**:

    grep -rn "KIRMIZI-" sim/ src/ tests/     # boş olmalı
    find . -name "*.m03yedek"                # boş olmalı

Yedek varsa geri alma tamamlanmamış demektir; `copy` + `touch` ile yedekten
dönüp yedeği silin (`copy2` DEĞİL — zaman damgası ikiliyi eskitip derlemeyi
atlatıyor).
"""

import os
import pathlib
import shutil
import subprocess
import sys
import time

KOK = pathlib.Path(__file__).resolve().parent.parent
DOTNET = str(pathlib.Path.home() / ".dotnet" / "dotnet")

SET = KOK / "sim/Bizigo.Simulators/Mcp/Tools/ScenarioSetTool.cs"
BURST = KOK / "sim/Bizigo.Simulators/Mcp/Tools/SyslogBurstTool.cs"
DURUM = KOK / "sim/Bizigo.Simulators/Mcp/SimulatorStateStore.cs"
KAPI = KOK / "tests/Bizigo.UnitTests/McpComplianceTests.cs"

OLCUMLER = [
    {
        "ad": "A · Yanlış yüzey NOT_FOUND dönerse",
        "dosya": SET,
        "eski": "            var code = definition is null ? McpToolError.NotFound : McpToolError.WrongSurface;",
        "yeni": "            var code = McpToolError.NotFound; // KIRMIZI-A",
        "isaret": "KIRMIZI-A",
        "kirmizi": "SimulatorMcpToolTests.Yanlis_yuzeye_uygulanan_senaryo_yuzeyi_soyluyor",
        # Var olmayan ad ZATEN not_found; kusur onu bozmuyor. Kontrolün
        # geçmesi, kusurun DAR olduğunu ve ölçümün doğru kapıyı işaret
        # ettiğini gösteriyor.
        "kontrol": "SimulatorMcpToolTests.Var_olmayan_senaryo_wrong_surface_degil_not_found",
    },
    {
        "ad": "B · HER ŞEY wrong_surface dönerse (ters yön)",
        "dosya": SET,
        "eski": "            var code = definition is null ? McpToolError.NotFound : McpToolError.WrongSurface;",
        "yeni": "            var code = McpToolError.WrongSurface; // KIRMIZI-B",
        "isaret": "KIRMIZI-B",
        # A'nın tek başına ölçtüğü şey eksikti: bu uygulama A'yı GEÇİYOR.
        "kirmizi": "SimulatorMcpToolTests.Var_olmayan_senaryo_wrong_surface_degil_not_found",
        "kontrol": "SimulatorMcpToolTests.Yanlis_yuzeye_uygulanan_senaryo_yuzeyi_soyluyor",
    },
    {
        "ad": "C · Config yüzeyinin reddi WRONG_SURFACE'e düşerse",
        "dosya": SET,
        "eski": "                McpToolError.Unavailable,\n                ConfigSurfaceUnavailable(device, scenario),",
        "yeni": "                McpToolError.WrongSurface, // KIRMIZI-C\n                ConfigSurfaceUnavailable(device, scenario),",
        "isaret": "KIRMIZI-C",
        "kirmizi": "SimulatorMcpToolTests.Config_yuzeyi_calisirken_degistirilemiyor_ve_cozumu_soyluyor",
        "kontrol": "SimulatorMcpToolTests.Yanlis_yuzeye_uygulanan_senaryo_yuzeyi_soyluyor",
    },
    {
        "ad": "D · Durum kilidi kaldırılırsa",
        "dosya": DURUM,
        "eski": "        using var gate = AcquireLock();",
        "yeni": "        using var gate = new MemoryStream(); // KIRMIZI-D: kilit yok",
        "isaret": "KIRMIZI-D",
        "kirmizi": "SimulatorStateStoreTests.Iki_esZamanli_yazar_birbirinin_isini_silmiyor",
        # Atomiklik kilitten BAĞIMSIZ: `rename` hâlâ yerinde, yani tek yazarlı
        # yol bozulmuyor. Kontrolün geçmesi bunu gösteriyor.
        "kontrol": "SimulatorStateStoreTests.Yazmadan_sonra_gecici_dosya_kalmiyor",
    },
    {
        "ad": "E · Susturma basımda okunmazsa",
        "dosya": BURST,
        "eski": "        var state = context.State.Read().For(profile.Id);",
        "yeni": "        var state = new SimulatorDeviceState(); // KIRMIZI-E: durum okunmuyor",
        "isaret": "KIRMIZI-E",
        "kirmizi": "SimulatorMcpToolTests.Susturulmus_cihaz_basmiyor",
        # Susturma bayrağının YAZILMASI hâlâ çalışıyor; bozulan tek şey onun
        # bir DAVRANIŞA çevrilmesi. İkisi ayrı iddia, ayrı test.
        "kontrol": "SimulatorMcpToolTests.Susturma_kaldirilinca_damga_temizleniyor",
    },
    {
        "ad": "F · Uyum kapısı simülatör yüzeyine API kökünden bakarsa",
        "dosya": KAPI,
        "eski": "        McpSurface.Simulator => typeof(Bizigo.Cli.McpCommandHandlers).Assembly,",
        "yeni": "        McpSurface.Simulator => typeof(global::Program).Assembly, // KIRMIZI-F",
        "isaret": "KIRMIZI-F",
        # Kapının ESKİ hâli buydu ve yedi aracı hiç görmüyordu. Bu ölçüm,
        # düzeltmenin gerçek bir kusuru kapattığını gösteriyor.
        "kirmizi": "McpComplianceTests.Sunucunun_ilan_ettigi_araclar",
        "kontrol": "McpComplianceTests.Kesif_yeni_bir_araci_soylenmeden_buluyor",
    },
]


def kosk(filtre: str) -> bool:
    """Testi koşturur; True = geçti."""
    sonuc = subprocess.run(
        [DOTNET, "test", "tests/Bizigo.UnitTests", "--filter", f"FullyQualifiedName~{filtre}"],
        cwd=KOK,
        capture_output=True,
        text=True,
    )

    cikti = sonuc.stdout + sonuc.stderr

    # "0 test bulundu" SESSİZ BİR BAŞARISIZLIK: filtre yanlış yazılmışsa koşum
    # yeşil döner ve hiçbir şey ölçülmemiş olur. Bu deponun adını koyduğu sınıf.
    if "No test matches" in cikti or "hiçbir teste uymuyor" in cikti:
        print(f"    !! FİLTRE HİÇBİR TESTİ BULMADI: {filtre}")
        return None

    return sonuc.returncode == 0


def derle() -> bool:
    sonuc = subprocess.run(
        [DOTNET, "build", "Bizigo.sln", "-v", "quiet"], cwd=KOK, capture_output=True, text=True
    )
    return sonuc.returncode == 0


def main() -> int:
    yedekler = {}

    for olcum in OLCUMLER:
        dosya = olcum["dosya"]
        if dosya not in yedekler:
            yedek = dosya.with_suffix(dosya.suffix + ".m03yedek")
            shutil.copy(dosya, yedek)  # copy2 DEĞİL: zaman damgası taşınmıyor
            yedekler[dosya] = yedek

    basarisiz = []

    try:
        for olcum in OLCUMLER:
            print(f"\n=== {olcum['ad']}")
            dosya = olcum["dosya"]
            metin = dosya.read_text(encoding="utf-8")

            if olcum["eski"] not in metin:
                print(f"    !! DESEN BULUNAMADI — kusur uygulanamadı: {dosya.name}")
                basarisiz.append(olcum["ad"] + " (desen yok)")
                continue

            dosya.write_text(metin.replace(olcum["eski"], olcum["yeni"], 1), encoding="utf-8")
            os.utime(dosya, None)  # ikili eskimesin diye damga ŞİMDİ

            # İDDİA ADIMI: kusur gerçekten dosyada mı.
            if olcum["isaret"] not in dosya.read_text(encoding="utf-8"):
                print("    !! KUSUR DOSYADA YOK — ölçüm yapılmadı")
                basarisiz.append(olcum["ad"] + " (kusur yazılamadı)")
                continue

            print(f"    kusur dosyada doğrulandı ({olcum['isaret']})")

            if not derle():
                print("    !! KUSURLU HÂL DERLENMEDİ — ölçüm yapılamadı")
                basarisiz.append(olcum["ad"] + " (derlenmedi)")
            else:
                kirmizi = kosk(olcum["kirmizi"])
                kontrol = kosk(olcum["kontrol"])

                print(f"    bekçi   {olcum['kirmizi']}: {'GEÇTİ' if kirmizi else 'DÜŞTÜ'}")
                print(f"    kontrol {olcum['kontrol']}: {'GEÇTİ' if kontrol else 'DÜŞTÜ'}")

                if kirmizi is not False:
                    basarisiz.append(olcum["ad"] + " — bekçi kırmızı YANMADI")
                if kontrol is not True:
                    basarisiz.append(olcum["ad"] + " — kontrol düştü (kusur dar değil)")

            # Sıradaki ölçümden önce geri al.
            shutil.copy(yedekler[dosya], dosya)
            os.utime(dosya, None)

    finally:
        for dosya, yedek in yedekler.items():
            shutil.copy(yedek, dosya)
            os.utime(dosya, None)
            yedek.unlink()

    print("\n=== GERİ ALMA DERLEMEYE ULAŞTI MI — tam paket")

    # BU ADIM ZORUNLU. İddia adımı kusurun GİRDİĞİNİ kanıtlıyor; bu adım
    # ÇIKTIĞINI. Bu depoda geri alma bir kez derlemeye hiç ulaşmadı ve
    # `grep` temiz, `build` "0 hata", `test` kırmızı dedi — üçü de kendi
    # içinde doğruydu.
    if not derle():
        print("    !! GERİ ALINMIŞ HÂL DERLENMİYOR")
        basarisiz.append("geri alma derlenmedi")
    else:
        sonuc = subprocess.run(
            [DOTNET, "test", "tests/Bizigo.UnitTests", "--no-build"],
            cwd=KOK,
            capture_output=True,
            text=True,
        )
        print("    " + "\n    ".join(sonuc.stdout.strip().splitlines()[-6:]))

        if sonuc.returncode != 0:
            basarisiz.append("geri almadan sonra tam paket KIRMIZI")

    print("\n" + "=" * 60)

    if basarisiz:
        print("ÖLÇÜM BAŞARISIZ:")
        for hata in basarisiz:
            print(f"  · {hata}")
        return 1

    print(f"{len(OLCUMLER)} ölçümün hepsinde bekçi kırmızı yandı, kontroller geçti, geri alma temiz.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
