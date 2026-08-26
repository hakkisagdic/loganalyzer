---
title: "T48 — `Produces<T>` kapısının kör noktası: dördüncü tekrar"
kind: ticket
status: 0
---

# T48 — Kapının asgari servis listesi ile uç dosyaları arasına bir bağ

## 1 · Bu ticket bir kusur değil, bir tekrar sayısı

`ProducesContractTests` uçları bulmak için minimal API'yi yansıtmayla geziyor
ve bunun için **asgari bir servis listesi** tutuyor. Bir uç dosyası listede
olmayan bir servisi enjekte ederse, minimal API o parametreyi **gövde
parametresi** sanıyor ve o dosyadaki **bütün uçlar** kapıya görünmez oluyor.

Bu delik dört kez açıldı:

| # | Eksik servis | Bedeli |
| --- | --- | --- |
| 1 | `AlertPreview` | Uçlar kapıya görünmez |
| 2 | `CatalogCoverageCache` | Aynı |
| 3 | `ParserPublishGate` | Aynı |
| 4 | `RcaAdmission` | `EvidenceEndpoints.cs`'in **tamamı** — 15 test birden düştü |

**Her seferinde bulan kişi farklı.** Yani sorun dikkat değil, bir mekanizmanın
yokluğu. Dördüncü tekrar bir kalıp değil bir karar noktası.

## 2 · Neden bu deponun en tehlikeli kapı sınıfı

Kapının kendisi §7'nin *"bir bekçinin sessizce atlaması"* örneği olarak zaten
kayıtlı: liste elle tutulduğu için **16 uç kapıya hiç görünmedi ve üç test de
yeşildi.** Yeşilliği hiçbir şey ifade etmiyordu.

Şimdiki hâlinde iki katman var ve ikisi de sessiz:

1. **Uç görünmez olur** — kapı onun sözleşmesini hiç sınamaz.
2. **Kapı yine yeşil kalır** — çünkü göremediği bir şeyi eksik saymıyor.

Dördüncü açılışta 15 test düşmesi bir iyileşme değil **tesadüf**: `RcaAdmission`
bir *gövde* parametresi gibi görünecek şekle sahipti ve yansıma patladı. Şekli
biraz farklı olsaydı uçlar sessizce kaybolurdu.

## 3 · Ne isteniyor

**Uç dosyasının enjekte ettiği servisler ile kapının asgari listesi arasına bir
bağ.** Bağın şekli açık bırakılıyor; üç aday var ve seçimi ticket sahibi
gerekçeleyecek:

| Aday | Ne yapar | Riski |
| --- | --- | --- |
| **A · Listeyi türet** | Servisleri DI kayıtlarından ya da uç imzalarından **keşfet**, elle listeden değil | Keşif yüzeyi kapının kendi kör noktasını devralabilir |
| **B · Sayım kapısı** | Kapının **kaç uç gördüğünü** sabitle; sayı düşerse kırmızı | Sayı elle tutulan ikinci bir liste olur — ama tek bir sayı, ve `ExpectedExemptCount` emsali var |
| **C · Dosya kapsaması** | Her uç **dosyasının** kapıda en az bir ucu olduğunu sın | Boş bir dosya ya da tek uçlu bir dosya yanlış güven verir |

**A ve C birlikte muhtemelen doğru cevap** ama ölçülmeden seçilmemeli. B'nin
tek değeri şu: **bugün ölçülebilir ve hemen kırmızı yanabiliyor**, diğer ikisi
yazılana kadar bir emniyet ağı sağlıyor.

## 4 · Kabul kriterleri

1. Bir uç dosyasına listede olmayan bir servis eklemek **kırmızı yakıyor**, ve
hata mesajı **hangi dosya** ve **hangi servis** olduğunu söylüyor. Bugünkü hata
ucu değil **parametre çıkarımını** gösteriyor ve okuyan kişiyi yanlış yere
gönderiyor.
2. Kırmızı yanabildiği **ölçülmüş** ve rapora yazılmış (§6).
3. Kapının **göremediği** bir uç kalırsa bu **sayılıyor** — yani kapı kendi
kapsamını beyan ediyor. Beyan etmeyen bir kapı, kapsamını iddia etmiş sayılır.
4. Seçilen adayın **kaçırdığı hâl yazılı**. Üç adayın üçü de bir şey kaçırıyor;
kaçırdığı yazılmayan bir bekçi, bu deponun beş kez adını koyduğu şeye dönüşüyor.

## 5 · Kapsam dışı

- `Produces<T>` bildirimlerinin **içeriğini** doğrulamak — bu kapı zaten var.
- `Pending` / `Exempt` listelerinin bakımı — ayrı disiplin, §8'de yazılı.

## 6 · Ne ölçüldü, ne arandı

**İlk bakılacak yer:** `ProducesContractTests`'in asgari servis listesi ve
yansıma yolu.

**Aradım ve elemedim:** dört olayın dördü de aynı mekanizmadan doğdu —
farklı uçlar, farklı ajanlar, tek sebep. Bedelin dördüncüde büyümesi mekanizma
değişikliğinden değil, eksik servisin **şeklinden** kaynaklandı.

**Aramadım:** aynı yansıma kalıbını kullanan başka bir kapı var mı — `ui/`
tarafındaki sözleşme kapıları ve `ArchitectureTests` bu açıdan taranmadı. Aynı
kör noktanın ikinci bir örneği orada duruyor olabilir.
