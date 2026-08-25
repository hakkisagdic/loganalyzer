---
title: Gerekçesi kayıtta olmayan sabit
category: concepts
tags: [surec, mimari, kavram, bizigo]
aliases: [sihirli sayı, ayarlanmamış eşik, kayıtta yok kalemi]
relationships:
  - target: "[[concepts/olcum-kirmizi-yanamayan-sayi]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: related_to
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: related_to
sources:
  - docs/epic/t01-kararlar/index.md
  - docs/epic/t02-kararlar/index.md
  - docs/epic/t03-kararlar/index.md
  - docs/epic/t04-kararlar/index.md
  - docs/epic/t05-kararlar/index.md
  - docs/epic/t06-kararlar/index.md
  - docs/epic/t07-kararlar/index.md
  - docs/epic/t09-kararlar/index.md
  - docs/epic/t10-kararlar/index.md
  - docs/epic/t12-kararlar/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
source_digest: "sha256-12/v1 docs/epic/t01-kararlar/index.md=d4ef9138571f docs/epic/t02-kararlar/index.md=b3cf84aad7ba docs/epic/t03-kararlar/index.md=0eafdbfe4ce2 docs/epic/t04-kararlar/index.md=f5befc3ecf24 docs/epic/t05-kararlar/index.md=fa28e6db349e docs/epic/t06-kararlar/index.md=3a34fdcba4c4 docs/epic/t07-kararlar/index.md=f945b43c4277 docs/epic/t09-kararlar/index.md=b21d65bda95a docs/epic/t10-kararlar/index.md=98995baaaacf docs/epic/t12-kararlar/index.md=454710cef295 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f"
summary: "Ticket belgelerinin çoğunda bir 'gerekçesi kayıtta yok' tablosu var. Bedeli T04'te ölçüldü — birbirine bağlı üç sayı birbirinden habersiz seçilmiş ve koruma penceresi kapanmıyor."
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

# Gerekçesi kayıtta olmayan sabit

Bu dilimdeki ticket belgeleri ortak bir bölümle bitiyor: **"gerekçesi kayıtta
olmayanlar".** Tek tek okununca her biri küçük bir kalem; yan yana konunca
deponun en tekrarlayan boşluğu.

Belgelerin hepsi geriye dönük yazıldığı için sebebi de yazılı: *o an tartışılıp
reddedilen alternatifler kayıtta yok.* Ama boşluk yalnızca alternatifte değil —
**seçilen sayının kendisinde**.

## Envanter

| Sabit | Nerede | Belge |
| --- | --- | --- |
| `matchTimeout = 50 ms` | grok motoru | `t05-kararlar` §1, §4 · `t08-motor-geri-beslemesi` §10 |
| `ScrubSampleSize = 20` · `SegmentRetention = 48:00:00` | ham arşiv | `t04-kararlar` #2, #3 |
| `MaxTotalBytes = 8 GB` · `RetryAfterSeconds = 5` | WAL / ingest kapısı | `t03-kararlar` #5 |
| `PermitLimit = 4` · `QueueLimit = 8` | hız sınırı | `t10-kararlar` §2.6, §4.5 |
| `FailureThreshold=5` · `BreakDuration=5dk` · `Timeout=2sn` · `QueueCapacity=2048` · `SampleRate=%1` · `TemplateCacheCapacity=50 000` | sidecar | `t12-kararlar` §3 |
| `sparseGrams(3, 20, 5)` | tam metin indeksi | `t02-kararlar` #2 |
| `CA1863 = suggestion` · `InvariantGlobalization=false` · `rollForward: latestFeature` | derleme ayarları | `t01-kararlar` §2.2, §4 |
| `admin`'in kapsam muafiyeti · denetim alanlarının `Truncate` edilmesi | kimlik | `t09-kararlar` §2.3, §4.4 |
| `specificity` sıralamasının nasıl belirlendiği | dispatcher | `t06-kararlar` §6 |
| `core` kümesinin neden tam bu on bir alan olduğu | normalizasyon | `t07-kararlar` §4.5 |

## Üç farklı boşluk, tek başlık altında

Belgeler farkı kendileri çiziyor:

1. **Sayı var, gerekçe yok.** `PermitLimit=4`. T10 hız sınırının *kullanıcı
   başına* olmasının gerekçesini yazıyor (küresel bir sınır kalabalık bir ekibi
   tek kişilik bir ekiple aynı kefeye koyardı, risk #6) — ama 4 ile 8'in nereden
   geldiğini yazmıyor.
2. **Gerekçe var, ölçüm yok.** T07: ticket `core` kümesi için *"sorguların
   ~%90'ı bunlara vuruyor"* diyor; **ölçümün kaynağı kayıtta yok.**
3. **Bilinçlilik yazılı, sebep yazılı değil.** T09: `admin` rolünün kapsam
   filtresinden muaf olması için kod yorumu *"BİLİNÇLİ ve tek yerde"* diyor —
   *"kararın bilinçli olduğu yazılı, sebebi değil."*

Üçüncüsü en sinsi: bir yorumun varlığı gerekçenin de yazıldığı izlenimini
veriyor. ^[inferred]

## Bedeli bir kez ölçüldü — T04'ün üç sayısı

Tek tek gerekçesizlik pahalı olmayabilir. **Birbirine bağlı sabitlerin ayrı ayrı
gerekçesizliği** pahalı, ve T04 bunu ölçmüş:

- `ScrubSampleSize = 20`, scrub 6 saatte bir koşuyor, `SegmentRetention = 48 saat`.
- Yani 48 saatte **160 nesne** taranıyor; ~64 MB'lık nesnelerle **~10 GB**.
- Arşiv bundan büyükse tam tarama, kurtarma penceresinden **uzun sürüyor** — ve
  kayıp, WAL segmenti silindikten *sonra* fark ediliyor.

Belgenin cümlesi: **"Üç sayı birbirinden habersiz seçilmiş."**

Aynı belge ikinci bir kusuru daha kaydediyor ve o da aynı kökten:
`DeleteExpiredSegmentsAsync` silme kararını yalnızca `VerifiedAt`'e bakarak
veriyor, `State` sorguya hiç girmiyor — yani **kaybın tespit edilmiş olması
kurtarma kaynağını korumuyor**. (`docs/epic/t04-kararlar/index.md`, açıkta
kalanlar)

T03 aynı şeklin ikinci örneğini taşıyor ve **ölçülmemiş** bırakıyor: 8 GB'ın kaç
dakikalık akışa karşılık geldiği ve 5 saniyenin collector'ın yeniden deneme
aralığıyla ilişkisi. İki sayı zincirin iki ucunda duruyor ve aralarındaki bağ
kayıtta yok. ^[inferred]

## Gerekçesiz sabit bir sonraki ölçümün girdisi oluyor

T12 sidecar'ın altı sabitinden hiçbirinin gerekçesinin koddan okunmadığını
yazıyor ve örnek olarak `SampleRate = %1`'i seçiyor: *bir ölçüme mi dayanıyor,
tahmine mi — bilinmiyor.*

T29 aynı `%1`'i sıcak yol ölçümünün kurulumunda **"bugünün gerçek profili"** diye
kullanıyor. Ölçüm doğru — bugünkü davranışı ölçüyor — ama gerekçesiz sabit
böylece ölçülmüş bir sayının içine yerleşiyor ve bir daha sorulmuyor. ^[inferred]
(`docs/epic/t12-kararlar/index.md` §3, `docs/epic/t29-sicak-yol-olcumu/index.md` §3)

## Karşı örnek: bu depo gerekçe yazmayı biliyor

Boşluk bir disiplin eksikliği değil, çünkü aynı belgelerde gerekçenin **kodun
içinde** durduğu örnekler var:

- `.editorconfig`'deki her CA kuralı gerekçesini satır sonunda taşıyor:
  CA1848/CA2007/CA1062 neden `none`, CA1304–CA1311 neden `error`
  (`tr-TR`'de `I → ı` ve aramanın sessizce ıskalaması). (`t01-kararlar` §2.2)
- `WriteAheadLog` grup commit'i **reddedilen alternatif olarak** yorumda duruyor,
  yanında *"ölçmeden yapılmaz"* notuyla. (`t03-kararlar`)
- `RawRefFor` iki alternatifi de tartıp neden alınmadıklarını yazıyor — T07'nin
  ifadesiyle *"reddedilen seçeneklerin koda yazıldığı tek yer."* (`t07-kararlar` §2.3)
- `ORDER BY (owner_group, source_id, ts)` DDL'i üç adayı ve seçim gerekçesini
  birlikte veriyor; T02 bunu *"deponun en iyi yazılmış karar notu"* diye anıyor.

Yani ayrım, sayıya mı yoksa yapıya mı karar verildiği. Yapısal kararlar gerekçe
almış; **eşikler ve tamponlar almamış.** ^[inferred]

## Açık sorular

- `sparseGrams(3, 20, 5)` için T02 *"indeks boyutu ölçülmedi — üç sayının
  seçilme sebebi olan asıl soru"* diyor. T10 §4.2 ise `idx_body` 13,3 MiB /
  tablo 29,4 MiB (indeks tablonun %45'i) diye bir ölçüm **veriyor**. İkisinin
  aynı şeyi ölçüp ölçmediğini doğrulamadım; ya T02'nin açık kalemi eskimiş, ya
  iki ölçüm farklı sorulara bakıyor. ^[ambiguous]
- Bu envanteri bir kapıya çevirmek mümkün mü — yoksa
  [[concepts/olcum-sayi-kapsam-degil]]'de anlatılan %7 isabet tuzağına mı düşer?
  Belgelerde denenmiş değil. ^[inferred]

## Uygulanabilir kural

Bir sabit yazarken ikisinden biri kodun yanında dursun: **ölçümün kendisi**, ya
da *"ölçmeden değiştirilmez"* notu (T03'ün grup commit kalıbı). Ve **birbirine
bağlı sabitler birlikte seçilsin** — T04'ün üç sayısı ayrı ayrı makuldü, birlikte
çalışmıyordu.

## Kaynaklar

- `docs/epic/t04-kararlar/index.md` — açıkta kalanlar, "Üç sayı birbirinden habersiz"
- `docs/epic/t03-kararlar/index.md` — açıkta kalanlar #5; WAL grup commit notu
- `docs/epic/t12-kararlar/index.md` — §3
- `docs/epic/t09-kararlar/index.md` — §2.3, §4.4
- `docs/epic/t10-kararlar/index.md` — §2.6, §4.5
- `docs/epic/t01-kararlar/index.md` — §2.2, §4
- `docs/epic/t02-kararlar/index.md` — `ORDER BY` notu, açıkta kalanlar #2
- `docs/epic/t05-kararlar/index.md` — §1, §4
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §3, örnekleme oranı
