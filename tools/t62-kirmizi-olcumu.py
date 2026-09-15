#!/usr/bin/env python3
"""T62 — BFF hazırlık ucunun bekçilerinin KIRMIZI YANABİLDİĞİNİN ölçümü.

Koşum yordamı ortak (`kirmizi_olcumu`); burada kalan tek şey kusur listesi.

Bu ticket'ın bekçileri **iki pakette** duruyor ve ölçüm de öyle: mantık ve rota
`ui/` içinde (vitest), compose bağı `tests/Bizigo.UnitTests` içinde (dotnet).
İkisi ayrı koşum, tek yordam.

Ölçülen şey ucun ne söylediği değil, **neyi göremediğinde sustuğu**: T49'un
sondası üç bağımlılığın hepsi kırıkken yeşil kalıyordu. O yüzden kusurların
yarısı kapıyı körleştiriyor (hazır olmayanı hazır saymak, bağlantıya bakmamak),
diğer yarısı bağlanmışlığı hedefliyor — rotanın durum kodunu, üretimin
yoklamasını, ve compose'un ucu gerçekten yokladığını.

    python3 tools/t62-kirmizi-olcumu.py           # ui/ tarafı (vitest)
    python3 tools/t62-kirmizi-olcumu.py --dotnet  # compose bağı
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, dotnet_kosumu, olc, vitest_kosumu

UI = [
    Kusur(
        # Kapının kendisi: hazır olmayan bir bağımlılık hazır sayılıyor.
        ad="hazır olmayan bağımlılık hazır sayılıyor",
        dosya="ui/src/lib/health/readiness.ts",
        bul='    ready: checks.every((check) => check.state === "ready"),',
        koy="    ready: true,",
        kirmizi_bekleniyor=["kırıkken hazır DEĞİL"],
        # Rota testi `readiness`'i taklit ediyor, yani mantıktan bağımsız.
        # İkisinin ayrı ayrı ölçülmesi bu satırın gösterdiği şey.
        yesil_kalmali=["rota hazır değilken 503 dönüyor"],
    ),
    Kusur(
        ad="Redis yoklaması bağlantıya bakmıyor",
        dosya="ui/src/lib/auth/redis-store.ts",
        bul='    return { kind: "redis", reachable: await this.#ready() };',
        koy='    return { kind: "redis", reachable: true };',
        kirmizi_bekleniyor=["Redis deposu bağlantı durumunu bildiriyor"],
        yesil_kalmali=["bellek içi depo yapısı gereği erişilebilir"],
    ),
    Kusur(
        # Güvenlik kararının bekçisi: yük serbest metin taşımaya başlıyor.
        ad="yük hata metnini taşıyor",
        dosya="ui/src/lib/health/readiness.ts",
        bul='    { name: "api", state: settled(api) ? "ready" : "unreachable" },',
        koy='    { name: "api", state: settled(api) ? "ready" : "unreachable",\n'
            '      detail: api.status === "rejected" ? String(api.reason) : undefined } as never,',
        kirmizi_bekleniyor=["yükte adres, ana makine adı ya da hata metni geçmiyor"],
    ),
    Kusur(
        # Bağlanmışlık: rota raporu okuyor ama durum kodunu ona bağlamıyor.
        ad="rota durum kodunu sabitliyor",
        dosya="ui/src/app/api/health/ready/route.ts",
        bul="    status: report.ready ? 200 : 503,",
        koy="    status: 200,",
        kirmizi_bekleniyor=["rota hazır değilken 503 dönüyor"],
        yesil_kalmali=["kırıkken hazır DEĞİL"],
    ),
    Kusur(
        # T49'un ASIL kusurunun birebir modeli: sonda depoya hiç sormuyor.
        # Enjekte yoklamalarla koşan testlerin hiçbiri bunu göremiyor ve
        # `yesil_kalmali` satırı tam olarak o körlüğü ölçüyor.
        ad="üretimin yoklaması depoya hiç sormuyor",
        dosya="ui/src/lib/health/readiness.ts",
        bul="    sessionStore: () => sessionStore().probe(),",
        koy='    sessionStore: async () => ({ kind: "memory" as const, reachable: true }),',
        kirmizi_bekleniyor=["üretimin yoklaması yapılandırılmış depoya soruyor"],
        yesil_kalmali=["kırıkken hazır DEĞİL"],
    ),
]

COMPOSE = [
    Kusur(
        # Compose eski sondaya dönüyor: `ui/` tarafındaki 13 testin hiçbiri
        # bunu göremez, çünkü hepsi ucun kendisini ölçüyor.
        ad="compose eski sondaya dönüyor (kök sayfa, < 500)",
        dosya="deploy/docker-compose.yml",
        bul="fetch('http://127.0.0.1:3000/api/health/ready').then(r => process.exit(r.ok ? 0 : 1))",
        koy="fetch('http://127.0.0.1:3000/', { redirect: 'manual' })"
            ".then(r => process.exit(r.status < 500 ? 0 : 1))",
        kirmizi_bekleniyor=[
            "Arayuz_saglik_kontrolu_hazirlik_ucunu_yokluyor",
            "Olcut_yanit_kodunun_basarisi",
        ],
    ),
]


if __name__ == "__main__":
    if "--dotnet" in sys.argv:
        sys.exit(olc(COMPOSE, ".t62-olcum-yedek", dotnet_kosumu()))

    sys.exit(olc(UI, ".t62-olcum-yedek", vitest_kosumu("ui")))
