---
title: "T26 — Change: cihaz config fark tespiti"
kind: ticket
status: 2
---

# T26 — Change: cihaz config fark tespiti

**Bağımlılık:** T25 · **Sonraki:** T27
**Yöneten karar:** K34

## Amaç

RCA'nın en değerli kanıt kaynağı: "şu hata arttı" ile "çünkü şu değişti"
arasındaki fark. Ağ cihazının config'i değiştiğinde bunu **kendiliğinden**
yakalamak.

<user_quoted_section>⚠️ Bu ticket F2'nin en büyük tek parçası ve kendi başına bir alt sistem.Cihaz erişimi, kimlik yönetimi, vendor başına farklı toplama yöntemi ve farkalgoritması içeriyor. F2'nin sonuna konuldu ki ondan önceki her şey kendibaşına çalışır durumda olsun — bu ticket kayarsa F2 yine de teslim edilebilir.</user_quoted_section>

## Kapsam

### İçinde

- Periyodik config çekimi, T25'in connector zamanlaması üzerinden.
- Vendor başına toplama yöntemi. F1'in kataloğuyla aynı sırayla başlanmalı:
**FortiGate, Cisco ASA, MikroTik** — parser'ları zaten var, yani cihazları
zaten tanıyoruz.
- Normalize fark alma: gürültü (zaman damgası, sayaç, oturum kimliği) elenip
**anlamlı** değişiklik çıkarılıyor.
- Fark → `change_events` kaydı: hangi cihaz, hangi bölüm, ne değişti.
- Config anlık görüntülerinin saklanması ve saklama süresi.

### Dışında

- Cihaza **yazma** — bu ürün config değiştirmiyor, yalnızca okuyor.
- Config uyumluluk denetimi ("standarda uygun mu") — F2'de değil.

## Kabul kriterleri

- Üç vendor'un en az birinde gerçek (ya da gerçeğe birebir benzeyen) config
üzerinde fark doğru çıkarılıyor.
- Gürültü elenmesi sınanmış: yalnızca zaman damgası değişen iki çekim
**değişiklik üretmiyor**. Bu olmadan tablo işe yaramaz gürültüyle dolar.
- Cihaza erişilemediğinde connector hata kaydediyor ve çekim döngüsü ölmüyor.
- Kimlik bilgisi hiçbir log, hata mesajı veya `change_events` kaydında
görünmüyor.
- Çekim maliyeti sınırlı: yüzlerce cihazda eşzamanlılık limiti var.

## Notlar

Bu, ürünün cihazlara bağlanan **ilk** parçası — okuma amaçlı da olsa güvenlik
incelemesi gerektiriyor. Kimlik bilgileri T25'in şifreli deposunda durmalı ve
en dar yetkiyle (salt okuma hesabı) kullanılmalı.

Vendor başına yöntem farklı: SSH + komut, REST API, ya da SNMP. Üçünü tek
soyutlamaya sıkıştırmaya çalışmak erken genelleme olur — önce iki vendor somut
yazılıp ortak yüzey oradan çıkarılmalı.
