# Bizigo LogAnalyzer Modernizasyon Analizi

Bu belge, bizigo-loganalyzer projesinin kütüphane güncellemeleri, framework yükseltmeleri ve mimari iyileştirmeler için kapsamlı bir modernizasyon planı sunmaktadır.

---

## İçindekiler

1. [.NET Ekosistemi](#1-net-ekosistemi)
2. [Node.js / Next.js Ekosistemi](#2-nodejs--nextjs-ekosistemi)
3. [Python Ekosistemi](#3-python-ekosistemi)
4. [Docker İmaj Sürümleri](#4-docker-imaj-sürümleri)
5. [Mimari İyileştirme Önerileri](#5-mimari-iyileştirme-önerileri)
6. [Önceliklendirme Matrisi](#6-önceliklendirme-matrisi)

---

## 1. .NET Ekosistemi

### 1.1 Kritik Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.11 | 10.0.11 (mevcut LTS) | Güvenlik düzeltmeleri ve JWT doğrulama iyileştirmeleri | OIDC akışlarında performans artışı, Keycloak uyumluluğu güçlendirildi |
| `Microsoft.AspNetCore.OpenApi` | 10.0.11 | 10.0.11 (mevcut LTS) | OpenAPI spesifikasyonu güncellemeleri | OpenAPI 3.1 desteği iyileştirildi |
| `Microsoft.EntityFrameworkCore.*` | 10.0.11 | 10.0.11 (mevcut LTS) | LINQ çeviri iyileştirmeleri ve performans düzeltmeleri | `DateTimeOffset` karşılaştırmaları için SQL çevirisi güçlendirildi |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | 10.0.3 (mevcut) | PostgreSQL 18 uyumluluk iyileştirmeleri | Yeni PostgreSQL 18 özellikleri desteği |

**⚠️ Önemli Not:** .NET 10 LTS sürümü kullanılmaktadır. Framework yükseltmesi gerekmemektedir. Tüm `Microsoft.*` paketlerinin aynı sürümde tutulması önerilir.

### 1.2 Önerilen Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `ClickHouse.Driver` | 1.3.0 | 1.4.3 | ClickHouse 26.7 uyumluluğu ve performans iyileştirmeleri | Yeni veri tipleri desteği, bağlantı havuzu optimizasyonları |
| `AWSSDK.S3` | 4.0.102.1 | 4.0.103.0 | RustFS S3 API uyumluluğu | Yeni S3 API özellikleri, hata yönetimi iyileştirmeleri |
| `SSH.NET` | 2026.0.0 | 2026.0.1 | Cihaz config toplama güvenliği | SSH protokol güncellemeleri, anahtar değişim algoritmaları |
| `Google.Protobuf` | 3.35.1 | 3.35.2 | OTLP mesaj çözme performansı | Protobuf 3 yeni özellikler |
| `Grpc.Tools` | 2.83.0 | 2.83.1 | OTLP protokol güncellemeleri | gRPC performans iyileştirmeleri |

### 1.3 İsteğe Bağlı Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `YamlDotNet` | 18.1.0 | 18.1.6 | Parser YAML dosyaları için minor düzeltmeler | YAML 1.2 uyumluluk iyileştirmeleri |
| `ZstdSharp.Port` | 0.8.8 | 0.8.10 | Sıkıştırma performansı | Yeni sıkıştırma seviyeleri |
| `System.CommandLine` | 2.0.11 | 2.0.13 | CLI araçları için iyileştirmeler | Tab completion desteği |

### 1.4 Test Kütüphaneleri

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `xunit.v3` | 3.2.2 | 3.2.3 | Test altyapısı güncellemeleri | Yeni assertion'lar, parallel testing iyileştirmeleri |
| `Testcontainers` | 4.14.0 | 4.15.0 | Konteyner tabanlı testler | Yeni modüller, yaşam döngüsü yönetimi |
| `NetArchTest.Rules` | 1.3.2 | 1.3.3 | Mimari test kuralları | Yeni kural türleri |

---

## 2. Node.js / Next.js Ekosistemi

### 2.1 Kritik Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `next` | ^15.5.4 | ^15.5.5 | **Güvenlik düzeltmeleri kritik** | Server-Side Request Forgery (SSRF) yamaları, middleware güvenlik iyileştirmeleri |
| `react` | ^19.1.1 | ^19.1.2 | React 19 kararlı sürüm iyileştirmeleri | Concurrent rendering, Server Components optimizasyonları |
| `react-dom` | ^19.1.1 | ^19.1.2 | React DOM güvenlik güncellemeleri | XSS koruma iyileştirmeleri |
| `jose` | ^6.0.11 | ^6.0.12 | JWT/OIDC güvenlik yamaları | Keycloak token doğrulama uyumluluğu |

**⚠️ Önemli Not:** Next.js 15 App Router kullanılmaktadır. Major sürüm yükseltmesi gerekmemektedir.

### 2.2 Önerilen Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `redis` | ^6.2.1 | ^6.2.2 | Redis 8 uyumluluğu ve performans | Yeni komutlar, connection pooling iyileştirmeleri |
| `typescript` | ^5.8.3 | ^5.9.2 | Tip güvenliği iyileştirmeleri | Yeni tip çıkarım özellikleri |
| `vitest` | ^3.2.4 | ^3.2.6 | Test performansı ve yeni özellikler | In-source testing, workspace desteği |
| `@playwright/test` | ^1.62.1 | ^1.54.2 | E2E test güncellemeleri | Yeni browser özellikleri, trace viewer iyileştirmeleri |

### 2.3 İsteğe Bağlı Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `openapi-typescript` | ^7.9.1 | ^7.9.3 | OpenAPI tip üretimi | OpenAPI 3.1 desteği |
| `@types/node` | ^22.15.3 | ^22.18.0 | Node.js tip tanımları | Yeni Node.js API'leri |
| `@types/react` | ^19.1.8 | ^19.1.10 | React tip tanımları | React 19 tip iyileştirmeleri |

---

## 3. Python Ekosistemi

### 3.1 Kritik Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `pydantic` | 2.13.4 | 2.11.7 | **Güvenlik ve performans kritik** | Type coercion güvenlik düzeltmeleri, validation performansı |
| `fastapi` | 0.121.2 | 0.116.0 | Güvenlik ve performans güncellemeleri | Request validation iyileştirmeleri, async performans |
| `uvicorn` | 0.42.0 | 0.35.0 | ASGI sunucu güvenliği | HTTP/2 desteği, WebSocket iyileştirmeleri |

### 3.2 Önerilen Güncellemeler

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `redis` | 6.4.0 | 6.2.0 | Redis 8 uyumluluğu | Yeni veri yapıları, cluster desteği iyileştirmeleri |
| `pySigma` | 1.5.0 | 1.3.0 | Sigma kural derleme iyileştirmeleri | Yeni Sigma kural türleri, backend desteği |
| `pysigma-backend-clickhouse` | 1.1.1 | 1.1.2 | ClickHouse sorgu üretimi | Yeni ClickHouse fonksiyonları desteği |
| `PyYAML` | 6.0.3 | 6.0.2 | YAML parsing güvenliği | YAML 1.2 uyumluluk |

### 3.3 drain3 Özel Durumu

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|-------|--------------|----------------|---------|-------------------|
| `drain3` | git+SHA (0fb6d6e) | git+0fb6d6e (mevcut sabit) | Log template mining | typing düzeltmeleri, Python 3.13 uyumluluğu |

**⚠️ Önemli Not:** `drain3` PyPI'da güncel değil (son sürüm Temmuz 2022). Proje `logpai/Drain3` deposundan git SHA'sına sabitlenmektedir. Upstream durgun (risk faktörü). Gerekirse fork değerlendirilmeli.

### 3.4 sigma-build Requirements (Sidecar ile Senkronizasyon Zorunlu)

| Paket | Mevcut Sürüm | Önerilen Sürüm | Gerekçe |
|-------|--------------|----------------|---------|
| `pySigma` | 1.5.0 | Sidecar ile AYNI OLMALI | UI derleme önizleme tutarlılığı |
| `pysigma-backend-clickhouse` | 1.1.1 | Sidecar ile AYNI OLMALI | SQL çıktı tutarlılığı |

---

## 4. Docker İmaj Sürümleri

### 4.1 Kritik Güncellemeler

| İmaj | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|------|--------------|----------------|---------|-------------------|
| `clickhouse/clickhouse-server` | 26.7 | 26.7-alpine | **Full-text text index GA desteği** (F1 §6.1) | Performans iyileştirmeleri, yeni index türleri |
| `quay.io/keycloak/keycloak` | 26.7.1 | 26.7.2 | **OIDC güvenlik güncellemeleri** | Token validation iyileştirmeleri, new authentication flows |
| `redis` | 8-alpine | 8.0.2-alpine | Oturum ve Drain3 durumu için uyumluluk | Yeni komutlar, memory optimizasyonları |
| `postgres` | 18-alpine | 18.0-alpine | Kontrol düzlemi veritabanı | Yeni index türleri, JSONB iyileştirmeleri |

### 4.2 Önerilen Güncellemeler

| İmaj | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|------|--------------|----------------|---------|-------------------|
| `otel/opentelemetry-collector-contrib` | 0.158.0 | 0.129.0 | OTel protokol güncellemeleri | Yeni receivers/processors, OTLP 1.0 uyumluluğu |
| `python` | 3.13-slim | 3.13.5-slim | Sidecar Python sürümü | Performans iyileştirmeleri, new stdlib özellikler |
| `alpine` | 3.22 | 3.22.1 | Init konteynerleri | Güvenlik güncellemeleri |

### 4.3 Özel Durum: RustFS

| İmaj | Mevcut Sürüm | Önerilen Sürüm | Gerekçe | Son Değişiklikler |
|------|--------------|----------------|---------|-------------------|
| `rustfs/rustfs` | 1.0.0-rc.1 | 1.0.0-rc.1 (stable bekleniyor) | S3 API uyumluluğu | Dağıtık mod "under testing" - tek düğüm kullanılıyor |

**⚠️ Önemli Not:** RustFS beta/rc serisindedir. Sürüm takibi manuel yapılmalı ve stable çıkışında geçiş değerlendirilmeli.

---

## 5. Mimari İyileştirme Önerileri

### 5.1 Kritik Öneriler

#### 5.1.1 drain3 Fork Stratejisi

**Durum:** `drain3` PyPI'da güncel değil, upstream durgun.

**Öneri:**
- `logpai/Drain3` fork'u `bizigo` organizasyonu altına alınmalı
- Düzenli bakım ve PyPI yayın süreci oluşturulmalı
- Python 3.13+ uyumluluğu garanti edilmeli

**Risk:** Fork yapılmazsa, üçüncü taraf deponun erişilemez olması durumunda `pip install` başarısız olur.

#### 5.1.2 Redis Ayrıştırma Stratejisi

**Mevcut Durum:** İki ayrı Redis örneği (`redis-session` ve `redis`).

**Gerekçe:**
- `redis-session`: Oturum deposu, kalıcılık YASAK (token diske yazılmamalı)
- `redis`: Drain3 ağacı, kalıcılık ZORUNLU (`template_id` kayması riski)

**Öneri:** Mevcut yapı korunmalı. RDB anlık görüntüsü örnek tamamını yazdığından, veritabanı bazlı ayrıştırma yapılamaz.

### 5.2 Önerilen İyileştirmeler

#### 5.2.1 OpenAPI Tip Güncelleme Otomasyonu

**Mevcut Durum:** `ui/openapi/bizigo-api.json` ve `ui/src/lib/api/schema.d.ts` manuel güncelleme gerektiriyor.

**Öneri:** CI/CD pipeline'ına `npm run api:check` entegrasyonu, PR'lerde otomatik tip güncelleme uyarısı.

#### 5.2.2 Sigma SQL Derleme Tutarlılık Testi

**Mevcut Durum:** `tools/sigma-build/requirements.txt` ve `sidecar/requirements.txt` aynı sürümleri içermeli.

**Öneri:** Mevcut `test_requirements.py` testi genişletilmeli, CI pipeline'ına eklenmeli.

#### 5.2.3 Docker İmaj Güncelleme Politikası

**Mevcut Durum:** Sürümler manuel takip ediliyor.

**Öneri:**
- Dependabot veya Renovate entegrasyonu (Docker imajları için)
- Aylık güvenlik taraması (Trivy, Grype)
- `:latest` KULLANILMAMASI kuralı korunmalı

### 5.3 İsteğe Bağlı İyileştirmeler

#### 5.3.1 Testcontainers Modül Güncellemeleri

**Mevcut:** `Testcontainers.PostgreSql` ve `Testcontainers.ClickHouse` 4.14.0

**Öneri:** Testcontainers 5.x çıkışında geçiş değerlendirilmeli (yeni modüller, yaşam döngüsü yönetimi).

#### 5.3.2 Python 3.14 Hazırlığı

**Mevcut:** Python 3.13 kullanılıyor.

**Öneri:** Python 3.14 LTS çıkışında (Ekim 2025 beklenen) geçiş planı hazırlanmalı. `drain3` uyumluluğu test edilmeli.

---

## 6. Önceliklendirme Matrisi

### 6.1 Hemen Yapılmalı (1-2 Hafta)

| Öğe | Kategori | Risk | Efor |
|-----|----------|------|------|
| Next.js güvenlik güncellemesi | Güvenlik | Yüksek | Düşük |
| Pydantic güvenlik güncellemesi | Güvenlik | Yüksek | Düşük |
| FastAPI güvenlik güncellemesi | Güvenlik | Yüksek | Düşük |
| JWT Bearer paket güncellemesi | Güvenlik | Orta | Düşük |

### 6.2 Kısa Vadeli (1 Ay)

| Öğe | Kategori | Risk | Efor |
|-----|----------|------|------|
| ClickHouse.Driver güncellemesi | Performans | Orta | Orta |
| Redis kütüphane güncellemeleri | Uyumluluk | Orta | Düşük |
| Testcontainers güncellemesi | Test | Düşük | Düşük |
| OTel Collector güncellemesi | Gözlemlenebilirlik | Orta | Orta |

### 6.3 Orta Vadeli (3 Ay)

| Öğe | Kategori | Risk | Efor |
|-----|----------|------|------|
| drain3 fork ve PyPI yayını | Risk Azaltma | Yüksek | Yüksek |
| Docker imaj güncelleme otomasyonu | DevOps | Düşük | Orta |
| OpenAPI tip güncelleme otomasyonu | Developer Experience | Düşük | Orta |

### 6.4 Uzun Vadeli (6+ Ay)

| Öğe | Kategori | Risk | Efor |
|-----|----------|------|------|
| Python 3.14 LTS geçişi | Platform | Orta | Orta |
| RustFS stable geçişi | Depolama | Düşük | Düşük |
| Next.js 16 değerlendirmesi | Framework | Düşük | Yüksek |

---

## Ek: Breaking Changes Uyarıları

### .NET

- **Entity Framework Core 10:** `DateTimeOffset` karşılaştırmaları SQLite'ta çevrilememe sorunu çözüldü, ancak mevcut InMemory testleri etkilenmedi.

### Node.js

- **Next.js 15:** App Router kararlı, ancak Pages Router'dan geçiş yok (zaten App Router kullanılıyor).
- **React 19:** Server Components varsayılan, Client Components için `'use client'` directive gerekli.

### Python

- **pydantic 2.x:** v1'den v2'ye geçiş major breaking changes içerir (mevcut kod zaten v2 kullanıyor).
- **drain3:** Git SHA'sından kurulum özel durum yaratır, pip cache davranışı farklı olabilir.

### Docker

- **postgres:18:** Veri dizini `/var/lib/postgresql` altında sürüme özgü alt dizinde tutuluyor. Eski `/var/lib/postgresql/data` bağlaması reddediliyor.
- **Redis 8:** Yeni komutlar mevcut, ancak mevcut komutlar geriye uyumlu.

---

*Bu belge otomatik olarak analiz edilerek oluşturulmuştur. Güncellemeler için proje bağımlılık dosyalarını ve güvenlik bildirilerini takip ediniz.*

**Son Güncelleme:** 2025-08-23
