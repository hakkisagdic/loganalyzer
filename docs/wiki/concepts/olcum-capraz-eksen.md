---
title: İki bağımsız eksen
category: concepts
tags: [test, log-analiz, kavram, bizigo]
aliases: [çapraz doğrulama, metin ekseni ve alan ekseni, ayrışma sayacı]
relationships:
  - target: "[[concepts/olcum-sayi-kapsam-degil]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: extends
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: related_to
sources:
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
  - docs/epic/t08-kararlar/index.md
  - docs/epic/t12-kararlar/index.md
  - docs/epic/t01-kararlar/index.md
source_digest: "sha256-12/v1 docs/epic/t01-kararlar/index.md=d4ef9138571f docs/epic/t08-kararlar/index.md=0ef90b5576bc docs/epic/t12-kararlar/index.md=454710cef295 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t39-alan-kapsami/index.md=7d06bfcf6e0c"
summary: Aynı soruya iki bağımsız yoldan bakmak, tek bir aracın "buldum" demesinden farklı bir kanıt üretiyor — ve bir eksenin sistematik sapması ancak öbür eksenden görünüyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.8
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:31:44Z
updated: 2026-08-24T17:31:44Z
---

# İki bağımsız eksen

Bu dilimde iki farklı ihtiyaç aynı çözüme çıkıyor: bir soruya **ortak varsayım
paylaşmayan iki yoldan** bakmak. Biri doğrulama için (iki uygulama ayrışabilir),
öbürü teşhis için (bir eksenin körlüğü ancak öbüründen görünüyor).

## Teşhis — metin ekseni ile alan ekseni

T30 ve T39 aynı soruyu soruyor: *bu Sigma kuralı neden hiçbir satır bulmadı?*
İki araç iki farklı eksende bakıyor:

| Araç | Eksen | Sorduğu |
| --- | --- | --- |
| `explain_misses.py` (T30) | **metin** | kuralın aradığı dizge örnek dosyalarda var mı, sözcük sınırında mı yoksa daha uzun bir sözcüğün içinde mi |
| `bizigo fields coverage` (T39) | **alan** | o bilgi bir alan olarak adreslenebiliyor mu, hangi adla |

T39'un ifadesi: *"İki araç farklı eksende bakıyor ve tam da bu yüzden birbirini
tamamlıyorlar."* Metin ekseni `absent` kutusunu kapatıyor; alan ekseni kapanmayan
`present` kutusunun **içini** üçe bölüyor ve her biri farklı bir iş emri veriyor.

### Bir eksenin sistematik sapması

Ve kritik bulgu: metin ekseninin **kendi körlüğü** ancak alan ekseninden
görülüyor.

`fortigate_user_auth_fail` kuralı `status: 'failure'` arıyor; ham FortiGate
satırı `status="failed"` yazıyor. Metin ekseni *"yok"* dedi ve kural `failed`'a
çevrildi. **Düzeltme kuralı bozdu:** `catalog/mappings/auth_outcome.yaml` ingest
sırasında `failed → failure` çeviriyor, yani kolonda duran değer zaten
`failure`'dı. `failed` o tabloda bir **anahtar**, bir çıktı değil — kolonda
hiçbir zaman durmuyor. Kural baştan doğruydu; geri alındı.

T39'un genellemesi: bir eşleme tablosu cihazın sözcüğünü normalleştiriyorsa,
kuralın aradığı normalleştirilmiş değer ham satırda **hiç geçmez** ve `absent`
görünür. `absent` kutusu bu yüzden bir **üst sınır** — payda etkisi
[[concepts/olcum-sayi-kapsam-degil]]'de.

T30 aynı sınıfı 11. tuzak olarak kaydetmiş ve maliyeti aynı: *"doğru kural bu
sebeple bir kez bozuldu."*

### İki yöntemin aynı bulguya varması

`Reset-I` iki kez, iki yöntemle bulundu: biri örnek dosyayı gözle okuyarak (`RST`
yalnızca `first`/`burst` içinde geçiyor), öbürü kapsanmamış metin aralıklarını
sayarak. T39'un notu: *"Yöntemler ortak hiçbir varsayım paylaşmıyor. Bu, tek bir
aracın 'buldum' demesinden başka bir şey."*

## Doğrulama — iki uygulama ayrışabilir, ayrışma sayılmalı

Aynı fikir ürün tarafında üç kez uygulanmış:

**T12 · iki maskeleme.** İmza .NET tarafında yerelde hesaplanıyor, sidecar
**kendi** maskelemesini uyguluyor. İki uygulama demek, ayrışabilirler demek — ve
o ayrışma `SignatureDrift` sayacıyla açıldı. Belgenin cümlesi: *sayaç olmasa iki
taraf sessizce farklı şablonlar üretir ve `template_id` anlamını yitirirdi.*
`MasksVersion` de aynı sebeple var.

**T29 · hash'in veritabanına karşı doğrulanması.** `signature_hash` XXH64, seed 0,
UTF-8 baytlar — ClickHouse'un `xxHash64()`'ü ile **birebir** olmak zorunda;
ayrışırsa *veritabanına karşı doğrulama imkânı kaybolur*. Altın vektör belgede
duruyor: `XXH64(UTF-8("<IPV4>")) = 14733834131172344067`, ve
`SELECT xxHash64('<IPV4>')` aynısını veriyor. `SignatureHashStorageTests` bunu
her golden örnekte doğruluyor.

**T01 · test yığını ile geliştirme yığını.** `DevStackFixture` imaj sürümlerini
`deploy/docker-compose.yml` ile aynı tutuyor ve gerekçe yorumda: *test ile
geliştirme ortamı ayrışırsa testin değeri düşer.* Aynı disiplin CI'ın
`sigma-explain` işinde tekrarlanıyor.

Üçünün ortak şekli: **iki gerçeğin tek olması gerekiyorsa, tekliği bir mekanizma
tutmalı** — sayaç, altın vektör ya da sürüm eşlemesi. Yoksa ayrışma
[[concepts/sessiz-yanlis-davranis]] sınıfına düşüyor. ^[inferred]

## Çapraz kontrolün sınırı da yazılıyor

T39'un iki yarısı (katalog yarısı ClickHouse'suz, ClickHouse yarısı canlı)
birbirini denetliyor ama karşılaştırma **varlık düzeyinde**, oran düzeyinde
değil. Gerekçe: tohumlama Zipf ağırlıklı, yani aynı alanın veritabanında bambaşka
bir oranla dolu olması arıza değil. Arıza olan tek şey, katalogda dolan bir
alanın veritabanında **hiç** dolmaması.

Ve iki eksenin sayıları da karıştırılmamalı: T39 iki tablosundaki `unmapped`
anahtar sayılarının **aynı şeyi saymadığını** uyarı olarak basıyor.

Aynı belge bir eşleştirmeyi bilerek **yapmıyor**: eşanlamlı tablosu yok, çünkü
hangi `unmapped` anahtarının hangi OCSF kolonuna karşılık geldiğini iddia etmek
*"ölçümün cevaplaması istenen soruyu ölçümün girdisine taşımak olurdu."* Tek
istisna yargı gerektirmeyen **biçim** farkı (`proto_token=UDP` ↔
`connection_info_protocol_name=udp`), ve o da `[biçim: …]` diye işaretleniyor.

## Ne zaman ikinci eksen yazılmaz

T39 sınırı açıkça çiziyor: alan düzeyindeki kesişim ölçümü *değer* düzeyine
inmiyor, çünkü *"üçüncü bir değerlendirici yazmak §9'un yasakladığı şey"* —
ürünün SQL yolu zaten kural değerlendirmesi yapıyor. İkinci eksen bir **ikinci
kopya** olmamalı; farklı bir soruyu farklı bir varsayım kümesiyle sormalı.

## Açık sorular

- ~~T39 kesişim ölçümünde MikroTik'in 12 çifti…~~ — **ölçüldü.** Çiftler artık
  iki kolonu dolduran **parser kümelerine** göre ayrılıyor: kümeler ayrıksa
  ayrılık kalıcı, kesişiyorsa bugünkü örneklemin tesadüfü. Sonuç **19 çiftin
  19'u da kalıcı**, tesadüf sıfır. Ve belirleyici olan parser *sayısı* değil:
  dört vendor'ın dördünde de iki parser var, ama nginx'in ikisi **aynı konuyu**
  iki biçimde anlatıp aynı kolonları doldurduğu için **hiç çift üretmiyor**.
  Diğer üçü konuyu bölüyor (kimlik ↔ ağ, olay ↔ trafik, sistem ↔ güvenlik
  duvarı). Yani kaynak, cihazın davranışı değil **kataloğun parser'ları neye
  göre böldüğü** — `docs/epic/t08-kararlar/index.md` §2'nin kararının ölçülmüş
  bir yan etkisi. Yeni bir vendor eklendiğinde sorulacak soru: *parser'lar
  biçim mi bölüyor, konu mu?*

## Kaynaklar

- `docs/epic/t39-alan-kapsami/index.md` — "Sigma tarafının üç kutusuyla eşleşme", "Metin ekseni yanılıyor", çapraz kontrol
- `docs/epic/t30-sigma-olcumu/index.md` — 10. ve 11. tuzak
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §1 sözleşme tablosu, §5
- `docs/epic/t12-kararlar/index.md` — §4
- `docs/epic/t01-kararlar/index.md` — §2.4
