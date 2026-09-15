"""M12 — stdio ürün yüzeyi bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam `m10-kirmizi-olcumu.py`'den devralındı; gerekçesi ve iki katmanlı geri
alma dersi orada yazılı.

M12'nin ölçtüğü şey biraz farklı: bu turda bekçi kusurdan ÖNCE yazıldı ve
kırmızı yandığı GÖRÜLDÜ (kusur zaten ağaçtaydı — yüzey hiç kalkmıyordu).
Dolayısıyla buradaki kusurlar bekçinin *geri dönebildiğini* değil,
**hangi bekçinin hangi kusuru gördüğünü** ölçüyor — ve `yesil_kalmali`
sütunu bu turda asıl bilgi:

  * `Tools(...)` kapsam çözücüsü kapısını KOŞTURMUYOR; onu yalnızca
    `CreateOptions` koşturuyor. Yani çözücüyü düşüren bir kusur, grafik
    bekçisini YEŞİL bırakıp yalnızca sunucu-kurulum bekçisini düşürmeli.
    İkisi ayrı test olmasının tek gerekçesi bu, ve aşağıda ölçülüyor.

  * Ürün grafiğini koşulsuz kaydeden bir kusur, ürün bekçilerini YEŞİL
    bırakıp yalnızca simülatör bekçisini düşürmeli — çünkü kaybı simülatör
    yüzeyinin ilgisiz bir ayarı istemeye başlaması.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m12-olcum-yedek"

CLI = "src/Bizigo.Cli/McpCommandHandlers.cs"


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)
    not_: str = ""


GRAFIK = "Her_yuzeyin_servis_grafigi_kurulabiliyor"
KURULUM = "Urun_yuzeyi_sunucu_secenekleriyle_kurulabiliyor"
EKSIK_AYAR = "Eksik_ayar_adiyla_reddediliyor"
RET_YOK = "Ayarlar_verildiginde_ret_yok"
SIM = "Simulator_yuzeyi_urun_ayarlarini_istemiyor"


KUSURLAR = [
    Kusur(
        # M02→M12'nin ta kendisi: ilan var, grafik yok.
        ad="ürün grafiği hiç kaydedilmiyor (bugüne kadarki hâl)",
        dosya=CLI,
        bul="        services.AddControlPlane(controlPlane);",
        koy="        if (surface is McpSurface.Product) { return services.BuildServiceProvider(); }   // KIRMIZI-A\n"
            "        services.AddControlPlane(controlPlane);",
        kirmizi_bekleniyor=[GRAFIK, KURULUM],
        yesil_kalmali=[SIM, RET_YOK],
        not_="arızanın kendisi; simülatör bekçisi yeşil kalıyor — kayıp yüzeye ÖZGÜ",
    ),
    Kusur(
        ad="kapsam çözücüsü kaydı düşüyor",
        dosya=CLI,
        bul="        services.AddSingleton<AccessScopeResolver>();",
        koy="        // KIRMIZI-B: çözücü kaydı düştü",
        kirmizi_bekleniyor=[KURULUM],
        # ÖLÇÜMÜN ASIL BİLGİSİ: `Tools(...)` çözücü kapısını koşturmuyor, yani
        # grafik bekçisi bu kusuru GÖRMÜYOR. İki testin ayrı olmasının gerekçesi.
        yesil_kalmali=[GRAFIK],
        not_="grafik bekçisi bu kusuru GÖRMÜYOR — `Tools()` çözücü kapısını koşturmuyor",
    ),
    Kusur(
        ad="ürün grafiği KOŞULSUZ kaydediliyor (yüzey ayrımı kalkıyor)",
        dosya=CLI,
        bul="        if (surface is not McpSurface.Product)\n        {\n            return services.BuildServiceProvider();\n        }",
        koy="        // KIRMIZI-C: yüzey ayrımı kalktı",
        kirmizi_bekleniyor=[SIM],
        yesil_kalmali=[GRAFIK, KURULUM],
        not_="ürün bekçileri yeşil kalıyor — kayıp simülatörün ilgisiz bir ayarı istemesi",
    ),
    Kusur(
        # KUSUR ŞEKLİ ÖLÇÜLEREK DÜZELTİLDİ. İlk hâl `if (missing.Count == 0)
        # { return null; }` bloğunu düz bir `return null;` ile değiştiriyordu ve
        # DERLENMEDİ: altındaki satırlar ulaşılamaz hâle geliyor, `CS0162`
        # uyarısı bu depoda hata. O sonucu "kapı derleyicide" diye okumak yanlış
        # olurdu — kırılan şey benim kusurumun şekliydi, bekçinin ölçtüğü şey
        # değil. Aynı ders M10'un ilk kusurunda da çıkmıştı.
        #
        # Bu hâl derleniyor ve bekçinin ölçtüğü şeyi ölçüyor: iki ayardan
        # YALNIZCA BİRİ kontrol ediliyor.
        ad="eksik ayar kontrolü yarım: `BIZIGO_CONTROLPLANE` gözden kaçıyor",
        dosya=CLI,
        bul="        if (string.IsNullOrWhiteSpace(controlPlane))\n        {\n            missing.Add(ControlPlaneVariable);\n        }",
        koy="        // KIRMIZI-D: ikinci ayar hiç kontrol edilmiyor",
        kirmizi_bekleniyor=[EKSIK_AYAR],
        yesil_kalmali=[RET_YOK],
        not_="iki ayarın AYRI AYRI sınanmasının gerekçesi: yarım bir kontrol tek vakayla geçerdi",
    ),
    Kusur(
        # Ret VAR ama mesaj yanlış yüzeyi işaret ediyor — bugüne kadarki
        # arızanın mesajının aynısı.
        ad="ret mesajı ayarı değil DI'yı işaret ediyor",
        dosya=CLI,
        bul='        return $"`{McpSurfaces.ProductName}` yüzeyi için bağlantı ayarı eksik: "',
        koy='        return "Unable to resolve service for type \'AlertRuleService\'";   // KIRMIZI-E\n'
            '#pragma warning disable CS0162\n'
            '        return $"`{McpSurfaces.ProductName}` yüzeyi için bağlantı ayarı eksik: "',
        kirmizi_bekleniyor=[EKSIK_AYAR],
        yesil_kalmali=[RET_YOK],
        not_="ölçüt 'reddetti mi' DEĞİL 'ne yazması gerektiğini söyledi mi'",
    ),
]


def kos(argv: list[str]) -> subprocess.CompletedProcess[str]:
    ortam = dict(os.environ)
    ortam["DOTNET_ROOT"] = str(Path.home() / ".dotnet")
    ortam["PATH"] = f"{Path.home() / '.dotnet'}:{ortam.get('PATH', '')}"

    return subprocess.run(
        argv, cwd=KOK, env=ortam, capture_output=True, text=True, check=False)


def yedekle(yollar: set[str]) -> None:
    YEDEK.mkdir(exist_ok=True)

    for yol in yollar:
        # `copy2` DEĞİL: zaman damgasını taşımak geri almayı derlemeye
        # ulaştırmıyor (T44'te ölçüldü).
        shutil.copy(KOK / yol, YEDEK / yol.replace("/", "__"))


def geri_al(yollar: set[str]) -> None:
    for yol in yollar:
        shutil.copy(YEDEK / yol.replace("/", "__"), KOK / yol)
        (KOK / yol).touch()


def uygula(kusur: Kusur) -> None:
    yol = KOK / kusur.dosya
    metin = yol.read_text(encoding="utf-8")

    if kusur.bul not in metin:
        raise SystemExit(
            f"[{kusur.ad}] ÇAPA BULUNAMADI: {kusur.dosya}. Kusur uygulanamadı — "
            "ölçüm koşmadan durduruluyor.")

    yol.write_text(metin.replace(kusur.bul, kusur.koy, 1), encoding="utf-8")
    yol.touch()


def iddia_et(kusur: Kusur) -> None:
    """§6'nın İDDİA ADIMI: kusur dosyada gerçekten var mı."""
    metin = (KOK / kusur.dosya).read_text(encoding="utf-8")

    if kusur.koy not in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: kusur dosyada yok, ölçüm yalancı olurdu.")

    if kusur.bul not in kusur.koy and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: eski metin hâlâ orada.")

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI")


def derle() -> tuple[bool, str]:
    sonuc = kos(["dotnet", "build", "--nologo"])
    return sonuc.returncode == 0, sonuc.stdout + sonuc.stderr


def test_kos(filtre: str) -> int:
    """Ölçüt ÇIKIŞ KODU: 0 yeşil, değilse kırmızı.

    Sayıları çıktı metninden ayıklamak yerel dile bağlı olurdu (`Başarısız:` ↔
    `Failed:`) ve ayıklama düştüğünde sessizce sıfır üretirdi.
    """
    return kos([
        "dotnet", "test", "tests/Bizigo.UnitTests", "--no-build", "--nologo", "--filter", filtre,
    ]).returncode


def main() -> int:
    dosyalar = {k.dosya for k in KUSURLAR}
    yedekle(dosyalar)

    print(f"yedek: {YEDEK}")
    print(f"{len(KUSURLAR)} kusur ölçülecek\n")

    rapor: list[tuple[str, str]] = []

    try:
        for kusur in KUSURLAR:
            print(f"[{kusur.ad}]")
            uygula(kusur)
            iddia_et(kusur)

            basarili, cikti = derle()

            if not basarili:
                hata = next(
                    (s.strip() for s in cikti.splitlines() if ": error " in s),
                    "(hata satırı okunamadı)")
                rapor.append((kusur.ad, f"DERLENMEDİ — kapı derleyicide: {hata[:100]}"))
                print(f"    DERLENMEDİ: {hata[:100]}\n")
                geri_al({kusur.dosya})
                continue

            for test in kusur.kirmizi_bekleniyor:
                durum = "KIRMIZI ✓" if test_kos(f"FullyQualifiedName~{test}") != 0 else "YEŞİL KALDI ✗"
                rapor.append((f"{kusur.ad} → {test}", durum))
                print(f"    {test}: {durum}")

            for test in kusur.yesil_kalmali:
                durum = ("yeşil kaldı ✓ (bekçiler ayrı şey ölçüyor)"
                         if test_kos(f"FullyQualifiedName~{test}") == 0 else "KIRILDI ✗")
                rapor.append((f"{kusur.ad} → {test} [yeşil kalmalı]", durum))
                print(f"    {test}: {durum}")

            if kusur.not_:
                rapor.append((f"    ↳ {kusur.ad}", kusur.not_))

            print()
            geri_al({kusur.dosya})
    finally:
        # Geri alma `finally` içinde ve döngüden ÇIKARKEN koşuyor: M10'da bir
        # kabuk betiğinin `trap`'i çalıştı, geri aldı, ve döngü DEVAM EDİP bir
        # sonraki kusuru uyguladı. Çalışmış bir geri alma, durmuş bir koşum
        # değildir.
        geri_al(dosyalar)
        shutil.rmtree(YEDEK, ignore_errors=True)

    print("\n=== geri alındı; kusur ARANIYOR (varsaymıyoruz) ===")

    kalan = [
        f"{yol}:{no}"
        for yol in dosyalar
        for no, satir in enumerate((KOK / yol).read_text(encoding="utf-8").splitlines(), 1)
        if "KIRMIZI-" in satir
    ]

    if kalan:
        print("  ✗ AĞAÇTA KUSUR KALDI: " + ", ".join(kalan))
        return 1

    print("  ✓ hiçbir dosyada `KIRMIZI-` izi yok")

    print("\n=== TAM PAKET yeniden koşuyor ===")

    basarili, _ = derle()

    if not basarili:
        print("GERİ ALMA DERLEMEYE ULAŞMADI.")
        return 1

    kod = test_kos("FullyQualifiedName!~SidecarLive")
    print("tam paket: " + ("YEŞİL" if kod == 0 else "KIRMIZI"))

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    return 0 if kod == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
