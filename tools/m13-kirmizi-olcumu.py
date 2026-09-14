"""M13 — stdio kimliği bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam `m12-kirmizi-olcumu.py`'den devralındı; iki katmanlı geri alma dersi
(`öldürülmüş bir ölçüm geri alınmış bir ölçüm değildir` · `çalışmış bir geri
alma durmuş bir koşum değildir`) orada yazılı.

M13'ün kusurları iki eksende duruyor ve `yesil_kalmali` sütunu ikisini
ayırıyor:

  * AYAR ekseni — hangi ayar zorunlu, ve reddin ADI söyleyip söylemediği.
  * DOĞRULAMA ekseni — kitle/issuer gerçekten doğrulanıyor mu, ve süre sonu
    cümlesi kimliğin YOKLUĞUNDAN ayrı mı.

İkisi ayrı testlerde, ve bir eksendeki kusur diğerini düşürmemeli: düşürüyorsa
iki bekçi aynı şeyi ölçüyor demektir ve biri gereksiz (§9).
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m13-olcum-yedek"

IDENTITY = "src/Bizigo.Cli/McpStdioIdentity.cs"
SCOPE = "src/Bizigo.Mcp/McpCallerScope.cs"


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)
    not_: str = ""


AD_AYRI = "Belirtec_degiskeni_test_istemcisinin_degiskeninden_ayri"
EKSIK = "Belirtec_varsa_eksik_ayar_adiyla_reddediliyor"
RET_YOK = "Ayarlar_tamsa_ret_yok"
BELIRTECSIZ = "Belirtec_yoksa_ayar_zorunlu_degil"
KITLE = "Kitle_ve_issuer_dogrulaniyor"
SURE = "Sure_sonu_cumlesi_kimlik_yokluğundan_ayri"
SURE_YOK = "Suresi_dolmamis_belirtec_icin_sebep_yok"


KUSURLAR = [
    Kusur(
        # M13'ün ikinci şartının tam tersi: kitle doğrulaması kapatılıyor.
        # API için basılmış bir belirteç bu yüzeyde de geçer olurdu (RFC 8707).
        ad="kitle doğrulaması kapatılıyor",
        dosya=IDENTITY,
        bul="            ValidateAudience = true,",
        koy="            ValidateAudience = false,   // KIRMIZI-A",
        kirmizi_bekleniyor=[KITLE],
        yesil_kalmali=[EKSIK, SURE],
        not_="ayar ekseni etkilenmiyor — iki bekçi ayrı şey ölçüyor",
    ),
    Kusur(
        # Daha sinsi hâl: doğrulama AÇIK ama beklenen kitle API'nin kitlesi.
        # `ValidateAudience` bayrağına bakan bir bekçi bunu GÖRMEZDİ.
        ad="beklenen kitle API'nin kitlesine çevriliyor",
        dosya=IDENTITY,
        bul="            ValidAudience = settings.Resource,",
        koy='            ValidAudience = "bizigo-api",   // KIRMIZI-B',
        kirmizi_bekleniyor=[KITLE],
        yesil_kalmali=[EKSIK, SURE, RET_YOK],
        not_="bayrak açık, DEĞER yanlış — bekçinin değeri de sınamasının gerekçesi",
    ),
    Kusur(
        ad="issuer doğrulaması kapatılıyor",
        dosya=IDENTITY,
        bul="            ValidateIssuer = true,",
        koy="            ValidateIssuer = false,   // KIRMIZI-C",
        kirmizi_bekleniyor=[KITLE],
        yesil_kalmali=[SURE],
        not_="ağ içinden erişilebilen herhangi bir IdP'ye güvenmek",
    ),
    Kusur(
        # KUSUR ŞEKLİ ÖLÇÜLEREK DÜZELTİLDİ: ilk hâl ternary'nin yalnızca ilk
        # satırını değiştiriyordu ve parantezler dengesiz kalıp DERLEMEYİ
        # kırıyordu — ölçülen şey bekçi değil benim kusurumun sözdizimi olurdu.
        # Aynı ders M10'un ilk ve M12'nin dördüncü kusurunda da çıktı.
        ad="süre sonu cümlesi kimlik yokluğuyla AYNI yapılıyor",
        dosya=IDENTITY,
        bul="    public static string? ExpiryReason(DateTimeOffset expiresAt, DateTimeOffset now) =>",
        koy="    public static string? ExpiryReason(DateTimeOffset expiresAt, DateTimeOffset now) =>\n"
            "        IsExpiredAt(expiresAt, now) ? Bizigo.Mcp.McpCallerScope.NoIdentityMessage : null;"
            "   // KIRMIZI-D\n\n"
            "    public static string? OriginalExpiryReason(DateTimeOffset expiresAt, DateTimeOffset now) =>",
        kirmizi_bekleniyor=[SURE],
        yesil_kalmali=[KITLE, EKSIK, SURE_YOK],
        not_="koordinatörün sorduğu şey: ikisi aynı cümleye düşerse okuyan yanlış yere bakar",
    ),
    Kusur(
        ad="süre sonu hiç dolmuyor (sınırsız kimlik)",
        dosya=IDENTITY,
        bul="    public static bool IsExpiredAt(DateTimeOffset expiresAt, DateTimeOffset now) => now >= expiresAt;",
        koy="    public static bool IsExpiredAt(DateTimeOffset expiresAt, DateTimeOffset now) => false;   // KIRMIZI-E",
        # `SURE_YOK` de kırmızı yanıyor ve YANMASI DOĞRU — ilk hâlde onu
        # `yesil_kalmali`ya yazmıştım ve ölçüm tahminimi çürüttü: o test sınırı
        # da sınıyor (`IsExpiredAt(now, now)` TRUE olmalı). İki bekçinin bu
        # kusurda örtüşmesi bir kopya değil, sınırın iki yönden tutulması.
        kirmizi_bekleniyor=[SURE, SURE_YOK],
        yesil_kalmali=[KITLE],
        not_="`exp` aşılırsa stdio kimliği hiç sona ermez; tahminim yanlıştı, ölçüm düzeltti",
    ),
    Kusur(
        ad="belirteç yokken de ayar zorunlu kılınıyor",
        dosya=IDENTITY,
        bul="        if (!HasToken)\n        {\n            return null;\n        }",
        koy="        // KIRMIZI-F: belirteçsiz koşum da reddediliyor",
        kirmizi_bekleniyor=[BELIRTECSIZ],
        yesil_kalmali=[EKSIK, KITLE],
        not_="M13'ün üçüncü şartı: belirteç yoksa bugünkü davranış aynen kalıyor",
    ),
    Kusur(
        ad="eksik ayar reddi adı söylemiyor",
        dosya=IDENTITY,
        bul='        return $"`{TokenVariable}` verildi ama yanındaki zorunlu ayar eksik: "',
        koy='        return "kimlik yapılandırması eksik";   // KIRMIZI-G\n#pragma warning disable CS0162\n'
            '        return $"`{TokenVariable}` verildi ama yanındaki zorunlu ayar eksik: "',
        kirmizi_bekleniyor=[EKSIK],
        yesil_kalmali=[RET_YOK, KITLE],
        not_="ölçüt 'reddetti mi' DEĞİL 'hangi ayarı söyledi mi'",
    ),
    Kusur(
        ad="belirteç değişkeni test istemcisinin değişkeniyle aynı yapılıyor",
        dosya=IDENTITY,
        bul='    public const string TokenVariable = "BIZIGO_MCP_TOKEN";',
        koy='    public const string TokenVariable = "BIZIGO_MCP_API_TOKEN";   // KIRMIZI-H',
        kirmizi_bekleniyor=[AD_AYRI],
        yesil_kalmali=[KITLE, SURE],
        not_="M13'ün birinci şartı: ikisi farklı şey, aynı ada koymak bir sanmaktır",
    ),
    Kusur(
        # `McpCallerScope`'un M13 ile eklenen tek satırı: sebep sorulmuyor.
        # Süre sonu cümlesi üretiliyor ama ARACA ULAŞMIYOR.
        ad="sebep araca taşınmıyor (kapı eski cümlede kalıyor)",
        dosya=SCOPE,
        bul="                string.IsNullOrWhiteSpace(refusal) ? NoIdentityMessage : refusal);",
        koy="                NoIdentityMessage);   // KIRMIZI-I",
        kirmizi_bekleniyor=["Sebep_araca_tasiniyor"],
        yesil_kalmali=[SURE],
        not_="saf cümle DOĞRU üretilirken kapı onu yok sayıyor — iki bekçinin ayrı olma sebebi",
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
    `Failed:`) ve ayıklama düştüğünde sessizce sıfır üretirdi — yani ölçüm
    aracının kendisi §7'nin sınıfına girerdi.
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
