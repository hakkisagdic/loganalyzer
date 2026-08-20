---
title: "T15/T16 sonrası açık kalan üç karar — kapandı"
kind: spec
---

# T15/T16 sonrası üç karar — hepsi kapandı

Bu belge T15/T16 birleştiğinde açık kalan üç kararı tutuyordu. **Üçü de
karara bağlandı**; kayıt olarak duruyor çünkü gerekçeler koddan okunamıyor.

> Kapanış anındaki `main`: `48d8d1c` + T17 dalı. Sonrasında değişenler burada
> yansımıyor.

## 1. Kayıtlı arama — `localStorage` kalıyor ✅

**Soru:** T21'in alarm kuralı kayıtlı aramaya mı bağlanacak?

**Cevap: hayır.** `AlertRuleEntity.SearchJson` kuralın aramasını **kendi
satırında gövde olarak** taşıyor (`AlertSearch` → `AlertSearchCodec`); kayıtlı
arama tablosuna referans yok ve T21 bunu bilerek böyle yapmış. Ticket'taki
"T21'in alarm kuralları buna bağlanacak" cümlesi bayat bir tahmindi.

Sonuç: kayıtlı aramalar tarayıcıda kalıyor, ekrandaki "yalnızca bu tarayıcıda"
notu doğru. Sunucu tarafı kayıtlı arama için bir sebep kalmadı.

### Kalan risk: "bir arama" iki yerde temsil ediliyor

`ui/src/lib/events/criteria.ts` içindeki `PARAM` kümesi ile
`src/Bizigo.Alerting/AlertSearch.cs` ayrışırsa, ekrandan kurulan alarm ekranda
görülen sonuçtan başkasını izler ve **hiçbir yerde kırmızı yanmaz**.

Bugünkü karşılıklar `criteria.ts`'in başındaki tabloda yazılı. Özeti: tam metin,
kaynak, `parse_status` ve alan filtreleri birebir eşleşiyor (aynı `FieldFilter`
üçlüsü, aynı operatör beyaz listesi); **grup daraltması ve zaman aralığı
`AlertSearch`'te yok**. Ekran bir gün "bu aramadan alarm kur" düğmesi kazanırsa
düşen o iki alanı kullanıcıya söylemek zorunda.

## 2. T27'nin kabul kriteri — liste ikiye bölündü ✅

Tek liste iki farklı şeyi taşıyordu ve bu yüzden "liste boşaldı mı" sorusunun
cevabı asla evet olamıyordu.

| Liste | Anlamı | Boşalır mı |
| --- | --- | --- |
| `Pending` | henüz tipsiz, ekran indikçe çıkacak | **evet** — T27'nin kriteri |
| `Exempt` | hiç tüketicisi olmayacak | hayır; sayısı `ExpectedExemptCount` ile sabit |

Muafiyet bedava değil: listeye bir satır eklemek sabiti de değiştirmeyi
gerektiriyor, yani kaçış kapısı sessizce genişleyemiyor. Bugünkü muaflar —
`POST /v1/logs`, `POST /v1/changes/webhooks/{endpointId}` ve üç `DELETE` (204,
gövdesiz).

T27'nin kabul kriteri buna göre güncellendi.

## 3. Bekçinin yapısal açıkları — ikisi de kapandı ✅

**Birinci açık** (`f342aaa` ile bulundu, T17'de kapatıldı): kapı uçları **elle
yazılmış bir `Map*` listesinden** topluyordu. T21/T22/T24 indiğinde 16 uç kapıya
hiç görünmedi ve üç testin üçü de geçti — bir bekçinin en tehlikeli başarısızlık
biçimi, çünkü yeşildi ve yeşilliği hiçbir şey ifade etmiyordu.

Artık `Bizigo.Api` derlemesindeki her `IEndpointRouteBuilder` uzantısı
**yansımayla bulunup çağrılıyor**. Unutulacak bir liste yok. Beklenmeyen imzalı
bir `Map*` sessizce atlanmıyor, açık bir hatayla düşüyor — aksi hâlde aynı delik
başka kılıkta geri gelirdi.

**İkinci açık** (aynı turda kapatıldı): `V1Endpoints()` yalnızca `/v1/` önekine
bakıyordu, yani bir gün açılacak `/v2/` ya da önek dışı bir ürün ucu sessizce
kapsam dışı kalırdı. Önek filtresi kaldırıldı; denetlenen küme artık "uç
dosyalarının kaydettiği her şey". `/internal/*`, `/healthz` ve `/` zaten
`Program.cs` içinde satır içi kayıtlı, bir `Map*` uzantısından geçmiyorlar ve bu
kümeye hiç girmiyorlar. Yan kazanç: `/auth/me` de artık denetleniyor.

### Ölçüm

Kapının kırmızı yanabildiği, tam olarak **eskiden sessiz kalan senaryoyla**
sınandı: kimsenin listelemediği yeni bir uç dosyası eklendi
(`ZzTempProbeEndpoints`, tipsiz bir uç). İki test birden düştü — biri yeni
dosyayı gördü, biri tipsiz ucu. Dosya silindi, kapı yeşile döndü.

Muafiyet sayacı da ayrıca sınandı: listeye bir satır eklemek
`Muafiyet_listesi_sessizce_buyuyemez` testini düşürüyor.
