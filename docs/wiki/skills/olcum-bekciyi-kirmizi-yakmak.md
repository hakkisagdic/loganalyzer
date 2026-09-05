---
title: Bekçiyi kırmızı yakmak
category: skills
tags: [test, surec, yordam, bizigo]
aliases: [bekçi doğrulama yordamı, mutasyon turu, yanlış pozitif kontrolü]
relationships:
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: uses
  - target: "[[concepts/olcum-kirmizi-yanamayan-sayi]]"
    type: related_to
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: related_to
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: extends
sources:
  - docs/epic/t28-denetim-bulgulari/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/t27-ad-govde-kesfi/index.md
  - docs/epic/t27-kapanis-taramasi/index.md
  - docs/epic/t01-kararlar/index.md
  - docs/epic/t07-kararlar/index.md
  - docs/epic/t09-kararlar/index.md
  - docs/epic/t10-kararlar/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=a06e12975995 docs/epic/t01-kararlar/index.md=d4ef9138571f docs/epic/t07-kararlar/index.md=f945b43c4277 docs/epic/t09-kararlar/index.md=b21d65bda95a docs/epic/t10-kararlar/index.md=98995baaaacf docs/epic/t27-ad-govde-kesfi/index.md=e14d05d8d833 docs/epic/t27-kapanis-taramasi/index.md=fc23cd892d11 docs/epic/t28-denetim-bulgulari/index.md=7f6ce6b9f3f7 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t39-alan-kapsami/index.md=7d06bfcf6e0c"
summary: Geçen bir test geçtiğini kanıtlar, kırılabildiğini değil. Bu depoda bekçiler koruduğu hata geri konularak sınanıyor; yanlış pozitif vermediği de ayrıca ölçülüyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.83
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:31:44Z
updated: 2026-08-24T17:31:44Z
---

# Bekçiyi kırmızı yakmak

`CLAUDE.md` §6'nın kuralı: **bekçinin kırmızı yanabildiğini ölç, sonra geri al.**
Bu dilimdeki dört belge o kuralı uygulamış ve uygularken **yordamın kendisini**
altı adıma çıkarmış.

## Adım 1 — koruduğu hatayı geri koy

T28 dokuz bulgunun sekizine bekçi yazdı ve **dokuzunu da tek tek kırdı**:

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

T29 aynısını üç bekçiye uyguladı (hash ham satırdan alınıyor → `SignatureHashTests`
5 iddia; `ParsingSink` imzayı yazmıyor → `SignatureHotPathTests` 7'de 5;
`ReplayDiff` imzayı karşılaştırmıyor → `ReplayDiffTests`). T39 blob kuralını
kaldırıp iki kutu-1 testinin düştüğünü ölçtü ve geri aldı.

### Adım 1.5 — kusurun dosyada **gerçekten** olduğunu iddia et

`CLAUDE.md` §6'ya 2026-08-26'da eklendi ve sebebi bir turda **üç bağımsız
örnek**: kırmızı ölçümünde yeşil bir sonuç iki şey anlatabiliyor — *"kusur
etkisiz"* ya da **"kusur hiç uygulanmadı"**. İkisi aynı çıktıyı veriyor ve
birincisi varsayılıyor.

| Ne oldu | Yeşilin sebebi |
| --- | --- |
| `python3 -c` içinde tırnak kaçışı tutmadı | Dosya **hiç değişmedi** |
| Kusur uygulandı ama ölçüme sabit bir zaman damgası verildi | Kova hiç değişmedi — *"kusur yok"* hâliyle **aynı şey** ölçüldü |
| Yeni kavram eklenirken onu görecek eski kod aranmadı | Kusur başka bir katmanda kaldı; birim paketi sessiz, CI kırmızı |

Yordam: kusuru yaz → dosyayı **oku** ve kusurun orada olduğunu **iddia et**
(`assert 'KIRMIZI' in dosya`) → koştur → geri al.

İddia adımı olmazsa ölçüm kendi başarısızlığını sessizce başarı diye
raporluyor. Yani **ölçüm aracının kendisi**
[[concepts/sessiz-yanlis-davranis]] sınıfına giriyor — bu sayfanın tarif ettiği
disiplinin kendi üstüne kapanan hâli.

> **Yeşil bir sonuç, ölçümün yapılmadığı anlamına da gelebiliyor.**

Üçünün de kendi ajanı tarafından yakalanması ayrıca bir veri: tuzak bir dikkat
eksikliği değil, **ölçüm yordamının eksik bir adımı**ydı. ^[extracted]

### Adım 1.6 — geri almanın derlemeye ulaştığını da gör

`CLAUDE.md` §6'ya 2026-09-05'te eklendi. Adım 1.5 kusurun **girdiğini**
kanıtlıyor; **çıktığını** kanıtlayan bir adım yoktu.

T44'ün ölçüm aracı geri almayı `shutil.copy2` ile yapıyordu ve o zaman
damgasını da geri yüklüyor: düzeltilmiş kaynak derlenmiş ikiliden **eski**
göründü, MSBuild projeyi atladı, `dotnet build` *0 hata 0 uyarı* dedi ve koşan
ikili hâlâ kusurluydu.

| | Kaynak | İkili | Görünen |
| --- | --- | --- | --- |
| `grep` | temiz | — | "geri alındı" |
| `build` | — | kusurlu, atlandı | "0 hata, 0 uyarı" |
| `test` | — | kusurlu | **kırmızı** |

Üç araç üç farklı şey söyledi ve üçü de kendi içinde doğruydu. Bu, Adım 1.5'in
sınıfının **tersten** hâli: orada yeşil bir sonuç *"ölçüm yapılmadı"*
olabiliyordu, burada kırmızı bir sonuç *"geri alma görülmedi"* oluyor — ve
ikincisi daha sinsi, çünkü kırmızı bir test insanı **kodun kendisini aramaya**
gönderiyor. Yakalayan şey bir bekçi değildi: geri aldıktan sonra tam paketi bir
kez daha koşturmaktı.

Kardeş vaka aynı turda FS-b'de çıktı ve geri almanın **aracını** hedefliyor:
`git checkout <dosya>` commit edilmemiş işin üstüne yazıyor — ajanın kendi
yazdığı `entrypoint.sh` bir önceki ticket'ın hâline döndü. Fark etti ve yeniden
yazdı, ama fark etmeyebilirdi. Ölçüm yordamı **yedek dosyaya** dayanmalı;
`git checkout` çalışma ağacının tamamına bakan bir araç ve tek bir kusurun geri
alınması için fazla geniş.

İkisi birlikte aynı şeyi söylüyor: *"geri aldım"* bir iddia, ve bu depoda
iddialar ölçülür. ^[extracted]

## Adım 2 — yanmaması gereken yerde yanmadığını da ölç

T28'in ayrı satırı, ve bu dilimin en dar dersi:

> **Ve bir kez, yanmaması gereken yerde yanmadığı ölçüldü:** çıplak
> `toISOString()` eklendiğinde zaman bekçisi yeşil kalıyor.

Gerekçesi de yazılı: *bir bekçinin yanlış pozitifi, kırmızı yanmamasından farklı
bir tehlike — insanlar onu susturmayı öğreniyor.* Yani yanlış pozitif, bekçiyi
uzun vadede [[concepts/elle-tutulan-liste-bekciyi-korlestirir]] hâline getiriyor.

## Adım 3 — yeşil kalıyorsa bekçi değil, şans ölçülüyordur

T30'un ölçüm aracında dört düzeltmenin dördü kırmızı yandı. **Beşincisi
yanmadı** — *"sondalar damga taşımıyor"* testi geri alındığında yeşil kaldı,
çünkü bugünkü örnek dosyalarında en uzun satırların ortası zaten damgasızdı.
Yani test filtreyi değil **verinin şansını** ölçüyordu.

Yapılan: ortası bilerek damga olan bir satırla ikinci bir test yazıldı; o kırmızı
yanıyor. Aracın test sayısı 13 → **19**.
(`docs/epic/t30-sigma-olcumu/index.md`)

## Adım 4 — bekçinin doğru **yeri** koruduğunu da tut

T28'in 8. bulgusu için iki test yazıldı. Birincisi `cellBody`/`cellNumeric`
sınıflarının `display` tanımlamamasını tutuyor; ikincisi o sınıfların **gerçekten
`<td>`'ye verildiğini**. Gerekçe belgede: *taşınsalar bekçi geçmeye devam eder
ama hiçbir şeyi korumazdı.*

Aynı sınıf T39'da "sürüklenemeyen iki liste" başlığıyla geçiyor: görünümün
kolonları göç dosyasından **okunuyor**; elle yazılsaydı görünüme eklenen bir
kolon hiç sorulmaz ve eksik tablo tam görünürdü — `Produces` kapısının 16 ucu
görmemesiyle aynı sınıf.

Üçüncü üye saklama süresi: `EventRetention` `TTL … INTERVAL N DAY`'i şemadan
okuyor. C# tarafına `90` yazmak, şema değiştiği gün kapının **yanlış cevabı
vermesi** demekti. Ve bulunamadığında *"sınırsız saklama"* varsayılmıyor,
**atılıyor** — okuyamama hâli sınırsızlık diye okunsaydı kapı hiç kapanmaz,
yani var olmayan bir kapı olurdu.

### İki yeni kapı, ikisi de "hata vermeyen kayıp"a karşı

T39'un iki eklemesi aynı sınıfı hedefliyor: ClickHouse'un **hata vermeden**
veri kaybettiği ya da çoğalttığı yollar.

| Kapı | Ne yakalıyor | Kırmızı yandığı ölçüldü |
| --- | --- | --- |
| TTL kapısı | Saklama süresinin dışındaki satır — ClickHouse kabul edip **siliyor**, yazan "yazdım" diye okuyor | `--span-days 100` → çıkış **1**, 2028 satırın 210'u dışarıda; varsayılan 30 gün → çıkış **0** |
| `--replace` doğrulaması | `ALTER TABLE … DELETE` bir **mutasyon**; `mutations_sync = 2` sunucu tarafında geçersiz kılınabiliyor ve beklemeden dönerse eski satırlar kalır, yeniler eklenir, **veri çoğalır** | Silmeden sonra sayım tekrarlanıyor; sıfır değilse yazıma hiç geçilmiyor |

İkincisinin izi ölçümde de görünüyordu: altın örnek sayıları bir gün **2476×3 =
7428** değil **7426** çıkmıştı — tam katı olmaması, kısmen tamamlanmış bir
mutasyonun bıraktığı iz.

## Adım 5 — kuralı yazmadan önce ölç; literal hâli yanlış olabilir

T28 iki kuralı **yazmadan önce** ölçüp daralttı:

| Kural | Literal hâli | Ölçüm | Sonuç |
| --- | --- | --- | --- |
| zaman biçimi | *"`format*` yalnızca `lib/ui/`de tanımlanabilir"* | `formatSeverity`/`formatParseStatus` alana özgü etiket tabloları | yasak `format*` değil **zaman görüntüleme** oldu |
| dört durum sırası | *"`EmptyState` çizen her ekran `screenState` kullanmalı"* | 16 dosyanın 13'ü işaretlendi; üçü açıldı, doğru sıradaydılar | yalnızca **veri çeken** ekranlar |

Ve tersi de yazılmış: T28'in *"iki şey bulgu değil çıktı"* bölümünde `AppShell`'in
ikinci kez kurulmadığı ölçülünce görüldü. Belgenin cümlesi — *ölçmeden teste
bağlansaydı, var olmayan bir sorunu sabitleyen ve bir gün gerçek bir değişikliği
engelleyecek bir test kalırdı.*

## Adım 6 — bekçi yazmamak da bir sonuç; gerekçesiyle yazılır

T28 dokuzuncu bulgu için **bilinçli olarak** bekçi yazmadı ve dört bilinçli açığı
gerekçeleriyle listeledi. Ortak gerekçe: *kırılgan bir bekçi, susturulmayı
öğrenilen bir teste dönüşür.*

T27'nin keşfi aynı kararı bir adım ileri götürüyor — ölçüm betiği `tools/` altına
bile konmadı:

> Koşulabilir bir şey, er ya da geç CI'a girer; CI'a giren bir şey yeşil/kırmızı
> olmak zorunda kalır; ve bu ölçüm %7 isabetle kırmızı yanamaz. Her zaman yeşil
> yanan bir "bekçi" ise bu deponun adını koyduğu hata sınıfı.

## Ölçmediğini yaz

Yordamın son parçası rapor biçiminde. Dört ticket belgesi aynı cümleyi taşıyor:
**"Bu bekçilerin kırmızı yanabildiğini bu turda ölçmedim."**
(`t01`, `t07`, `t09`, `t10`) Belge geriye dönük; kod okundu, ölçüm yapılmadı.

T27'nin taraması bunu bir bölüm hâline getirmiş: ayrı bir **"Aramadım"** başlığı,
ve içinde hangi kalemin neden kapanmadığı. `CLAUDE.md` §10'un *"aradım, yok"* ile
*"aramadım"* ayrımının uygulanmış hâli.

### İşaretin yanında kapsamı da yaz

Ayrım raporun tamamı için geçerli ama bir **"ilk bakılacak yer"** notunun
yanında ayrıca söylenmesi gerekiyor: tek satırlık bir işaretin arkasında iki
saatlik bir eleme de olabilir tek bir sezgi de, ve okuyan ikisini ayırt
edemeyip **birincisini varsayıyor**. Biçim:

> **İlk bakılacak yer:** X. **Aradım ve elemedim:** Y, Z. **Aramadım:** W.

Gerekçe S04'te ölçüldü. Bir ajan `sir-dondu` testinin maskeleme biçimini işaret
etti; işaret **doğruydu** ve koordinatör doğrudan oraya baktı. Ama aynı turda
**üç testi birden düşüren** şey başka bir yerdeydi — baseline'ın iki gösterimi —
ve ajan onu aramamıştı. Yazmadığı için de kimse aramadığını bilmiyordu.

Yanlış işaret işaretsizlikten kötüdür; **kapsamsız doğru işaret** de aynı yöne
çekiyor. İşaretin değeri, arkasındaki elemenin genişliğiyle birlikte okunuyor.

Ve aynı belgede sonucu: T27'de yazılan dört entegrasyon testi o dalda
koşturulmadığı için ticket `status` 1'de bırakıldı — *"koşturulmamış bir testi
yeşil sayıp ticket'ı kapatmak, bu belgenin baştan sona karşı çıktığı şeyin
kendisi olurdu."*

## Kontrol listesi

- [ ] Hatayı geri koy, **dosyada gerçekten olduğunu iddia et**, kırmızı yandığını gör, geri al.
- [ ] Yanmaması gereken bir değişiklikle yeşil kaldığını gör.
- [ ] Yeşil kaldıysa: bekçi mi kırık, yoksa veri mi şanslı?
- [ ] Bekçinin bağlı olduğu yerin taşınabileceğini düşün; yeri de tut.
- [ ] Kuralın literal hâlini ölç, gerekiyorsa daralt.
- [ ] Bekçi yazmamaya karar verdiysen gerekçesini yaz.
- [ ] Ölçmediğini "ölçmedim" diye yaz; "aradım, yok" ile karıştırma.
- [ ] Bir yeri işaret ediyorsan **aradıklarını ve aramadıklarını** da yaz.

## Kaynaklar

- `docs/epic/t28-denetim-bulgulari/index.md` — "Bekçiler kırmızı yanabiliyor — ölçüldü", "Bilinçli açıklar"
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §5
- `docs/epic/t30-sigma-olcumu/index.md` — "Bekçilerin kırmızı yanabildiği ölçüldü"
- `docs/epic/t39-alan-kapsami/index.md` — "Aracın kendi sessiz yalanı", "Sürüklenemeyen iki liste"
- `docs/epic/t27-ad-govde-kesfi/index.md` — "Neden sevk edilmedi"
- `docs/epic/t27-kapanis-taramasi/index.md` — §4 "Aramadım", §5
- `docs/epic/t01-kararlar/index.md` — §3 sonundaki ölçmedim notu
- `CLAUDE.md` §6, §10
