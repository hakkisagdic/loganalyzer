---
title: "T42 — Model sağlayıcısı soyutlaması ve K6'nın kapısı"
kind: ticket
status: 2
---

# T42 — Model sağlayıcısı soyutlaması

> **Önkoşulu [T41](../prompt-redaksiyon-tabani/index.md) karşıladı.** T41 redaksiyon
> kapısını bir tipe bağlarken bir şart koymuştu — prompt'u kuran taraf girdisini
> `string` değil o tipten almalı. Bu ticket o şartı uyguluyor.

**Yöneten kararlar:** K6 (LLM: yerel + uzak GPU kümesi; log verisi kurum dışına
çıkmaz) · K10/K16 (tek kurum, tek tenant) · K15 (her adım tek iş) ·
[RCA §2.1](../../rca-raporu-ozelligi/index.md) (içerik düzeyi ayarlanabilir bir
parametre)

---

## 1 · Sorulan soru ve verilen cevap

Koordinatörün sorusu: **uzak bir sağlayıcı seçiliyken `raw` düzeyi açılabilmeli
mi?** Üç meşru cevap önerilmişti — yalnızca `summary`, `masked`'e kadar, ya da
düzeyin kurum kararı olması.

**Cevap: sorunun ekseni yanlış, ve doğru eksende cevap sert.**

K6'nın kendi metni bunu söylüyor:

> **K6** | LLM | Yerel (Ollama/vLLM) + **uzak GPU cluster** | OpenAI-uyumlu
> endpoint soyutlaması; base URL config'den. **Log verisi kurum dışına çıkmaz**

K6 uzak bir GPU kümesini **açıkça kapsıyor**. Yasak olan uzaklık değil, **kurum
dışına çıkmak**. Ve RCA §2.1 aynı ayrımı açılış cümlesinde yapıyor: K6 *ağ
sınırını* çiziyor, prompt'un *içeriği* ayrı bir soru.

Buradan iki sonuç çıkıyor ve ikisi de mekanizmaya bağlandı:

| Eksen | Kural | Nerede |
| --- | --- | --- |
| **Sınır** (kurum içi / dışı) | Kurum dışı bir uçta **hiçbir düzey geçmiyor** — `summary` dahil | `ModelBoundaryGate` |
| **Düzey** (`summary`/`masked`/`raw`) | **Kurumun kararı**, sağlayıcı türüne bağlı değil. `raw` ayrı bir bilinçli hareket istiyor | `ModelRequest.Create` |

**`summary`'nin de yasak olmasının gerekçesi:** özet de müşteri verisi.
*"edge-rtr-07 14:02'de sustu"* cümlesi host adı, sahiplik grubu ve topoloji
taşıyor. K6 *"ham log çıkmaz"* demiyor, **"log verisi çıkmaz"** diyor — ve özet
o verinin türevi. Kapıyı düzey eksenine kurmak, K6'ya düzeylerden birini
istisna yazmak olurdu.

**Düzeyin sağlayıcıya bağlanmamasının gerekçesi:** bağlansaydı yerel bir Ollama
ile kurum içi bir GPU kümesi farklı düzeylere sahip olurdu — oysa K6'nın baktığı
eksende ikisi **aynı yerde**. Ayrıca §2.1 düzeyin ayarlanabilir bir parametre
olduğuna zaten karar vermişti; sağlayıcıya bağlamak o kararı geri almak olurdu.

**Ve K6 bir ayara dönüşmüyor** — koordinatörün üçüncü seçeneğe koyduğu itiraz
buydu. K6 artık *daha sert* bir şeyi tutuyor: ucun **kullanılıp
kullanılamayacağını**.

## 2 · K6 bugüne kadar bir cümleydi

Mimari karar tablosunda yazılıydı ve onu tutan **hiçbir mekanizma yoktu**.
`ModelBoundaryGate` o cümleyi bir kapıya çeviriyor, üç kuralla:

1. **Beyan zorunlu.** `DataBoundary` varsayılanı `Unspecified = 0` ve o değerde
uç kurulmuyor. Sıfırın "iç ağ" sayılması bu deponun en pahalı hata sınıfı
olurdu: yapılandırmayı yazan kişi alanı hiç görmemiş olur, ürün çalışır, ve
kurumun en büyük sözü **kimse karar vermeden** boşa çıkar.
2. **Kurum dışı beyan reddediliyor** — her düzey için.
3. **İç beyan doğrulanıyor.** Çözülen **her** adres yönlendirilemeyen olmak
zorunda. Biri bile genelse beyan ile gerçek çelişiyor demektir.

Üçüncü kuralın iki alt kuralı ayrıca yazılı: **çözülemeyen bir ad iç ağ
sayılmıyor** (*"bakamadım"* ile *"temiz"* aynı çıktıya inerse bekçi bir gürültü
bastırıcıya döner), ve **adreslerden birinin iç olması yetmiyor** (çift yığınlı
bir ad içeriden ve dışarıdan birden çözülebilir; dışarıdaki yolu görünmez
yapmak istemiyoruz).

### Kapının tutamadıkları — yazılı olması şart

- **Yalan söyleyen bir yöneticiyi tutmuyor, ve tutamaz.** Ürün tek kurum / tek
tenant (K10, K16); yapılandırmayı yazan kişi **kurumun kendisi**. Kapının işi
kararı imkânsız kılmak değil, **kazara** olmasını imkânsız kılmak ve bilinçli
olanı **görünür** kılmak.
- **Adresi doğruluyor, kurumu değil.** Özel adres uzayında duran bir tünelin ucu
dışarısı olabilir.
- **Kurumun kendi AS'indeki yönlendirilebilir adres yanlış yere düşer.** Bilinen
bir yanlış pozitif; çözümü muafiyet ve muafiyet **gerekçe yazılmadan
açılmıyor**. Gerekçe koşum kaydına giriyor — §8'in muafiyet disiplini.
- **Genel LLM sağlayıcılarının listesi YOK ve bilinçli yok.** Tam olması gereken
bir liste, tam olmadığı gün bekçiyi körleştirir; bu depo o dersi beş kez ödedi.
Adres sınıfı ölçütü mekanik ve kendi sınıfı için eksiksiz.

## 3 · T41'in şartı nasıl uygulandı

İki tip, ikisinin de yapıcısı `private`:

- **`ModelEndpoint`** — ancak `ModelBoundaryGate`'ten çıkabiliyor. Var olması K6'yı
geçmiş olması demek.
- **`ModelRequest`** — bir `ModelEndpoint`, bir düzey ve **iki `RedactedPrompt`**
istiyor.

`IModelProvider.CompleteAsync` girdi olarak `ModelRequest` alıyor, `string`
değil. Yani **redaksiyon kapısını ve K6 kapısını atlayan bir çağrı
derlenmiyor**. Alternatifi sağlayıcıya seçenekleri verip kapıları çağırmayı
hatırlamaktı; o zaman iki söz de birer çağrı alışkanlığı olurdu ve unutuldukları
gün hiçbir şey kırılmazdı.

**Ve ikisi tek yerde buluşuyor.** Ayrı yerlerde kontrol edilselerdi ayrışırlardı
— bu turda dört kez ödenen şeklin beşincisi.

## 4 · Ölçüldü

| Ölçüm | Sonuç |
| --- | --- |
| `dotnet build` | 0 hata, 0 uyarı |
| `dotnet test tests/Bizigo.UnitTests` | **1120 geçti / 0 düştü / 4 atlandı** |
| `ModelBoundaryTests` + `ModelProviderTests` | 33/33 |

### Kırmızı yanabildiği ölçüldü — sekiz kusur, hepsi geri alındı

Her kusur uygulandıktan sonra **dosyada gerçekten olduğu doğrulandı**, sonra
koşuldu, sonra geri alındı.

| # | Kusur | Düşen |
| --- | --- | --- |
| 1 | Beyansız uç "iç ağ" sayılıyor | 1 |
| 2 | Kurum dışı uçta `summary` geçiyor (kapı düzey eksenine kaydı) | 1 |
| 3 | "Adreslerden biri iç ağsa geçsin" | 1 |
| 4 | Çözülemeyen ad iç ağ sayılıyor | 1 |
| 5 | Muafiyet gerekçesiz kabul ediliyor | 1 |
| 6 | `raw` ayrı bir hareket istemiyor | **2** |
| 7 | Sağlayıcıya `string` giriş açılıyor (T41'in şartı düşüyor) | 1 |
| 8 | Bildirilmemiş belirteç sayısı sıfır yazılıyor | 1 |

**Ve bir bekçi kendiliğinden kırmızı yandı:**
`ArchitectureTests.Kapsam_bekcisi_butun_kayit_uzantilarini_kendisi_buluyor`
yeni kayıt uzantısını **kendisi buldu** ve beklenen kümede olmadığı için düştü.
Bekçi tam da tasarlandığı gibi çalıştı; uzantı gerekçesiyle listeye yazıldı.

## 5 · Kapsam dışı ve sınırlar

- **Token bütçesi burada uygulanmıyor** — T46'nın alanı. Buradaki taahhüt
yalnızca ölçülen sayıyı **bildirmek**. İkisi aynı yerde olsaydı aynı kısıt iki
kez yazılır ve biri sessizce ayrışırdı.
- **Bildirilmemiş belirteç sayısı `null`, sıfır değil.** Sıfır "ölçüldü ve
sıfır" demek; bildirilmemiş bir sayıyı sıfır yazmak token bütçesini sessizce
yanıltırdı — bütçe hiç tükenmez, kapı hiç kapanmaz, sebep hiç görünmez.
- **Prompt'un kendisi kurulmuyor** — hangi düzeyin ne içerdiği T44'ün işi.
T42 düzeyi taşıyor ve tabanı zorluyor, içeriği doldurmuyor.
- **Uç kayıt anında doğrulanmıyor**, kullanım anında. Kayıt anına konsaydı DNS
erişilemediğinde API'nin tamamı ayağa kalkmazdı.
- **Canlı bir modelle ölçüm yapılmadı.** Sağlayıcı sahte bir `HttpMessageHandler`
ile sınandı; gerçek bir Ollama/vLLM koşumu **koordinatörün** işi (§2).

## 6 · Tereddüt

**Muafiyet gerekçesi bugün yalnızca yapılandırmada ve `AuditFields()` içinde
duruyor; bir koşum kaydına henüz yazılmıyor** çünkü koşum kaydı (`rca_runs`)
T46'nın alanı. Alan hazır (`model_boundary_override_reason`), bağlanması T44/T46
turunda. Bağlanmazsa muafiyet **yapılandırmada görünür ama raporda görünmez**
kalır — ve o hâl, muafiyetin sessiz olduğu hâle yakındır.
