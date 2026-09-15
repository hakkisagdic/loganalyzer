#!/usr/bin/env python3
"""B02 — varış defteri bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam ve gerekçeleri `tools/m06-kirmizi-olcumu.py` ile aynı (§6): kusuru yaz →
dosyayı OKU ve kusurun orada olduğunu İDDİA ET → koştur → YEDEK DOSYADAN geri al
→ sonunda tam paketi bir kez daha koştur.

Bu ticket'ın kusurlarının ORTAK ŞEKLİ var ve kayda değer: sekizinin altısı
"ölçemedim yerine sıfır dön" biçiminde. Defterin tamamı o tek hataya karşı
kurulu (`tools/README.md` — `machine-resources.sh` Linux'ta bellek için 100,
disk için 0 basıyordu), yani kırmızı ölçümünün de o eksende olması gerekiyor.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".b02-olcum-yedek"

ORTAM = {"DOTNET_ROOT": str(Path.home() / ".dotnet")}


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)


WIRE = "sim/Bizigo.Capacity/WireDropReader.cs"
METRICS = "sim/Bizigo.Capacity/CollectorMetricsReader.cs"
LEDGER = "sim/Bizigo.Capacity/ArrivalLedger.cs"
COMPOSE = "deploy/docker-compose.yml"

KUSURLAR = [
    Kusur(
        # Defterin var olma sebebi. `machine-resources.sh`'in Linux'ta yaptığı
        # şeyin aynısı: okuyucu yoksa uydurma sayı.
        ad="tanınmayan platformda uydurma sıfır dönüyor",
        dosya=WIRE,
        bul="""        return LedgerReading.Limited(
            LedgerLayer.Wire,
            RuntimeInformation.OSDescription,
            "bu platform için düşürme sayacı okuyucusu YOK (hedef: Linux + Windows). " +
            "Uydurma sıfır basmak `tel temiz` diye okunurdu.");""",
        koy="""        return LedgerReading.Measured(
            LedgerLayer.Wire,
            RuntimeInformation.OSDescription,
            0);""",
        kirmizi_bekleniyor=[
            "Desteklenmeyen_platformda_uydurma_sayi_yok",
            "Platform_beyani_okuyucunun_davranisiyla_ortusuyor",
        ],
    ),
    Kusur(
        ad="negatif `drops` sıfıra kırpılıyor",
        dosya=WIRE,
        bul="            if (drops < 0)\n            {",
        koy="            if (false && drops < 0)\n            {",
        kirmizi_bekleniyor=["Negatif_drops_kirpilmiyor_kisit_oluyor"],
    ),
    Kusur(
        ad="port bulunamazsa sıfır dönüyor",
        dosya=WIRE,
        bul="        return matched == 0\n            ? LedgerReading.Limited(",
        koy="        return matched == -1\n            ? LedgerReading.Limited(",
        kirmizi_bekleniyor=["Port_bulunamazsa_sifir_degil_kisit"],
    ),
    Kusur(
        # Yerelleştirilmiş Windows çıktısı: etiket yok, sayı yok, ve sıfır
        # dönmek o makinede "tel temiz" diye okunur.
        ad="`netstat` etiketi bulunamazsa sıfır dönüyor",
        dosya=WIRE,
        bul="        return found\n            ? LedgerReading.Measured(LedgerLayer.Wire, NetstatSummary, total)",
        koy="        return found || total >= 0\n            ? LedgerReading.Measured(LedgerLayer.Wire, NetstatSummary, total)",
        kirmizi_bekleniyor=["Yerellestirilmis_netstat_ciktisi_kisit_uretiyor"],
    ),
    Kusur(
        ad="metrik sergilemede yoksa sıfır dönüyor",
        dosya=METRICS,
        bul="        return found\n            ? LedgerReading.Measured(LedgerLayer.Collector, metric, (long)total)",
        koy="        return found || total >= 0\n            ? LedgerReading.Measured(LedgerLayer.Collector, metric, (long)total)",
        kirmizi_bekleniyor=["Metrik_ucu_erisilemezse_kisit"],
    ),
    Kusur(
        # Ölçüm arızasını ürünün kaybı olarak raporlamak.
        ad="sayaç sıfırlanması sıfıra kırpılıyor",
        dosya=METRICS,
        bul="        return delta < 0\n            ? LedgerReading.Limited(",
        koy="        return false\n            ? LedgerReading.Limited(",
        kirmizi_bekleniyor=["Sayac_sifirlanmasi_kayip_olarak_raporlanmiyor"],
    ),
    Kusur(
        ad="`_total` ekli ad artık tanınmıyor",
        dosya=METRICS,
        bul="            if (rest.StartsWith(TotalSuffix, StringComparison.Ordinal))\n            {",
        koy="            if (false && rest.StartsWith(TotalSuffix, StringComparison.Ordinal))\n            {",
        kirmizi_bekleniyor=["Total_ekli_ad_da_okunuyor"],
    ),
    Kusur(
        # UYGULAMA TURUNDA GERÇEKTEN OLDU: defterin ilk hâli düşürme sayacını
        # koşulsuz suçluyordu ve ilk koşumda yakalandı.
        ad="düşürme sayacı koşulsuz suçlanıyor (`unseen` sınırı kaldırıldı)",
        dosya=LEDGER,
        bul="        if (drops > 0 && unseen > 0)",
        koy="        if (drops > 0)",
        kirmizi_bekleniyor=["Boslugu_aciklamayan_dusurme_tele_yazilmiyor"],
    ),
    Kusur(
        # LEDGER-LIMITED'ın kendisi: boşluk varken okunamayan sayaç.
        ad="okunamayan sayaçla boşluk yine de yerleştiriliyor",
        dosya=LEDGER,
        bul="        if (missingCounters.Length > 0)\n        {",
        koy="        if (false && missingCounters.Length > 0)\n        {",
        kirmizi_bekleniyor=[
            "Sayac_okunamazsa_kayip_degil_LEDGER_LIMITED",
            "Desteklenmeyen_platformda_uydurma_sayi_yok",
        ],
        # Boşluk YOKKEN hüküm değişmiyor: iki hâl ayrı ve ayrı test ediliyor.
        yesil_kalmali=["Bosluk_yokken_okunamayan_sayac_uyari_olarak_yaziliyor"],
    ),
    Kusur(
        # Yapılandırma bağı: iki parçadan biri eksikse uç SESSİZCE çalışmıyor.
        ad="compose 8888'i yayınlamayı bırakıyor",
        dosya=COMPOSE,
        bul='      - "${OTEL_METRICS_PORT:-8888}:8888"',
        koy="",
        kirmizi_bekleniyor=["Collector_metrik_ucu_iki_parcali_ve_ikisi_de_yerinde"],
    ),
]


def kos(argv: list[str]) -> subprocess.CompletedProcess:
    ortam = dict(os.environ)
    ortam.update(ORTAM)
    ortam["PATH"] = f"{Path.home() / '.dotnet'}:{ortam['PATH']}"

    return subprocess.run(argv, cwd=KOK, env=ortam, capture_output=True, text=True)


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
            f"[{kusur.ad}] ÇAPA BULUNAMADI: {kusur.dosya}. Ölçüm koşmadan durduruluyor — "
            "sessizce yeşil raporlamak §6'nın yasakladığı şey."
        )

    yol.write_text(metin.replace(kusur.bul, kusur.koy, 1), encoding="utf-8")
    yol.touch()


def iddia_et(kusur: Kusur) -> None:
    """§6'nın İDDİA ADIMI: kusur dosyada gerçekten var mı."""
    metin = (KOK / kusur.dosya).read_text(encoding="utf-8")

    if kusur.koy and kusur.koy not in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: kusur dosyada yok, ölçüm yalancı olurdu.")

    degistirme = kusur.bul not in kusur.koy

    if degistirme and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: eski metin hâlâ orada.")

    if not kusur.koy and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: silinmesi gereken metin hâlâ orada.")

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI")


def derle() -> tuple[bool, str]:
    sonuc = kos(["dotnet", "build", "--nologo"])
    return sonuc.returncode == 0, sonuc.stdout + sonuc.stderr


def test_kos(filtre: str) -> int:
    """Filtreli koşum. Ölçüt ÇIKIŞ KODU: sayıları metinden ayıklamak yerel dile
    bağlı olurdu ve ayıklama düştüğünde sessizce sıfır üretirdi."""
    return kos(["dotnet", "test", "tests/Bizigo.UnitTests", "--nologo", "--filter", filtre]).returncode


def main() -> int:
    # Süzgeç: `python3 tools/b02-kirmizi-olcumu.py netstat metrik` yalnızca adı
    # eşleşen kusurları ölçüyor. Kusur ENJEKSİYONU düzeltildiğinde bütün turu
    # yeniden koşturmak gerekmesin diye var; son adım (tam paket) süzgeçten
    # ETKİLENMİYOR.
    global KUSURLAR

    if len(sys.argv) > 1:
        KUSURLAR = [k for k in KUSURLAR if any(a in k.ad for a in sys.argv[1:])]

        if not KUSURLAR:
            raise SystemExit(f"Süzgeç hiçbir kusurla eşleşmedi: {sys.argv[1:]}")

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
                hata = next((s.strip() for s in cikti.splitlines() if ": error " in s), "(okunamadı)")
                rapor.append((kusur.ad, f"derleme kırıldı — TEST kırmızısı ölçülüyordu: {hata[:100]}"))
                print(f"    derleme kırıldı (beklenmiyordu): {hata[:100]}\n")
                geri_al({kusur.dosya})
                continue

            for test in kusur.kirmizi_bekleniyor:
                durum = "KIRMIZI ✓" if test_kos(f"FullyQualifiedName~{test}") != 0 else "YEŞİL KALDI ✗"
                rapor.append((f"{kusur.ad} → {test}", durum))
                print(f"    {test}: {durum}")

            for test in kusur.yesil_kalmali:
                durum = (
                    "yeşil kaldı ✓ (bekçiler ayrı şey ölçüyor)"
                    if test_kos(f"FullyQualifiedName~{test}") == 0
                    else "KIRILDI ✗"
                )
                rapor.append((f"{kusur.ad} → {test} [yeşil kalmalı]", durum))
                print(f"    {test}: {durum}")

            print()
            geri_al({kusur.dosya})
    finally:
        geri_al(dosyalar)
        shutil.rmtree(YEDEK, ignore_errors=True)

    print("\n=== geri alındı; TAM PAKET yeniden koşuyor ===")
    print("(bu adım isteğe bağlı değil: T44'te kusuru yakalayan şey bir bekçi değil bu koşumdu)\n")

    sonuc = kos(["dotnet", "test", "tests/Bizigo.UnitTests", "--nologo"])
    cikti = sonuc.stdout + sonuc.stderr
    print(next((s for s in cikti.splitlines() if "Başarısız:" in s or "Failed:" in s), "(özet okunamadı)"))

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    if sonuc.returncode != 0:
        print("\nGERİ ALMA DERLEMEYE ULAŞMADI ya da paket kırmızı. Düşen test(ler):\n")

        for satir in cikti.splitlines():
            if "[FAIL]" in satir or satir.strip().startswith(("Başarısız ", "Failed ")):
                print(f"  {satir.strip()}")

        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
