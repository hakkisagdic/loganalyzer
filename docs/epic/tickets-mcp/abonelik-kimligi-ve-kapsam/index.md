---
title: "Abonelik kimliği ve kapsam süzgeci"
kind: ticket
status: 0
---

# Abonelik bildiriminin kimliği ve kapsamı

M07 abonelik kanalını kurdu ve **iki sınırını ölçtü**. İkisi de bir özellik
eksiği değil bir **mekanizma** sorusu, ve ikisinin de çözümü aynı yerde:
`SubscriptionsListenHandler`'ı SDK'dan devralmak.

Bu ticket o devralmayı **değerlendiriyor** — kararı vermiyor. Ölçüm yapılmadan
verilecek karar, M07'nin kaçındığı şeyin aynısı olurdu.

## 1 · Bugünkü hâl — ÖLÇÜLDÜ

Ölçümler `McpResourceSubscriptionTests` içinde ve **ham JSON-RPC** ile yapıldı
(sebebi §4).

| Ne | Ölçülen |
| --- | --- |
| Abonelik kurulumu | `subscriptions/listen` + `resourceSubscriptions` · **çalışıyor** |
| Sunucunun onayı | `subscriptions/acknowledged`, **etiketli** (`_meta` subscriptionId taşıyor) |
| Bizim bildirimimiz | `notifications/resources/updated`, **etiketsiz** |
| Kapsam süzgeci | **yok** — bildirim bütün abonelere gidiyor |
| Bayrağın etkisi | `SupportsSubscription` kapalıyken sunucu aboneliği **onaylamıyor** |

`resources/subscribe` ve `resources/unsubscribe` çivilediğimiz revizyonda
(`2026-07-28`, SEP-2575) **kaldırılmış**; sunucu eski metodu göç ipucuyla
reddediyor.

## 2 · Birinci sınır — bildirim etiketlenmiyor

Spesifikasyon her abonelik bildiriminin
`_meta/io.modelcontextprotocol/subscriptionId` taşımasını istiyor: istemci aynı
kanalı paylaşan abonelikleri **ancak öyle** ayırabiliyor (özellikle stdio'da, tek
kanal).

Erişebildiğimiz tek yayın ilkeli oturum geneline yazan
`McpServer.SendNotificationAsync`. SDK'nın kendi yönlendirmesi
(`SendSubscriptionNotificationAsync`, `ActiveSubscription`) **`internal`**.

**Bugünkü sonuç:** tek aboneliği olan istemci için fark yok; aynı kanalda iki
abonelik açan istemci bildirimleri ayırt edemez.

## 3 · İkinci sınır — kapsam süzgeci yok

Bildirim **içerik taşımıyor**, yalnızca adres — ve o adresi okumak kapsam
kapısından geçiyor (`BizigoMcpResource.ReadAsync`). Yani **veri sızmıyor**.

Sızan şey **zamanlama**: abone, kapsamı dışındaki bir grupta bir RCA koşumunun
durum değiştirdiğini bildirimin **geldiği andan** öğreniyor.

### Bu bir yan kanal, ve büyüklüğü ÖLÇÜLMEDİ

Bir RCA koşumunun **varlığını** öğrenmek ile **içeriğini** okumak arasındaki fark
gerçek bir fark, ama *"küçük"* demek ölçmek değil. Cevaplanması gereken:

- Bir abone, gözlemlediği bildirim zamanlamalarından **hangi bilgiyi** çıkarabilir?
  (Kaç grup var, hangi gruplarda alarm yoğunluğu var, bir olay ne zaman başladı.)
- Bu bilgi K6/K17'nin koruduğu şeyin **neresine** düşüyor?
- Süzgeç kurulunca **kaybedilen** bir şey var mı? (Örn. sistem kapsamıyla koşan
  bir operatör aboneliği.)

## 4 · SDK'nın istemci tarafı sunucu tarafını izlemiyor — kalıcı ölçüm notu

`McpClient.SubscribeToResourceAsync` hâlâ **kaldırılmış** `resources/subscribe`
RPC'sini çağırıyor ve `subscriptions/listen` için **hiçbir istemci API'si yok**.
Aynı paket, aynı sürüm.

Bunun iki sonucu var ve ikincisi bu ticket'ın kendisini etkiliyor:

1. **Zincir SDK'nın kendi istemcisiyle ölçülemiyor** — bekçiler ham JSON-RPC
   yazmak zorunda. Bu bir zahmet değil, **kanıtın kendisi**.
2. Devralma kararı verilirken *"SDK zaten yapıyor"* varsayımı **yapılamaz**:
   sunucu tarafının bir revizyona geçmesi, istemci tarafının geçtiğini
   göstermiyor.

## 5 · Cevaplanacak sorular

1. **Etiketleme için hangi yüzey gerekiyor?** SDK bir gün genel bir yönlendirme
   yüzeyi açar mı, yoksa `SubscriptionsListenHandler`'ı devralmak tek yol mu?
2. **Devralmanın bedeli ne?** SDK'nın kendi işleyicisi aynı akışta
   `*/list_changed` yayılımını da taşıyor (`ActiveSubscription`, `GrantsListChanged`).
   Devralınca o yayılımı **biz** yazmak zorunda mıyız, ve o zaman `listChanged`
   iddiası ne olur?
3. **Kapsam süzgeci abonelik başına kimliği nereden alır?** `subscriptions/listen`
   bir istek, yani `RequestContext` taşıyor — kimlik oradan `McpCallerScope` ile
   çözülebilir mi, ve çözülen kapsam aboneliğin ömrü boyunca **saklanabilir** mi
   (saklanan bir kapsam, bir kullanıcının grupları değiştiğinde bayatlar)?
4. **Yan kanalın büyüklüğü** (§3) — ölçülmeden süzgecin gerekliliği bir varsayım.

## 6 · Bekçiler bu ticket'ın kırmızı dedektörü

M07 iki sınırı **kilitledi**:
`McpResourceSubscriptionTests.Bildirim_abonelik_kimligiyle_etiketlenmiyor`
bildirimin etiketsiz olduğunu ve onayın etiketli olduğunu **ikisini birden**
iddia ediyor.

Bu, bir sınırı savunmak değil **haber vermek** için: SDK bir gün yönlendirmeyi
genel yaparsa ya da davranışı değişirse burası kırmızı yanıyor ve bu ticket'ın
birinci sorusu kendiliğinden cevaplanıyor.

## 7 · Bağımlılık ve sıra

**M07** (kaynak kanalı ve abonelik altyapısı) — main'de olması şart, çünkü bu
ticket onun ölçtüğü sınırları çözüyor.

**T54'ten sonra**: `RcaAdmission.TryStartAsync`'in yayın noktası bağlandığında
koşum başına bildirim 2'den 3'e çıkıyor, yani yan kanalın ölçümü de o hâlde
yapılmalı.
