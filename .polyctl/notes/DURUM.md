# LogAnalyzer — Proje Durum Raporu (DURUM.md)

**Tarih:** 14 Kasım 2026  
**Depo:** `hakkisagdic/loganalyzer`  
**Çalışma Dizini:** `~/Projects/bizigo-loganalyzer`  
**Güncel Dal:** `review/polyctl-onboarding`  

---

## 1. Genel Bakış ve Mevcut Durum

LogAnalyzer, kurumsal altyapı hizmetlerimizde aktif olarak kullanılan merkezi gözlemlenebilirlik ve olay kayıt (incident flight recorder) platformudur. Proje iki ana formda yaşamaktadır:

1. **LogAnalyzer Core (Tam Ölçekli Gözlemlenebilirlik Platformu):**
   - **Teknoloji:** .NET 9 arka uç, ClickHouse (zaman serisi log/metrik/izler), PostgreSQL (topoloji, erişim denetimi, anomali politikaları), MinIO/S3 (WAL ve ham arşiv), Next.js UI.
   - **Yetenekler:** OTLP uyumlu veri kabulü (Collector → WAL → ClickHouse), Sigma kural derleme ve eşleme, nedensellik DAG korelasyonu ve kök neden analizi (RCA), çoklu kiracı kapsam izolasyonu (`IScopedQuery`).

2. **LogAnalyzer Lite (Incident Flight Recorder):**
   - **Teknoloji:** .NET 10 hafif bağımsız web servisi, sıfır dış bağımlılık (ClickHouse/PG gerektirmez), dosya tabanlı atomik olay kalıcılığı (`LITE_DATA_DIR`), dahili incident web konsolu (`/`), Slack ve Discord webhook alarmları.
   - **Dağıtım:** Coolify ve Docker Compose uyumlu (`deploy/docker-compose.lite.yml`, port `5060`).
   - **Güvenlik:** `X-LogAnalyzer-Ingest-Token` ile korunan ingest hattı, `X-Requested-With: loganalyzer-lite` ve Origin/CORS kontrolleriyle korunan yönetim uçları, PII ve sır maskeleme.

---

## 2. Dahili Ürün Entegrasyonları (SupportBot & OrtakKup)

LogAnalyzer, şirket içi altyapı ürünlerimizde aktif canlı kullanımda bulunmaktadır:

### A. OrtakKup (IntegrationGateway) Entegrasyonu
- **Kullanım Biçimi:** `IntegrationGateway`, tüm gelen/giden API çağrılarını ve hata durumlarını `ILogAnalyzerSink` / `LogAnalyzerHttpSink` aracılığıyla LogAnalyzer Lite'a (`/api/v1/ingest`) asenkron olarak gönderir.
- **Dayanıklılık:** Bellek içi bounded channel (10.000 sınır) + SQLite tabanlı yerel denetim günlüğü (`IAuditJournal`). LogAnalyzer geçici olarak erişilemez olsa dahi Gateway çağrıları aksamaz, hatalar kuyrukta birikir ve LogAnalyzer açıldığında otomatik olarak boşaltılır (flush).
- **Yönetim Köprüsü (Proxy):** Gateway, `/loganalyzer/*` yolunu LogAnalyzer Lite'a güvenli olarak vekil eder. SuperAdmin yetkisi gerektirir; tarayıcının `Origin` ve `Sec-Fetch-*` başlıklarını izole ederek CSRF/CORS korumalarının iç ağda sağlıklı çalışmasını sağlar. Gateway Next.js panelinde `/admin/loganalyzer` altında gömülü konsol olarak sunulur.
- **Güvenlik Belirteçleri:** `LOGANALYZER_INGEST_TOKEN` ve `LOGANALYZER_ADMIN_TOKEN` ile iki uçlu kimlik doğrulaması sağlanır.

### B. SupportBot Entegrasyonu
- **Kullanım Biçimi:** SupportBot, müşteri destek süreçleri ve e-ticaret (İkas) sipariş/paket takibi için IntegrationGateway üzerinden çalışır.
- **Log Akışı:** SupportBot üzerinden tetiklenen servis çağrılarındaki hatalar ve operasyonel kırılmalar Gateway tarafından yakalanıp LogAnalyzer Lite'a iletilir.
- **PII Koruması:** Müşteri hassas verileri (PII), destek mesaj gövdeleri ve gizli anahtarlar LogAnalyzer yükünden arındırılır (`requestPayload: null` politikası).

---

## 3. Çalışma Kuralları ve Güvenlik İlkeleri

- **İş ve Değişiklik Yönetimi:** Tüm geliştirmeler `review/<konu>` dallarında yürütülür; doğrudan ana dala kontrolsüz müdahale yapılmaz.
- **Yayın/Release Yetkisi:** Admin onayı olmadan hiçbir ortama kontrolsüz sürüm/yayın yapılmaz.
- **Sır Güvenliği:** Gizli anahtarlar, parolalar ve `.env` değerleri kesinlikle okunmaz, loglara veya dosyalara açık metin olarak yazdırılmaz.

---

## 4. Çalışma Ağacı Temizliği ve 24 Değişikliğin Ayrıştırılması

Başlangıçta tespit edilen 24 commitlenmemiş değişiklik şu şekilde ayrıştırılmıştır:
1. **Üretilmiş Çıktılar ve Örnek Loglar:** `graphify-out/` altındaki grafik çıktıları ve `catalog/` altındaki örnek `.log` dosyaları `.gitignore` kapsamına alınarak kaynak deposundan ayrılmıştır.
2. **Kaynak ve Yapılandırma Dosyaları:** `.gitignore` güncellemeleri, `.polyctl/` yönetim şemaları ve notları, `.claude/` ve `.kiro/` araç tanımları kaynak olarak korunup sürüm kontrolüne dahil edilmiştir.

---

## 5. Sıradaki Eylemler

1. `review/polyctl-onboarding` dalındaki kaynak değişikliklerinin commitlenip uzak depoya gönderilmesi (push).
2. `docs/DOGRULAMA.md` dokümanının hazırlanması (uçtan uca test protokolü, sentetik veri ve canlı entegrasyon doğrulama adımları).
3. Pazar/rakip analizi (Datadog, Sentry, Grafana Loki, SigNoz, HyperDX karşılaştırması) ve önceliklendirilmiş ürün iş listesi (backlog).
