---
title: "T01 — İskelet, geliştirme ortamı ve CI"
kind: ticket
status: 2
---

# T01 — İskelet, geliştirme ortamı ve CI

**Bağımlılık:** yok · **Sonraki:** T02, T05
**Yöneten belgeler:** [F1 teknik plan §12, §13](../../f1-teknik-plan/index.md) ·
[mimari kararlar K23, K25, K26](../../mimari-kararlar/index.md)

## Amaç

Sonraki 11 ticket'ın üstüne inşa edeceği zemin: çözüm düzeni, tek komutla ayağa
kalkan geliştirme ortamı, göç altyapısı ve CI. Bu ticket bittiğinde **hiçbir iş**
**mantığı yok** ama `docker compose up` + `dotnet test` yeşil.

## Kapsam

### İçinde

1. **Çözüm düzeni** — `net9.0` (makinede SDK 9.0.203; .NET 10 yok).
[F1 §12](../../f1-teknik-plan/index.md)'deki proje listesi birebir kurulur.
`Directory.Build.props` ile ortak ayarlar: `nullable enable`,
`TreatWarningsAsErrors`, `ImplicitUsings`, `LangVersion latest`.
`Directory.Packages.props` ile merkezi paket sürümü yönetimi.
2. **Git deposu** — `git init`, `.gitignore` (.NET + Python + yerel veri klasörleri).
**Commit atılmaz** — kullanıcı onayı olmadan commit/push yok.
3. **docker-compose geliştirme ortamı** — tek `up` ile: ClickHouse, PostgreSQL,
RustFS, Keycloak, OTel Collector, Python sidecar (boş iskelet). Sağlık kontrolleri
ve servisler arası `depends_on: condition: service_healthy`.
4. **Göç altyapısı**
  - **Postgres:** EF Core migrations. Bu ticket'ta yalnızca boş `ControlPlaneDbContext`
    - ilk (boş) migration + uygulama anında otomatik göç kancası.
  - **ClickHouse:** EF Core yok. Sıralı `.sql` dosyalarını çalıştıran küçük bir
runner + `schema_migrations` tablosu. Dosyalar `db/clickhouse/NNNN_ad.sql`.
5. **Testcontainers koşum takımı** — ClickHouse + Postgres + RustFS(S3) container'larını
ayağa kaldıran paylaşılan `IAsyncLifetime` fixture'ı. Bir "smoke" testi: her üçüne
de bağlanılıyor.
6. **CI** — GitHub Actions: `restore → build → test`. Docker gerektiren testler
`Category=Integration` ile etiketlenir ve CI'da da koşar.
7. **Türkçe kültür lint kuralı** — `ToLower()` / `ToUpper()` / kültür duyarlı
`string.Compare` kullanımını **hata** yapan analiz kuralı
(`.editorconfig`: CA1304, CA1305, CA1310, CA1311 → `error`).
Gerekçe: [F1 §2.4](../../f1-teknik-plan/index.md) — `tr-TR`'de `I → ı` aramayı
sessizce bozuyor.

### Dışında

Şema içeriği (T02), OTLP uç (T03), herhangi bir iş mantığı, deployment/monitoring.

## Kabul kriterleri

dotnet build sıfır uyarı (uyarılar hata) — 0 uyarı / 0 hataBirim testleri yeşil — 10/10 (SQL ifade ayırıcısı)Çözüm düzeni, CPM, .editorconfig, .gitignore, global.jsonEF Core Initial göçü üretildi; ClickHouse migration runner + sürüklenme tespiti yazıldıCI workflow'u yazıldı (build+birim / entegrasyon / compose doğrulaması olarak üç iş)docker compose config geçerli; imajların tamamı çekildi ve sürümleri sabitlendi⛔ docker compose up -d → yedi servis de healthy — engelli, bkz. §Engel⛔ Entegrasyon smoke testleri (Testcontainers) — aynı engel⛔ dotnet run --project src/Bizigo.Api göçleri uyguluyor — aynı engel

## Engel — Docker Desktop disk sınırı

Yığın ayağa kalkmadı. İki ayrı sorun çıktı:

1. **Docker VM diski dolu (asıl engel).** ClickHouse `/var/lib/clickhouse` için
`Total space: 7.78 GiB, Available space: 0.00 B` raporlayıp öldü; postgres,
rustfs ve otel-collector da aynı nedenle çıktı. Yığının imajları tek başına
**~3.2 GB**, makinedeki tüm imajlar **6.9 GB** — Docker Desktop'ın sanal disk
sınırı bu yığın için yetersiz. Ana makinede 36 GB boş var; sınır Docker
ayarında. **Kullanıcı kararı gerekiyor** (disk sınırını büyütmek veya başka
ajanların imajlarını temizlemek).
2. **RustFS izin hatası — düzeltildi.** `Permission denied (os error 13)`:
konteyner 10001:10001 ile koşuyor, adlandırılmış birim root'a ait oluşuyor.
Compose'a `rustfs-init` yardımcı servisi eklendi (upstream'in kendi çözümü).
Doğrulaması disk engeli kalkınca yapılacak.

Ayrıca **swap %77'ye çıktı** (CLAUDE.md eşiği %80). Yığın indirildi, build cache
temizlendi. Başkasının imajları körlemesine silinmedi.

## .NET sürümü — çözüldü

İlk taramada .NET 10 görünmedi ve `net9.0` seçilmişti; **yanlıştı.** `dotnet` PATH'te
`/usr/local/share/dotnet`'e çözülüyor ve orada yalnızca SDK 8.0.408 + 9.0.203 var.
arm64 **SDK 10.0.302** `~/.dotnet` altında duruyor (`/usr/local/share/dotnet/x64`
altındaki kopya Rosetta, kullanılmaz).

Yapılan: hedef `net10.0` (LTS), `global.json` → 10.0.302, EF Core 9.0.4 → **10.0.11**,
Npgsql EF → **10.0.3**, `dotnet-ef` → 10.0.11, göç yeniden üretildi.
Derleme **0 uyarı / 0 hata**, birim testleri **10/10**.

Geliştirici notu: `export PATH="$HOME/.dotnet:$PATH"` ya da `~/.dotnet/dotnet ...`.
`global.json` 10.0.302 istediği için yanlış muxer sessizce SDK 9'a düşmez, net hata verir.

## Notlar

- **Sürüm sabitleme zorunlu** (K25 riski): compose'da `latest` etiketi kullanılmaz.
RustFS ve OTel Collector sürümleri tam sabitlenir.
- Keycloak `start-dev` **kullanılmaz** — `start --optimized` + Postgres + `--import-realm`
([F1 §10.1](../../f1-teknik-plan/index.md)). Realm dosyası T09'da doldurulur; bu
ticket'ta minimum geçerli bir realm yeterli.
- `Bizigo.Storage.ClickHouse` projesi **internal**: T02'de NetArchTest kuralı gelecek,
bu ticket'ta yalnızca proje referans grafiği doğru kurulur (kimse doğrudan
referans vermez).
