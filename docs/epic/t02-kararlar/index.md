---
kind: spec
title: "T02 — Depolama ve kapsam kapısı: kararlar ve açıkta kalanlar"
---

# T02 — Depolama şemaları ve kapsam kapısı

> ⚠️ **Bu belge geriye dönük yazıldı.** Kaynağı kod, commit geçmişi ve F1
> kapanışı. Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan
> gerekçeler kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen
> alternatifler kayıtta yok.

**Yöneten kararlar:** K17, K23, K8 · **Ticket:** `tickets/depolama-ve-kapsam-kapisi`

> **Numara notu:** koordinatör bu ticket'ı "T07" diye verdi; ticket'ın kendisi
> **T02**. `tickets/normalizasyon` T07. Belge dizinin gerçek numarasıyla
> adlandırıldı.

## Ne yaptı

İki depolama düzlemi ve **kapsam ayrımının tek kapısı**. F1'in en geri
alınamaz kararı burada: `ORDER BY` bir kez seçilir, tablo yeniden yazılmadan
değişmez.

Bu ticket'ın ürettiği şeylerin çoğu bugün hâlâ ayakta ve **fazlar boyunca
değişmedi** — bu, geriye dönük bakınca en görünen özelliği.

## Kodda görünen kararlar

### Kapsam **kaynaktan** gelir, olaydan değil

`AccessScope` bunu tipin ilk cümlesinde söylüyor. Kullanıcının IdP grupları
`idp_group_mapping` üzerinden `owner_group` kümesine çevriliyor; olayın kendi
içeriğinden kapsam türetilmiyor.

### Kapalı başlar

```csharp
public bool IsEmpty => !IsUnrestricted && OwnerGroups.Count == 0;
```

Ve `ToSqlFragment` boş kapsamda `"0"` üretiyor — `"1"` değil, filtre yokluğu
değil. Yorumun ifadesi: *"boş kapsam sessizce 'filtre yok'a dönüşmüyor."*

Bu, §7'nin tarif ettiği hata sınıfına karşı alınmış bir önlem: boş kapsamın
"her şey" anlamına gelmesi hiçbir hata üretmeden her satırı sızdırırdı.

### Daraltma kapsamı **genişletemez**

`ScopePredicate.From(scope, narrowTo)` kesişim alıyor. Kullanıcı sorgusunda
kapsamında olmayan bir grup istese bile sonuç boş kalıyor — istek bir **filtre**,
bir yetki değil.

Sınırsız kapsamda daraltma yine de uygulanıyor: admin de "yalnızca şu grubu
göster" diyebiliyor.

### Kapıyı atlamak **derlenmiyor**

Üç katman üst üste:

1. `IScopedQuery`'nin her metodu `AccessScope` istiyor — kapsamsız çağrı
   yazılamıyor.
2. `ScopePredicate` yalnızca bir `AccessScope`'tan üretilebiliyor ve okuma
   metotları onu parametre olarak istiyor.
3. `ArchitectureTests` `ClickHouse.Driver`'a bu derlemenin **dışından**
   referans verilmesini yasaklıyor.

Üçüncüsü olmadan ilk ikisi kâğıt üstünde kalırdı: biri kolaylık olsun diye
`Bizigo.Api`'de bir bağlantı açar, filtre atlanır ve kimse fark etmez. Testin
yorumu bunu açıkça yazıyor: *"amacı insanı yakalamak değil, o yolu baştan
kapatmak."*

### ClickHouse row policy **tercih edilmedi**

`IScopedQuery`'nin yorumunda gerekçesiyle duruyor: tek kapı olması gereken yer
uygulama katmanı, çünkü F3'ün kanıt toplayıcısı ve F4'ün MCP sunucusu da aynı
API'yi kullanacak. Row policy yalnızca SQL'den geçeni korurdu.

### `ORDER BY (owner_group, source_id, ts)`

DDL'in başındaki blok bu deponun en iyi yazılmış karar notu — çünkü **üç adayın
tabloya bağlandığını** ve neden bunun seçildiğini birlikte veriyor:

- Her sorgu API'den zorunlu `owner_group` filtresiyle geliyor (K17), yani daima
  ön-ek taraması.
- `owner_group` kardinalitesi düşük (onlarca), sonraki kolonların sıkıştırmasını
  bozmuyor.
- `ts` sıralama anahtarının sonunda kaldığı için "son 15 dk, tüm grubum"
  sorgusunun bedeli `idx_ts` (minmax) ile kapatılıyor.

F1 bu ölçümü sonradan yaptı ve sonuç **koşullu** çıktı: keyset sayfalama ancak
`owner_group` + `source_id` verildiğinde sabit süreli. Bu, ORDER BY'ın doğrudan
sonucu ve F2'de **ekrana** yansıdı — kaynak filtresi teşviki ve alarm
bağlantısının kaynak taşıması buradan geliyor, kolaylıktan değil.

### `attrs` → `Map`, JSON değil

Ticket'ın kendi karar notunda: F1'de ayrıştırılan alanlar zaten string, tipli
sıcak alanlar core kolonlarında, `mapKeys` üzerinde bloom filter var. JSON
kararı **gerçek kardinalite verisiyle** yeniden değerlendirilmek üzere
ertelendi.

### `lowerUTF8()` yok — Türkçe yüzünden

Tam metin indeksinde `preprocessor` kullanılmadı: `lowerUTF8()` Türkçe `İ/ı`'da
**bayt uzunluğunu değiştiriyor** ve skip index'te bu **yanlış negatif** demek —
yani var olan satırın hiç bulunamaması.

Bedeli kabul edildi: arama şimdilik büyük/küçük harf duyarlı. Duyarsız aramanın
kararı ölçüme bırakıldı.

Bu, sessiz yanlışın gürültülü eksikliğe tercih edildiği açık bir örnek.

### `sparseGrams(3, 20, 5)` — ve sayıların açık kalem olduğu yazılı

DDL'de `⚠️ AÇIK KALEM` etiketiyle duruyor: üç sayı gerçek gövdelerle ölçülüp
indeks boyutuna göre kesinleştirilecek. F1 ölçtü ve sonucu **koşullu**: indeks
~10-11 karakterden sonra seçici, alfabeden bağımsız. F2'de ekranda kısa sorgu
uyarısına dönüştü.

### Eşleşmeyen kaynak reddedilmiyor

`_unassigned` grubuna düşüyor, sağlık uyarısı üretiyor ve
`bizigo.unassigned_source_key` ile hangi anahtarın eşleşmediği **saklanıyor**.
Gerekçe F1 §8'de: veri kaybı, eksik envanterden kötüdür.

Kapsamı olan bir grup adı olduğu için `_unassigned` verisi de kapsam kapısından
geçiyor — "sınıflandırılamayan" ile "herkese açık" ayrı şeyler.

### Tek uzun ömürlü ClickHouse istemcisi

`ClickHouseBulkCopy` 1.3.0'da kullanımdan kalkmış → `InsertBinaryAsync`. Ayrıca
`ClickHouseContext` tek istemci tutuyor: bağlantı başına `HttpClient` soket
tüketirdi.

### Kontrol düzleminde `snake_case`

`EFCore.NamingConventions`. Gerekçe: elle yazılan SQL ve `psql` oturumları
okunabilir kalsın. (API'nin tel sözleşmesindeki `snake_case` kararı ayrı ve
`JsonPropertyName` ile — §8.)

## Bugün duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `ArchitectureTests` (5) | Sürücü referansının tek derlemeye kapalı olması; API'nin okuyuculara doğrudan erişememesi; `Contracts`'ın altyapıdan bağımsızlığı; **DI grafiğinin kapsam doğrulamasından geçmesi** |
| `ScopeNegativeTests` (12, entegrasyon) | Başka grubun olayının arama, kimlikle okuma, OCSF/OTel görünümü, envanter, son görülme ve değişiklik yazımı yollarının **hiçbirinden** dönmediği; daraltmanın kapsamı genişletemediği; kapsam dışı sayımının içerik sızdırmadığı |
| `StorageSchemaTests` (9, entegrasyon) | Şemanın idempotent kurulduğu, boş kapsamın hiçbir satır döndürmediği, çok dilli alt dizi araması, izin listesi dışı alan filtresinin reddi, keyset sayfalamanın tekrar/atlama yapmadığı, toplu yazım hızı |

`ScopeNegativeTests`'in on iki ayrı yolu tek tek denemesi bu ticket'ın vaadinin
şekli: kapsam kapısı "bir yerde doğru" değil, **her yolda** doğru olmak
zorunda. Yeni bir okuma yolu eklendiğinde bu listeye bir satır eklenmesi
gerekiyor — ve bu, elle bakıma bağlı kalan yerlerden biri (§7'nin elle liste
kalıbı).

### Sonradan eklenen bekçi: singleton → scoped

`Uretim_DI_grafi_kapsam_dogrulamasindan_geciyor` bu ticket'ın değil, T26'nın
kusurundan doğdu: singleton bir servis `IScopedQuery` tutuyordu, o da
`ControlPlaneDbContext` taşıyor. Üretimde `ValidateScopes` kapalı olduğu için
**sessizce yaşadı**; ortaya çıkaran şey T14'ün OpenAPI belge üretiminin `Main`'i
gerçekten çalıştırması oldu.

Kapsam kapısının derleme zamanı korumaları bu sınıfı yakalamıyordu: kapıdan
geçiyordu, ama kapının **arkasındaki** nesne paylaşılıyordu.

## Açıkta kalanlar

| # | Ne | Durum |
| --- | --- | --- |
| 1 | **`attrs` JSON tipine geçer mi** | Ticket'ta "gerçek kardinalite verisiyle yeniden değerlendirilecek" diye ertelendi; ölçüm yapılmadı |
| 2 | **`sparseGrams(3, 20, 5)`** | Açık kalem olarak DDL'de duruyor. F1 seçicilik eşiğini ölçtü (~10-11 karakter) ama **indeks boyutu** ölçülmedi — üç sayının seçilme sebebi olan asıl soru |
| 3 | **Büyük/küçük harf duyarsız arama** | Karara bağlanmadı; `lowerUTF8()` reddedildi, alternatif aranmadı |
| 4 | **1M satır bulk insert** | Kabul kriteri 1M diyor; test **100k** koşuyor ve bunu yorumunda gerekçesiyle söylüyor ("CI'da tam 1M pahalı"). Sayı `TestOutputHelper`'a yazılıyor ama **hiçbir eşikle karşılaştırılmıyor** — tek `Assert` satır sayısında. Yani hız iki katına yavaşlasa test yeşil kalır |
| 5 | **Yeniden deneme** | Ticket "yeniden deneme + kısmi başarısızlık davranışı tanımlı" istiyor. Kısmi başarısızlık **tanımlı**: batch düşürülüyor, `_dropped` sayacı artıyor, hata seviyesinde loglanıyor ve *"veri ham arşivde, replay gerekebilir"* deniyor — yani kayıp kapatılabilir bir kayıp. Ama **yeniden deneme yok**; anlık bir ClickHouse kesintisi bir batch'i doğrudan replay'e havale ediyor. Bunun bilinçli sadelik mi olduğu **kayıtta yok** |
| 6 | **`ORDER BY`'ın üç adayı** | Seçilenin gerekçesi DDL'de tam; **reddedilen iki adayın ne olduğu** F1 §6.2'de kaldı, kodda yok |

Dördüncüsü, bu fazın öğrendiği kalıbın bir örneği: ölçülen ama **kırmızı
yanamayan** bir sayı, ölçülmemiş sayıdan yalnızca biraz iyi.
