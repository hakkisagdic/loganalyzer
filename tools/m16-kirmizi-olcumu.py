"""M16 — gözlem yüzeyi bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam `m15-kirmizi-olcumu.py`'den devralındı.

M16'nın bekçileri iki eksende:

  * OKUYUCUNUN VARLIĞI — `RcaQuotaUsage.BySource`'un üretimde bir çağıranı var
    mı (MemberRef tablosu). M15'ten önce bu bekçi KIRMIZI yanardı.
  * RAPORUN İÇERİĞİ — kırılım ve kaynak başına ETKİN sınır gerçekten basılıyor
    mu. Çağrının varlığı içeriği garanti etmiyor: alanı okuyup sonucu atan bir
    kod MemberRef tablosunda aynı satırı üretir.

`yesil_kalmali` ikisinin ayrı olduğunu ölçüyor.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m16-olcum-yedek"

RAPOR = "src/Bizigo.Cli/RcaQuotaCommandHandlers.cs"


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)
    not_: str = ""


OKUYUCU = "Kaynak_kirilimini_uretimde_bir_okuyan_var"
ICERIK = "Rapor_kaynak_kirilimini_ve_etkin_siniri_basiyor"
SINIRSIZ = "Sinirsiz_tavan_sifir_diye_basilmiyor"


KUSURLAR = [
    Kusur(
        # M15'ten ÖNCEKİ HÂL: kırılım hesaplanıyor ama okunmuyor.
        ad="rapor kırılımı hiç okumuyor (hesapla-ve-at hâline dönüş)",
        dosya=RAPOR,
        bul="            var used = usage.BySource.TryGetValue(source, out var count) ? count : 0;",
        koy="            var used = 0;   // KIRMIZI-A",
        # ÖLÇÜM TAHMİNİMİ DÜZELTTİ. `OKUYUCU` bekçisinin de yanacağını
        # yazmıştım; YEŞİL KALDI, ve sebebi belgelendi: rapor `BySource`'a
        # başka bir yerden de dokunuyor (`usage.BySource.Count == 0`), yani
        # MemberRef tablosunda satır duruyor. Bir çağrının VARLIĞI, o çağrının
        # işe yaradığını söylemiyor — ve `ICERIK` bekçisinin var olma sebebi
        # tam olarak bu.
        #
        # İlk turda `ICERIK` de yeşil kalmıştı (yalnızca adları ve etkin
        # sınırları sınıyordu, ikisi de kırılımı OKUMADAN üretilebilir);
        # `BySource`'tan gelen SAYILAR çivilendikten sonra kırmızı yanıyor.
        kirmizi_bekleniyor=[ICERIK],
        yesil_kalmali=[OKUYUCU, SINIRSIZ],
        not_="MemberRef bekçisi yeşil kalıyor (arızi `.Count` okuması) — içerik bekçisi şart",
    ),
    Kusur(
        # Kırılım okunuyor ama ETKİN sınır kaynak başına basılmıyor: rezervin
        # varlığı raporda görünmez oluyor.
        ad="etkin sınır kaynak başına değil tek sayı basılıyor",
        dosya=RAPOR,
        bul="            var effective = RcaQuotaGate.EffectiveLimit(\n"
            "                options.DailyPerGroup, options.EventReservePercent, source);",
        koy="            var effective = options.DailyPerGroup;   // KIRMIZI-B",
        kirmizi_bekleniyor=[ICERIK],
        yesil_kalmali=[OKUYUCU, SINIRSIZ],
        not_="OKUYUCU bekçisi yeşil kalıyor — çağrı duruyor, kaybolan şey rezervin görünürlüğü",
    ),
    Kusur(
        # Yalnızca tüketimi OLAN kaynaklar basılıyor: "hiç koşmamış" ile
        # "raporda yok" aynı şeye iniyor.
        ad="tüketimi olmayan kaynak raporda görünmüyor",
        dosya=RAPOR,
        bul="        foreach (var source in Enum.GetValues<RcaTriggerSource>().Order())",
        koy="        foreach (var source in usage.BySource.Keys.Order())   // KIRMIZI-C",
        kirmizi_bekleniyor=[ICERIK],
        yesil_kalmali=[OKUYUCU, SINIRSIZ],
        not_="'ajan hiç koşmamış' ile 'ajan raporda yok' aynı cevaba iniyor",
    ),
    Kusur(
        ad="sınırsız tavan `0` diye basılıyor",
        dosya=RAPOR,
        bul='        limit <= 0 ? "sınırsız" : limit.ToString(CultureInfo.InvariantCulture);',
        koy="        limit.ToString(CultureInfo.InvariantCulture);   // KIRMIZI-D",
        kirmizi_bekleniyor=[SINIRSIZ],
        yesil_kalmali=[OKUYUCU],
        not_="'tavan sıfır' ile 'tavan yok' zıt iş emri veriyor",
    ),
    Kusur(
        ad="boş pencere kendini söylemiyor",
        dosya=RAPOR,
        bul='                ? "Bu pencerede hiç koşum yok — rezerv kararı için veri henüz birikmedi."',
        koy='                ? "kota raporu"   // KIRMIZI-E',
        kirmizi_bekleniyor=[SINIRSIZ],
        yesil_kalmali=[OKUYUCU, ICERIK],
        not_="'veri birikmedi' ile 'kota dolu' aynı rapora düşerse operatör yanlış karar verir",
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
        raise SystemExit(f"[{kusur.ad}] ÇAPA BULUNAMADI: {kusur.dosya}. Ölçüm durduruluyor.")

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
                rapor.append((kusur.ad, f"DERLENMEDİ: {hata[:110]}"))
                print(f"    DERLENMEDİ: {hata[:110]}\n")
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
        # Geri alma `finally` içinde ve döngüden ÇIKARKEN koşuyor — M10'da bir
        # kabuk betiğinin `trap`'i çalıştı, geri aldı, ve döngü DEVAM EDİP bir
        # sonraki kusuru uyguladı.
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
