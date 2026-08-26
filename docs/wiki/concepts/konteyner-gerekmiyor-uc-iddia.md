---
title: "\"Konteyner gerekmiyor\" üç ayrı iddiadır"
category: concepts
tags: [surec, test, kavram, bizigo]
aliases: [docker gerekmiyor, konteynersiz]
relationships:
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: related_to
  - target: "[[skills/f3-sigma-derleme-kapilari]]"
    type: related_to
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: related_to
  - target: "[[skills/f1-veri-kaybeden-depoyla-tasarim]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
sources: [CLAUDE.md]
source_digest: "sha256-12/v1 CLAUDE.md=ec55cf4c830b"
summary: Bu depoda üç ayrı sayfa aynı cümleyi kuruyor ve üçü farklı soruya cevap veriyor. Üçüncüsü yeni doğdu; ayrılmazsa biri diğerinden yanlış bir hak çıkarıyor.
provenance:
  extracted: 0.55
  inferred: 0.45
  ambiguous: 0.0
base_confidence: 0.62
lifecycle: draft
lifecycle_changed: 2026-08-26
tier: core
created: 2026-08-26T00:00:00Z
updated: 2026-08-26T00:00:00Z
---

# "Konteyner gerekmiyor" üç ayrı iddiadır

Bu depodaki üç sayfa neredeyse aynı cümleyi kuruyor — *"Docker gerekmiyor"*,
*"konteyner istemiyor"* — ve **üçü farklı bir soruya cevap veriyor**. Cümle
sabit, anlam değil.

| Nerede | Hangi soruya cevap | Hangi kararı besliyor |
| --- | --- | --- |
| [[skills/olcum-protokolu-sonuctan-once]] | *"Bu ölçüm neye bağlı?"* | Ölçümün **gerçekten koşulup koşulmayacağı** |
| [[skills/f3-sigma-derleme-kapilari]] | *"Bu kapı nerede koşuyor?"* | Kapının **yerleştirileceği aşama** |
| [[skills/f1-veri-kaybeden-depoyla-tasarim]] | *"Bu bekçiyi kim koşturabilir?"* | Ajan/koordinatör **bölünmesi** |

## 1 · "Bu ölçüm neye bağlı" — bağımlılık iddiası

T29'un notu: *"Docker, canlı sidecar, Python venv gerekmiyor: ölçülen şey saf
CPU işi. Ölçümü venv'e bağlamak, hiç koşulmamasının en kolay yolu olurdu."*

Buradaki iddia **koşum hakkı değil, koşum olasılığı**. Az bağımlılık taşıyan
bir ölçüm daha sık koşuluyor; ağır bağımlılığa bağlanan bir ölçüm, kimse
kurulumu yapmadığı için sessizce ölüyor.

Bu iddia yanlışlanırsa sonucu: ölçüm koşulmaz ve **kimse fark etmez**.

## 2 · "Bu kapı nerede koşuyor" — yerleşim iddiası

Sigma derleme kapılarında Kapı 1 derleme anında, hattın içinde koşuyor —
Docker yok, ClickHouse yok. **Kapı 2 ve 3 konteyner istiyor** ve CI'da ayrı
işlerde duruyorlar.

Yani o sayfadaki *"Docker yok"* cümlesi **üç kapının biri** hakkında ve
sayfanın kendi tablosunda ötekilerin konteyner istediği yazılı.

Bu iddia bir **tasarım tercihini** anlatıyor: bir kontrolü mümkün olan en erken
aşamaya koymak. Kimin koşturacağıyla ilgisi yok.

## 3 · "Bu bekçiyi kim koşturabilir" — **yeni doğan** iddia

T40'ın dokuz bekçisinden yalnızca biri gerçek depo istiyor; kalanı bellek içi
sahteyle koşuyor. Bu, yazıldığı gün bir **tasarım tercihiydi** — dayanağı F1
kapanışının notu: *beş hatanın dördü konteyner gerektirmeden yakalanabiliyordu*.

`CLAUDE.md` §2'nin ekseni *"hangi paket"*ten *"konteyner gerekiyor mu"*ya
çevrilince aynı cümle **bir hak** hâline geldi: konteyner istemeyen bir bekçiyi
ajan koşturabiliyor — Docker kapalıyken, ve yalnızca **geçen** test kanıt
sayılarak ([[skills/paralel-ajan-koordinasyonu]]).

**Bu sayfanın var olma sebebi bu üçüncü satır.** Önceki ikisi hep vardı;
üçüncüsü kural değişince doğdu. Yani cümle hiç değişmeden **anlam kümesi
büyüdü** — ve büyüyen bir anlam kümesi, eski cümleleri geriye dönük olarak
belirsizleştiriyor.

## Neden ayrılmaları gerekiyor

Ayrılmazlarsa yanlış çıkarım şu yönde işliyor:

> Biri Kapı 1'in *"Docker yok"* satırını okur, oradan §2'nin koşum hakkını
> çıkarır, ve **Sigma kapılarını** koşturmaya kalkar. Kapı 2 konteyner istiyor.

Docker kapalıysa Kapı 2 daemon'a bağlanamayıp düşer — yani mekanizma tutuyor ve
kimse zarar görmüyor. Ama **hata mesajı yanlış yere işaret ediyor**: kişi
"kapı bozuk" sanır, oysa çıkarımı bozuktu. Bu, bir arızayı veri arızası gibi
gösteren hata mesajı sınıfı.

Ters yön daha pahalı:

> Biri T29'un *"ölçüm Docker istemiyor"* cümlesini bir **yerleşim** iddiası
> sanır ve ölçümü derleme adımına taşır. Ölçüm her derlemede koşar, süre
> şişer, ve birileri onu bir bayrağın arkasına alır — sonra bayrak kapanır ve
> ölçüm hiç koşmaz. Başladığımız yere, sessizce.

## Ayırıcı soru

Bir *"konteyner gerekmiyor"* cümlesi gördüğünde sorulacak tek soru:

> **Bu cümle bir bağımlılığı mı, bir aşamayı mı, yoksa bir izni mi anlatıyor?**

Üçü farklı şeyi yanlışlar:

- **Bağımlılık** yanlışsa: kurulum gerekir, ölçüm koşmaz.
- **Aşama** yanlışsa: kapı yanlış yerde durur, geç yakalar ya da hiç yakalamaz.
- **İzin** yanlışsa: yanlış kişi koşturmayı dener; §2'nin mekanizması onu
  durdurur ama sebebini söylemez.

## Açık sorular

- Bu ayrım bir **bekçiye** çevrilebilir mi? Bugün yalnızca okuma disiplinine
  bağlı ve bu depoda okuma disiplinine bağlanan kurallar tekrar tekrar
  kaybetti ([[concepts/elle-tutulan-liste-bekciyi-korlestirir]]). Ama üç iddia
  da düz metinde yaşıyor; denetlenebilir bir yüzeyi yok. ^[ambiguous]
- Dördüncü bir anlam doğar mı? Üçüncüsü bir kural değişikliğiyle doğdu;
  aynı şey yeniden olabilir ve o gün bu sayfanın güncellenmesi gerekecek.
  ^[inferred]

## Kaynaklar

- `CLAUDE.md` §2 — ekseni "konteyner gerekiyor mu"ya çeviren üç madde
- [[skills/olcum-protokolu-sonuctan-once]] — T29'un bağımlılık notu
- [[skills/f3-sigma-derleme-kapilari]] — üç kapı, biri konteynersiz
- [[skills/f1-veri-kaybeden-depoyla-tasarim]] — T40'ın konteynersiz bekçileri
