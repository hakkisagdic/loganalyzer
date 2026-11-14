# LogAnalyzer — Uçtan Uca Doğrulama, Rakip Analizi ve Ürün İş Listesi (Backlog)

**Doküman Sürümü:** 1.0.0  
**Tarih:** 14 Kasım 2026  
**Durum:** Aktif Ürün ve Mühendislik Referansı  

---

## BÖLÜM 1: Uçtan Uca Doğrulama Kılavuzu (E2E Verification Guide)

LogAnalyzer iki ana dağıtım modeline sahiptir: **LogAnalyzer Core** (kurumsal, dağıtık, ClickHouse tabanlı tam gözlemlenebilirlik platformu) ve **LogAnalyzer Lite** (sıfır bağımlılıklı, hafif, incident flight recorder). Her iki katmanın doğrulama adımları aşağıda açıklanmıştır.

### 1. LogAnalyzer Lite Doğrulama Protokolü (Canlı Altyapı Modu)

Lite sürüm, SupportBot ve OrtakKup (IntegrationGateway) tarafından doğrudan kullanılan üretim bileşenidir.

#### A. Servis Başlatma ve Sağlık Kontrolü
```bash
# Yerel bağımsız çalıştırma
dotnet run --project src/LogAnalyzer.Lite --launch-profile http

# Veya Docker Compose ile çalıştırma
docker compose -f deploy/docker-compose.lite.yml up -d --build
```
- **Sağlık Sondası (Liveness/Readiness):**
  ```bash
  curl -fsS http://localhost:5060/health
  # Beklenen yanıt: 200 OK, {"status":"Healthy","version":"...","commit":"..."}
  ```

#### B. Ingest Hattı ve Güvenlik Belirteci Doğrulaması
1. **Belirteçsiz / Hatalı Belirteç (Negatif Test):**
   ```bash
   curl -i -X POST http://localhost:5060/api/v1/ingest \
     -H "Content-Type: application/json" \
     -d '{"endpoint":"/test","level":"Error"}'
   # LITE_INGEST_TOKEN tanımlıysa: 401 Unauthorized dönmelidir.
   ```
2. **Geçerli Belirteçle Olay Kaydı (Pozitif Test):**
   ```bash
   curl -i -X POST http://localhost:5060/api/v1/ingest \
     -H "Content-Type: application/json" \
     -H "X-LogAnalyzer-Ingest-Token: ${LITE_INGEST_TOKEN}" \
     -d '{
       "trackingId": "e2e-track-001",
       "correlationId": "corr-12345",
       "level": "Error",
       "source": "Gateway[test-tenant]",
       "endpoint": "/api/v1/orders/create",
       "httpMethod": "POST",
       "httpStatus": 500,
       "durationMs": 142,
       "errorMessage": "İkas API timeout",
       "clientIp": "192.168.1.50"
     }'
   # Beklenen yanıt: 200 OK, {"id":"...","status":"Queued"}
   ```

#### C. Yönetim Uçları ve CSRF/Origin İzolasyonu
- Yönetim API'leri (`/api/v1/alerts/*`, `/api/v1/logs/purge`, `/api/v1/demo/*`), `X-Requested-With: loganalyzer-lite` başlığı ve aynı köken (same-origin / allowed origins) gerektirir.
- Çapraz kökenli istekler (`Sec-Fetch-Site: cross-site`) 403 Forbidden ile fail-closed reddedilmelidir.

#### D. Alarm ve Webhook İletim Testi
```bash
curl -fsS -X POST http://localhost:5060/api/v1/alerts/test \
  -H "Content-Type: application/json" \
  -H "X-Requested-With: loganalyzer-lite" \
  -d '{"target":"slack"}'
# Slack ve Discord URL'leri regex doğrulamasına tabidir; geçersiz şema/hostlar reddedilir.
```

#### E. Test Paketi Koşumu
```bash
dotnet test tests/LogAnalyzer.Lite.Tests/
# Beklenen: Tüm IngestToken, BrowserPolicy, WebhookConfig ve Storage testleri eksiksiz geçmelidir.
```

---

### 2. LogAnalyzer Core Doğrulama Protokolü (Tam Küme)

#### A. Altyapı ve Veri Depoları
```bash
# Veri tabanı ve aracı servisleri başlatın
docker compose -f deploy/docker-compose.yml up -d postgres clickhouse minio otel-collector
```
1. **PostgreSQL:** 5432 portu erişilebilir olmalı, `bizigo` şeması ve EF Core migration'ları uygulanmalıdır.
2. **ClickHouse:** 8123 (HTTP) ve 9010 (Native) portları sağlıklı olmalı, `telemetry_events` tabloları hazır bulunmalıdır.
3. **MinIO / S3:** 9000 portunda `bizigo-raw` bucket'ı hazır olmalıdır.

#### B. Collector → WAL → ClickHouse Akış Doğrulaması
```bash
# OTLP Collector duman testi
python3 tools/otlp-collector-smoke.py

# Replay ve WAL dayanıklılık kontrolü
python3 tools/telemetry-legacy-replay-smoke.py
```

#### C. Kapsam ve İzolasyon Doğrulaması
- Çok kiracılı mimaride kiracı A'nın sorgusu kiracı B'nin olaylarını göremez (`IScopedQuery`).
- İmzalı sayfalama cursor'ları manipülasyona karşı doğrulanır.

#### D. Çekirdek Test Paketleri
```bash
# .NET birim testleri (2400+ test)
dotnet test tests/LogAnalyzer.UnitTests/

# Entegrasyon ve operasyonel E2E testleri
dotnet test tests/LogAnalyzer.IntegrationTests/ --filter FullyQualifiedName~OperationalE2EIntegrationTests

# UI Vitest ve API tipi eşlik kontrolü
cd ui && npm test && npm run api:check
```

---

## BÖLÜM 2: Rakip Analizi ve Pazar Konumlandırması (Competitive Analysis)

| Özellik / Kriter | **LogAnalyzer (Lite + Core)** | **Datadog** | **Sentry** | **Grafana Loki / Mimir** | **SigNoz / HyperDX** |
|---|---|---|---|---|---|
| **Dağıtım Modeli** | Hibrit: Tek ikili (Lite) + Kendi Sunucunda Dağıtık (Core) | Yalnızca SaaS (Bulut) | SaaS + Karmaşık Kendi Sunucunda | Kendi Sunucunda / Cloud | Kendi Sunucunda / Cloud |
| **Maliyet Yapısı** | Kendi altyapında sıfır lisans / sabit sunucu maliyeti | GB/host başına katlanarak artan yüksek fatura | İşlem/olay başına kota | İndeks ve disk maliyeti | ClickHouse donanım maliyeti |
| **Entegrasyon Kolaylığı** | Lite için sıfır bağımlılık, anında Docker/Coolify | Tescilli ajan kurulumu gerekir | SDK kurulumu kolay | Promtail/Grafana Agent gerekir | OTel Collector gerekir |
| **Kök Neden Analizi (RCA)** | Yerleşik topoloji ve DAG tabanlı nedensellik çıkarımı | Yapay zeka eklentisi (Watchdog) | Sadece issue gruplama | Harici eklenti gerekir | Temel korelasyon |
| **Siber Güvenlik / Kural Motoru** | Yerleşik **SigmaHQ kural derleme** ve SQL projeksiyonu | Cloud SIEM (Ekstra ücretli) | Yok | Loki LogQL alert kuralları | Temel alarm kuralları |
| **Veri Güvenliği ve Kapsam** | Sıkı fail-closed `IScopedQuery`, PII arındırma | Dış buluta veri transferi riski | Dış buluta veri transferi riski | İç ağda güvenli | İç ağda güvenli |
| **Uygulama İçi Gömülebilirlik** | **Var:** Lite, admin panellerine iframe/proxy ile gömülebilir | Yok (harici dashboard) | Embed widget yok | Grafana embedding zor ve hantal | Yok |

### Güçlü Yönlerimiz (Differentiators):
1. **Lite / Flight Recorder Yaklaşımı:** Birçok mikroservis veya altyapı ürünü için tam ölçekli Datadog/ELK kümesi kurmak israftır. LogAnalyzer Lite; tek container, sıfır DB, dosya tabanlı güvenilir arabellek ve webhook desteğiyle dakikalar içinde canlıya alınabilir.
2. **Core ile Kurumsal Güç:** İhtiyaç büyüdüğünde ClickHouse + OTLP + PostgreSQL mimarisine geçiş yapılarak milyarlarca log olayına ölçeklenebilir.
3. **Kapsam ve Denetim İzolasyonu:** Çok kiracılı yapılarda veri sızıntısını matematiksel olarak önleyen mimari.

### Açıklarımız / Gelişim Alanlarımız (Gaps):
1. **Esnek Dashboarding:** Grafana veya Datadog gibi sürükle-bırak grafik ve serbest metrik gösterge paneli yoktur.
2. **Kolektör Ekosistemi:** Fluentbit, Logstash gibi zengin hazır eklenti ekosistemi yerine standart OTLP ve HTTP API'ye odaklanılmıştır.
3. **Kullanıcı Yetkilendirme:** Lite sürümünde rol bazlı detaylı erişim yönetimi (RBAC) yoktur; yönetim tamamen tek admin tokenına dayalıdır.

---

## BÖLÜM 3: Ürün İş Listesi (Backlog)

### 🚀 P0 — Kritik ve Yakın Dönem (Acil / Operasyonel)
- [ ] **LITE-CORE-BRIDGE:** LogAnalyzer Lite üzerinde toplanan arıza ve incident kayıtlarının, merkezi LogAnalyzer Core / ClickHouse deposuna asenkron arka plan görevlisiyle aktarılabilmesi (Offline-first Flight Recorder → Cloud Sync).
- [ ] **COOLIFY-ONE-CLICK:** Coolify için `docker-compose.lite.yml` şablonunun pürüzsüz hale getirilmesi, kalıcı disk (`/app/data`) izinlerinin otomatik 0700 ayarlanması ve otomatik güncelleme hook'unun belgelenmesi.
- [ ] **NEXTJS-DEPS-UPDATE:** `ui/` paketindeki Next.js ve bağımlılıkların güvenlik yamalarının uygulanması, `npm audit` temizliği.
- [ ] **PII-SCRUBBER-V2:** Ingest hattına eklenecek genel kurallı (regex tabanlı) otomatik TCKN, Kredi Kartı ve E-posta maskeleyici middleware'i (özellikle SupportBot kaynaklı serbest metinlerde sıfır sızıntı garantisi).

### ⚡ P1 — Önemli Katma Değer (Fonksiyonel ve Deneyim)
- [ ] **ALERT-CHANNELS-EXPANSION:** LogAnalyzer Lite alarm motoruna Telegram Bot ve Genel Webhook (Custom JSON payload) kanallarının eklenmesi.
- [ ] **SEARCH-INDEX-LITE:** Lite sürümünde incident arama hızını artırmak için bellek içi hafif ters indeks (inverted index) mekanizması.
- [ ] **DYNAMIC-SAMPLING:** Gateway ve servislerden gelen 200 OK trafiği için yüzdesel örnekleme (sampling), 4xx ve 5xx hatalarında ise %100 yakalama garantisi.
- [ ] **RCA-VISUALIZER-LIGHT:** Core'daki karmaşık DAG grafiğinin basitleştirilmiş bir SVG/Canvas sürümünün Lite detay modallarına eklenmesi (Neden çöktü? Servis A → Servis B → Timeout).

### 🔮 P2 — Gelecek Vizyon ve İleri Yetenekler (Stratejik)
- [ ] **AI-INCIDENT-EXPLAINER:** Lite web arayüzüne yerel LLM (Ollama) veya Claude/OpenAI API üzerinden tek tıkla çalışan "Hatayı Açıkla ve Çözüm Öner" asistanının eklenmesi.
- [ ] **K8S-HELM-CHART:** LogAnalyzer Core ve Lite için üretim sınıfı Helm Chart ve Kubernetes Operator paketi.
- [ ] **PROMETHEUS-METRIC-EXPORTER:** `/metrics` uç noktası üzerinden Prometheus ve OpenMetrics standart formatında metrik ihracı.
