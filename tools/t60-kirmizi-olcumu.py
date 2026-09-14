#!/usr/bin/env python3
"""T60 — entropi terfisi ölçümünün bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam ve gerekçeleri `tools/m06-kirmizi-olcumu.py` ile aynı (§6): kusuru yaz →
dosyayı OKU ve kusurun orada olduğunu İDDİA ET → koştur → YEDEK DOSYADAN geri al
→ sonunda tam paketi bir kez daha koştur.

Bu ticket'a özel bir tuzak var ve iki kusur onu ölçüyor: T60'ın bekçilerinin
çoğu bir SAYIYA çivili, ve çivili bir sayı BOŞ KÜME üzerinde de tutabilir.
6 numaralı kusur (korpus okuyucusu boşalıyor) tam bunu sınıyor — o kusurla yeşil
kalan bir bekçi, hiçbir şey ölçmüyor demektir.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".t60-olcum-yedek"

ORTAM = {"DOTNET_ROOT": str(Path.home() / ".dotnet")}


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    derleme_kirilmali: bool = False
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    #: Kusura RAĞMEN yeşil kalması beklenen testler — iki bekçinin birbirinin
    #: kopyası OLMADIĞINI gösteriyor.
    yesil_kalmali: list[str] = field(default_factory=list)


REDACTED = "src/Bizigo.Contracts/Security/RedactedPrompt.cs"
CLASSIFIER = "src/Bizigo.Contracts/Security/ShadowTokenClassifier.cs"
FIXTURES = "tests/Bizigo.UnitTests/RedactionFixtures.cs"

KUSURLAR = [
    Kusur(
        # T41'in en önemli değişmezi: A SAYIYOR, maskelemiyor. T60 kararı
        # "kalıcı ölçüm" yaptığı için bu değişmez artık kalıcı bir iddia.
        ad="katman A sessizce maskelemeye başlıyor",
        dosya=REDACTED,
        bul="        var masked = SecretRedactor.RedactExact(normalized, discovered);\n",
        koy="        var kirmiziOnce = SecretRedactor.RedactExact(normalized, discovered);\n"
        "        foreach (var kirmiziToken in ShadowTokenize(kirmiziOnce, minShadowTokenLength).Evaluated)\n"
        "        {\n"
        "            if (kirmiziToken.Entropy >= entropyThreshold) { discovered.Add(kirmiziToken.Text); }\n"
        "        }\n"
        "        var masked = SecretRedactor.RedactExact(normalized, discovered);\n",
        kirmizi_bekleniyor=[
            "Golge_katman_ciktiyi_degistirmiyor",
            "Golge_sayac_kalici_olcum_olarak_yayiliyor",
            "Altin_korpusta_yanlis_pozitif_yok",
        ],
    ),
    Kusur(
        # Kararın kendisi. Bir yorum olsaydı bu kusur diye bir şey OLMAZDI.
        ad="karar geri alınıyor: sayaç yine terfi bekliyor",
        dosya=REDACTED,
        bul="ShadowLayerPurpose.PermanentInstrument;",
        koy="ShadowLayerPurpose.PromotionCandidate;",
        kirmizi_bekleniyor=["Golge_sayac_kalici_olcum_olarak_yayiliyor"],
        # Maskeleme davranışı değişmediği için T41'in bekçisi yeşil kalmalı:
        # iki bekçi iki AYRI şey ölçüyor (davranış ↔ karar).
        yesil_kalmali=["Golge_katman_ciktiyi_degistirmiyor"],
    ),
    Kusur(
        ad="sınıf dağılımı kanıt paketine yayılmıyor",
        dosya=REDACTED,
        bul="        foreach (var (sinif, sayi) in ShadowClasses)\n"
        "        {\n"
        "            fields[\"redaction_shadow_class_\" + sinif.ToString().ToLowerInvariant()] = sayi;\n"
        "        }\n",
        koy="",
        kirmizi_bekleniyor=["Golge_sayac_kalici_olcum_olarak_yayiliyor"],
    ),
    Kusur(
        # Kararın belkemiği: onaltılık sınıfın AYRI adı olması. Yutulursa bir
        # sha256 ile bir WPA PSK'nın ayırt edilemezliği görünmez oluyor.
        ad="onaltılık sınıfı `Opaque` içine yutuluyor",
        dosya=CLASSIFIER,
        bul="        if (Hexadecimal(segments))\n        {\n            return ShadowTokenClass.Hexadecimal;\n        }\n",
        koy="",
        kirmizi_bekleniyor=[
            "Sinif_gecidi_gercek_bir_sir_bicimini_muaf_tutardi",
            "Altin_korpusun_golge_adaylari_siniflara_ayriliyor",
            "Siniflandirici_ay_adini_onaltilik_sanabiliyor",
        ],
    ),
    Kusur(
        ad="karakter sınıfı koşulu kaldırılıyor (uzunluk tek ölçüt)",
        dosya=CLASSIFIER,
        bul="segment.Length >= opaqueRunLength && CharacterClasses(segment) >= 2",
        koy="segment.Length >= opaqueRunLength",
        kirmizi_bekleniyor=["Altin_korpusun_golge_adaylari_siniflara_ayriliyor"],
    ),
    Kusur(
        ad="minimum belirteç uzunluğu 20 → 8 (payda kayıyor)",
        dosya=REDACTED,
        bul="    public const int DefaultMinShadowTokenLength = 20;",
        koy="    public const int DefaultMinShadowTokenLength = 8;",
        kirmizi_bekleniyor=["Altin_korpusun_golge_adaylari_siniflara_ayriliyor"],
    ),
    Kusur(
        # §7 — bir bekçinin BOŞ KÜME üzerinde dönmesi. Çivili sayılar boş küme
        # üzerinde de "tutabiliyor" gibi görünür; tutmadığı ölçülmeli.
        ad="ölçüm korpusu sessizce boşalıyor",
        dosya=FIXTURES,
        bul='.Where(l => l.Length > 0 && !l.StartsWith(\'#\')),',
        koy='.Where(l => l.Length > 0 && !l.StartsWith(\'#\') && l.Contains("KIRMIZI-YOK", StringComparison.Ordinal)),',
        kirmizi_bekleniyor=[
            "Altin_korpusun_golge_adaylari_siniflara_ayriliyor",
            "Altin_korpusta_yanlis_pozitif_yok",
            "Sinif_ekseninde_temiz_bir_ayrim_noktasi_yok",
        ],
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

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI")


def derle() -> tuple[bool, str]:
    sonuc = kos(["dotnet", "build", "--nologo"])
    return sonuc.returncode == 0, sonuc.stdout + sonuc.stderr


def test_kos(filtre: str) -> tuple[int, str]:
    """Filtreli koşum. Ölçüt ÇIKIŞ KODU: 0 yeşil, değilse kırmızı.

    Sayıları çıktı metninden ayıklamak yerel dile bağlı olurdu ve ayıklama
    düştüğünde sessizce sıfır üretirdi — ölçüm aracının kendisi §7'nin sınıfına
    girerdi.
    """
    sonuc = kos(["dotnet", "test", "tests/Bizigo.UnitTests", "--nologo", "--filter", filtre])
    return sonuc.returncode, sonuc.stdout + sonuc.stderr


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

            if kusur.derleme_kirilmali:
                if basarili:
                    rapor.append((kusur.ad, "BEKLENEN KIRMIZI GELMEDİ (derleme geçti)"))
                    print("    DERLEME GEÇTİ — beklenen kırmızı gelmedi\n")
                else:
                    hata = next((s.strip() for s in cikti.splitlines() if ": error " in s), "(hata satırı okunamadı)")
                    rapor.append((kusur.ad, f"derleme KIRILDI: {hata[:120]}"))
                    print(f"    derleme KIRILDI ✓  {hata[:100]}\n")

                geri_al({kusur.dosya})
                continue

            if not basarili:
                hata = next((s.strip() for s in cikti.splitlines() if ": error " in s), "(hata satırı okunamadı)")
                rapor.append((kusur.ad, f"derleme kırıldı — TEST kırmızısı ölçülüyordu: {hata[:100]}"))
                print(f"    derleme kırıldı (beklenmiyordu): {hata[:100]}\n")
                geri_al({kusur.dosya})
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
            geri_al({kusur.dosya})
    finally:
        geri_al(dosyalar)
        shutil.rmtree(YEDEK, ignore_errors=True)

    print("\n=== geri alındı; TAM PAKET yeniden koşuyor ===")
    print("(bu adım isteğe bağlı değil: T44'te kusuru yakalayan şey bir bekçi değil bu koşumdu)\n")

    kod, cikti = test_kos("FullyQualifiedName!~SidecarLive")
    son = next((s for s in cikti.splitlines() if "Başarısız:" in s or "Failed:" in s), "(özet okunamadı)")
    print(son)

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    if kod != 0:
        print("\nGERİ ALMA DERLEMEYE ULAŞMADI ya da paket kırmızı. Düşen test(ler):\n")

        for satir in cikti.splitlines():
            if "[FAIL]" in satir or satir.strip().startswith(("Başarısız ", "Failed ")):
                print(f"  {satir.strip()}")

        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
