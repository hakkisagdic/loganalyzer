---
kind: spec
title: "T32 tasarımını okurken çıkan üç açık soru — T33'ü doğrudan bağlıyor"
---

# T32 → T33: üç açık soru

[T32 derleme tasarımı](../t32-derleme-tasarimi/index.md) okunurken çıktı. Üçü de
tasarımın **yanlış** olduğunu söylemiyor; ikisi tasarımda cevabı olmayan bir
soru, biri T33 ticket'ının kriterinde bir boşluk.

Buraya yazılmalarının sebebi §11: ajanlar ayrı worktree'lerde ve **yalnızca
depoyu okuyabiliyorlar**. Bir mesaj dizisinde duran bulgu, onu okuması gereken
ajana ulaşmıyor.

Yazan: T36'yı yapan ajan. Kararlar T32 ve T33'ün sahiplerinin.

---

## 1 · `remedy`'nin üç değeri de **birinin yapabileceği bir işi** adlandırıyor

Tasarım `blockers` girdisini şöyle tanımlıyor: `{ kind, column, message, remedy }`,
`remedy` ∈ `schema` / `pipeline` / `data`.

Üçü de bir **sahip** ve bir **eylem** gösteriyor. "Kimse yapamaz" için bir değer
yok. Oysa `kind` içinde `unsupported_construct` var ve o, tanımı gereği bizim
kapatamayacağımız sınıf olabilir: Sigma kuralı ClickHouse backend'inin
desteklemediği bir yapı kullanıyorsa, yukarı akış ya da backend değişmeden asla
derlenmez.

`unsupported_construct` zorla `remedy: pipeline`'a yazılırsa **gated listesinin
tamamı kapanabilir görünür ve hiç kapanmaz.**

Bu, `CLAUDE.md` §8'in adını koyduğu desenin aynısı:

> "Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz. İkisi tek
> listedeyken "liste boşaldı mı" sorusunun cevabı asla evet olamaz.

`Pending` ile `Exempt`'in ayrı durmasının sebebi buydu ve `Exempt`'in sayısı
`ExpectedExemptCount` ile sabitlenmişti — muafiyet eklemek **iki ayrı bilinçli
hareket** gerektirsin diye.

**Öneri (T32'nin kararı):**

- `remedy`'ye dördüncü değer: `upstream` ya da `none` — kapatılamaz olanı
  adlandıran bir değer.
- `ExpectedGatedCount` yerine **iki sabit**: kapanabilir olanlar (azalması
  beklenen) ve kapanamayanlar (sabit kalması beklenen).

**T33 tarafındaki karşılığı:** gated bölümü `remedy`'ye göre gruplanmalı.
*"31'i şema bekliyor, 11'i asla derlenmeyecek"* iki farklı cümle; ikincisi
kullanıcıya **kapsam sınırı** olarak sunulmalı, iş kalemi olarak değil.

---

## 2 · T33'ün kriteri iki değişiklik olayından yalnızca birini kapsıyor

T33 ticket'ı: *"Kaynak kural sürümü değiştiğinde kullanıcı bunu görüyor."*

T32 tasarımı **iki** olay tanımlıyor ve manifest ikisini mekanik olarak
ayırıyor:

| Olay | Manifest'te | T33 kriteri kapsıyor mu |
| --- | --- | --- |
| Kaynak kural değişti | `source_sha` **ve** `output_sha` değişir, `pipeline_sha` aynı | ✅ evet |
| **Pipeline değişti** | `pipeline_sha` değişir, `source_sha` **aynı**, `output_sha` değişir | ❌ **hayır** |

İkincisi şu demek: kullanıcının **etkin** bir kuralının ürettiği SQL değişti,
yani ne yakaladığı oynadı — ama kaynak kural hiç değişmedi, dolayısıyla T33'ün
kriteri sessiz kalıyor. Kullanıcı açtığı kuralın hâlâ aynı şeyi yakaladığını
sanıyor.

Tasarım bu soruyu zaten soruyor ve cevaplıyor — *"269 dosyadan hangisinin anlamı
gerçekten oynadı"* → `output_sha` değişen girdilerin listesi. Yani **veri var,
T33'ün kriteri onu istemiyor.**

Bu, F1'in beş kez ısırdığı şeklin aynısı: bir şey değişti, hiçbir yerde hata
yok, fark eden olmadı.

**Öneri (T33'ün kararı):** kriter *"kaynak kural sürümü değiştiğinde"* değil
**"kuralın ürettiği SQL değiştiğinde"** olmalı. Tek ölçüt `output_sha`; sebebin
kaynak mı pipeline mı olduğu `source_sha`'nın değişip değişmediğinden okunuyor.
Tek yerden akıyor, ikinci bir bildirim yolu gerekmiyor, iki olay da kapanıyor.

---

## 3 · `failed` T33 ekranında görünmemeli, ve gated sayısı tek kaynaktan gelmeli

Tasarım `gated` ile `failed`'i ayırıyor: `gated` derleniyor ama koşmuyor,
`failed` **derlenemiyor** — yani pipeline kırık, `failed` için sabit sıfır ve CI
kırmızı.

Buradan iki şey çıkıyor:

**`failed` bir kural durumu değil, bizim build'imizin durumu.** T33'ün gated
bölümüne karıştırılırsa kullanıcı bizim hatamızı kapsam sınırı sanır. Zaten
üretime çıkmıyor — CI onu geçirmiyor.

**`ExpectedGatedCount` sabiti ile ekranın gösterdiği sayı aynı sayı olmalı,
aynı kaynaktan.** T33 ekranı filtreleyip gösterirse (ör. yalnızca etkin
vendor'ların gated'leri), çivilenmiş sabit ile kullanıcının gördüğü sayı
ayrışır — ve bekçi, kullanıcıya görünen iddiayı artık korumaz. Bekçinin
koruduğu şey ile kullanıcının okuduğu şey aynı olmalı.

---

## Bir de durum sayısı: T33'te **üç** var, iki değil

| Durum | Ne | Kimin kararı |
| --- | --- | --- |
| `etkin` | Koşuyor | Kullanıcı |
| `pasif` | Koşmuyor, **koşabilirdi** | Kullanıcı |
| `gated` | Koşamaz — SQL üretilemedi | **Yetenek sınırı** |

`pasif` ile `gated`'i tek "kapalı" değerine indirgemek, *"kullanıcı istemedi"*
ile *"biz yapamadık"*ı karıştırmak demek.

Bu, T36'nın devir notundaki ayrımın aynı sınıfı: `Empty` ("baktık, yok") ile
`NeverFed` ("bakamadık") tek bir "veri yok" değerine indirgenemez
([T36 devir notu §0](../t36-devir-notu/index.md)).

**Test tarafında bir tuzak:** T33'ün kabul kriteri *"pasif kural hiç sorgu
üretmiyor — kapalı kuralın maliyeti sıfır olmalı"* diyor. `gated` bir kural bu
kriteri **tanım gereği** sağlıyor (zaten SQL'i yok). Bir test ikisini
karıştırırsa `pasif` yolunun gerçekten sınandığı görünmez olur — bekçinin
yanlış sebeple yeşil yanması.
