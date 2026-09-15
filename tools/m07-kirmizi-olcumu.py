#!/usr/bin/env python3
"""M07'nin KAYNAK yarısının kırmızı ölçümü (§6).

Koşum yordamı ortak (`kirmizi_olcumu.py`). Bu dosya M07'nin birinci turundan
taşındı: o tur kendi yordamını yazmıştı çünkü ortak modül dalın tabanında henüz
yoktu. Merge sonrası kopya kaldırıldı — aynı yordamın iki uygulaması §9'un
yasakladığı şey, ve birinci turun kopyasında bulunan üç kusurdan ikisi ortak
yordamda zaten kapalıydı.

BİRİNCİ TURDA ÖLÇÜM ARACININ KENDİSİNDE BULUNAN ÜÇÜNCÜ KUSUR burada da yok:
iki `--filter` argümanı `dotnet test`'i kullanım yardımına düşürüyor ve çıktıda
ne "Başarısız!" ne "error CS" kalıyor — araç ölçüm hiç yapılmamışken YEŞİL
sayıyordu. Ortak yordam tek filtre kullanıyor.

İKİ KALEM BİLEREK YEŞİL BEKLENİYOR (`yesil_kalmali`): yüzey ayrımı İKİ BAĞIMSIZ
kapıdan geçiyor ve tek kapıyı kırmak bekçiyi kırmızı yakmıyor. Bunu ölçmek,
"bekçi çalışıyor mu" sorusunun yanına "kaç katman var" sorusunu koyuyor.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from kirmizi_olcumu import Kusur, olc  # noqa: E402

MCP = "src/Bizigo.Mcp"
PROD = "src/Bizigo.Mcp.Product/Resources"

KUSURLAR = [
    Kusur(
        ad="1 · Gövde serbest string ile kurulabiliyor",
        dosya=f"{MCP}/McpResourceBody.cs",
        bul="        return new McpResourceBody(redacted.Text, mimeType);",
        koy="        return new McpResourceBody(redacted.Text, mimeType);\n"
            "    }\n\n"
            "    /// <summary>KUSUR — kapıyı atlayan aşırı yükleme.</summary>\n"
            "    public static McpResourceBody Of(string text, string mimeType)\n"
            "    {\n"
            "        return new McpResourceBody(text, mimeType);",
        kirmizi_bekleniyor=["Govdeyi_ureten_her_uye_redaksiyon_kapisi_istiyor"],
    ),
    Kusur(
        ad="2 · Gövdeyi kuran ikinci bir metot",
        dosya=f"{MCP}/McpResourceBody.cs",
        bul="        return new McpResourceBody(redacted.Text, mimeType);",
        koy="        return new McpResourceBody(redacted.Text, mimeType);\n"
            "    }\n\n"
            "    /// <summary>KUSUR — ikinci fabrika.</summary>\n"
            "    internal static McpResourceBody Kacak(RedactedPrompt redacted)\n"
            "    {\n"
            "        return new McpResourceBody(redacted.Text, McpResourceMimeTypes.Json);",
        kirmizi_bekleniyor=["Govdeyi_newobj_ile_kuran_tek_metot_var"],
    ),
    Kusur(
        ad="3 · Kaynaksız yüzeyde boş koleksiyon bırakılıyor",
        dosya=f"{MCP}/BizigoMcpServer.cs",
        bul="            options.ResourceCollection = null;",
        koy="            options.ResourceCollection ??= []; // KUSUR",
        kirmizi_bekleniyor=[
            "Kaynaksiz_yuzeyde_koleksiyon_hic_kurulmuyor",
            "Desteklenmeyen_yetenek_ilan_edilmiyor",
        ],
    ),
    Kusur(
        ad="4 · Bir belge türü sessizce kayboluyor",
        dosya=f"{PROD}/ParserDefinitionResource.cs",
        bul='public const string ResourceKind = "parser";',
        koy='public const string ResourceKind = "parser-v2"; // KUSUR',
        kirmizi_bekleniyor=["Dort_belge_turu_de_ilan_ediliyor"],
    ),
    Kusur(
        ad="5 · Adres kapsam taşıyor",
        dosya=f"{MCP}/McpResourceUri.cs",
        bul='$"{Prefix}{Require(kind, nameof(kind))}/{{{IdVariable}}}"',
        koy='$"{Prefix}{Require(kind, nameof(kind))}/{{owner_group}}/{{{IdVariable}}}" /* KUSUR */',
        kirmizi_bekleniyor=["Adres_kapsam_tasimiyor"],
    ),
    Kusur(
        ad="6 · Kanıt paketi kapsam kontrolü kaldırıldı",
        dosya=f"{PROD}/EvidenceBundleResource.cs",
        bul="return bundle is null || !bundle.Scope.IsReadableBy(read.Scope) ? null : Body(bundle);",
        koy="return bundle is null ? null : Body(bundle); // KUSUR",
        kirmizi_bekleniyor=["Kapsam_disi_paketin_adresi_reddediliyor"],
    ),
    Kusur(
        ad="7 · Rapor kapsamı paketten devralmıyor",
        dosya=f"{PROD}/RcaReportResource.cs",
        bul="        if (bundle is null || !bundle.Scope.IsReadableBy(read.Scope))",
        koy="        if (bundle is null) // KUSUR",
        kirmizi_bekleniyor=["Rapor_kapsamini_paketten_devraliyor"],
    ),
    Kusur(
        ad="8 · İptal belirteci gözetilmiyor",
        dosya=f"{PROD}/ParserDefinitionResource.cs",
        bul="        cancellationToken.ThrowIfCancellationRequested();\n\n"
            "        // Örnek GERÇEK şekillendirme yolundan geçiyor",
        koy="        // KUSUR: iptal gözetilmiyor\n\n"
            "        // Örnek GERÇEK şekillendirme yolundan geçiyor",
        kirmizi_bekleniyor=["Her_kaynak_iptal_edilmis_belirteci_gozetiyor"],
    ),
    # ---- Yüzey ayrımı: İKİ BAĞIMSIZ KATMAN, tek katman YETMİYOR ----
    Kusur(
        ad="9 · Yüzey beyanı katmanı tek başına (YEŞİL kalmalı)",
        dosya=f"{PROD}/ProductResource.cs",
        bul="    public sealed override McpSurface Surface => McpSurface.Product;",
        koy="    public sealed override McpSurface Surface => McpSurface.Simulator; // KUSUR",
        yesil_kalmali=["Simulator_yuzeyi_hic_kaynak_sunmuyor"],
    ),
    Kusur(
        ad="10 · Derleme listesi katmanı tek başına (YEŞİL kalmalı)",
        dosya="src/Bizigo.Cli/McpCommandHandlers.cs",
        bul="McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly],",
        koy="McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly, "
            "typeof(Bizigo.Mcp.Product.Resources.ProductResource).Assembly], // KUSUR",
        yesil_kalmali=["Simulator_yuzeyi_hic_kaynak_sunmuyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, "m07-kaynaklar"))
