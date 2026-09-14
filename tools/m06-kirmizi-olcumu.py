#!/usr/bin/env python3
"""M06 — redaksiyon ve K6 kapılarının KIRMIZI YANABİLDİĞİNİN ölçümü.

Koşum yordamı ORTAK ve `kirmizi_olcumu` içinde: yordamın kendisi bu dosyada
yazılmıştı, M11 ikinci bir ölçüme ihtiyaç duyunca ortaklaştırıldı (§9). Bu
dosyada kalan tek şey M06'nın kusur listesi — ve kalması gereken tek şey o:
hangi kusurun hangi bekçiyi kırmızı yakması gerektiği ticket'ın bilgisi.

    python3 tools/m06-kirmizi-olcumu.py
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, olc

KUSURLAR = [
    Kusur(
        ad="menteşeye ham `string` geçiriliyor",
        dosya="src/Bizigo.Mcp/McpToolResult.cs",
        bul="        return new McpToolResult(\n            Payload,\n            Error,\n"
            "            [.. LogText, .. redacted.Select(McpLogText.FromRedacted)]);",
        koy="        return new McpToolResult(\n            Payload,\n            Error,\n"
            "            [.. LogText, McpLogText.FromRedacted(\"KIRMIZI ham metin\")]);",
        derleme_kirilmali=True,
    ),
    Kusur(
        ad="ikinci fabrika: `string` alan bir üretim yolu",
        dosya="src/Bizigo.Mcp/McpLogText.cs",
        bul="    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        koy="    internal static McpLogText KIRMIZI_FromText(string text) => new(text);\n\n"
            "    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        kirmizi_bekleniyor=[
            "Log_metni_uretmenin_her_yolu_redaksiyon_kapisindan_geciyor",
            "Tasiyiciyi_kuran_tek_metot_redaksiyon_fabrikasi",
        ],
    ),
    Kusur(
        # İmzası DOĞRU, gövdesi YANLIŞ. Kapı 1 imzalara bakıyor ve bu kusuru
        # göremiyor; Kapı 2 gövdelere bakıyor ve görüyor. İkisinin ayrı
        # olmasının gerekçesi tam olarak bu satır.
        ad="imzası doğru gövdesi yanlış fabrika",
        dosya="src/Bizigo.Mcp/McpLogText.cs",
        bul="    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        koy="    internal static McpLogText KIRMIZI_Sahte(RedactedPrompt p) => new(\"ham\");\n\n"
            "    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        kirmizi_bekleniyor=["Tasiyiciyi_kuran_tek_metot_redaksiyon_fabrikasi"],
        yesil_kalmali=["Log_metni_uretmenin_her_yolu_redaksiyon_kapisindan_geciyor"],
    ),
    Kusur(
        ad="fabrika `public` yapılıyor",
        dosya="src/Bizigo.Mcp/McpLogText.cs",
        bul="    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        koy="    public static McpLogText FromRedacted(RedactedPrompt redacted) =>",
        kirmizi_bekleniyor=["Araclarin_gordugu_tek_yol_redaksiyon_kapisi"],
    ),
    Kusur(
        ad="taşıyıcının yapıcısı `internal` yapılıyor",
        dosya="src/Bizigo.Mcp/McpLogText.cs",
        bul="    private McpLogText(string text) => Text = text;",
        koy="    internal McpLogText(string text) => Text = text;",
        kirmizi_bekleniyor=["Log_metni_uretmenin_her_yolu_redaksiyon_kapisindan_geciyor"],
    ),
    Kusur(
        ad="üretim yapılandırması `External` beyan ediyor",
        dosya="src/Bizigo.Api/appsettings.json",
        bul='    "DataBoundary": "Internal"\n  },\n  "Security"',
        koy='    "DataBoundary": "External"\n  },\n  "Security"',
        kirmizi_bekleniyor=["Uretim_yapilandirmasi_siniri_beyan_ediyor"],
    ),
    Kusur(
        ad="üretim yapılandırması sınırı hiç beyan etmiyor",
        dosya="src/Bizigo.Api/appsettings.json",
        bul='  "Mcp": {\n    "DataBoundary": "Internal"\n  },\n',
        koy="",
        kirmizi_bekleniyor=["Uretim_yapilandirmasi_siniri_beyan_ediyor"],
    ),
    Kusur(
        ad="K6 kapısı sunucu kurulumundan çıkarılıyor",
        dosya="src/Bizigo.Mcp/BizigoMcpServer.cs",
        bul="        McpBoundaryGate.Require(boundary, surface);",
        koy="        // KIRMIZI: kapı kurulumdan çıkarıldı.",
        kirmizi_bekleniyor=["Kurum_disi_beyan_sunucu_kurulumunda_reddediliyor"],
    ),
    Kusur(
        ad="`Unspecified` beyanı kabul ediliyor",
        dosya="src/Bizigo.Mcp/McpBoundary.cs",
        bul="        if (boundary == DataBoundary.Unspecified)\n        {",
        koy="        if (false && boundary == DataBoundary.Unspecified)\n        {",
        kirmizi_bekleniyor=["Beyansiz_sunucu_kurulamiyor"],
    ),
    Kusur(
        ad="gerekçesiz beyan kabul ediliyor",
        dosya="src/Bizigo.Mcp/McpBoundary.cs",
        bul="        if (string.IsNullOrWhiteSpace(basis))\n        {",
        koy="        if (false && string.IsNullOrWhiteSpace(basis))\n        {",
        kirmizi_bekleniyor=["Gerekcesiz_beyan_kurulamiyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, ".m06-olcum-yedek"))
