#!/usr/bin/env python3
"""Bekçilerin KIRMIZI YANABİLDİĞİNİ ölçen ortak koşum yordamı.

Geçen bir test geçtiğini kanıtlamaz; kırılabildiğini göstermek kanıtlar
(CLAUDE.md §6). Bu modül her kusur için dört adım koşuyor:

  1. kusuru yaz
  2. dosyayı OKU ve kusurun orada olduğunu İDDİA ET
  3. koştur ve beklenen kırmızıyı gör
  4. YEDEK DOSYADAN geri al

İkinci adım olmazsa yeşil bir sonuç iki şey anlatıyor — "kusur etkisiz" ya da
"kusur hiç uygulanmadı" — ve birincisi varsayılıyor.

Geri alma `git checkout` ile DEĞİL, yedek dosyadan yapılıyor: `git checkout`
çalışma ağacının tamamına bakan bir araç ve bu depoda bir kez commit edilmemiş
işin üstüne yazdı (FS-b, S06).

Zaman damgası TAŞINMIYOR (`copy2` değil, `copy` + `touch`): geri yüklenen
kaynak derlenmiş ikiliden eski görünürse MSBuild projeyi atlıyor, `dotnet
build` "0 hata" diyor ve koşan ikili hâlâ kusurlu oluyor (T44'te ölçüldü).

Son adım bu yüzden bir dosya kopyasıyla bitmiyor: geri aldıktan sonra TAM
PAKET bir kez daha koşuyor.

BU DOSYA M11'DE DOĞDU ve yeni bir mekanizma DEĞİL: yordamın tamamı
`m06-kirmizi-olcumu.py` içinde yazılmıştı ve M11'in ölçümü onun ikinci
kopyasını gerektiriyordu (§9: ikinci kopya yazma). Ticket'a ait olan tek şey
KUSUR LİSTESİ; koşum yordamı ortak. Böylece yordamın bir gün düzeltilen hatası
bütün ölçümlerde düzeliyor — ilk hâlinde iki kez düzeltilmesi gerekirdi.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent

ORTAM = {"DOTNET_ROOT": str(Path.home() / ".dotnet")}


@dataclass
class Kusur:
    """Bir kusur ve ondan beklenen kırmızı."""

    ad: str
    dosya: str
    bul: str
    koy: str
    #: Beklenen kırmızının türü: derleme hatası mı, test mi.
    derleme_kirilmali: bool = False
    #: Kırmızı yanması beklenen testler (isim parçası).
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    #: Kusura RAĞMEN yeşil kalması beklenen testler — iki bekçinin
    #: birbirinin kopyası OLMADIĞINI gösteriyor.
    yesil_kalmali: list[str] = field(default_factory=list)


def kos(argv: list[str], **kwargs) -> subprocess.CompletedProcess:
    ortam = dict(os.environ)
    ortam.update(ORTAM)
    ortam["PATH"] = f"{Path.home() / '.dotnet'}:{ortam['PATH']}"

    return subprocess.run(argv, cwd=KOK, env=ortam, capture_output=True, text=True, **kwargs)


def yedekle(yollar: set[str], yedek: Path) -> None:
    yedek.mkdir(exist_ok=True)

    for yol in yollar:
        hedef = yedek / yol.replace("/", "__")
        # `copy2` DEĞİL: zaman damgasını taşımak geri almayı derlemeye
        # ulaştırmıyor (T44'te ölçüldü).
        shutil.copy(KOK / yol, hedef)


def geri_al(yollar: set[str], yedek: Path) -> None:
    for yol in yollar:
        kaynak = yedek / yol.replace("/", "__")
        shutil.copy(kaynak, KOK / yol)
        (KOK / yol).touch()


def uygula(kusur: Kusur) -> None:
    yol = KOK / kusur.dosya
    metin = yol.read_text(encoding="utf-8")

    if kusur.bul not in metin:
        raise SystemExit(
            f"[{kusur.ad}] ÇAPA BULUNAMADI: {kusur.dosya}. Kusur uygulanamadı — "
            "ölçüm koşmadan durduruluyor. (Sessizce yeşil raporlamak §6'nın yasakladığı şey.)"
        )

    yol.write_text(metin.replace(kusur.bul, kusur.koy, 1), encoding="utf-8")
    yol.touch()


def iddia_et(kusur: Kusur) -> None:
    """§6'nın İDDİA ADIMI: kusur dosyada gerçekten var mı."""
    metin = (KOK / kusur.dosya).read_text(encoding="utf-8")

    if kusur.koy and kusur.koy not in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: kusur dosyada yok, ölçüm yalancı olurdu.")

    # "Eski metin gitti mi" sorusu YALNIZCA değiştirme tipi kusurlarda geçerli.
    # Ekleme tipi kusurlarda (`koy`, `bul`u içeriyor) çapa yerinde KALIYOR ve
    # kalması gerekiyor — bu betiğin ilk hâli tam olarak burada düştü, ve
    # düşmesi iddia adımının işini yaptığını gösterdi.
    degistirme = kusur.bul not in kusur.koy

    if degistirme and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: eski metin hâlâ orada.")

    if not kusur.koy and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: silinmesi gereken metin hâlâ orada.")

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI")


def derle() -> tuple[bool, str]:
    sonuc = kos(["dotnet", "build", "--nologo"])
    return sonuc.returncode == 0, sonuc.stdout + sonuc.stderr


def test_kos(filtre: str) -> tuple[int, str]:
    """Filtreli koşum. Ölçüt ÇIKIŞ KODU: 0 yeşil, değilse kırmızı.

    Sayıları çıktı metninden ayıklamak yerel dile bağlı olurdu (bu makinede
    `Başarısız:`, CI'da `Failed:`) ve ayıklama düştüğünde sessizce sıfır
    üretirdi — yani ölçüm aracının kendisi §7'nin sınıfına girerdi.
    """
    sonuc = kos([
        "dotnet", "test", "tests/Bizigo.UnitTests", "--nologo",
        "--filter", filtre,
    ])

    return sonuc.returncode, sonuc.stdout + sonuc.stderr


def olc(kusurlar: list[Kusur], yedek_adi: str) -> int:
    """Kusur listesini ölçer. Çağıran YALNIZCA listeyi ve yedek dizinini veriyor."""
    yedek = KOK / yedek_adi
    dosyalar = {k.dosya for k in kusurlar}
    yedekle(dosyalar, yedek)

    print(f"yedek: {yedek}")
    print(f"{len(kusurlar)} kusur ölçülecek\n")

    rapor: list[tuple[str, str]] = []

    try:
        for kusur in kusurlar:
            print(f"[{kusur.ad}]")
            uygula(kusur)
            iddia_et(kusur)

            if kusur.derleme_kirilmali:
                basarili, cikti = derle()

                if basarili:
                    rapor.append((kusur.ad, "BEKLENEN KIRMIZI GELMEDİ (derleme geçti)"))
                    print("    DERLEME GEÇTİ — beklenen kırmızı gelmedi\n")
                else:
                    hata = next(
                        (s.strip() for s in cikti.splitlines() if ": error " in s),
                        "(hata satırı okunamadı)",
                    )
                    rapor.append((kusur.ad, f"derleme KIRILDI: {hata[:120]}"))
                    print(f"    derleme KIRILDI ✓  {hata[:100]}\n")
            else:
                # Derleme geçmeli, testler kırmızı yanmalı.
                basarili, cikti = derle()

                if not basarili:
                    rapor.append((kusur.ad, "derleme kırıldı — bu kusur TEST kırmızısı ölçüyordu"))
                    print("    derleme kırıldı (beklenmiyordu)\n")
                    geri_al({kusur.dosya}, yedek)
                    continue

                for test in kusur.kirmizi_bekleniyor:
                    kod, _ = test_kos(f"FullyQualifiedName~{test}")
                    durum = "KIRMIZI ✓" if kod != 0 else "YEŞİL KALDI ✗"
                    rapor.append((f"{kusur.ad} → {test}", durum))
                    print(f"    {test}: {durum}")

                for test in kusur.yesil_kalmali:
                    kod, _ = test_kos(f"FullyQualifiedName~{test}")
                    durum = "yeşil kaldı ✓ (bekçiler ayrı şey ölçüyor)" if kod == 0 else "KIRILDI ✗"
                    rapor.append((f"{kusur.ad} → {test} [yeşil kalmalı]", durum))
                    print(f"    {test}: {durum}")

                print()

            geri_al({kusur.dosya}, yedek)
    finally:
        geri_al(dosyalar, yedek)
        # Yedek dizini koşum sonunda siliniyor: bırakılan bir yedek
        # `git status`'ta izlenmeyen bir dizin olarak durur ve bir sonraki
        # `git add .` onu depoya sokar.
        shutil.rmtree(yedek, ignore_errors=True)

    print("\n=== geri alındı; TAM PAKET yeniden koşuyor ===")
    print("(bu adım isteğe bağlı değil: T44'te kusuru yakalayan şey bir bekçi değil bu koşumdu)\n")

    kod, cikti = test_kos("FullyQualifiedName!~SidecarLive")
    son = next((s for s in cikti.splitlines() if "Başarısız:" in s or "Failed:" in s), "(özet okunamadı)")
    print(son)

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    if kod != 0:
        # BU BLOK BİR ÖLÇÜM BULGUSUNDAN DOĞDU. İlk hâli yalnızca özet satırını
        # basıyordu ve son koşum bir kez kırmızı geldi: "1 başarısız" yazıyordu,
        # HANGİSİ yazmıyordu. Yani araç, kendi yakaladığı kusuru okunamaz hâlde
        # raporluyordu — okuyanı bütün pakete geri gönderiyor.
        #
        # Bu, aracın §6'ya karşı işlediği ikinci suç olurdu: birincisi "yeşil
        # bir sonuç ölçümün yapılmadığı anlamına gelebiliyor", ikincisi
        # "kırmızı bir sonuç neyin kırmızı olduğunu söylemiyor".
        print("\nGERİ ALMA DERLEMEYE ULAŞMADI ya da paket kırmızı. Düşen test(ler):\n")

        for satir in cikti.splitlines():
            if "[FAIL]" in satir or satir.strip().startswith(("Başarısız ", "Failed ")):
                print(f"  {satir.strip()}")

        print(
            "\nUYARI: bu koşum makine YÜKLÜYKEN yapıldıysa sebep kusur olmayabilir "
            "(CLAUDE.md §6, duvar saatine bağlı testler). Sonucu 'kararsız test' diye "
            "GEÇİŞTİRME — `machine-resources.sh check` yeşilken tekrar koştur ve gördüğünü yaz."
        )

        return 1

    return 0
