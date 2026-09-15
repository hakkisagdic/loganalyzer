"""M15 — kota rezerv ekseni bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam `m14-kirmizi-olcumu.py`'den devralındı.

M15'in özel durumu şu: **bekçi vardı ve YANLIŞ EKSENDE yeşildi.** Kusur dört
yerde birden yazılıydı ve birbirini doğruluyordu —

  1. uygulama            `EffectiveLimit`: `source != Schedule`
  2. `EffectiveLimit`'in belgesi  `Alert`, `User`, `Api` (son ikisi enum'da yok)
  3. `RcaQuotaTests`     `Rezervasyon_yalnizca_takvim_kaynagini_daraltiyor`
  4. `McpIdempotencyTests` `Ajan_kota_rezervinden_etkilenmiyor`

Dördü tutarlı olduğu için hiçbiri kırmızı yanmadı. Dolayısıyla bu turun
kusurları iki soruyu ölçüyor:

  * Yeni bekçiler eski eksene DÖNÜŞÜ görüyor mu (daralma geri gelirse kırmızı).
  * Kapı ile saf fonksiyon AYRI ölçülüyor mu — saf fonksiyon doğru olup kapının
    onu hiç çağırmadığı hâl mümkün, ve o hâlde rezerv açılmasına rağmen hiçbir
    şey değişmez.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m15-olcum-yedek"

KOTA = "src/Bizigo.Rca/RcaQuota.cs"


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)
    not_: str = ""


EKSEN = "Rezervasyon_istek_tetikli_her_kaynagi_daraltiyor"
KAPI = "Rezerv_kapidan_geciyor_ajani_daraltip_alarmi_koruyor"
AJAN = "Ajan_kota_rezervinden_etkileniyor"


KUSURLAR = [
    Kusur(
        # BUGÜNE KADARKİ HÂLİN TA KENDİSİ: eksen `Schedule`'a daralıyor.
        ad="eksen eski hâline dönüyor (`source != Schedule`)",
        dosya=KOTA,
        bul="        if (dailyLimit <= 0 || reservePercent <= 0 || source is RcaTriggerSource.Alert)",
        koy="        if (dailyLimit <= 0 || reservePercent <= 0 || source != RcaTriggerSource.Schedule)"
            "   // KIRMIZI-A",
        kirmizi_bekleniyor=[EKSEN, KAPI, AJAN],
        not_="dört bekçinin dördü birden yanıyor — daralma artık tek yerden gizlenemiyor",
    ),
    Kusur(
        # Ters yön: rezerv HERKESE uygulanıyor, yani olay tetikli korunmuyor.
        ad="rezerv olay tetikliye de uygulanıyor (koruma anlamsızlaşıyor)",
        dosya=KOTA,
        bul="        if (dailyLimit <= 0 || reservePercent <= 0 || source is RcaTriggerSource.Alert)",
        koy="        if (dailyLimit <= 0 || reservePercent <= 0)   // KIRMIZI-B",
        kirmizi_bekleniyor=[EKSEN, KAPI, AJAN],
        not_="kapı bekçisi de yanıyor: alarm artık daraltıldığı için 'korunan' yönü düşüyor",
    ),
    Kusur(
        # Kapı saf fonksiyonu çağırmıyor: rezerv açık ama hiçbir şey değişmiyor.
        ad="kapı `EffectiveLimit`'i hiç çağırmıyor",
        dosya=KOTA,
        bul="        var limit = EffectiveLimit(options.DailyPerGroup, options.EventReservePercent, request.Source);",
        koy="        var limit = options.DailyPerGroup;   // KIRMIZI-C",
        kirmizi_bekleniyor=[KAPI],
        yesil_kalmali=[EKSEN, AJAN],
        not_="saf fonksiyon DOĞRU kalırken kapı onu yok sayıyor — iki bekçinin ayrı olma sebebi",
    ),
    Kusur(
        # Rezerv yüzdesi hesabı sessizce sıfırlanıyor.
        ad="rezerv hesabı sıfıra iniyor",
        dosya=KOTA,
        bul="        var reserved = (int)Math.Ceiling(dailyLimit * (reservePercent / 100.0));",
        koy="        var reserved = 0;   // KIRMIZI-D",
        # TAHMİN DÜZELTİLDİ: ilk hâlde koruma bekçisinin yeşil kalacağını
        # yazmıştım ve ölçüm çürüttü — rezerv sıfırlanınca ajan da geçiyor, yani
        # "daraltılan" yönü düşüyor. Rezerve DUYARLI bir bekçinin bu kusurda
        # kırmızı yanması doğru.
        kirmizi_bekleniyor=[EKSEN, KAPI, AJAN],
    ),
    Kusur(
        # `BySource` boş dönüyor: gözlem yarısı tamamen kaybolur.
        ad="kaynak kırılımı boş dönüyor",
        dosya=KOTA,
        bul="            rows.ToDictionary(r => r.Source, r => r.Count));",
        koy="            new Dictionary<RcaTriggerSource, int>());   // KIRMIZI-E",
        kirmizi_bekleniyor=["Kaynak_basina_tuketim_rezervasyon_kapaliyken_de_sayiliyor"],
        yesil_kalmali=[EKSEN, KAPI],
        not_="kırılım hesaplanıyor ama YÜZEYE ÇIKMIYOR (M15 ölçümü); yine de sayılması ölçülüyor",
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
