#!/usr/bin/env python3
"""M11 — K6'nın TOPOLOJİ kapısının kırmızı yanabildiğinin ölçümü.

Koşum yordamı ortak (`kirmizi_olcumu`); burada kalan tek şey kusur listesi.

Ölçülen şey kapının ne söylediği değil, **neyi göremediğinde sustuğu**. M11'in
kapattığı hâl tam olarak buydu: beyan `internal`, dinleyici `0.0.0.0`, ve
hiçbir yerde kırmızı yanmıyor. O yüzden kusurların yarısı kapının kendisini
körleştiriyor (joker bağlamayı kanıt saymak, muafiyeti sessiz yapmak), diğer
yarısı bağlanmışlığı ve sevk edilen muafiyeti hedefliyor.

    python3 tools/m11-kirmizi-olcumu.py
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, olc

KUSURLAR = [
    Kusur(
        # T50'nin ayrımı: var olmak ile BAĞLI olmak. Kapı kusursuz cevap
        # verirken hiç kaydedilmemiş olabilir ve saf kapı testleri bunu
        # göremez — aşağıdaki `yesil_kalmali` satırı tam olarak o körlüğü
        # ölçüyor.
        ad="kapı MCP kaydından çıkarılıyor",
        dosya="src/Bizigo.Api/McpEndpoints.cs",
        bul="        services.AddHostedService<McpListenerBoundaryCheck>();",
        koy="        // KIRMIZI: kapı kayıttan çıkarıldı.",
        kirmizi_bekleniyor=[
            "Kapi_uretimin_mcp_kaydinda_bagli",
            "Gercek_kestrel_gerekcesiz_joker_baglamada_kalkmiyor",
        ],
        yesil_kalmali=["Joker_baglama_gerekcesiz_reddediliyor"],
    ),
    Kusur(
        # M11'in var olma sebebinin birebir tersi: joker bağlamayı KANIT
        # saymak. Sessiz hâl bu satırla geri geliyor.
        ad="joker bağlama kanıt sayılıyor",
        dosya="src/Bizigo.Api/McpListenerBoundary.cs",
        bul='                + "arayüzlerin dışa açık olduğu süreç içinden bilinemiyor",\n'
            "                Routable: true);",
        koy='                + "arayüzlerin dışa açık olduğu süreç içinden bilinemiyor",\n'
            "                Routable: false);",
        kirmizi_bekleniyor=[
            "Joker_baglama_gerekcesiz_reddediliyor",
            "Gercek_kestrel_gerekcesiz_joker_baglamada_kalkmiyor",
        ],
        yesil_kalmali=["Yonlendirilemez_dinleyici_dogrulaniyor"],
    ),
    Kusur(
        # Joker dalının BİR BAŞKA işi var: `+` ve `*` geçerli bir `IPAddress`
        # değil ve dal kapanınca ad çözümüne düşüyorlar. Yani kusur yalnızca
        # mesajı değil SONUCU da değiştiriyor.
        ad="joker dalı hiç koşmuyor (ad çözümüne düşüyor)",
        dosya="src/Bizigo.Api/McpListenerBoundary.cs",
        bul="        if (IsWildcard(host))",
        koy="        if (false && IsWildcard(host))",
        kirmizi_bekleniyor=[
            "Joker_ile_genel_adres_ayri_anlatiliyor",
            "Joker_baglama_gerekcesiz_reddediliyor",
        ],
    ),
    Kusur(
        ad="boş gerekçe muafiyet açıyor",
        dosya="src/Bizigo.Api/McpListenerBoundary.cs",
        bul="        if (!string.IsNullOrWhiteSpace(overrideReason))",
        koy="        if (overrideReason is not null)",
        kirmizi_bekleniyor=["Bos_gerekce_muafiyet_acmiyor"],
        yesil_kalmali=["Gerekce_yazildiginda_joker_baglama_geciyor"],
    ),
    Kusur(
        # Bu deponun beş kez ödediği sınıf: boş küme üzerinde dönen bekçi
        # yeşil rapor ediyor. `NoListener` ile `Verified` aynı ada düşerse
        # `TestServer` ile koşan her host "doğrulandı" sayılır.
        ad="adressiz kalkış doğrulama sayılıyor",
        dosya="src/Bizigo.Api/McpListenerBoundary.cs",
        bul="                McpListenerBoundaryOutcome.NoListener,",
        koy="                McpListenerBoundaryOutcome.Verified,",
        kirmizi_bekleniyor=["Adressiz_kalkis_dogrulama_sayilmiyor"],
    ),
    Kusur(
        ad="sevk edilen muafiyet siliniyor",
        dosya="src/Bizigo.Api/Dockerfile",
        bul='ENV Mcp__ListenerBoundaryOverrideReason="Konteyner agi:',
        koy='ENV KIRMIZI_Muafiyet_Silindi="Konteyner agi:',
        kirmizi_bekleniyor=["Sevk_edilen_muafiyet_baglamasina_bagli_ve_sayisi_civili"],
    ),
    Kusur(
        # Bekçinin İKİNCİ yönü, ve asıl değeri burada: bağlama loopback'e
        # çekilirse muafiyetin gerekçesi geçersiz oluyor ve dosyada kalan satır
        # "bu kurulum muaf" diye okunmaya devam ediyor. Muafiyetin kendi
        # gerekçesinden uzun yaşaması bu depoda ölçülmüş bir olay (T53).
        ad="bağlama loopback'e çekiliyor, bayat muafiyet kalıyor",
        dosya="src/Bizigo.Api/Dockerfile",
        bul="ENV ASPNETCORE_URLS=http://0.0.0.0:8080",
        koy="ENV ASPNETCORE_URLS=http://127.0.0.1:8080",
        kirmizi_bekleniyor=["Sevk_edilen_muafiyet_baglamasina_bagli_ve_sayisi_civili"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, ".m11-olcum-yedek"))
