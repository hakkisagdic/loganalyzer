---
title: Duvar saati neyi ölçtüğünü söylemez
category: concepts
tags: [test, surec, kavram, bizigo]
aliases: [süre bütçeli test, wall clock tuzağı, kararsız test yanılgısı]
relationships:
  - target: "[[concepts/olcum-kirmizi-yanamayan-sayi]]"
    type: related_to
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: extends
sources:
  - docs/epic/t05-kararlar/index.md
  - docs/epic/t08-motor-geri-beslemesi/index.md
  - docs/epic/t08-kararlar/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
  - docs/epic/t07-kararlar/index.md
  - docs/epic/t12-kararlar/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=ed0e0490c47f docs/epic/t05-kararlar/index.md=fa28e6db349e docs/epic/t07-kararlar/index.md=f945b43c4277 docs/epic/t08-kararlar/index.md=0ef90b5576bc docs/epic/t08-motor-geri-beslemesi/index.md=5ce87e36f831 docs/epic/t12-kararlar/index.md=454710cef295 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f"
summary: Mutlak süre bütçesi pattern'in davranışını değil makinenin o anki hızını ölçer. Bu depoda üç kez oldu; ikisi teste, biri ürünün kendisine sızdı.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:31:44Z
updated: 2026-08-24T17:31:44Z
---

# Duvar saati neyi ölçtüğünü söylemez

`CLAUDE.md` §6'nın kuralı tek cümle: **bir testin geçme sebebinin duvar saatiyle
ilgisi olmamalı.** Bu dilimdeki belgeler o kuralın üç ayrı örneğini taşıyor ve
üçüncüsü testte değil **üründe**.

## Örnek 1 — test, ve "kararsız test" diye yanlış teşhis edilmişti

T05'in `GrokPropertyTests`'i 5 000 rastgele pattern üretip motorun ya çalıştığını
ya `GrokCompilationException` ile reddettiğini sınıyor. Düzeltmeden önce
doğrusal olmayan her ifadeyi `Stopwatch` ile **2 saniyelik mutlak bir bütçeye**
karşı ölçüyordu.

Belgenin ifadesi: o bütçe pattern'in davranışını değil **makinenin hızını**
ölçüyordu — test tek başına geçiyor, eşzamanlı bir Release build varken
düşüyordu. Yani sağlıklı bir eşleşme *"bozuk pattern"* diye raporlanıyordu.

Düzeltme iddiayı **süreden yapıya** çevirdi: doğrusal zaman garantisi olmayan
her ifade, **neden olmadığını söyleyebilmeli** (`FallbackReason` dolu olmalı).
Gerekçe ürün tarafından geliyor — `parser lint` çıktısı ve yayın kapısı tam o
alana bakıyor. Ölçülen etki: **2 dk 36 sn → 6 sn**.

Dürüstlük notu belgede duruyor: sonsuz döngü bu değişiklikle görünmez olmuyor,
çünkü *bir asılma zaten asılmadır* ve koşum zaman aşımına uğrar. Kaybolan tek
şey, yüklü bir makinede sağlıklı kodu suçlayan bir bütçe.
(`docs/epic/t05-kararlar/index.md` §2)

## Örnek 2 — aynı sınıf, ama ürünün içinde

T08'in motor geri beslemesi #10 aynı hatayı **çalışan kodda** buluyor:
`GrokCompilerOptions.MatchTimeout` 50 ms ve `Regex.Match` bunu duvar saati
olarak uyguluyor. CPU baskısı altında geçen süre pattern'in karmaşıklığından
bağımsız; işlem zaman dilimi alamadığında da sayaç işliyor.

Somut gözlem, makine swap %89'dayken tek oturumda:

| Gözlem | Sonuç |
| --- | --- |
| Aynı ikili, aynı 87 satırlık örnek kümesi, iki ardışık `parser coverage` | biri `ok 83 / failed 1`, öbürü `ok 84 / failed 0` |
| Düşen satırın parser'ı tek başına | sorunsuz ayrışıyor |
| `parser test` bir koşumda | düz literal alternasyonu bir pattern "zaman aşımına uğradı" dedi — lookaround yok, `NonBacktracking` ile derleniyor, girdide doğrusal |
| `dotnet test` bir koşumda | 4 test düştü; ardışık koşumda 301/301 geçti |

Bedeli üç yönlü yazılmıştı: (1) sonuç `failed`, yani *"motor meşguldü"* ile
*"bu satır bu parser'a uymuyor"* ayırt edilemiyor ve satır keşif kuyruğuna
düşüyor; (2) sürekli timeout veren parser karantinaya alındığı için sağlıklı bir
parser karantinaya girebilir; (3) `parser coverage` kapısı `failed > 0` ise
kırıyor, yani yüklü bir runner'da rastgele kırılma.

> **İkinci madde ölçüldü ve doğru değil** (T05, bu tur). Karantina hiçbir yerde
> sıcak yola bağlı değil: `ParserQuarantine` üretimde **hiç örneklenmiyor** ve
> `parsers.quarantined` kolonuna **kimse yazmıyor**. Yani sağlıklı bir parser
> karantinaya giremez — hiçbir parser giremez. Tehlike gerçek değil; **bekçi
> yok**. (`docs/epic/t05-kararlar/index.md` §4, ölçüm 4)

Maddenin bugünkü hâli, yazıldığı hâlinden farklı ve üç öneri de aynı yerde
durmuyor:

| Öneri | Durum |
| --- | --- |
| Doğrusal ifadede `Regex.InfiniteMatchTimeout` | **Uygulanmış.** `GrokCompiler:93`, gerekçesi yanında yazılı |
| Ayrı bir `engine_busy` statüsü | **Reddedildi.** Tüketicisi yok (§8) *ve* üreticisi ateşlenmezdi |
| Karantinanın orana bakması | **Reddedildi.** Sınıf zaten pencere içi sayı, yani bir hız — ve hiçbir yere bağlı değil |

Reddedilen ikisinin ortak önkoşulu vardı ve o kapandı: zaman aşımı bilgisi
dispatcher'da düşüyordu. Varsayılan `on_failure: fail` ile zaman aşımı `failed`
üretiyor, dispatcher da `failed` sonucu *"uymadı"* diye eleyip sıradakine
geçiyordu — bayrak da elenen sonucun içinde eleniyordu. Sevk edilen katalogda
**14 grok adımının hiçbiri `on_failure` yazmıyor**, yani bu yol istisna değil
**tek yol**du. `DispatchResult.TimedOutParsers` bilgiyi taşıyor.
(`docs/epic/t05-kararlar/index.md` §4, ölçüm 3)

Bu, [[concepts/sessiz-yanlis-davranis]] sınıfının bu dilimdeki en temiz örneği:
hata yok, sayaç yok, yalnızca farklı bir sonuç. Karantina bulgusu ise bir adım
ötesi — **belirti üretmeyen davranış değil, hiç var olmayan mekanizma**, üstelik
üç ayrı yerde (belge, linter mesajı, DB süzgeci) var gibi görünen.

## Örnek 3 — doğru biçim: mutlak bütçe yerine aynı süreçte oran

T29 ölçümü aynı dersi tasarımına yazmış: **mutlak bir bütçe yok — bilerek.**
Üretilen tek anlamlı çıktı, *aynı süreçte aynı satırlar üzerinde* alınmış bir
tabana **oran**.

Kurulumun her ayrıntısı bu karara hizmet ediyor:

| Ayrıntı | Gerekçe (belgeden) |
| --- | --- |
| 40.000 olay × 5 tur, **en küçük tur** raporlanıyor | girişim ölçümü yalnızca yukarı çeker |
| 5.000 olaylık ısınma | `RegexOptions.Compiled` kod üretimi + JIT |
| Girdi: katalogdaki **gerçek** vendor satırları | sentetik satır maskeleme maliyetini istediği yere çeker |
| Dört arm, hepsi tek süreçte | iki commit'i iki süreçte karşılaştırmak F1'in dersine göre geçersiz |
| **`-c Release` şart** | aşağıda |

`-c Release` bir detay değil ve gerekçesi **ölçülmüş**: Debug ölçümü *tek yönde*
yanıltıyor. Ayrıştırma bizim kodumuz ve Debug'da orantısız yavaşlıyor; maskeleme
BCL regex olduğu için etkilenmiyor — imza maliyeti iki derlemede de aynı çıktı
(5,94 → 5,93 µs). Yani Debug tabanı şişirip `C/B` oranını olduğundan **küçük**
gösteriyor, K35'i hak etmediği kadar ucuz gösteriyor.
(`docs/epic/t29-sicak-yol-olcumu/index.md` §3)

### Ve tek koşum yeterli sayılmadı

T29'un belgedeki sayısı `C/B = 1,46×` ve altında bir uyarı duruyor: bu bir
**doğrulama koşusu**, beş ajanın paylaştığı yüklü bir makinede alındı, mutlak
sayılar bağlayıcı değil.

İkinci koşum koordinatörde **1,62×** çıktı. `CLAUDE.md` §6 o koşumun neden
şüpheli olduğunu da yazıyor: ikinci koşumda *yalnız ayrıştırma* kolu
*ayrıştırma+etiketleme*'den yavaş göründü — fiziksel olarak imkânsız, yani makine
sessiz değildi. Kural buradan doğdu: **tek sayı seçmek yerine iki koşumu da
kaydet.** (`docs/epic/t12-kararlar/index.md` §5 ve `CLAUDE.md` §6)

## Ölçüt

Belgelerin ortak sorusu şu ve `CLAUDE.md` §6'da kural hâline gelmiş:

> **Test neyi ölçmek istiyor?** Duvar saati değilse süreyi denklemden çıkar,
> büyütme. Ölçüyorsa mutlak bütçe yerine **aynı süreçte alınan bir tabana oran**
> kullan.

"Büyütme" kısmı önemli: T05'in düzeltmesi bütçeyi 2 sn'den 10 sn'ye çıkarmak
değildi, bütçeyi **kaldırmaktı**. Süreyi büyütmek aynı testi yalnızca daha
seyrek yanıltıcı yapar. ^[inferred]

## Açık sorular

- 50 ms `matchTimeout` değerinin nereden geldiği üç belgede de *"kayıtta yok"*
  diye işaretli — bkz. [[concepts/olcum-gerekcesiz-sabit]]. Duvar saati sorunu
  çözülse bile o sayı yerinde kalıyor. ^[ambiguous]
- T05 bu soruyu **bilerek ikinci sıraya aldı**: *"kaç olmalı"*nın cevabı
  `matchTimeout`'un **ne ölçmesi gerektiğine** bağlı, ve bugün ürettiği bilgi
  kayda hiç girmediği için sayının ölçülebilir bir zemini yoktu. Zeminin ilk
  taşı (bilginin taşınması) bu turda kondu; sayının kendisi hâlâ açık.
  ^[ambiguous]

## Kaynaklar

- `docs/epic/t05-kararlar/index.md` — §2, `GrokPropertyTests`
- `docs/epic/t08-motor-geri-beslemesi/index.md` — §10
- `docs/epic/t08-kararlar/index.md` — §3, madde 10 (bugünkü durum: açık)
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §3, "Kurulum" ve "`-c Release` şart"
- `docs/epic/t07-kararlar/index.md` — §4.3, "oran değil varlık sınanıyor"
- `docs/epic/t12-kararlar/index.md` — §5, T29 devri
- `CLAUDE.md` §6
