---
title: Kanıt önce, akıl sonra
category: concepts
tags: [mimari, log-analiz, kavram, bizigo]
aliases: [K22, deterministik rapor, kanıt paketi, RCA dürüstlüğü]
relationships:
  - target: "[[concepts/f3-bosluk-tek-cins-degildir]]"
    type: uses
  - target: "[[concepts/f3-determinizm-bir-kapi-sartidir]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
sources:
  - docs/epic/rca-raporu-ozelligi/index.md
  - docs/epic/t34-kanit-sozlesmesi/index.md
  - docs/epic/t35-korelasyonlar/index.md
  - docs/epic/t36-kanit-paketi/index.md
  - docs/epic/t36-devir-notu/index.md
  - docs/epic/t37-rapor-ekrani/index.md
  - docs/epic/tickets-f3/altin-kume/index.md
source_digest: "sha256-12/v1 docs/epic/rca-raporu-ozelligi/index.md=dff063df9345 docs/epic/t34-kanit-sozlesmesi/index.md=8cb4e8b028b3 docs/epic/t35-korelasyonlar/index.md=90159516d982 docs/epic/t36-devir-notu/index.md=5027982dbb85 docs/epic/t36-kanit-paketi/index.md=3916d770a854 docs/epic/t37-rapor-ekrani/index.md=53dc34e77027 docs/epic/tickets-f3/altin-kume/index.md=9bb15adc35bc"
summary: RCA'nın tek gerçek riski inandırıcı ama yanlış rapor; tasarımın tamamı bu tek riske karşı kurulu. F3 kanıtı LLM'siz üretiyor, saklıyor ve raporun her dürüstlük satırını mekanizmaya bağlıyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.83
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Kanıt önce, akıl sonra

K22'nin ayrımı ve F3'ün tamamının dayandığı ilke: **kanıt F3'te, akıl F4'te.**

Gerekçe tek bir risk: `docs/epic/rca-raporu-ozelligi/index.md` §2, RCA'nın tek
gerçek riskini **inandırıcı ama yanlış rapor** diye tanımlıyor ve yerel model
kararıyla (K6) bu riskin daha da yükseldiğini yazıyor. Tasarımın tamamı bu tek
riske karşı kurulu.

## Üç taşıyıcı kural

RCA belgesinden alınıp F3'te birer mekanizmaya çevrildi:

1. **Kanıt paketi LLM'siz üretilir ve tek başına değerlidir.** Model kapalıyken
   bile kullanıcı *"pencerede ilk kez şu 3 imza göründü, öncesinde şu config
   değişti, şu 12 cihaz sustu"* raporunu alır. Kabul kriteri de bu:
   *"model kapalıyken rapor okunabiliyor ve işe yarıyor"* — K22'nin tek sınavı.
2. **Her cümle bir kanıt kimliğine bağlanır.** F4'te
   `evidence_ids_must_exist` **motorda zorlanacak**, prompt'ta rica edilmeyecek.

   **Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
   gösteriliyor** (karar 2026-08-24; önceki hâli *"rozetle göster"*di).
   İçerik atılıyor, sayı kalıyor: kullanıcı uydurmayı görmüyor ama modelin ne
   kadar uydurduğunu **biliyor**. Yalnızca göstermek bir rozeti okumayanı ikna
   ederdi; yalnızca atmak kaliteyi ölçülemez yapardı — *"ölçemedim"* ile
   *"sorun yok"*un aynı çıktıya inmesi.
3. **Kanıt paketi saklanır → rapor tekrar üretilebilir.** Aynı paket üzerinde
   farklı model/prompt koşturulabiliyor; "yerel model yeterli mi" sorusunun tek
   ölçülebilir hâli bu.

   **Ve F4'ün prompt kararı buna dayanıyor** (2026-08-25): içerik düzeyi
   (`summary` · `masked` · `raw`) bir **sınır değil ayarlanabilir parametre**,
   çünkü hangisinin işe yaradığını ölçüm söyleyebiliyor — atılan cümle 2.
   kural gereği sayılıyor, paket bu kural gereği saklanıyor, yani aynı paket
   üzerinde iki düzey karşılaştırılabiliyor.

   Ama **taban ayarlanabilir değil**: sır içeren satır hiçbir düzeyde prompt'a
   girmemeli, ve bugün o tabanı sağlayan bileşen **yok**. Maske kataloğu
   şablon madenciliği için yazıldı ve bir sır redaksiyon kapısı **olmadığı
   ölçüldü** — ASA'nın IKEv2 söz dizimi belirteç sınırını aşıyordu, ham anahtar
   normalize edilmiş metinde kalıyordu. Bu yüzden bugün yalnızca `summary`
   sevk edilebilir; `masked` ve `raw` taban ölçülene kadar kapalı.

## Sınırın nereden geçtiği

F3'ün her katmanı aynı sınırı bir kez daha çiziyor: **ham olgu bir yerde,
yargı başka yerde.**

| Sınır | Bir tarafta | Diğer tarafta | Kaynak |
| --- | --- | --- | --- |
| Hesap | ClickHouse: kümeleme, sayım, anti-join | C#: Poisson/z-score, lift, eşik, sıralama | `t35-korelasyonlar` §1 |
| Sıralama | Kanıt satırları (değişmez) | Skor (türetilmiş yargı) | `t36-kanit-paketi` §1 |
| Yorum | Gözlemler, kaynaklarıyla ve sırasıyla | Hipotez — F4 | `t36-devir-notu` §2 |

T35'in gerekçesi bu sayfanın özeti gibi: **yanlış hesaplanmış bir z-score
hiçbir yerde hata vermez.** Sorgu koşar, rapor üretilir, hipotezler sıralanır ve
sıralama sessizce yanlış olur. İstatistik SQL'in içine gömülseydi yalnızca canlı
ClickHouse'la sınanabilirdi; dışarıda olduğu için elle hesaplanmış değerlere
karşı sabitlenebiliyor.

## Sıralama bir yargı, ve öyle olduğu her yerde yazılı

`Weight` sağlayıcı **içinde** anlamlı, sağlayıcılar **arasında** değil: kaynak
sayısı (1–20), z-score (3–40), lift (2–10), `1/(1+saniye)` (0,001–1). Beşini
doğrudan karşılaştırmak sıralamayı **ölçek kazasına** bırakırdı — z-score her
zaman kazanır, yayılma hiçbir zaman üste çıkamaz.

Seçilen yol `Score = ClassRank + (w / dilimdeki_en_büyük_w)`. Sınıf adımı 1,0
olduğu için **sınıf içi hiçbir büyüklük bir üst sınıfı geçemiyor**: kararı veren
yargı, ölçek değil. Sınıf içinde **oran** kullanıldı, sıra değil — sıra
"z=20 ile z=3,1" farkını siler, oysa o fark sağlayıcının söylemek istediği şey.

Sınıf ekseni tek: **kök nedene yakınlık.** `change.feed` en üstte, çünkü
elimizdeki tek **niyet** kaydı; `logs.window` en altta, çünkü bağlam, bulgu
değil.

Üç koruma bu yargının donmasını engelliyor:

- **Skor pakete yazılmıyor.** Sıralama zamanla düzeltilecek; skoru saklamak o
  düzeltmeyi geçmişe uygulanamaz yapardı — oysa paketin saklanma sebebi tam
  tersi.
- **Skor kullanıcıya gösterilmiyor.** `4.73` ekranda hiçbir şey ifade etmiyor ve
  gösterilirse **ölçülmüş bir kesinlik iddiası** olur.
- **Tanınmayan sağlayıcı düşürülmüyor, en dibe konuyor** (`ClassRank = -1`) ve
  bir bekçi kayıtlı her sağlayıcının tabloda olduğunu tutuyor. F5'te trace
  sağlayıcısını tabloya yazmayı unutmak, kanıtını "en önemsiz" ilan etmek olurdu
  ve hiçbir şey kırmızı yanmazdı.

## Raporun dürüstlük satırları

Rapor hipotez üretmiyor; ürettiği şey gözlemler. Dürüstlüğü dört ayrım ve üç
uyarı taşıyor:

**Dört ayrım** — [[concepts/f3-bosluk-tek-cins-degildir]] sayfasının konusu:
`Empty` / `NeverFed` / `Unavailable`-`Failed` / `NotRegistered`, artı
"ölçülemedi" ile "sıfır" farkı.

**Üç uyarı** ve hepsi raporun **en üstünde**:

| Uyarı | İçeriği | Sıfırken |
| --- | --- | --- |
| Kapsam dışı | *"Kapsamınız dışında 342 ilişkili kayıt var"* — yalnızca sayı | satır **hiç yazılmıyor** |
| Zaman güvenilirliği | `time_source != parsed` sayısı + oranı | ölçüldü ve temizse **uyarı yok** |
| Eksik kanıt | sağlayıcı patladı / bütçeye takıldı / kırpıldı | — |

Uyarıların en üstte olmasının gerekçesi mekanik: **raporun sonunda duran bir
kısıt okunmuyor, ve okunmayan bir kısıt hiç yazılmamış gibi.** Aynı satırların
export'ta da bulunması ayrıca sınanıyor — ekranı okuyan görüp PDF'i okuyan
görmüyorsa uyarı işe yaramaz.

Ve tersi de bir bekçi: **her raporda duran bir uyarı hiçbir şey söylemez.**
Zamanların hepsi güvenilirse uyarı yok, ve bunu tutan ayrı bir test var.

## Kapsam dışı dürüstlüğü — bilgi sızdırmadan yanlış güveni engellemek

RCA sahibinin kapsamıyla koşuyor (K17). Kök neden başka grubun cihazındaysa
rapor bunu **bilmeden** yanlış sonuca varır. Karşı önlem: toplayıcı kapsam
dışında **kaç** eşleşme olduğunu sayıyor, içeriğini değil.

Ayrımın maliyeti ölçülü ve kararı da: değişiklik tarafında sayaç **yoktu** ve
`0` dönmek **sessiz bir yalan** olurdu — rapor *"kapsamınız dışında ilişkili
değişiklik yok"* cümlesini kurardı. `CountOutOfScopeChangesAsync` bu yüzden
eklendi.

Ekranda **hangi grubun** sahibiyle görüşüleceğini yazmak cazip ama o bilgi
kasten yok: grup adı da bir sızıntı. Cümle bilinçli olarak belirsiz.

## Saklanan kanıt, kaçmayan yetki

Kanıt paketi saklandığı için iki tuzak doğuyor ve ikisi de kapatılmış:

- **Drilldown ham SQL değil, `EventQuery`.** SQL dizgisi yazmak, kapsam kapısını
  atlayan bir yolu **diske yazmak** olurdu; altı ay sonra paketi açan biri o
  günkü yetkisinden bağımsız bir sorgu elde ederdi. Yapılandırılmış sorgu
  tıklandığında `IScopedQuery`'den geçiyor ve K17 yeniden uygulanıyor.
- **Paketin okunması da kapsamlı.** T36 ucu bilerek yazmamıştı, dolayısıyla açık
  **sahipsizdi**: A grubunun kapsamıyla toplanmış bir paketi B grubundan biri
  `GET /v1/rca/{id}` ile isteyebilirdi. Kural `BundleScope.IsReadableBy`'da, ve
  okuyamayan **404** alıyor — 403 paketin var olduğunu doğrular, *"şu pencerede
  RCA koşulmuş"* tek başına bir sızıntı.

## Desteklenmeyen filtreyi sessizce genişletmemek

Drilldown'un ikinci yarısı: arama ekranının parametre kümesi kanıt
sağlayıcılarının ürettiği her filtreyi karşılamıyor (`signature_hash`'in kutusu
yok, olumsuzlamanın da).

**Sessizce düşürmek en kötü seçenek** — kullanıcı kanıt satırının gösterdiğinden
daha **geniş** bir kümeye bakar ve baktığı kümenin o satırın kümesi olduğunu
sanır. Rozet tıklamadan önce görünüyor; tıkladıktan sonra öğrenmek geç. Ve
**desteklenmeyen operatör eşitliğe çevrilmiyor**: çevirmek, dolu ve inandırıcı
ama yanlış bir küme göstermek olurdu — bildirilen bir boşluktan beter.

## Yargının sınanacağı tek yer: altın küme

Sıralama tablosu, yedi ölçülmemiş eşik ve raporun işe yarayıp yaramadığı —
üçünün de doğrulanacağı yer aynı: `(kanıt paketi, gerçek kök neden)` çiftleri.

RCA belgesinin 2. riski bu kümenin doğal düşmanı: **kimse "doğru muydu?"
düğmesine basmazsa altın küme boş kalır.** Karşı önlem plandan aynen alındı:
alarm tetikli RCA'larda inceleme, **alarmı kapatma akışının zorunlu parçası** —
kullanıcı zaten oradadır, ayrı bir "geri bildirim ver" adımı hiç kullanılmaz.

Kümenin kendisinin de dürüstlük kuralları var ve hepsi aynı aileden:

- **"Bilmiyorum" bir karar** ve doğruluk oranının **paydasına girmiyor**. Kaçış
  kapısı olmasaydı gerçekten bilmeyen kişi rastgele seçer ve küme sessizce
  gürültüyle dolardı — ölçülemezlikten **kötü**, çünkü ölçülüyormuş gibi görünür.
- **Çelişen kanıt ayrı bir soru** ve karara **bağlı değil**. Alt soru yapmak
  cazipti ve belirli bir yönde başarısız oluyor: tiyatronun tehlikeli hâli,
  raporun **bütün olarak doğru** olduğu ve o bölümü yine de doldurmuş olduğu hâl.
  Soruyu olumsuz karara bağlamak, ölçümün **var olma sebebi olan durumu hiç
  örneklememesi** demek.
- **Alan bugün açılıyor**, F4'te kullanılacak olsa bile: sonradan eklenirse
  geçmiş kayıtlar onu taşımaz ve altın kümenin en eski yarısı o boyutta kör kalır.
- **Küme boşken sayı görünüyor**, gizlenmiyor: gizlenen bir sıfır *"henüz
  ölçülmedi"* ile *"ölçüldü, sıfır"* farkını siler.

## Kaynaklar

- `docs/epic/rca-raporu-ozelligi/index.md` — §2 tasarım ilkesi, §3 sözleşme, §7 altın küme, §10 riskler
- `docs/epic/t34-kanit-sozlesmesi/index.md` — sözleşme, kapsam dışı sayım, drilldown kararı
- `docs/epic/t35-korelasyonlar/index.md` — SQL/C# sınırı, üç sessiz tuzak, ölçülmemiş yedi sayı
- `docs/epic/t36-kanit-paketi/index.md` — ağırlık normalleştirme, saklama kararları
- `docs/epic/t36-devir-notu/index.md` — `RankedEvidence` ne değildir, uyarıların yeri
- `docs/epic/t37-rapor-ekrani/index.md` — paket okuma kapsamı, drilldown rozeti, inceleme kararları
- `docs/epic/tickets-f3/altin-kume/index.md` — beş karar ve gerekçeleri
