---
kind: spec
title: "T01 — iskelet, geliştirme ortamı ve CI'da alınan kararlar"
---

# T01 — iskelet, geliştirme ortamı ve CI'da alınan kararlar

> Bu belge geriye dönük yazıldı: kaynağı kod, commit geçmişi ve F1 kapanışı.
> Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan gerekçeler
> kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen alternatifler
> kayıtta yok.

Ticket: [T01 — İskelet, geliştirme ortamı ve CI](../tickets/iskelet-ve-ci/index.md) ·
Yöneten kararlar: [K23, K25, K26](../mimari-kararlar/index.md)

**Commit geçmişi bu ticket için ayrım vermiyor.** T01'in kendi commit'i yok;
deponun ilk commit'i `e44807f` "Initial commit: F1 pipeline foundation (T01,
T02, T05)" üç ticket'ı birlikte taşıyor. Yani "hangi karar hangi sırayla
alındı" sorusunun cevabı kayıtta yok; aşağıdakiler **bugünkü koddan** okundu.

## 1 · Ticket ne yaptı

İş mantığı üretmeyen zemin: çözüm düzeni, merkezî paket/derleme ayarları,
`docker compose` geliştirme yığını, iki ayrı göç altyapısı (Postgres için EF
Core, ClickHouse için elle yazılmış runner), Testcontainers fixture'ı ve CI.

F1 kapanışı T01'i "14 proje, net10.0, Testcontainers, üç işli CI" diye
kaydetmiş. Bugün **17 proje** ve CI **altı iş** — büyüme F2/F3'te geldi, T01'in
kurduğu şekil değişmedi.

## 2 · Koddan okunan kararlar

### 2.1 Hedef çatı — bir düzeltme olarak duruyor

`Directory.Build.props` `net10.0`, `global.json` `10.0.302` istiyor. Ticket
metni bunun bir **düzeltme** olduğunu yazıyor: ilk tarama `dotnet`'i PATH'te
`/usr/local/share/dotnet`'e çözmüş, orada yalnızca SDK 8/9 görmüş ve `net9.0`
seçilmiş. arm64 SDK 10 `~/.dotnet` altındaymış.

`global.json` bir sürüm istediği için yanlış muxer **sessizce SDK 9'a düşmüyor,
net hata veriyor** — düzeltmenin kalıcı hâli bu satır. `rollForward:
latestFeature` seçiminin gerekçesi kayıtta yok.

### 2.2 Uyarı = hata, ve kültür kuralları o kapıdan geçiyor

| Ayar | Nerede | Koddan okunan gerekçe |
| --- | --- | --- |
| `TreatWarningsAsErrors` | `Directory.Build.props` | Yorumu kendi yazıyor: kabul kriteri "sıfır uyarı" idi. Kriteri **derlemeye** taşımak, kriterin sonradan aşınmasını imkânsız kılıyor |
| `AnalysisLevel=latest`, `EnableNETAnalyzers`, `EnforceCodeStyleInBuild` | aynı dosya | Gerekçesi kayıtta yok — ama `TreatWarningsAsErrors` ile birlikte, analizör bulgusunun derlemeyi kırması demek |
| CA1304/1305/1307/1310/1311/1862 → `error` | `.editorconfig` | Gerekçe dosyanın kendi başlığında: `tr-TR`'de `I → ı` ve `INTERFACE` gibi kelimeler **aramada sessizce eşleşmiyor**. Ürünün işi log araması olduğu için bu tuzak doğrudan ürünü bozuyor |
| CA1848 / CA2007 / CA1062 → `none` | `.editorconfig` | Üçünün de gerekçesi satır sonunda yazılı: sıcak yol dışında `LoggerMessage` zorunlu değil; ASP.NET Core'da `ConfigureAwait` gereksiz; null kontrolü nullable ile kapsanıyor |
| `tests/**` için CA1707, CA1861 → `none` | `.editorconfig` | Test adlarında alt çizgi ve test verisinde sabit dizi serbest |
| `CA1863` → `suggestion` | `.editorconfig` | **Gerekçesi kayıtta yok.** Kültür bloğunun içinde duruyor ama tek `suggestion` o |
| `InvariantGlobalization=false` | `Directory.Build.props` | **Gerekçesi kayıtta yok.** Kültür duyarlılığının ürün için taşıdığı anlamla tutarlı, ama bunu yazan bir yorum yok |
| `GenerateDocumentationFile=false` + `NoWarn=CS1591` | `Directory.Build.props` | Gerekçesi kayıtta yok |

**Kural kümesi ticket'ın istediğinden geniş.** Ticket CA1304/1305/1310/1311
sayıyor; `.editorconfig` bunlara **CA1307 ve CA1862**'yi de ekliyor. Genişlemenin
gerekçesi kayıtta yok.

### 2.3 İki göç altyapısı, tek sebep

Postgres EF Core migrations kullanıyor; ClickHouse kullanmıyor.
`ClickHouseMigrator`'ın sınıf yorumu gerekçeyi yazıyor: ClickHouse'un DDL'i
(`ORDER BY`, `PARTITION BY`, skip index, `CODEC`) ilişkisel göç araçlarına
sığmıyor ve **şemanın kendisi karar belgesinin parçası** — elle yazılmış SQL
okunabilir olmalı.

Runner'ın üç ayrı kararı var ve üçü de koddan okunuyor:

| Karar | Ne yapıyor | Gerekçe |
| --- | --- | --- |
| **Sürüklenme tespiti** | Uygulanmış bir dosyanın içeriği değişirse göç `InvalidOperationException` ile duruyor | Hata metni kendi gerekçesini taşıyor: "Uygulanmış göç dosyaları düzenlenmez — yeni bir göç dosyası ekleyin." Sessiz alternatif, şemanın dosyadan ayrışması olurdu |
| **CRLF normalizasyonu** | Sağlama alınmadan önce `\r\n → \n` | Yorumu yazıyor: satır sonu farkı göç sürüklenmesi sayılmasın. Yoksa Windows'ta bir kez açılan dosya bütün göçleri "değişmiş" gösterirdi |
| **`argMax(checksum, applied_at)`** | Aynı sürüm birden fazla kez kaydedilmişse en sonuncusu geçerli | Gerekçesi kayıtta yok — MergeTree'de tekilleştirme olmamasının doğal sonucu |

`SqlStatementSplitter` ayrı bir sınıf çünkü ClickHouse istemcisi **tek komutta
birden fazla ifade kabul etmiyor** (sınıf yorumu). Tırnak içi metin, tanımlayıcı
tırnakları ve yorumlardaki `;` ayraç sayılmıyor.

### 2.4 Test yığını geliştirme yığınıyla aynı sürümleri kullanıyor

`DevStackFixture` imaj sürümlerini sabit tutuyor
(`clickhouse-server:26.7`, `postgres:18-alpine`, `rustfs:1.0.0-rc.1`) ve
yorumu gerekçeyi yazıyor: sürümler `deploy/docker-compose.yml` ile aynı, **test
ile geliştirme ortamı ayrışırsa testin değeri düşer**. Aynı disiplin CI'ın
`sigma-explain` işinde de tekrarlanıyor — orada da ClickHouse sürümü compose ile
eşleniyor ve gerekçe yazılı.

Sürüm sabitleme ticket'ta K25 riskine bağlanmış: `latest` etiketi
kullanılmıyor.

### 2.5 CI — `actions/setup-dotnet` kullanılmıyor

Üç iş yerine bugün altı iş var (`build`, `ui`, `sigma-build`, `sigma-explain`,
`integration`, `compose`), ama T01'in koyduğu desen duruyor: **her iş .NET SDK'yı
resmî `dotnet-install.sh` betiğiyle kuruyor.** Gerekçe yaml'ın içinde yazılı ve
ölçülmüş: `actions/setup-dotnet` `codeload.github.com`'dan iniyor, GitHub orayı
sınırlandırdığında iş **kurulumda** ölüyor — tek test koşmadan, ilgisiz bir hata
mesajıyla. Tek oturumda üç kez olmuş (429/503). Betik Microsoft CDN'inden
iniyor.

`compose` işindeki `|| { logs; exit 1; }` kalıbının da gerekçesi yazılı:
`up --wait` düştüğünde yalnızca "container X exited (1)" yazıyor ve konteynerin
**kendi** hata mesajını yutuyor.

## 3 · Bugün ayakta duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `TreatWarningsAsErrors` + `.editorconfig` kültür kuralları | `ToLower()` yazan bir satır **derlemeyi kırıyor**. Bekçi CI'ın `build` adımında da aynı şekilde çalışıyor (yaml yorumu bunu açıkça söylüyor) |
| `ClickHouseMigrator` sağlama karşılaştırması | Uygulanmış bir `.sql` dosyasının düzenlenmesi. Şemanın dosyadan sessizce ayrışması bu yüzden mümkün değil |
| `global.json` | Yanlış SDK ile derleme. Sessizce SDK 9'a düşmüyor |
| `SqlStatementSplitterTests` (birim) | Ayırıcının tırnak/yorum davranışı. T01'in kabul kriterindeki "10/10 birim testi" buydu |
| CI `compose` işi | Yığının gerçekten ayağa kalkması + sağlıksız servis taraması + `down -v` temizliği |
| CI `integration` işi | Testcontainers paketinin CI'da **koşuyor** olması — ticket'ın `Category=Integration` niyeti, ayrı bir iş olarak |

**Bu bekçilerin hiçbirinin kırmızı yanabildiğini bu turda ölçmedim.** Belge
geriye dönük; ölçüm yapılmadı, kod okundu.

## 4 · Açıkta kalanlar

**Ticket üç kabul kriterini ⛔ ile bırakmış** — compose yığınının ayağa kalkması,
Testcontainers smoke testleri ve `dotnet run` ile göç uygulanması. Engel Docker
Desktop'ın sanal disk sınırıydı (ClickHouse `/var/lib/clickhouse` için
`Available space: 0.00 B` raporlayıp ölmüş; yığının imajları ~3.2 GB) ve
ticket'ta **kullanıcı kararı gerektiği** yazıyor.

Bugünkü durum: üçünün de karşılığı CI'da bir iş olarak **duruyor** — `compose`
yığını `--wait` ile kaldırıyor, `integration` Testcontainers paketini koşturuyor,
`sigma-explain` göçleri ürünün kendi migrator'ıyla uyguluyor. Yani kriterler
yerel makineden CI'a taşınmış. **CI'ın bu işlerinin yeşil olduğunu ölçmedim** —
yalnızca işlerin var olduğunu ve neyi koşturduklarını okudum.

| Kalem | Durum |
| --- | --- |
| `rustfs-init` izin düzeltmesinin doğrulaması | Ticket "disk engeli kalkınca yapılacak" diyor. Bugün compose'da ne olduğuna bu belgede bakılmadı |
| `CA1863 = suggestion`, `InvariantGlobalization=false`, `rollForward: latestFeature` | Gerekçeleri kayıtta yok |
| Kültür kural kümesinin ticket'tan geniş olması (CA1307, CA1862) | Gerekçesi kayıtta yok |
| T01 kararlarının sırası ve reddedilen alternatifler | Commit geçmişi ayrım vermiyor (§ başı) |
