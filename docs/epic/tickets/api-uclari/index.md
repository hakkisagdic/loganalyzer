---
title: "T10 — API uçları"
kind: ticket
status: 2
---

# T10 — Sorgu ve yazma API uçları

**Bağımlılık:** T03, T09 · **Sonraki:** —
**Yöneten belgeler:** [F1 §10.2, §10.3](../../f1-teknik-plan/index.md)

## Amaç

F1'in dışarıya açılan yüzü. UI (F2), agent'lar ve MCP server (F4) hep buradan
geçecek — **tek kapı** ilkesi burada somutlaşıyor.

## Kapsam

### İçinde

| Uç | İş |
| --- | --- |
| `GET /v1/events` | Arama: zaman aralığı, kapsam, alan filtreleri, tam metin. Sayfalama (keyset), sıralama |
| `GET /v1/events/{id}/raw` | Ham bayta iniş; kapsam doğrulanır, nesne anahtarındaki `owner_group` kontrol edilir |
| `GET /v1/sources` · `POST /v1/sources` | Envanter okuma/yazma, CSV yükleme |
| `POST /v1/changes` | Değişiklik olayı yaz (RCA F3 için geçmiş biriktirir) |
| `POST /v1/parsers` · `POST /v1/parsers/{id}/test` | Katalog + canlı test (F2 editörünün API'si) |
| `POST /v1/replay` | Replay işi başlat (T11 servisini çağırır) |
| `GET /v1/health/pipeline` | `bound_ratio`, `parse_status` dağılımı, WAL derinliği, scrub durumu, sidecar devre kesici |

Ek olarak:

1. **Sorgu dili** — v1'de yapılandırılmış filtre (alan/operatör/değer) + tam metin.
Serbest SQL **açılmaz**; kapsam zorlaması delinir.
2. **Sayfalama** — keyset (`ts` + `event_id`), offset değil. Log verisinde offset
sayfalama derin sayfalarda çöker.
3. **Hız sınırı** — kullanıcı başına eşzamanlı sorgu ve süre tavanı.
[Risk #6](../../mimari-kararlar/index.md) (gürültülü komşu) F1'de de geçerli.
4. **OpenAPI** — üretilen şema; F2'nin istemci kodu buradan doğacak.

### Dışında

MCP server (F4), Sigma/detection uçları (F3), alert uçları (F2), UI'ın kendisi (F2).

## Kabul kriterleri

Her uç IScopedQuery'den geçiyor — mimari test bunu doğruluyorNegatif test: her uç için "başka grubun verisi" testi geçiyor/v1/events/{id}/raw kapsam dışı nesneyi indirmiyorKeyset sayfalama 1M satırda derin sayfada da sabit süredeTam metin araması TR/AR/CJK gövdede sonuç veriyor/v1/health/pipeline altı göstergeyi de veriyorOpenAPI şeması üretiliyor ve geçerliSerbest SQL kabul eden hiçbir uç yok

## Uygulama sonucu

| Uç | Durum |
| --- | --- |
| `POST /v1/events/search` · `GET /v1/events/{id}` | ✅ keyset sayfalama, alan filtreleri, tam metin |
| `GET /v1/events/{id}/raw` | ✅ manifest üzerinden ön ek araması (K29) |
| `GET /v1/sources` · `POST /v1/sources` · `POST /v1/sources/csv` | ✅ |
| `GET /v1/changes` · `POST /v1/changes` | ✅ |
| `GET /v1/health/pipeline` | ✅ altı gösterge |
| `GET /v1/parsers` · `GET /v1/parsers/{id}` · `POST /v1/parsers/try` | ✅ |
| `POST /v1/replay` | ✅ varsayılan kuru koşu |

**Parser ucu okuma-yalnız — bilinçli.** Ticket'ta `POST /v1/parsers` yazıyordu;
uçtan parser **yayınlamak** yapılmadı. Katalog bu fazda repodan geliyor ve sıcak
yeniden yükleme atomik; yayın ucu, taslak→inceleme→yayın akışı olmadan kataloğu
tek bir isteğin bozabileceği bir yere çevirirdi. O akış F2'nin işi. Yerine
`POST /v1/parsers/try` var: bir satırı kataloğa karşı deniyor, hiçbir şey yazmıyor.

`try` **yazar** rolü istiyor (`bizigo:author`), okuyucu değil — keyfi bir satırı
motora koşturmak veri okumak değil ama bedeli sınırsız bir hesaplama.
Dispatcher'la çalıştırıldığında hangi **kademenin** karar verdiğini de döndürüyor:
envanter bağı yerine literal filtreye düşen bir satır, parser doğru olsa bile
envanterin eksik olduğunu söylüyor.

Katalog uçlarına kapsam filtresi **uygulanmıyor**: katalog veri değil
yapılandırma, ve hangi parser'ların var olduğunu görmek kimsenin logunu görmek
değil.

**Bir tasarım düzeltmesi yapıldı.** Envanter listesi ilk yazımda kapsam filtresini
uç katmanında elle uyguluyordu — yani zorlama iki ayrı yerdeydi ve K17'nin
kaçındığı durum tam olarak buydu. `IScopedQuery.SearchSourcesAsync` eklendi,
filtre tek kapıya taşındı. Envanter ClickHouse verisi kadar kapsamlı: bir ekip
başka bir ekibin cihaz listesini görmemeli.

**Yazma da tek kapıdan geçiyor.** `IScopedQuery.WriteChangeAsync` çağıranın
yalnızca kendi kapsamındaki bir gruba yazabildiğini doğruluyor; aksi halde bir
ekip başka bir ekibin zaman çizelgesine olay düşürebilir ve RCA yanlış kanıtla
çalışırdı.

**Kapsam dışı olay 404 dönüyor, 403 değil.** 403 "böyle bir olay var ama
göremezsin" bilgisini sızdırırdı.

**CSV yüklemesi ya hep ya hiç.** Yarı yüklenmiş envanter, hangi cihazın hangi
gruba düştüğünü belirsiz bırakır ve o belirsizlik doğrudan kapsam hatasına döner.

**Doğrulama:** derleme 0 uyarı, birim testleri **317/317**. Yeni testler:
`ApiSurfaceTests` (API'nin nesne deposuna ve `EventWriter`'a doğrudan
erişemediği, `IScopedQuery`'nin her metodunun kapsam istediği, operatör
kümesinin kapalı olduğu) ve `ScopeNegativeTests` (8 negatif kapsam testi —
arama, tekil okuma, envanter, değişiklik yazma, kapsam daraltmasının
genişletememesi).

**Doğrulanmayanlar:** keyset sayfalamanın 1M satırda sabit süre verdiği
ölçülmedi; TR/AR/CJK tam metin araması uçtan uca denenmedi; OpenAPI şeması
üretiliyor ama geçerliliği bir araçla doğrulanmadı.

## Notlar

- Serbest SQL yasağı tartışmaya açık görünebilir ama K17'nin tek zorlama noktası
sorgu API'si. Serbest SQL açılırsa kapsam ayrımı arka kapıdan delinir. Analitik
ihtiyaç için F2'de sınırlı bir "kayıtlı sorgu" mekanizması düşünülebilir.
- `POST /v1/changes` küçük ama F3'ün RCA kalitesi buna bağlı — beslemeler F2'de
bağlanacak, uç F1'de hazır olmalı.
