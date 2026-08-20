---
title: "T39 — `specificity` seçim ölçütü: kim, neye göre?"
kind: ticket
status: 0
---

# T39 — `specificity` seçim ölçütü: kim, neye göre?

**Bağımlılık:** — · **Sonraki:** —
**Kaynak:** [T06 kararları](../../t06-kararlar/index.md) — *"seçim ölçütü kayıtta yok"*

## Amaç

`metadata.specificity` bugün üç yerde çalışıyor: şema kabul ediyor, katalog
dolduruyor, dispatcher sıralıyor. **Eksik olan tek şey, bir sayının neden o sayı
olduğu.** Yeni bir vendor parser'ı yazan kişinin başvuracağı hiçbir ölçüt yok;
mevcut değerlere bakıp benzetiyor.

Bugün kimseyi yakmıyor. Katalog sekiz parser ve aday çakışması pratikte
oluşmuyor. Katalog yüzlerce parser'a çıktığında (K12) yakacak.

## Bugünkü durum — ölçüldü

Sekiz parser, sekiz değer:

| Parser | Değer | Gerekçe kayıtlı mı |
| --- | --- | --- |
| `cisco.asa.auth` | 95 | ✅ *"Ağ parser'ından yüksek: gövde pattern'leri çok daha dar"* |
| `fortinet.fortigate.event` | 90 | ✅ *"Traffic parser'ından yüksek: `logdesc` literalleri çok daha dar"* |
| `cisco.asa.network` | 85 | ❌ |
| `mikrotik.routeros.firewall` | 80 | ❌ |
| `mikrotik.routeros.system` | 80 | ❌ |
| `fortinet.fortigate.traffic` | 70 | ❌ |
| `nginx.access.json` | 60 | ✅ *"Combined'dan yüksek: JSON'u önce denemek bir grok denemesini atlatıyor"* |
| `nginx.access.combined` | 50 | ❌ |

### Geriye dönük çıkarılabilen ve çıkarılamayan

**Çıkarılabiliyor:** *vendor içi* sıralama. Üç yorumun üçü de aynı biçimde
göreli — "şundan yüksek, çünkü daha dar". Yani ölçüt fiilen **darlık**: dar
kapsamlı parser genel olandan önce denenmeli, ki genel olan dar satırı
sahiplenmesin.

**Çıkarılamıyor, üç ayrı soru:**

1. **Mutlak değerler.** Neden 95 ve 90, 2 ve 1 değil? Aralarındaki 5'in bir
anlamı var mı? Hiçbir yerde yazmıyor.
2. **Vendor'lar arası karşılaştırma.** `cisco.asa.auth` 95, `fortinet.fortigate.event`
90. İkisi de "dar kimlik doğrulama parser'ı". ASA'nın FortiGate'ten önce
denenmesinin bir sebebi var mı, yoksa sayılar bağımsız mı seçildi?
3. **Eşitlik.** İki RouterOS parser'ı da 80. Bu bilinçli bir eşitlik mi
(*"ikisi aynı derecede dar"*), yoksa ikisi sırayla yazılırken aynı sayı mı
kopyalandı? Kayıtta yok.

   Eşitlikte sıralamanın **ne olduğu** ise belli: `BuildSnapshot`
   `.ThenBy(p => p.Id, StringComparer.Ordinal)` uyguluyor, yani tekrarlanabilir
   ve dosya okuma sırasına bağlı **değil**. Ama sıralama ölçütü alfabetik, yani
   *belirli* ama **anlamlı değil**: `…firewall` `…system`'den önce deneniyor,
   çünkü `f` < `s`. Soru "tekrarlanabilir mi" değil, "eşitliğe izin verilmeli mi"
   — çünkü eşitlik, kararı sessizce alfabeye devrediyor.

### Ölçütün pratik ağırlığı hakkında bir gözlem

Dispatcher `specificity`'yi yalnızca **kademe 3'te** kullanıyor: literal ön
filtreden geçen adaylar arasında. Ön filtre vendor'ları zaten büyük ölçüde
ayırıyor (`%ASA-`, `logdesc=`, `firewall,info`, `HTTP/1.1"`), dolayısıyla
**vendor'lar arası sıralama pratikte nadiren devreye giriyor.**

Bu, 2. sorunun cevabını değiştirebilir: eğer vendor'lar arası karşılaştırma
hiçbir zaman koşmuyorsa, o sayıları anlamlandırmaya çalışmak boşa emek olur ve
doğru cevap "ölçüt **vendor içi**dir, dışı tanımsızdır" olabilir. **Bu bir
varsayım, ölçülmedi.**

## Kapsam

### İçinde

1. **Ölçütü yaz.** Darlık nasıl ölçülür — literal sayısı, pattern'in bağlılığı
(`^`/`$`), yakalanan alan sayısı, yoksa yazarın kararı mı? Ölçüt öznelse bunu
söyle; öznel bir ölçüt yazılı olmayan bir ölçütten iyidir.
2. **Vendor'lar arası sıralamanın gerçekten koşup koşmadığını ölç.** Kataloğun
altın örneklerini dispatcher'dan geçirip kademe 3'e kaç satırın düştüğüne ve o
satırlarda kaç adayın yarıştığına bak. Sayı sıfırsa 2. soru kapanır.
3. **Eşitliğe izin verilip verilmeyeceğine karar ver.** Bugün eşitlik
tekrarlanabilir biçimde çözülüyor (kimliğe göre alfabetik) ama **anlamlı
biçimde** değil: karar sessizce alfabeye geçiyor. Ya eşitlik meşru sayılıp
"alfabetik" belgelenir, ya eşitlik yasaklanır ve bir bekçi tutar.
4. **Kim karar verir.** Değer parser'ın kendi dosyasında; yazan kişi koyuyor.
İnceleme sırasında sorgulanıyor mu, yoksa geçiyor mu? T18'in yayın kapısı
`specificity`'ye hiç bakmıyor — bakmalı mı?
5. **Bugünkü sekiz değeri gerekçelendir ya da değiştir.** Ölçüt yazıldıktan
sonra dördü gerekçesiz kalıyorsa, ya gerekçe yazılmalı ya değer düzeltilmeli.

### Dışında

- `match.contains` literallerinin seçimi — ayrı bir konu (T08 raporu #4).
- Kademe 1/2'nin davranışı — bu ticket yalnızca kademe 3'ün sıralamasıyla ilgili.

## Kabul kriterleri

- Ölçüt `catalog/parsers/README.md`'de yazılı ve yeni bir parser yazan kişi ona
bakarak sayı koyabiliyor.
- Vendor'lar arası sıralamanın koşup koşmadığı **ölçülmüş** ve sonucu yazılı.
- Eşitlik ya belgelenmiş ya yasaklanmış; hangisi olursa olsun bir test tutuyor.
- Bugünkü sekiz değerin her birinin ya gerekçesi var ya değeri değişti.

## Notlar

**Bu ticket bir kusuru düzeltmiyor, bir boşluğu kapatıyor.** Bugün yanlış çalışan
bir şey yok — kimse yanlış sıralamadan şikâyetçi değil ve altın örneklerin
tamamı doğru parser'a düşüyor. Kapatılan şey, katalog büyüdüğünde **kimsenin
cevaplayamayacağı bir soru**.

Bu yüzden "ölçüt öznel" de meşru bir cevap. Meşru olmayan tek cevap, bugünkü
hâl: ölçüt var gibi davranmak ama hiçbir yerde yazmamak.
