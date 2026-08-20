---
title: "T11 — Replay ve dry-run fark raporu"
kind: ticket
status: 2
---

# T11 — Replay ve `--dry-run` fark raporu

**Bağımlılık:** T04, T06 · **Sonraki:** —
**Yöneten belgeler:** [F1 §7.2](../../f1-teknik-plan/index.md) · [K12](../../mimari-kararlar/index.md)

## Amaç

Parser düzeltildiğinde geçmişi yeniden işlemek. Cribl'ın en çok satan özelliği;
sonradan eklenmesi çok pahalı olduğu için F1'de.

## Kapsam

### İçinde

1. **Replay akışı** — ham nesneleri seç → **sabitlenmiş parser sürümüyle** yeniden
ayrıştır → gölge tabloya yaz → `ALTER TABLE … REPLACE PARTITION`.
Granülerlik = 1 gün. Atomik; sorgu tarafında `FINAL` maliyeti yok.
2. **`--dry-run` fark raporu** — çalıştırmadan önce: kaç satır değişecek, kaç
`failed` → `ok` olacak, kaç alan farklı çıkacak, örnek farklar.
**Bu olmadan replay korkutucu bir düğmedir ve kimse basmaz.**
3. **Manifest doğrulaması** — replay aralığındaki nesneler manifest'e karşı kontrol
edilir. Eksik nesne varsa replay **sessizce kısa dönmez**, eksik aralığı bildirir
ve kullanıcıya devam edip etmeyeceğini sorar.
4. **`parse_generation`** — hangi satırın kaçıncı kuşaktan geldiği denetlenebilir.
5. **CLI + API** — `bizigo replay --from … --to … --parser …@… [--dry-run]` ve
`POST /v1/replay`. İş durumu izlenebilir (kuyruk, ilerleme, hata).
6. **Kaynak filtresi** — replay tüm partition yerine belirli `source_id`/`owner_group`
ile sınırlanabilir; ama `REPLACE PARTITION` bölüm bütünlüğü gerektirdiği için
filtreli replay'in nasıl birleştirileceği çözülmeli (öneri: filtre dışı satırlar
gölge tabloya **değiştirilmeden** kopyalanır).

### Dışında

Otomatik replay tetikleme (parser yayınlanınca), tiering/lifecycle (v2).

## Kabul kriterleri

--dry-run gerçek çalıştırmayla aynı sonucu öngörüyor (fark raporu doğru)Manifest'te eksik nesne varsa replay uyarıyor, sessizce eksik veri üretmiyorReplay sırasında canlı ingest bozulmuyor; sorgular tutarlı sonuç veriyorFiltreli replay bölüm bütünlüğünü koruyor — filtre dışı satırlar kaybolmuyorAynı replay iki kez çalıştırılabiliyor (idempotent)

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| ClickHouse tarafı | `src/Bizigo.Storage.ClickHouse/ReplayStore.cs` — gölge tablo, `REPLACE PARTITION`, bölüm okuma |
| Motor | `src/Bizigo.Replay/ReplayEngine.cs` |
| Fark mantığı | `src/Bizigo.Replay/ReplayDiff.cs` (saf, veritabanısız sınanabilir) |
| API | `src/Bizigo.Api/ReplayEndpoints.cs` — `POST /v1/replay` |

**Dört tasarım noktası:**

1. **Kuru koşu ile gerçek çalıştırma AYNI akışı koşuyor**, tek fark son adımın
atlanması. İki ayrı yol yazmak, raporun gerçeği öngörmediği bir gün getirirdi —
ve o günü kimse fark etmezdi, çünkü raporu kontrol etmenin yolu raporun kendisi.
2. **`ingested_at` ve `parse_generation` fark sayılmıyor.** İkisi de her
replay'de değişir; sayılsalardı rapor "her satır değişti" der ve gerçek farkları
görünmez kılardı.
3. **Sürümsüz parser sabitlemesi reddediliyor** (`400`). "En güncel parser" ile
koşmak, aynı komutun iki ay sonra farklı sonuç vermesi demek.
4. **Varsayılan kuru koşu.** `dryRun` gönderilmezse rapor üretilir, yazma
yapılmaz. Varsayılanı "uygula" yapmak, bir alan unutulduğunda üretim verisini
değiştirirdi.

**Eksik nesne davranışı:** manifest'te olup arşivde bulunmayan nesne varsa replay
**duruyor** ve `409` ile eksik listesini döndürüyor. Devam etmek açık bir bayrak
istiyor. Sessizce kısa dönmek, manifest'in (K25 koruma #4) var olma sebebini
ortadan kaldırırdı.

**Filtreli replay:** filtre dışı satırlar gölge tabloya **değiştirilmeden**
kopyalanıyor. `Filtre_disi_satirlar_kopyalanmazsa_kayboluyor` testi tuzağı
açıkça belgeliyor — kopyalanmayan satır `REPLACE PARTITION` ile siliniyor.

**Yapılmayan: CLI komutu.** `bizigo replay` için CLI'nin tüm DI grafiğini
(ClickHouse + Postgres + S3 + parser kataloğu) barındırması gerekiyor; şu an
yalnızca `ParserToolbox` ve tek bağlantılı `migrate` var. API ucu aynı yeteneği
veriyor, o yüzden CLI ertelendi.

**Doğrulanmayan:** replay sırasında canlı ingest'in bozulmadığı ölçülmedi
(`REPLACE PARTITION` atomik olduğu için beklenen doğru davranış, ama yük altında
sınanmadı). Kuru koşunun gerçek çalıştırmayla birebir aynı sonucu verdiği de
uçtan uca tek testte gösterilmedi — parçalar ayrı ayrı test edildi.

## Notlar

- `ReplacingMergeTree(parse_generation)` alternatifi değerlendirildi ve elendi: satır
granülerliğinde şık ama her sorguya `FINAL` maliyeti biner
([F1 §7.2](../../f1-teknik-plan/index.md)).
- Filtreli replay'in "filtre dışı satırları kopyala" çözümü partition başına tam
yeniden yazım demek. Kabul edilebilir çünkü replay nadir bir işlem — ama süresi
ölçülmeli ve dry-run raporunda gösterilmeli.
