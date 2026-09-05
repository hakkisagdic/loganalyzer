---
title: "S07 — İmzalı webhook üreteci"
kind: ticket
status: 2
---

# S07 — İmzalı webhook üreteci

**Bağımlılık:** S01 · **Sonraki:** — (FS-b)

## Amaç

GitHub / GitLab / Jenkins / generic imzalı webhook üreteci — yani T24'ün
alıcısını **gönderen tarafından** sınamak.

## Neden bu ticket, alıcı zaten test edilmişken

Bu soru soruldu ve cevabı ticket'ın var olma sebebi:

T24'ün testleri **kendi kurdukları gövdeyi kendi imzalıyor**. Yani imza
doğrulamasının doğru olduğunu değil, **kendi kendiyle tutarlı** olduğunu
kanıtlıyorlar. GitHub'ın gerçekte hangi başlığı hangi biçimde gönderdiği o
testlerden okunamaz.

Üreteç bunu ayırıyor: gövdeyi ve imzayı **vendor'ın belgelediği biçimde**
kuruyor, alıcı onu tanımak zorunda kalıyor. İkisi ayrıştığı gün fark
görünür oluyor.

Ayrıca F4'ün dört tetikleyicisinden biri **değişiklik olayı** ve o bu yoldan
besleniyor — S07 olmadan F4'ün o tetikleyicisi test edilemez.

## Kapsam

### İçinde

- Dört biçim: GitHub, GitLab, Jenkins, generic.
- İmza, **vendor'ın belgelediği algoritma ve başlık adıyla**.
- Aynı teslimatı iki kez gönderebilme (idempotans sınaması için).

### Dışında

- Alıcı tarafında değişiklik — T24 kapalı, bu ticket onu **sınıyor**, değiştirmiyor.
  Değiştirmesi gerekiyorsa bu bir bulgudur ve ayrı bir kalem açar.

## Kabul kriterleri

- İmza doğrulaması **dört biçimde de** geçiyor.
- Aynı teslimat iki kez gönderilince **tek** kayıt oluşuyor.
- Bozuk imza reddediliyor — ve reddin sebebi *"imza tutmadı"*, genel bir
  ayrıştırma hatası değil.
- Üretecin kurduğu gövde, vendor belgesinden alınmış **gerçek bir örneğe**
  karşı sınanıyor; kendi ürettiğine karşı değil.

  Bu madde ticket'ın tamamının anlamı: kendi ürettiğini doğrulayan bir üreteç,
  T24'ün testlerinin yaptığı şeyi ikinci kez yapardı.

## Sonuç

Üreteç `sim/Bizigo.Simulators/WebhookDeliveryFactory.cs`. Komut satırından:

```bash
dotnet run --project sim/Bizigo.Simulators -- \
    --profile fw-ankara-01 --webhook github \
    --url http://127.0.0.1:8080/v1/changes/webhooks/ci --secret anahtar --times 2
```

### Kendi kendini doğrulama tuzağından nasıl kaçıldı — iki ayrı çapa

Ticket'ın var olma sebebi *"T24'ün testleri kendi kurdukları gövdeyi kendi
imzalıyor"*. Bir üreteç yazmak bu tuzaktan **kendiliğinden** kurtarmıyor: aynı
imza fonksiyonunu çağıran bir üreteç, aynı tutarlılığı üçüncü kez ölçerdi.

1. **İmza yeniden yazıldı**, `WebhookSignature.Compute` çağrılmadı. Bağımsız
olan şey algoritma değil (HMAC-SHA256 tek bir şey), **biçim**: hangi başlık,
hangi önek, hex mi base64 mü, büyük harf mi küçük harf mi. Sağlayıcıların
ayrıştığı yer tam olarak orası.
2. **Gövdenin şekli sağlayıcıların yayımladığı yüke karşı sınanıyor**
(`tests/Bizigo.UnitTests/Fixtures/webhooks`). Karşılaştırma yönü
**üreteç ⊆ gerçek**: üretecin uydurduğu bir alan kırmızı yanıyor.

Ters yön (gerçek ⊆ üreteç) bilerek sınanmıyor — gerçek yükler üretecin hiç
ihtiyaç duymadığı onlarca alan taşıyor (`node_id`, `queue_id`, `artifacts`).
Eksiklik ayrı bir testin işi: üretilen gövde ile gerçek gövde **aynı `details`
anahtarlarını** üretmek zorunda.

### Dört biçim, dört ayrı karar

| Sağlayıcı | İmza | Başlık |
| --- | --- | --- |
| GitHub | HMAC-SHA256, `sha256=` önekli **küçük harf** hex | `X-Hub-Signature-256` |
| GitLab | **HMAC yok** — paylaşılan jeton düz metin | `X-Gitlab-Token` |
| Jenkins | HMAC-SHA256 — sağlayıcının standardı yok, başlık bizim | `X-Bizigo-Signature` |
| Generic | HMAC-SHA256 | `X-Bizigo-Signature` |

GitLab'ın satırı bir eksiklik değil bir **olgu** ve görünür olması gerekiyordu:
jeton gövdeye bakmadığı için GitLab'da gövde değişikliği imzayı düşürmüyor.
Ürünün TLS zorunluluğu tam olarak bunun için var.

Üç zaman biçimi de ayrı: GitHub ISO-8601, GitLab `"2026-08-18 09:19:47 UTC"`,
Jenkins epoch **milisaniye**. Üreteç hepsini ISO yazsaydı ürünün
`ParseTimestamp`'indeki iki dal hiç koşmazdı.

### Ölçülen bekçiler (kusur uygulandı, dosyada olduğu iddia edildi, geri alındı)

| Kusur | Kırmızı yanan |
| --- | --- |
| Üreteç uydurma bir alan basıyor | `Uretecin_bastigi_her_yol_gercek_govdede_de_var` |
| Eşlemenin `details`'e koyduğu alan düşüyor (`head_sha`) | `Uretilen_govde_gercek_govdeyle_ayni_alanlari_esliyor` |
| Jenkins epoch **saniye** yazıyor | aynı test — zaman "şimdi"ye düşüyor |
| GitHub imzası `sha256=` öneksiz | `Github_imzasi_kucuk_harf_hex_ve_onekli` |
| GitHub imzası **büyük harf** hex | aynı test |
| Üreteç belirlenimsiz (`Random` gövdede) | `Ayni_istek_ayni_baytlari_uretiyor` |

**Bir ölçüm yeşil kaldı ve sebebi kayda değer:** `sha256=` önekini düşürmek
`Imza_dort_bicimde_de_dogrulaniyor`'u **düşürmüyor**, çünkü alıcı öneksiz hâli
bilerek kabul ediyor (elle `openssl dgst` çıktısı yapıştırılabilsin diye).
Yani doğrulama testi biçim hatasını göremiyor — **görebilen ayrı bir biçim
testi olmak zorunda**, ve o yüzden var. Affedilen biçim hataları en uzun
yaşayanlar.

### Ürün doğruydu, testim yanlış varsaymıştı

İlk yazımda *"farklı teslimat kimliği farklı anahtar üretir"* diye bir iddia
vardı ve Jenkins'te **düştü**. Sebep bir kusur değil, tasarım: Notification
Plugin aynı yapıyı üç fazda üç kez gönderiyor ve alıcı mükerrerliği faz
üzerinden değil **yapı kimliği** üzerinden çözüyor (`{iş}#{numara}:{durum}`).
İddia düzeltildi ve davranış ayrı bir testle **çivilendi** —
`Jenkins_ayni_yapiyi_ikinci_bildirimde_tekillestiriyor` — çünkü yazılı
olmasaydı bir sonraki kişi de aynı yanılgıya düşerdi.

### Yapılmadı — ve kararı koordinatörün

**"İki POST → tek kayıt" gerçek Postgres'e karşı bir testte durmuyor.** Sebep
yapısal: `Bizigo.IntegrationTests` **on altı** proje referansı taşıyor ve
`src/` altındaki tek dışlanan proje `Bizigo.Api`. Yani dışlama bilinçli
görünüyor, ve `ChangeWebhookMapper`/`WebhookSignature` orada.

Kabul kriteri yine de karşılanıyor, ama **iki parçada**:

- **Kimliğin kararlılığı** birim paketinde: aynı teslimat aynı `DeliveryId`,
farklı olay farklı.
- **Kısıtın tekilleştirdiği** `ChangeWebhookDeliveryTests`'te, gerçek
Postgres'e karşı — anahtarlar elle kuruluyor.
- **Uçtan uca** CLI'dan: `--times 2` ikinci gönderimde `201` görürse çıkış
kodu `6` veriyor.

Eksik olan tek şey ikisinin **aynı testte** buluşması. Referans eklemek
üründe yapısal bir sınırı kaldırmak demek ve §9 gereği tek başıma
genişletmiyorum.
