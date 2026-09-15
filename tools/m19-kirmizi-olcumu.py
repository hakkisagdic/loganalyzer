#!/usr/bin/env python3
"""M19 · §6 kırmızı ölçümü — `HttpTokenExpiryTests` kırmızı yanabiliyor mu.

Desen M10–M18'den: `shutil.copy` (copy2 DEĞİL), `finally` içinde geri yükleme,
sonda ağaçta `KIRMIZI-` grep'i, ölçüt **çıkış kodu**.

Bu turun özel sorusu: bekçi bir YAPILANDIRMA değerini değil DAVRANIŞI ölçüyor
diye iddia ediyor. O yüzden kusurların çoğu üretim yapılandırmasına uygulanıyor
(`AuthenticationSetup`), teste değil — bekçi gerçekten ürünün kararına bağlıysa
orada yanmalı.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent

BEKCI = "HttpTokenExpiryTests"

AYAR = KOK / "src/Bizigo.Api/AuthenticationSetup.cs"
TEST = KOK / "tests/Bizigo.UnitTests/HttpTokenExpiryTests.cs"


@dataclass
class Kusur:
    ad: str
    dosya: Path
    eski: str
    yeni: str
    kirmizi_bekleniyor: bool = True
    not_: str = ""
    yesil_kalmali: list[str] = field(default_factory=list)


KUSURLAR = [
    # ---- ÜRETİM YAPILANDIRMASI: bekçi ürünün kararına bağlı mı ----
    Kusur(
        ad="KIRMIZI-tolerans-sifira-cekildi",
        dosya=AYAR,
        eski="            ClockSkew = TimeSpan.FromSeconds(30),",
        yeni="            ClockSkew = TimeSpan.Zero,  // KIRMIZI-tolerans-sifira-cekildi",
        not_="Tolerans değişirse hem çivi hem KABUL testi yanmalı. Bu, kriterin "
        "asıl sorusu: beş dakika ölçülmüş bir davranış mı, yoksa test kendi "
        "kurduğu bir dünyayı mı ölçüyor.",
    ),
    Kusur(
        ad="KIRMIZI-tolerans-on-dakikaya-cikti",
        dosya=AYAR,
        eski="            ClockSkew = TimeSpan.FromSeconds(30),",
        yeni="            ClockSkew = TimeSpan.FromMinutes(10),  // KIRMIZI-tolerans-on-dakikaya-cikti",
        not_="Ters yön: tolerans BÜYÜRSE 6 dakikalık belirteç kabul edilir ve RED "
        "testi yanmalı. Tek yön ölçmek, toleransı gevşetmeyi görünmez yapardı.",
    ),
    Kusur(
        ad="KIRMIZI-sure-dogrulamasi-kapatildi",
        dosya=AYAR,
        eski="            ValidateLifetime = true,",
        yeni="            ValidateLifetime = false,  // KIRMIZI-sure-dogrulamasi-kapatildi",
        not_="Süre doğrulaması kapanırsa süresi dolmuş HER belirteç kabul edilir. "
        "`ClaimMappingTests` bunu zaten çiviliyordu; burada da yanması çift "
        "kapının bilinçli hâli (§9 değil: aynı şeyi iki AYRI soruyla ölçüyorlar).",
    ),
    Kusur(
        ad="KIRMIZI-ozel-lifetime-validator-kondu",
        dosya=AYAR,
        eski="            ClockSkew = TimeSpan.FromSeconds(30),",
        yeni="            ClockSkew = TimeSpan.FromSeconds(30),\n"
        "            LifetimeValidator = static (_, _, _, _) => true,  // KIRMIZI-ozel-lifetime-validator-kondu",
        not_="Bir validator konursa `ClockSkew` sessizce anlamsızlaşır: çivi hâlâ "
        "300 s görür ama davranış değişir. Bekçinin `LifetimeValidator is null` "
        "iddiası tam bu sessiz hâli yakalamak için var.",
    ),
    Kusur(
        ad="KIRMIZI-hata-detayi-kapatildi",
        dosya=AYAR,
        eski="        jwt.MapInboundClaims = false;",
        yeni="        jwt.MapInboundClaims = false;\n"
        "        jwt.IncludeErrorDetails = false;  // KIRMIZI-hata-detayi-kapatildi",
        not_="Süre sonu istemciye ayırt edilebilir gelmezse yanmalı — M13'ün "
        "stdio'daki 'ayrı cümle' kararının HTTP karşılığı.",
    ),
    # ---- ÖLÇÜM ARACININ KENDİSİ ----
    Kusur(
        ad="KIRMIZI-test-uretim-parametrelerini-kullanmiyor",
        dosya=TEST,
        eski="        var parameters = EffectiveParameters().Clone();",
        yeni="        var parameters = new TokenValidationParameters { ValidateLifetime = true, ValidateIssuer = false, ValidateAudience = false };  // KIRMIZI-test-uretim-parametrelerini-kullanmiyor",
        not_="Test üretimden kopar ve kendi dünyasını ölçmeye başlarsa yanmalı. "
        "İLK KOŞUMDA YANMADI ve sebebi ölçüldü: kütüphanenin varsayılan "
        "toleransı da 300 s'ti, yani kopuk test aynı sonucu üretiyordu. "
        "`DogrulaAsync` içine parmak izi iddiaları (`RoleClaimType`, "
        "`ValidIssuer`, `ClockSkew`) kondu — kopma artık ölçülüyor. "
        "Bu satır §6'nın kendi işini yaptığı yer: yeşil bir ölçüm, ölçümün "
        "yapılmadığı anlamına gelebiliyordu.",
    ),
]


def kos(bekci: str) -> int:
    return subprocess.run(
        ["dotnet", "test", "tests/Bizigo.UnitTests", "--filter", f"FullyQualifiedName~{bekci}"],
        cwd=KOK, capture_output=True, text=True,
    ).returncode


def main() -> int:
    print(f"=== M19 §6 ölçümü · {len(KUSURLAR)} kusur\n")

    taban = kos(BEKCI)
    if taban != 0:
        print(f"DUR: taban zaten kırmızı (çıkış {taban}). Ölçüm anlamsız.")
        return 1
    print("taban: YEŞİL ✓\n")

    basarisiz: list[str] = []

    for kusur in KUSURLAR:
        yedek = kusur.dosya.with_suffix(kusur.dosya.suffix + ".m19yedek")
        shutil.copy(kusur.dosya, yedek)

        try:
            metin = kusur.dosya.read_text(encoding="utf-8")
            if kusur.eski not in metin:
                print(f"  ATLANDI (çapa yok): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: çapa bulunamadı")
                continue

            kusur.dosya.write_text(metin.replace(kusur.eski, kusur.yeni, 1), encoding="utf-8")

            # §6: kusurun DOSYADA olduğunu geri okuyarak doğrula.
            if kusur.ad not in kusur.dosya.read_text(encoding="utf-8"):
                print(f"  ATLANDI (kusur yazılamadı): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: kusur dosyada değil")
                continue

            cikis = kos(BEKCI)
            kirmizi = cikis != 0

            if kirmizi == kusur.kirmizi_bekleniyor:
                print(f"  {'KIRMIZI ✓' if kirmizi else 'YEŞİL ✓ (beklendiği gibi)'}  {kusur.ad}")
            else:
                beklenen = "kırmızı" if kusur.kirmizi_bekleniyor else "yeşil"
                print(f"  BAŞARISIZ  {kusur.ad} — {beklenen} beklendi, çıkış {cikis}")
                basarisiz.append(kusur.ad)

            if kusur.not_:
                print(f"      {kusur.not_}")
        finally:
            shutil.copy(yedek, kusur.dosya)
            yedek.unlink()

    son = kos(BEKCI)
    print(f"\ngeri yükleme sonrası: {'YEŞİL ✓' if son == 0 else f'KIRMIZI ✗ (çıkış {son})'}")

    kalinti = subprocess.run(
        ["grep", "-rn", "KIRMIZI-", "src/", "tests/"], cwd=KOK, capture_output=True, text=True,
    ).stdout.strip()
    print(f"ağaçta KIRMIZI- kalıntısı: {'YOK ✓' if not kalinti else 'VAR ✗'}")
    if kalinti:
        print(kalinti[:600])

    if basarisiz:
        print(f"\n{len(basarisiz)} kalem başarısız:")
        for ad in basarisiz:
            print(f"  - {ad}")
        return 1

    print(f"\nTÜMÜ GEÇTİ · {len(KUSURLAR)}/{len(KUSURLAR)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
