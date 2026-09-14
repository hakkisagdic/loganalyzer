---
kind: spec
title: "Statü bekçisinin kör noktası — ölçüldü, kapanmıyor"
---

# Statü bekçisinin kör noktası

T61 statü bekçisini merge dal adlarının üstüne kurdu ve **kendi kör noktasını
bildirdi**: bir ticket'ın işi başka bir ticket'ın dalında main'e girerse kimlik
kaybolur ve bekçi sessiz kalır. Bugün canlı bir örneği var — T54'ün işi
`Merge branch 't44-llm-adimlari'` altında girdi (`d718ca7`) ve T54 `status: 0`
ile bayat kaldı, bekçi görmedi.

Çözüm olarak iki şey önerildi. **İkisi de ölçüldü ve ikisi de yetmiyor.**

## Aday 1 · Commit mesajı konvansiyonu (`T54: …`)

**Reddedildi**, ve gerekçe bu depoda yazılı: `pre-push` kancasının kendi
belgesi *"kişinin hatırlamasına dayanan her çözüm aynı yere gider"* diyor ve
o cümle dört kez ödenmiş bir olayın kaydı. Bir konvansiyonun tutmadığı gün
bekçi yine sessiz kalıyor — yani konvansiyon bekçiyi güçlendirmiyor, yalnızca
suçu paylaştırıyor.

## Aday 2 · Merge'in dokunduğu dosyalardan ticket'ı türetmek

Mekanik ve liste doğurmuyor: `git diff --name-only <merge>^1 <merge>` ile
dokunulan `docs/epic/tickets-*/<ticket>/` dizinleri okunabiliyor. Denendi:

| Merge | Kimlik | Dokunulan ticket dizini |
| --- | --- | --- |
| `d718ca7` `Merge branch 't44-llm-adimlari'` | T44 | `tickets-f4/rca-report-kaliciligi` (**T51**) |
| `76d86d7` `Merge branch 't56-vault-iddia-denetimi'` | T56 | **hiçbiri** |

**Yöntem T54'ü bulmuyor** ve sebebi mekanik: T54'ün işi **koddu**, kendi belge
dizinine dokunmadı. Bulduğu şey (T51) gerçek ve doğru — ama aradığımız değil.
T56'da ise hiçbir şey bulmuyor, çünkü T56'nın o gün ticket dosyası yoktu.

Yani yöntemin hata biçimi **yanlış suçlama değil, yine sessizlik**. T61'in
bulanık ad eşleştirmesini elerken kullandığı ölçüt burada da geçerli, ama
sonucu farklı: bu yöntem yanlış suçlamıyor, sadece işe yaramıyor.

## Karar: kör nokta kalıyor, **görünür** oluyor

Kapatmıyoruz. Onun yerine **ölçülebilir** kılıyoruz: bekçi çözebildiği kimlik
sayısını ve **çözemediği merge sayısını** birlikte raporluyor. Bugünkü ölçüm
zaten elimizde — 179 merge mesajının 27'si `Merge branch '<dal>'` biçiminde,
24'ü ayrıştırılabilir kimlik taşıyor.

Bu bir çözüm değil bir **sayaç**, ve ayrımı yazmak gerekiyor: sayaç düşerse
biri bakar, ama sayaç yükselirken kaçan bir ticket'ı hâlâ göremiyor. Kapının
kapatabildiği ile kapatamadığı arasındaki fark burada duruyor ve kapanmış gibi
okunmamalı.

## Kapanmayan ikinci kalem: `1 → 2`

T61 dört bayat statüyü `0`'dan çıkardı ama üçünü **`2` değil `1`** yaptı
(M04, M05, M08) ve gerekçesi doğru: kabul kriterlerini okumadı, koşturmadı,
`2` yazmak ölçmediği bir şeyi iddia etmek olurdu.

`1 → 2` bugün **mekanikleşmiyor** ve sebebi tanım: *"bitti"* bir ticket'ın
kabul kriterlerinin karşılandığı anlamına geliyor ve o kriterler düzyazı. T53
aynı yolu bir kez deneyip elemişti (*"ölçtüğü şeyden daha kırılgan bir
tahmin"*).

Dolayısıyla `1` bu depoda **iki farklı şeyi** birden anlatıyor: gerçekten
sürmekte olan iş, ve bitmiş ama doğrulanmamış iş. İkisini ayırmak yeni bir
olgu istiyor — bugün yok, ve yokluğu burada yazılı.
