"""M10 — bekçilerin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Geçen bir test geçtiğini kanıtlamaz; kırılabildiğini göstermek kanıtlar
(CLAUDE.md §6). Yordam `m06-kirmizi-olcumu.py`'den devralındı ve dört adımı
aynen koşuyor:

  1. kusuru yaz
  2. dosyayı OKU ve kusurun orada olduğunu İDDİA ET
  3. koştur ve beklenen kırmızıyı gör
  4. YEDEK DOSYADAN geri al, zaman damgasını TAŞIMADAN

Bu turda yordamın kendisi bir kez ısırdı ve kaydı burada: ölçüm ilk kez bir
kabuk betiği olarak koşturuldu, koşum ORTASINDA öldürüldü ve kusur çalışma
ağacında KALDI (`AlertSuppression.cs`, yarı-açık aralık kapatılmış hâlde).
`trap` yazılıydı ama `traycer_stop_shell`'in gönderdiği sinyalle boru hattının
tamamı düştü. Betiğin Python hâlinde geri alma `finally` içinde ve süreç
öldürülse bile en kötü hâlde YEDEK DİZİNİ diskte kalıyor — kusur değil.

Dersin genel hâli: **öldürülmüş bir ölçüm, geri alınmış bir ölçüm değildir.**
Kesilen bir koşumdan sonra ağacın temiz olduğu VARSAYILMAZ, `grep` ile
doğrulanır.

M10'un ölçtüğü altı kusurdan biri ayrıca bir BULGU üretiyor — bkz. `KUSURLAR`
listesindeki ilk kalem.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m10-olcum-yedek"

LOGS = "src/Bizigo.Mcp.Product/Tools/LogsGetTool.cs"
MAINT = "src/Bizigo.Mcp.Product/Tools/AlertsMaintenanceTool.cs"
QUAL = "src/Bizigo.Mcp.Product/Tools/RcaQualityTool.cs"
SUPP = "src/Bizigo.Alerting/AlertSuppression.cs"


@dataclass
class Kusur:
    """Bir kusur ve ondan beklenen kırmızı."""

    ad: str
    dosya: str
    bul: str
    koy: str
    #: Kırmızı yanması beklenen testler (isim parçası).
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    #: Kusura RAĞMEN yeşil kalması beklenen testler — iki bekçinin
    #: birbirinin kopyası OLMADIĞINI gösteriyor.
    yesil_kalmali: list[str] = field(default_factory=list)
    #: Kusurun kendisi hakkında yazılacak not (rapora giriyor).
    not_: str = ""
    #: İkinci çapa. Bazı kusurlar TEK BİR SATIRLA ifade edilemiyor ve yarım
    #: uygulanmış bir kusur ölçümü yalancı yapıyor: alan tipini `string`
    #: yapmak derlemeyi kırıyor ama kırılma sebebi kapı değil, çağrı yerinin
    #: hâlâ eski tipi geçirmesi. İkisi birlikte uygulanmadan soru sorulamıyor.
    ikinci_bul: str = ""
    ikinci_koy: str = ""


KUSURLAR = [
    Kusur(
        # ── BULGU ────────────────────────────────────────────────────────────
        # BU KUSUR DERLENİYOR, ve derlenmesi M10'un koordinatöre bildirdiği
        # şey. M06'nın kapısı `WithLogText(params RedactedPrompt[])` imzasında
        # derleyicide duruyor; YAPISAL kanalda (`structuredContent`) böyle bir
        # şart YOK. M06 bunu kendi belgesinde beyan etmişti — "o alanın öyle
        # yazılması bugün MEKANİK OLARAK TUTULMUYOR" — ve bekçiyi bilerek
        # yazmamıştı, çünkü o gün tüketici yoktu (§8).
        #
        # M10 tüketiciyi getirdi, dolayısıyla bekçi yazılabildi. Ama bekçi bir
        # DERLEME şartı değil bir test; kapsamı bir araç.
        ad="yapısal kanalda kapıyı atla: gövde alanı `string`, çağrı yeri ham metin",
        dosya=LOGS,
        # ÇAPA İKİ YERDE BİRDEN olmak zorunda ve bu bir ölçüm dersi. İlk hâl
        # yalnızca ALAN TİPİNİ `string` yapıyordu ve derleme kırıldı — ama
        # kırılma sebebi kapı DEĞİLDİ: `Shape` hâlâ bir `RedactedPrompt`
        # geçiriyordu, yani kırılan şey benim yarım kusurumdu. O sonucu
        # "kapı derleyicide" diye okumak, ölçülmemiş bir şeyi ölçülmüş
        # göstermek olurdu (§6).
        #
        # Tam kusur: alan `string` VE çağrı yeri `source.Body`. Bu hâlin
        # derlenip derlenmediği koordinatörün sorduğu soru.
        bul='            source.RawRef,\n            body,',
        koy='            source.RawRef,\n            source.Body,   // KIRMIZI-A',
        ikinci_bul='        [property: JsonPropertyName("body")] RedactedPrompt Body,',
        ikinci_koy='        [property: JsonPropertyName("body")] string Body,   // KIRMIZI-A',
        kirmizi_bekleniyor=[
            "Logs_get_govdesi_RedactedPrompt_olarak_yazili",
            "Logs_get_sirri_maskelenmis_donduruyor",
        ],
        not_="ÖLÇÜLDÜ: kusur DERLENDİ — yapısal kanalda kapı derleyicide DEĞİL, iki bekçi tuttu",
    ),
    Kusur(
        # İkinci kusur birincinin tamamlayıcısı: TİP doğru, GÖVDE yanlış.
        # Yansıma bekçisi bunu göremiyor (tip hâlâ `RedactedPrompt`), davranış
        # bekçisi görüyor. İkisinin ayrı olmasının gerekçesi tam olarak bu.
        ad="tipi doğru gövdesi yanlış: kapı boş metne uygulanıyor",
        dosya=LOGS,
        bul="        var body = RedactedPrompt.Redact(source.Body);",
        koy="        var body = RedactedPrompt.Redact(source.Body);\n"
            "        body = RedactedPrompt.Redact(source.Body.Replace(\"psksecret \", \"\", "
            "StringComparison.Ordinal));   // KIRMIZI-B",
        kirmizi_bekleniyor=["Logs_get_sirri_maskelenmis_donduruyor"],
        yesil_kalmali=["Logs_get_govdesi_RedactedPrompt_olarak_yazili"],
        not_="yansıma bekçisi bu kusuru GÖRMÜYOR — davranış bekçisi görüyor",
    ),
    Kusur(
        ad="ham baytlar base64 olarak yüke ekleniyor (kapının en kolay atlatma yolu)",
        dosya=LOGS,
        bul='            "attrs_truncated":   { "type": "boolean" }',
        koy='            "attrs_truncated":   { "type": "boolean" },\n'
            '            "raw_b64":           { "type": "string" }',
        kirmizi_bekleniyor=["Logs_get_ham_baytlari_base64_olarak_dondurmuyor"],
        not_="şema tarafı tek başına yetiyor: ilan edilen alan kapıyı kırmızı yakıyor",
    ),
    Kusur(
        ad="`attrs` kesme sınırı kaldırılıyor",
        dosya=LOGS,
        bul="    private const int MaxAttributes = 60;",
        koy="    private const int MaxAttributes = 10_000;   // KIRMIZI-D",
        kirmizi_bekleniyor=["Logs_get_attrs_kesilmesi_yukte_gorunuyor"],
    ),
    Kusur(
        ad="yarı-açık aralık kapatılıyor: pencere bittiği anda hâlâ açık",
        dosya=SUPP,
        bul="        return now >= window.StartsAt && now < window.EndsAt;",
        koy="        return now >= window.StartsAt && now <= window.EndsAt;   // KIRMIZI-E",
        kirmizi_bekleniyor=["Alerts_maintenance_yururluk_karari_bastirma_motoruyla_ayni"],
        not_="kararın TEK yerde olmasının kanıtı: bir satır iki bekçiyi birden düşürüyor",
    ),
    Kusur(
        ad="bakım aracının kapsam filtresi düşürülüyor",
        dosya=MAINT,
        bul="            .Where(w => scope.Allows(w.OwnerGroup))",
        koy="            .Where(w => true)   // KIRMIZI-F",
        kirmizi_bekleniyor=["Alerts_maintenance_kapsam_disi_pencereyi_dondurmuyor"],
    ),
    Kusur(
        ad="MCP üzerinden YAZMA: bakım aracı `SaveChangesAsync` çağırıyor",
        dosya=MAINT,
        bul="        return McpToolResult.Structured(Shape(all, scope, now, limit, openOnly));",
        koy="        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // KIRMIZI-G\n\n"
            "        return McpToolResult.Structured(Shape(all, scope, now, limit, openOnly));",
        kirmizi_bekleniyor=["Hicbir_urun_araci_yazma_cagirmiyor"],
        not_="M10'un 'bu ürün MCP üzerinden yazma yapmıyor' kararının mekanik bekçisi",
    ),
    Kusur(
        ad="`null` oran sıfıra düşürülüyor: ölçülmemiş boyut 'mükemmel' görünüyor",
        dosya=QUAL,
        bul="        quality.Accuracy,",
        koy="        quality.Accuracy ?? 0,   // KIRMIZI-H",
        kirmizi_bekleniyor=["Rca_quality_bos_kumede_oranlari_null_donduruyor"],
        yesil_kalmali=["Rca_quality_baska_grubun_incelemesini_saymiyor"],
        not_="dolu kümede aynı alan doğru kalıyor — kusur YALNIZCA boş kümede görünüyor",
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
        # ulaştırmıyor (T44'te ölçüldü — MSBuild projeyi atlıyor, `dotnet
        # build` "0 hata" diyor ve koşan ikili hâlâ kusurlu oluyor).
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
            "ölçüm koşmadan durduruluyor. Sessizce yeşil raporlamak §6'nın yasakladığı şey."
        )

    metin = metin.replace(kusur.bul, kusur.koy, 1)

    if kusur.ikinci_bul:
        if kusur.ikinci_bul not in metin:
            raise SystemExit(
                f"[{kusur.ad}] İKİNCİ ÇAPA BULUNAMADI: {kusur.dosya}. Kusur YARIM kalırdı — "
                "ve yarım bir kusur, ölçümü yalancı yapar."
            )

        metin = metin.replace(kusur.ikinci_bul, kusur.ikinci_koy, 1)

    yol.write_text(metin, encoding="utf-8")
    yol.touch()


def iddia_et(kusur: Kusur) -> None:
    """§6'nın İDDİA ADIMI: kusur dosyada gerçekten var mı."""
    metin = (KOK / kusur.dosya).read_text(encoding="utf-8")

    if kusur.koy not in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: kusur dosyada yok, ölçüm yalancı olurdu.")

    # "Eski metin gitti mi" sorusu YALNIZCA değiştirme tipi kusurlarda geçerli;
    # ekleme tipinde çapa yerinde kalıyor ve kalması gerekiyor.
    if kusur.bul not in kusur.koy and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: eski metin hâlâ orada.")

    if kusur.ikinci_koy and kusur.ikinci_koy not in metin:
        raise SystemExit(
            f"[{kusur.ad}] İDDİA DÜŞTÜ: kusurun İKİNCİ yarısı dosyada yok.")

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI"
          + (" (iki çapa)" if kusur.ikinci_bul else ""))


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
        "dotnet", "test", "tests/Bizigo.UnitTests", "--no-build", "--nologo",
        "--filter", filtre,
    ])

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

            if not basarili:
                hata = next(
                    (s.strip() for s in cikti.splitlines() if ": error " in s),
                    "(hata satırı okunamadı)",
                )
                # DERLEME KIRILMASI BİR SONUÇ, bir aksaklık değil: kusur
                # ifade edilemiyorsa kapı DERLEYİCİDE duruyor demektir ve bu
                # bekçiden daha güçlü bir cevap.
                rapor.append((kusur.ad, f"DERLENMEDİ — kapı derleyicide: {hata[:100]}"))
                print(f"    DERLENMEDİ ✓✓ kapı derleyicide: {hata[:100]}\n")
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

            if kusur.not_:
                rapor.append((f"    ↳ {kusur.ad}", kusur.not_))

            print()
            geri_al({kusur.dosya})
    finally:
        # GERİ ALMA `finally` İÇİNDE, ve bu turda bunun bedeli ödendi: aynı
        # ölçüm bir kabuk betiği olarak koşturulup öldürüldüğünde kusur ağaçta
        # KALDI. Süreç burada öldürülse bile en kötü hâl yedek dizininin diskte
        # kalması — kusurun ağaçta kalması değil.
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
    print("(bu adım isteğe bağlı değil: T44'te kusuru yakalayan şey bir bekçi değil bu koşumdu)\n")

    basarili, _ = derle()

    if not basarili:
        print("GERİ ALMA DERLEMEYE ULAŞMADI.")
        return 1

    kod, cikti = test_kos("FullyQualifiedName!~SidecarLive")
    son = next((s for s in cikti.splitlines() if "Başarısız:" in s or "Failed:" in s), "(özet okunamadı)")
    print(son)

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    if kod != 0:
        print("\nPaket kırmızı. Düşen test(ler):\n")

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


if __name__ == "__main__":
    sys.exit(main())
