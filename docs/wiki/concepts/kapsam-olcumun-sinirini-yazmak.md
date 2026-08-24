---
title: Ölçümün sınırını yazmak
category: concepts
tags: [test, log-analiz, kavram, bizigo]
aliases: [ölçemedim ile sorun yok, sadakat seviyeleri, N1 N2 N3]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: extends
  - target: "[[concepts/kapsam-kestirme-besleme-yolu]]"
    type: related_to
  - target: "[[skills/kapsam-kapanacak-ile-kapanmayacagi-ayirmak]]"
    type: related_to
sources:
  - docs/epic/fs-simulatorler/index.md
  - docs/epic/t36-devir-notu/index.md
  - docs/epic/is-envanteri/index.md
  - docs/epic/devir-notu-kota-kesintisi/index.md
source_digest: "sha256-12/v1 docs/epic/devir-notu-kota-kesintisi/index.md=769f322740d7 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048 docs/epic/t36-devir-notu/index.md=5027982dbb85"
summary: Bir ölçüm neyi kanıtlamadığını söyleyemiyorsa "ölçemedim" ile "sorun yok" aynı çıktıya iniyor. Simülatör sadakat seviyeleri, kanıt paketindeki dört durum ve ölçüm aracının kendi sessiz yanlışı aynı kuralın üç yüzü.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.78
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Ölçümün sınırını yazmak

Bu depoda bir ölçümün çıktısı iki soruya birden cevap vermek zorunda: *ne
buldun* ve **ne bulamayacağın belliydi**. İkincisi yazılmadığında ortaya
[[concepts/sessiz-yanlis-davranis]]'ın ölçüm katmanındaki hâli çıkıyor — kota
devir notu bunu bir hata sınıfı olarak adlandırmış durumda:

> **Ölçüm aracının kendi sessiz yanlışı** — dört örnek, ortak imza:
> *"ölçemedim"* ile *"sorun yok"* aynı çıktıya iniyor.

Üç ayrı belge aynı kurala üç ayrı yerden varıyor.

## 1 · Sadakat seviyesinin sınırı testin yorumunda durur

`docs/epic/fs-simulatorler/index.md` §5 üç simülatör seviyesini yan yana
koyuyor ve tabloya **"Neyi kanıtlamıyor"** diye bir kolon açıyor — kapsam
disiplini tam olarak o kolon:

| Seviye | Kanıtlıyor | **Kanıtlamıyor** |
| --- | --- | --- |
| **N1** süreç içi sahte | Toplayıcı, normalize edici, fark motoru, maskeleme | SSH'ın kendisi |
| **N2** gerçek SSH sunucusu | Kimlik doğrulama, komut çalıştırma, çerçeveleme, zaman aşımı | Vendor kabuğunun davranışı |
| **N3** CLI öykünmesi | Toplayıcının gerçek bir kabukla baş edip edemediği | Cihazın kendi hataları |

Belgenin kendi uyarısı: N1 ile yazılan bir test *"cihazdan config çekiliyor"*
**diyemez**; diyebileceği şey *"toplayıcı verilen çıktıyı doğru işliyor"* — ve
"ikisini ayıran cümle testin özet yorumunda duracak". Sebep açıkça yazılı:
adı ile gövdesi ayrışan bir bekçinin yeşilliği hiçbir şey ifade etmiyor.

Aynı belgenin §11'i simülatörün **hiç** kapatamayacağı üç şeyi ayrıca sayıyor
— vendor sürüm farkları, ölçek, ağ gerçekliği — ve şunu ekliyor: bu fazdan
çıkan hiçbir sayı bağlayıcı değil. Bu, sınırın *ölçülmüş hâli*.

## 2 · "Veri yok" tek bir değer değil

`docs/epic/t36-devir-notu/index.md` §0 aynı kuralı ürünün yüzünde uyguluyor ve
bir değişmez olarak çiviliyor: dört durum — `Empty`, `NeverFed`,
`Unavailable`/`Failed`, `NotRegistered` — tek bir "veri yok" kutusuna
indirgenemez.

En pahalısı `NeverFed`: *"değişiklik akışı hiç beslenmemiş"* cümlesi ekranda
"değişiklik olmadı" diye görünürse, kullanıcı **en güçlü sinyalin yokluğunu
bir bulgu sanar**. Rapor bu ayrımı `Silent` (koştu, bulamadı) ve
`NotConsulted` (bakılamadı) olarak zaten ayırıyor — aynı ayrımın F3
boyunca kaç yerde kurulduğu [[concepts/f3-bosluk-tek-cins-degildir]]'de; devir notunun tek cümlelik
talimatı: *"İkisini tek listede birleştirmeyin."*

Zaman tarafında aynısı `WindowTrust` ile yapılmış:

| | Ekranda |
| --- | --- |
| `Measured = false` | "ölçülemedi — bilinmiyor" |
| ölçüldü, güvenilmez = 0 | **uyarı yok** |
| ölçüldü, güvenilmez > 0 | sayı + oran + kayma uyarısı |

`UnreliableRatio` ölçülmediyse `null` — **sıfır değil**. Ve ortadaki satır:
"her raporda duran bir uyarı hiçbir şey söylemez", aynı gerekçeyle
`OutOfScopeCount` sıfırken satır hiç yazılmıyor (§7).

## 3 · Anlamsız cevap veren gösterge de bir sınır ihlali

`fs-simulatorler` §1'deki üç ölçüm bu sayfanın en somut örneği:

```
envanter BOŞ              → "parser bağlama oranı"  %100
envanter 4 kaynak, bağsız → %0
envanter 4 kaynak, BAĞLI  → %0   ← değişmedi
```

Üçü de ürünün gerçek bağlama oranı hakkında hiçbir şey söylemiyor: `%100`
paydanın sıfır olması, `%0` ise sayacın envanteri değil **koşan sürecin
dağıttığı satırları** okuması. Gösterge "bu soruyu şu an cevaplayamam"
diyemediği için üç kez cevap veriyor.

## 4 · Düzeltmenin biçimi: yokluk kanıtı yerine varlık kanıtı

`docs/epic/is-envanteri/index.md` iki ölçüm aracının kendi sessiz yanlışını
tabloya döküyor ve **ikisinin düzeltmesinin de aynı biçimde** olduğunu
söylüyor:

- Sigma kapsam ölçümünün ön kontrolü *"tablo boş mu"* diye soruyordu (yokluk
  kanıtı); artık altın örnekten türetilmiş bir **sondayı gövdede arıyor**.
- Alan kapsamı ölçümünde `attrs['message']` satırın birebir kendisiydi, yani
  gövdede hiçbir aralık boşta kalmıyordu; artık gövdenin kopyası kapsama
  **sayılmıyor**.

Ve tavsiyeden mekanizmaya geçen üçüncü örnek: baseline süpürmesi tek `--zipf`
ile koşsaydı düzgün bir dirsek raporlayacaktı ve o sayı verinin değil
**tohumlama parametresinin** özelliği olurdu. Önleyen şey bir uyarı değil bir
**imza** — `BaselineFixtureVerdict.Compare` iki eğri olmadan derlenmiyor.
Sonuç da dürüsttü: *"SEÇİLEBİLİR TABAN YOK."*

## Uygulanabilir kural

1. Her ölçüm aracına *"ölçemediğimi söyleyebiliyor muyum?"* sorusunu sor.
2. Cevap hayırsa, sınırı **yorumla değil imzayla** kur — eksik girdiyle
   derlenmeyen bir tip, unutulabilen bir uyarıdan iyidir.
3. "Yok" cevabını sebebine göre ayır; ayrımı hem API'de hem ekranda hem
   export'ta taşı.
4. Her koşumda duran uyarıyı sil — sabit uyarı bilgi taşımıyor.
5. Ölçülen sayının bir eşiği yoksa ölçüm yarımdır:
   [[concepts/olcum-kirmizi-yanamayan-sayi]].

Bekçi katmanındaki kardeşi: [[concepts/elle-tutulan-liste-bekciyi-korlestirir]].
Aynı sorunun besleme yolundaki hâli:
[[concepts/kapsam-kestirme-besleme-yolu]].

## Açık sorular

- Bu sınır cümlelerinin yazıldığını denetleyen bir bekçi yok; bugün disipline
  bağlı. `CiCoverageTests` benzeri yapısal bir karşılığı olabilir mi? ^[inferred]

## Kaynaklar

- `docs/epic/fs-simulatorler/index.md` — §1 anlamsız gösterge, §5 sadakat
  seviyeleri, §11 kapanmayacak sınırlar
- `docs/epic/t36-devir-notu/index.md` — §0 dört durum, §6 `WindowTrust`,
  §7 kapsam dışı dürüstlüğü
- `docs/epic/is-envanteri/index.md` — ölçüm aracının kendi sessiz yanlışı,
  D6 baseline süpürmesi
- `docs/epic/devir-notu-kota-kesintisi/index.md` — §6 hata sınıfları tablosu
