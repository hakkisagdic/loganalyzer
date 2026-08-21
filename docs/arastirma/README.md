# Araştırma: 100 mikroservislik bir estate'te kod bilgisi

Soru şuydu: *bizim gibi bir kurumda 100 mikroservis ve kurumsal altyapı
servisleri olsaydı, uçtan uca bilgi alma ve kod araştırma için ne yapılmalı,
hangi araçlar uygun?*

Cevap [`kenar-guveni-mimarisi.md`](kenar-guveni-mimarisi.md) — L0'dan L10'a
bağımlılık sıralı, katmanlı bir referans mimari.

Okunabilir hâli: <https://claude.ai/code/artifact/3fb1cbad-6935-4326-ab1d-45722e363dce>

## Nasıl üretildi

Altı ayrı mercek paralel tarandı — ölçekte kod arama, kod grafı ve statik
analiz, servis kataloğu/IDP, sözleşme kayıt defteri, çalışma zamanı topolojisi,
AI ajan yüzeyi. **Her merceğin ardından onu çürütmeye çalışan bir doğrulama
ajanı** koştu: lisans ve fiyat iddiaları (modeller burada bayat bilgi
tekrarlıyor), ölçek kanıtı, ürünün hâlâ yaşayıp yaşamadığı, MCP/API yüzeyinin
gerçekten var olup olmadığı. Sonra bir eksiklik eleştirmeni "ne atlandı" diye
sordu ([`eksiklik-elestirisi.md`](eksiklik-elestirisi.md)), ve sentez bunların
üstüne yazıldı.

13 ajan tamamlandı, **1'i düştü**.

## Bu belgeyi okurken bilinmesi gerekenler

**Doğrulama aşamasının bir ajanı 529 hatasıyla düştü** — servis kataloğu
merceği. O merceğin iddiaları (Backstage, Cortex, OpsLevel, Port ve benzerleri
hakkında olanlar) adversarial kontrolden **geçmedi**. Belgedeki ağırlıkları
buna göre okunmalı. Bu, yeniden koşturulduğunda kapanabilecek bir boşluk;
kapanana kadar açık.

**Yeşil ve sarı çipler kökeni işaretliyor.** Belgede 40 `[doğrulandı]` ve 12
`[doğrulanmadı]` / `[doğrulanamadı]` işareti var. Bu, belgenin kendi tezinin
kendisine uygulanmış hâli: her kenar kökenini taşımalı, yoksa "bu doğru mu"
sorusunun cevabı asla evet olamaz.

**İddialar kaynağa bağlı DEĞİL, ve bu bilinen bir eksik.** Çipler bir iddianın
doğrulama turundan geçtiğini söylüyor; hangi kaynaktan, hangi sürümden ya da
hangi tarihte geçtiğini söylemiyor. İnceleme bunu haklı olarak işaretledi.
Sebebi sonradan düzeltilebilecek bir şey değil: taramayı koşturan iş akışının
şeması iddia başına kaynak URL'si **toplamıyordu**. Sonradan dipnot uydurmak,
olmayan bir kesinliği varmış gibi göstermek olurdu. Kapanması için tarama, alan
başına kaynak isteyen bir şemayla yeniden koşmalı.

**Hiçbir deney koşturulmadı.** Kullanıcı açıkça deney istememişti. Yani
buradaki her şey tarama, doğrulama ve muhakeme — bu depoda ölçülmüş bir sayı
değil. Belgenin 7. bölümü doğrulanamayan sayıları tek tek sayıyor.

## Üretilmiş dosyalar

Her ikisi de **üretilmiştir**, elle düzenlenmez:

| Dosya | Ne için |
| --- | --- |
| `kenar-guveni-mimarisi.html` | **Tam belge** — `<!doctype>`, `lang="tr"`, UTF-8. Depodan `file://` ile doğrudan açılan kopya bu. |
| `kenar-guveni-mimarisi-artifact.html` | **Parça** — `<title>` ile başlıyor, iskeleti yok. Artifact platformu kendi `<html>`/`<head>`/`<body>`'siyle sarıyor. |

İkisi ayrı çünkü gereksinimleri karşıt: doğrudan açılan bir dosyada `<!doctype>`
yokluğu tarayıcıyı quirks mode'a düşürüyor, sarılan bir parçada ise kendi
`<!doctype>`unu koymak `<body>` içine ikinci bir belge gömmek oluyor.

Kaynak markdown değişirse:

```bash
python3 docs/arastirma/uret.py
```

Üreteç yollarını kendi konumundan türetiyor; mutlak yol yok. Bu bilinçli —
`.githooks/post-commit` içindeki sabitlenmiş mutlak yol tam olarak bu yüzden
incelemede düştü, ve bir üretecin yalnızca yazanın makinesinde çalışması,
çalışmadığı hiçbir yerde görünmez.
