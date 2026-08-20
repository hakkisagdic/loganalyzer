---
title: "T28 denetimi — dokuz bulgu, bekçileri ve bilinçli boşluklar"
kind: review
---

# T28 denetimi

Yedi ekranın **birlikte bir ürün gibi** durup durmadığının denetimi. İki dilimde
yapıldı — önce bekçiler (`5f657f6`), sonra ekran görüntüleri — ve arkasından
bekçisi olmayan bulgular için bir tur daha.

Bu belge o üç turun **tek kaydı**. Daha önce ikiye bölünmüştü ("bulgular" ve
"bekçisiz bulgular"); "hangi bulgunun bekçisi var" sorusunun iki yerden
okunması gerekiyordu ve F2 kapanışında (T27) okunacak belge bu.

> **Denetleyen, denetlenen ekranların üçünü kendisi yazdı** (T15, T16, T17).
> Telafi "daha sert bakmak" değil, kuralları **ekran adı geçmeden** yazmak oldu:
> kural herkese aynı uygulanınca kimin yazdığı sorusu düşüyor. Dokuz bulgunun
> **beşi** denetleyenin kendi kodunda çıktı.

## Tablo — tek bakışta

| # | Bulgu | Düzeltildi | Bekçi | Geri gelirse |
| --- | --- | --- | --- | --- |
| 1 | Zaman biçimi üç ayrı yerde, biri **yerel saat** | ✅ | zaman görüntüleme kuralı | kırmızı |
| 2 | Sunucu ekranlarının yükleniyor durumu yok | ✅ | yükleniyor sınırı | kırmızı |
| 3 | Kontrast dört çiftte WCAG AA altında | ✅ | tema başına hesap | kırmızı |
| 4 | `describeError` iki uygulama, farklı davranış | ✅ | yinelenen ad | kırmızı |
| 5 | Dört durumun sırası kırılgan | ✅ | iki test | kırmızı |
| 6 | Ham renk (`#ffffff`) | ✅ | jeton disiplini | kırmızı |
| 7 | `toUpperCase` — kural düz yasak olamaz | ✅ | gerekçeli muafiyet + sabit sayı | kırmızı |
| 8 | Kırpma kuralı `<td>`'yi tablo hücresi olmaktan çıkarıyor | ✅ | mekanizma yasağı | kırmızı |
| 9 | Sütun en uzun etiketine dar | ✅ | **yok — bilinçli** | **sessiz** |

Sekizinin bekçisi var. Dokuzuncu ve ekran görüntüsü tazeliği **bilinçli açık**;
gerekçeleri en altta.

---

## 1 · Zaman damgası üç ayrı biçimde — biri yerel saat

En ciddi bulgu ve kozmetik değil.

| Nerede | Biçim | Örnek |
| --- | --- | --- |
| olay ekranları | UTC, saniyeli, `Z` ekli | `2026-08-16 12:30:00Z` |
| değişiklik tablosu | UTC, **dakika** hassasiyeti, `Z` yok | `2026-08-16 12:30` |
| alarm ekranları | **yerel saat**, `tr-TR` | `16.08.2026 15:30:00` |

Bir alarm tetiklenmesini log satırıyla eşleştiren kullanıcı **saat farkı kadar**
sapmış iki zaman görüyor ve hiçbir yerde bunun yazmıyor. İstanbul'da üç saat.
Log analizi yapan bir üründe bu doğrudan yanlış sonuç.

**Düzeltme:** `ui/src/lib/ui/time.ts` — tek biçim, UTC ve `Z` ile açıkça
işaretli.

**Bekçi ve nasıl daraltıldı.** İlk formülasyon "`format*` yalnızca `lib/ui/`de
tanımlanabilir"di. Yazılmadan önce ölçüldü ve **yanlış çıktı**:
`formatSeverity`/`formatParseStatus` alana özgü etiket tabloları ve ortak kite
taşınmaları onları ait olmadıkları yere koymak olurdu.

Yasaklanan şey `format*` değil **zaman görüntüleme**:

| Yasak (yalnızca `lib/ui/time.ts`) | Serbest |
| --- | --- |
| `toLocaleDateString` · `toLocaleTimeString` · `Intl.DateTimeFormat` | — |
| tarih seçenekli `toLocaleString` (`dateStyle`, `timeStyle`, `year`…) | sayı için `toLocaleString("tr-TR")` |
| `toISOString().slice(…)` / `.replace(…)` | **çıplak** `toISOString()` |

Son satır kritik: form değerini API gövdesine ISO yazmak sözleşme gereği ve üç
yerde meşru yapılıyor. Ayrım **serileştirme ile görüntüleme** arasında — çıplak
ISO tele, kesilmiş ISO ekrana.

> Yinelenen-ad bekçisi bu bulguyu **yakalayamazdı**: üç kopyadan ikisinin adı
> aynıydı, üçüncüsü `formatTimestamp` idi — aynı iş, başka ad.

## 2 · Sunucu bileşeni ekranlarının yükleniyor durumu yok

`LoadingState` dokuz dosyada kullanılıyordu ve hiçbiri sunucu bileşeni değildi.
`/`, `/olaylar`, `/olaylar/{id}` ve `/kaynaklar` HTML gelene kadar **hiçbir geri
bildirim vermiyordu**: ClickHouse sorgusu sürerken tarayıcı önceki sayfada
bekliyor ve bir şeyin çalışıp çalışmadığı anlaşılmıyordu.

**Düzeltme:** dört `loading.tsx` (Suspense sınırı).

**Bekçi:** her `page.tsx` ya kendi/üst klasöründe `loading.tsx` ya bölümünde
`LoadingState` taşımalı. Kök sınırı **yalnızca kökün kendi rotasını** kapsıyor
sayılıyor — Next açısından iç içe rotaları da kapsıyor ama onu geçerli saymak
bir ekranın kendi sınırını unutmuş olmasını görünmez kılardı.

## 3 · Kontrast dört çiftte AA altında

| Çift | Tema | Önce | Sonra |
| --- | --- | --- | --- |
| uyarı rozeti metni | açık | **2.07:1** | 4.84:1 |
| başarı rozeti metni | açık | **3.15:1** | 4.79:1 |
| hata metni | açık | **4.41:1** | 5.91:1 |
| tehlike düğmesi | koyu | **3.76:1** | 4.83:1 |

Sonuncusunun sebebi öğretici: `--danger` **iki iş** taşıyordu — yüzey üstünde
metin rengi ve dolgu rengi. Koyu tema birincisi için jetonu açıyor, bu da
ikincisini sessizce bozuyordu. Dolgu ayrıldı: `--danger-solid` + `--danger-on`.

**Bekçi:** `tokens.css` tema başına ayrıştırılıyor, `var()` zinciri çözülüyor ve
gerçekten yan yana duran 12 çift iki temada ölçülüyor. Ayrıca
`[data-theme="dark"]` ile `@media (prefers-color-scheme: dark)` bloklarının
ayrışmadığı sabitleniyor — ayrışsalar ürün, işletim sistemi koyu temadayken
düğmeyle geçmiş kullanıcıya göre **başka** görünürdü.

## 4 · `describeError` iki uygulamada

İkisi de `ApiError`'ı aynı ele alıyordu ama son dalları ayrışıyordu: biri düz
bir `Error`'ın mesajını koruyor, öbürü atıp genel bir cümle koyuyordu. **Aynı
hata, hangi ekranda olduğunuza göre farklı metinle** çıkıyordu.

**Düzeltme:** `lib/alerts/errors.ts` silindi, yedi çağrı yeri
`lib/api/errors.ts`'e bağlandı. (T19 ajanı aynı yinelenmeyi bağımsız bulup bir
yeniden-dışa-açım kabuğu bırakmıştı; birleşmede kabuğun tüketicisi kalmadığı
için silindi.)

## 5 · Dört durumun sırası kırılgandı

`screenState(rows, error)` önce `rows === null` bakıyordu, yani
`screenState(null, "hata")` → `"loading"`. İlk yüklemesinde düşen bir ekran
**sonsuza kadar iskelet** gösterip hatayı hiç söylemezdi. Çağıranlar bunu
`catch` içinde `setRows([])` yazarak telafi ediyordu — doğruluk, herkesin
hatırlaması gereken bir kurala bağlıydı.

**Düzeltme:** hata önce. Fonksiyon değişiklik akışına özgü bir modülden ortak
kite taşındı; orada durduğu için diğer ekranlar onu bulamamıştı.

**Bekçi, iki test:** `screenState` yalnızca `lib/ui/screen-state.ts`'te
tanımlanabilir; ve **veri çeken** bir ekranda `ErrorState`, `EmptyState`'ten
önce gelmeli.

> İkinci test ölçülerek daraltıldı. Literal hâli ("`EmptyState` çizen her ekran
> `screenState` kullanmalı") 16 dosyanın 13'ünü işaretledi; üçü açılıp doğru
> sırada oldukları görüldü. Veri çekmeyen sunum bileşenleri (`GateReport`,
> `TriggerHistory`) dışarıda bırakıldı — yükleme hatası olamayan bir bileşenden
> hata durumu istemek yanlış pozitif.

## 6 · Tek ham renk

`.buttonDanger { color: #ffffff }` — T13'ten kalma, ağaçtaki tek jeton ihlali.
**Bekçi:** `tokens.css` dışında ham renk yok.

## 7 · `toUpperCase` — kural düz yasak olamaz

İki kullanım var, ikisi de HTTP yöntemi ve tipi `"get"|"post"|…`, yani ASCII
olduğu **kanıtlanabilir**. Türkçe `İ`/`ı` tehlikesi yok.

Bulgu kullanımın kendisi değil **kuralın şekli**: dar ve gerekçeli bir muafiyet
taşımalı. `ProducesContractTests`'teki `ExpectedExemptCount` kalıbı uygulandı.

**Ek bekçi:** CSS `text-transform: uppercase` Türkçede ancak `lang="tr"` ile
doğru çalışıyor. Öznitelik yerinde — bulgu değil — ama korumasızdı ve
`toUpperCase` bekçisi CSS'i görmüyor.

## 8 · Gövde hücresi tablo hücresi olmaktan çıkıyordu

**Yalnızca ekran görüntüsü yakaladı.**

Kırpma kuralları doğrudan `<td>`'ye uygulanıyordu ve `display: -webkit-box` bir
tablo hücresine verilince hücre tablo hücresi olmaktan çıkıyor: satır ona göre
boyutlanmıyor, uzun bir gövdenin son satırı tablonun alt kenarından **yarım**
taşıyor. HTML doğru, CSS kuralı doğru, bozulan şey **yerleşim**.

Yolda iki yanlış deneme oldu ve ikisi de kayda değer: `calc(4 × satır × yazı)`
yarım aralığı hesaba katmadığı için üç noktayı kesti, `4lh` hiçbir şeyi çözmedi
— çünkü sorun yükseklik değil **hücrenin türü**ydü.

**Düzeltme:** kırpma iç öğeye (`.cellBodyText`) taşındı.

**Bekçi — yerleşimi simüle etmiyor, mekanizmayı yasaklıyor:** `DataTable`
hücrelere yalnızca `cellBody` ve `cellNumeric` veriyor ve bu iki sınıf `display`
tanımlayamıyor. İkinci bir test de bekçinin **doğru yeri koruduğunu** tutuyor:
o sınıflar gerçekten `<td>`'ye veriliyor mu. Taşınsalar bekçi geçmeye devam
eder ama hiçbir şeyi korumazdı.

## 9 · Önem sütunu dar

7rem'de "belirtilmemiş" kelime ortasından kırılıyordu (`belirtilmemi` / `ş`).
`overflow-wrap: anywhere` çok dilli gövdeler için doğru ama dar bir sütunda
Türkçe bir kelimeyi hecesiz bölüyor. 9,5rem'e çıkarıldı.

**Bekçisi yok ve olmayacak** — gerekçe aşağıda.

---

## Bekçiler kırmızı yanabiliyor — ölçüldü

Her biri, koruduğu hata geri konularak sınandı ve geri alındı.

| Bekçi | Nasıl kırmızı yandı |
| --- | --- |
| yükleniyor sınırı | bir `loading.tsx` silindi |
| ham renk | bir CSS modülüne `#ff0000` eklendi |
| Türkçe kasa | muaf olmayan bir dosyaya `.toUpperCase()` eklendi |
| yinelenen yardımcı | aynı ad iki modülde dışa açıldı |
| ham tablo | `DataTable` dışında `<table>` yazıldı |
| kontrast | `--danger-solid` açık kırmızıya çevrildi |
| durum sırası | veri çeken, hata durumu olmayan bir sonda ekran eklendi |
| zaman görüntüleme | yerel saatli biçim ve `toISOString().slice(…)` eklendi |
| hücre yerleşimi | `cellBody`'ye `display` eklendi |

**Ve bir kez, yanmaması gereken yerde yanmadığı ölçüldü:** çıplak
`toISOString()` eklendiğinde zaman bekçisi yeşil kalıyor. Bir bekçinin yanlış
pozitif vermediğini göstermek, kırmızı yanabildiğini göstermek kadar gerekli.

### Bekçiler iki kez yanlış sebeple yeşil/kırmızı yandı

Bu turun tekrar eden dersi, ve ikisi de kendi bekçilerimde çıktı:

1. **Yanlış pozitif:** ham `<table>` bekçisinin ilk hâli belge yorumlarındaki
   örnek etiketleri ihlal saydı. Yorumlar artık taranmadan çıkarılıyor.
2. **Yanlış sebeple yeşil:** ekran görüntüsü harness'ının ilk koşumunda ortak
   bileşen CSS'i bağlanmamıştı — hücreler ortalanmış, rozetler düz metin, gövde
   kırpılmamış çıktı **ve test geçti**, çünkü sayfanın *boyanıp boyanmadığına*
   bakıyordu, doğru göründüğüne değil.

Bir bekçinin yanlış pozitifi, kırmızı yanmamasından farklı bir tehlike:
insanlar onu susturmayı öğreniyor.

---

## Ekran görüntüleri

18 görüntü (9 sahne × açık/koyu tema), `docs/ekran-goruntuleri/`, 1 MB.

**Kanıtladığı:** bileşenlerin gerçek jetonlar ve gerçek CSS ile nasıl göründüğü
— çok dilli gövdelerin kırpılması ve hizalanması, rozet kontrastının okunurluğu,
500 satırlık tablonun düzeni bozup bozmadığı.

**Kanıtlamadığı:** Next yönlendirmesi, kimlik akışı ve düzen birleşimi. Onlar
için sunucu + sahte Keycloak + sahte API gerekiyordu — üç uzun ömürlü proses,
beş ajanlı bir makinede, protokol §3'ün anlattığı risk. Uçtan uca akışlar T27'de.

**Bir görüntü yakalamadır, iddia değil:** kusuru bulur ama tutmaz.

---

## İki şey bulgu değil çıktı

- **`px` kullanımları meşru:** hepsi `1px` kenarlık, `2px`/`3px` vurgu çizgisi
ya da yorum. Jetonlar kıl payı çizgileri kapsamıyor.
- **`AppShell` ikinci kez kurulmamış:** "iki kabuk" adayı ölçülünce düzelmiş
çıktı. Ölçmeden teste bağlansaydı, var olmayan bir sorunu sabitleyen ve bir gün
gerçek bir değişikliği engelleyecek bir test kalırdı.

## Bilinçli açıklar — kapatılmıyor ve sebebi yazılı

**1 · Sütun genişliği ile etiket uzunluğu (bulgu 9).** Genişliğin yeteceğini
bilmek yazı tipi ölçümü istiyor, o da tarayıcıda. Görsel gerileme dışında
dürüst yol yok ve bu ekran için orantısız. Kırılgan bir bekçi, susturulmayı
öğrenilen bir teste dönüşür.

**2 · Ekran görüntüsü tazeliği.** `DataTable` ya da jetonlar değiştiğinde
görüntülerin yeniden alınmasını tutan bir şey **yok**. Aynı sınıf: kapatılabilir
(görsel gerileme) ama bedeli değerinden büyük.

**3 · Satır içi dört durum sıralaması.** Bekçi `screenState` tanımlarını ada
göre buluyor ve "hata boş durumdan önce" kuralını kaynak sırası üzerinden vekil
olarak sınıyor; JSX'te sırayı gerçekten analiz etmiyor.

**4 · İki farklı veri çekme mimarisi.** Ekranların bir kısmı sunucu bileşeni,
bir kısmı istemcide `fetch`. Tekleştirmek **yeniden tasarım** olurdu ve ticket
onu kapsam dışı bırakıyor. Denetimin istediği, ikisinin de dört durumu tanımlı
olması — artık öyle.

## Denetimin dışından çıkan bir kural

Görüntü testi varsayılan koşuma giriyordu ama CI'da tarayıcı yoktu: raporda
"varsayılan pakete koymadım" yazıyordu, `vitest.config`'in `include` deseni aynı
fikirde değildi, ve ikisinin ayrıştığını kimse okumadı. Niyet yazıldı,
yapılandırma okunmadı.

> Dış bir ikili gerektiren test ya CI'da o ikiliyle koşmalı ya koşumdan açıkça
> dışlanmalı. Üçüncü hâl — *"koşuma giriyor ama ortam hazır değil"* — sessizce
> kırmızı yanan bir CI.

Kural `CLAUDE.md`'ye yazıldı; ihlali bir kişi değil bir **yapılandırma**
yapıyor, ve yapılandırmayı okuyan insan.
