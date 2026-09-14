#!/usr/bin/env python3
"""M07 · Kırmızı ölçüm aracı.

§6: bir bekçinin kırmızı YANABİLDİĞİ gösterilmeden yazılmış olması, bekçinin
çalıştığının kanıtı DEĞİL. Bu araç her bekçi için bir kusur enjekte ediyor,
kusurun dosyada GERÇEKTEN olduğunu doğruluyor, testi koşturuyor ve kırmızı
yanmasını bekliyor.

M05'te ölçülen iki kusur bu araçta kapalı:

1. GERİ ALMA DİSKE YAZILIYOR — enjeksiyondan ÖNCE. Araç öldürülürse geri alma
   sürecin belleğinde kaybolmuyor; /tmp/m07-geri-al.sh elle koşturulabiliyor.
2. İDDİA DOSYANIN TAMAMIYLA KARŞILAŞTIRILIYOR. "Aranan yeni metin var mı"
   ölçütü, kusur bir şeyi SİLDİĞİNDE yalan söylüyordu.
"""

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BACKUP = Path("/tmp/m07-yedek")
UNDO = Path("/tmp/m07-geri-al.sh")

MCP = "src/Bizigo.Mcp"
PROD = "src/Bizigo.Mcp.Product/Resources"

# (ad, dosya, eski, yeni, test filtresi)
KUSURLAR = [
    (
        "1 · Gövde serbest string ile kurulabiliyor",
        [(f"{MCP}/McpResourceBody.cs",
          "        return new McpResourceBody(redacted.Text, mimeType);",
          "        return new McpResourceBody(redacted.Text, mimeType);\n"
        "    }\n\n"
        "    /// <summary>KUSUR — kapıyı atlayan aşırı yükleme.</summary>\n"
        "    public static McpResourceBody Of(string text, string mimeType)\n"
        "    {\n"
        "        return new McpResourceBody(text, mimeType);")],
        "Govdeyi_ureten_her_uye_redaksiyon_kapisi_istiyor",
    ),
    (
        "2 · Gövdeyi kuran ikinci bir metot",
        [(f"{MCP}/McpResourceBody.cs",
          "        return new McpResourceBody(redacted.Text, mimeType);",
          "        return new McpResourceBody(redacted.Text, mimeType);\n"
        "    }\n\n"
        "    /// <summary>KUSUR — ikinci fabrika.</summary>\n"
        "    internal static McpResourceBody Kacak(RedactedPrompt redacted)\n"
        "    {\n"
        "        return new McpResourceBody(redacted.Text, McpResourceMimeTypes.Json);")],
        "Govdeyi_newobj_ile_kuran_tek_metot_var",
    ),
    (
        "3 · Kaynaksız yüzeyde boş koleksiyon bırakılıyor",
        [(f"{MCP}/BizigoMcpServer.cs",
          "            options.ResourceCollection = null;\n\n            return;",
          "            options.ResourceCollection ??= []; // KUSUR\n\n            return;")],
        "Kaynaksiz_yuzeyde_koleksiyon_hic_kurulmuyor|Desteklenmeyen_yetenek_ilan_edilmiyor",
    ),
    (
        "4 · Abonelik gerçeğe değil sabite bağlı",
        [(f"{MCP}/BizigoMcpServer.cs",
          "            Subscribe = resources.Any(static resource => resource.SupportsSubscription),",
          "            Subscribe = true, // KUSUR")],
        "Desteklenmeyen_yetenek_ilan_edilmiyor",
    ),
    (
        "5 · Bir belge türü sessizce kayboluyor",
        [(f"{PROD}/ParserDefinitionResource.cs",
          'public const string ResourceKind = "parser";',
          'public const string ResourceKind = "parser-v2"; // KUSUR')],
        "Uc_belge_turu_de_ilan_ediliyor",
    ),
    (
        "6 · Adres kapsam taşıyor",
        [(f"{MCP}/McpResourceUri.cs",
          '$"{Prefix}{Require(kind, nameof(kind))}/{{{IdVariable}}}"',
          '$"{Prefix}{Require(kind, nameof(kind))}/{{owner_group}}/{{{IdVariable}}}" /* KUSUR */')],
        "Adres_kapsam_tasimiyor",
    ),
    (
        # ÖLÇÜLDÜ: bu kusurun YALNIZ derleme listesi yarısı bekçiyi kırmızı
        # YAKMIYOR — `ProductResource.Surface` `sealed override` olduğu için
        # erişilebilir hâle gelen türler yüzey filtresinde eleniyor. Ve yalnız
        # yüzey yarısı da yakmıyor: derleme listesi o türlere hiç ulaşmıyor.
        # Yani yüzey ayrımı İKİ BAĞIMSIZ kapıdan geçiyor; kusur ikisini birden
        # kırıyor ve tek yarısının yetmediği ayrıca ölçüldü (7b, 7c).
        "7 · Yüzey ayrımının İKİ katmanı birden kırılıyor",
        [
            ("src/Bizigo.Cli/McpCommandHandlers.cs",
             "McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly],",
             "McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly, "
             "typeof(Bizigo.Mcp.Product.Resources.ProductResource).Assembly], // KUSUR"),
            (f"{PROD}/ProductResource.cs",
             "    public sealed override McpSurface Surface => McpSurface.Product;",
             "    public sealed override McpSurface Surface => McpSurface.Simulator; // KUSUR"),
        ],
        "Simulator_yuzeyi_hic_kaynak_sunmuyor",
    ),
    (
        "7b · Yalnızca derleme listesi katmanı (tek başına YETMİYOR)",
        [("src/Bizigo.Cli/McpCommandHandlers.cs",
          "McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly],",
          "McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly, "
          "typeof(Bizigo.Mcp.Product.Resources.ProductResource).Assembly], // KUSUR")],
        "Simulator_yuzeyi_hic_kaynak_sunmuyor",
    ),
    (
        "7c · Yalnızca yüzey beyanı katmanı (tek başına YETMİYOR)",
        [(f"{PROD}/ProductResource.cs",
          "    public sealed override McpSurface Surface => McpSurface.Product;",
          "    public sealed override McpSurface Surface => McpSurface.Simulator; // KUSUR")],
        "Simulator_yuzeyi_hic_kaynak_sunmuyor",
    ),
    (
        "8 · Kanıt paketi kapsam kontrolü kaldırıldı",
        [(f"{PROD}/EvidenceBundleResource.cs",
          "return bundle is null || !bundle.Scope.IsReadableBy(read.Scope) ? null : Body(bundle);",
          "return bundle is null ? null : Body(bundle); // KUSUR")],
        "Kapsam_disi_paketin_adresi_reddediliyor",
    ),
    (
        "9 · Rapor kapsamı paketten devralmıyor",
        [(f"{PROD}/RcaReportResource.cs",
          "        if (bundle is null || !bundle.Scope.IsReadableBy(read.Scope))",
          "        if (bundle is null) // KUSUR")],
        "Rapor_kapsamini_paketten_devraliyor",
    ),
    (
        "10 · İptal belirteci gözetilmiyor",
        [(f"{PROD}/ParserDefinitionResource.cs",
          "        cancellationToken.ThrowIfCancellationRequested();\n\n"
        "        // Örnek GERÇEK şekillendirme yolundan geçiyor",
          "        // KUSUR: iptal gözetilmiyor\n\n"
        "        // Örnek GERÇEK şekillendirme yolundan geçiyor")],
        "Her_kaynak_iptal_edilmis_belirteci_gozetiyor",
    ),
    (
        "11 · Bağlam maliyeti tavanı aşılıyor",
        [(f"{PROD}/EvidenceBundleResource.cs",
          '"Kapsam dışı bir paket \'bulunamadı\' döner.";',
          '"Kapsam dışı bir paket \'bulunamadı\' döner. "\n'
        '        + "KUSUR: bu cümle tavanı aşmak için uzatıldı ve modelin bağlamında yer yiyor. "\n'
        '        + "Kanıt paketi belgesi pencereyi, tabanı, güven ölçüsünü, sıralanmış kanıt "\n'
        '        + "dilimlerini, her dilimin ağırlığını, zaman dürüstlüğü notunu, kapsam beyanını, "\n'
        '        + "toplama bütçesini ve paketin şema sürümünü ayrıntılı biçimde taşır ve bunların "\n'
        '        + "hepsi burada tek tek sayılır ki tavan gerçekten aşılsın ve bekçi kırmızı yansın.";')],
        "Kaynak_ilaninin_baglam_maliyeti",
    ),
]


def yedekle(dosyalar):
    BACKUP.mkdir(parents=True, exist_ok=True)
    satirlar = ["#!/bin/sh", "# M07 kırmızı ölçümünün geri alma betiği.", f"cd {ROOT}"]

    for rel in sorted(dosyalar):
        hedef = BACKUP / rel.replace("/", "__")
        # `copy`, `copy2` DEĞİL: zaman damgasını taşımak, geri alınan dosyanın
        # MSBuild tarafından bayat sayılmasına yol açıyor (M05'te ölçüldü).
        shutil.copy(ROOT / rel, hedef)
        satirlar.append(f'cp "{hedef}" "{ROOT / rel}"')

    UNDO.write_text("\n".join(satirlar) + "\n")
    UNDO.chmod(0o755)
    print(f"Yedek: {BACKUP} · geri alma: {UNDO}", flush=True)


def geri_al(rel):
    shutil.copy(BACKUP / rel.replace("/", "__"), ROOT / rel)


def kosdur(filtre):
    # TEK `--filter`, `|` ile OR — ve bu ÖLÇÜLEREK düzeltildi.
    #
    # İlk hâli her ad için ayrı bir `--filter` yazıyordu. `dotnet test` iki
    # `--filter` görünce KULLANIM YARDIMINI basıp çıkıyor: çıktıda ne
    # "Başarısız!" ne "error CS" var, yani araç bunu YEŞİL sayıyordu. Ölçüm hiç
    # yapılmamışken "bekçi kusuru görmedi" raporlanıyordu — §6'nın iddia
    # adımının bu araçtaki üçüncü kör noktası.
    ifade = "|".join(f"FullyQualifiedName~{f}" for f in filtre.split("|"))
    sonuc = subprocess.run(
        f'export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"; '
        f'dotnet test tests/Bizigo.UnitTests --filter "{ifade}" --nologo -v q 2>&1 | tail -30',
        shell=True, cwd=ROOT, capture_output=True, text=True, timeout=1800)

    return sonuc.stdout


def main():
    dosyalar = {rel for _, duzenlemeler, _ in KUSURLAR for rel, _, _ in duzenlemeler}
    yedekle(dosyalar)

    rapor = []

    for ad, duzenlemeler, filtre in KUSURLAR:
        oncekiler = {rel: (ROOT / rel).read_text() for rel, _, _ in duzenlemeler}
        hedef_yok = [rel for rel, eski, _ in duzenlemeler if eski not in oncekiler[rel]]

        if hedef_yok:
            rapor.append((ad, "HEDEF YOK", f"kusur enjekte EDİLEMEDİ: {', '.join(hedef_yok)}"))
            print(f"[{ad}] HEDEF YOK", flush=True)
            continue

        for rel, eski, yeni in duzenlemeler:
            (ROOT / rel).write_text(oncekiler[rel].replace(eski, yeni, 1))

        # İDDİA: dosyaların HEPSİ gerçekten değişti. Ölçüt dosyanın tamamı —
        # "yeni metin var mı" ölçütü kusur bir şeyi sildiğinde yalan söylüyordu
        # (M05). Tek bir düzenleme tutmadıysa ölçüm YAPILMAMIŞ sayılıyor.
        degismeyen = [rel for rel in oncekiler if (ROOT / rel).read_text() == oncekiler[rel]]

        if degismeyen:
            for rel, onceki in oncekiler.items():
                (ROOT / rel).write_text(onceki)

            rapor.append((ad, "ENJEKSİYON YOK", f"değişmeyen: {', '.join(degismeyen)}"))
            print(f"[{ad}] ENJEKSİYON YOK", flush=True)
            continue

        cikti = kosdur(filtre)
        derleme = "error CS" in cikti or "error xUnit" in cikti

        # POZİTİF KONTROL: koşum gerçekten oldu mu. Derleme kırıldıysa test
        # sayısı basılmıyor ve bu meşru; ama derleme sağlamken sayı yoksa
        # `dotnet test` hiç test koşturmamış demektir (yanlış filtre, yanlış
        # bayrak) ve o hâl YEŞİL diye okunamaz — ölçüm YAPILMAMIŞTIR.
        if not derleme and "Toplam:" not in cikti:
            for rel in oncekiler:
                geri_al(rel)
            rapor.append((ad, "ÖLÇÜM YOK", "dotnet test hiç test koşturmadı"))
            print(f"[{ad}] ÖLÇÜM YOK", flush=True)
            continue

        kirmizi = "Başarısız!" in cikti or derleme
        sebep = "derleme kırıldı" if derleme else "test düştü"

        for rel in oncekiler:
            geri_al(rel)

        kirli = [rel for rel, onceki in oncekiler.items() if (ROOT / rel).read_text() != onceki]

        if kirli:
            rapor.append((ad, "GERİ ALMA BAŞARISIZ", f"DİKKAT: kirli kaldı: {', '.join(kirli)}"))
            print(f"[{ad}] GERİ ALMA BAŞARISIZ", flush=True)
            continue

        rapor.append((ad, "KIRMIZI" if kirmizi else "YEŞİL", sebep if kirmizi else "bekçi kusuru GÖRMEDİ"))
        print(f"[{ad}] {'KIRMIZI' if kirmizi else 'YEŞİL'} ({sebep if kirmizi else 'görmedi'})", flush=True)

    print("\n" + "=" * 72)
    print("M07 · KIRMIZI ÖLÇÜM RAPORU")
    print("=" * 72)

    for ad, sonuc, not_ in rapor:
        print(f"{sonuc:22} {ad}  —  {not_}")

    print("=" * 72)

    # "YETMİYOR" diye işaretli kalemler KONTROL ölçümü: beklenen sonuç YEŞİL.
    # Onları başarısızlık saymak, iki katmanın bağımsız olduğunu gösteren
    # ölçümü bir arıza gibi okumak olurdu.
    beklenen = [
        (ad, sonuc) for ad, sonuc, _ in rapor
        if ("YETMİYOR" in ad and sonuc != "YEŞİL") or ("YETMİYOR" not in ad and sonuc != "KIRMIZI")
    ]

    if beklenen:
        print("BEKLENMEYEN SONUÇ:", beklenen)

    return 0 if not beklenen else 1


if __name__ == "__main__":
    sys.exit(main())
