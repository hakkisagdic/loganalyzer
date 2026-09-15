---
title: "Abonelik kimliği ve kapsam süzgeci"
kind: ticket
status: 2
---

# Abonelik bildiriminin kimliği ve kapsamı

M07 abonelik kanalını kurdu ve **iki sınırını ölçtü**. M20 ikisini de kapattı —
ve kapatma biçimi ticket'ın asıl bulgusu: **SDK'nın işleyicisi devralınmadı.**

## 0 · Cevaplar — ölçülen hâlleriyle

| Soru | Cevap |
| --- | --- |
| Yan kanal ne kadar? | **İhmal edilebilir değil**: yabancı koşumların **tam sayısı** sızıyordu (koşum başına 3 bildirim) |
| Etiketleme mümkün mü? | **Evet, devralmadan** — `NotificationParams.Meta` + `MetaKeys.SubscriptionId` |
| Süzgeç kimliği nereden alır? | `subscriptions/listen` isteğinin `MessageContext.User`'ından, REST'in geçtiği **aynı** çözücüyle |
| `*/list_changed` devralmanın bedeli? | **Ödenmedi** — işleyici yerinde kaldı, filtre yoluyla girildi |
| Bağlam bütçesi? | **Sıfır ekledi** (594 → 594); `_meta` bildirimde, ilanda değil |

## 1 · Yan kanalın büyüklüğü — ÖLÇÜLDÜ, ve süzgeci gerekli kıldı

M07 *"zamanlaması bir sinyal"* demişti ve **ne kadar** sinyal olduğunu
ölçmemişti. Ölçüldü (`McpSubscriptionSideChannelTests`):

Kapsamı yalnızca `network/core`'u gören bir aboneye, `network/edge`'de koşan üç
RCA koşumu boyunca **dokuz** bildirim gidiyordu — yani **koşum başına üç**. Abone
bildirimleri sayıp üçe bölerek **göremediği gruptaki koşum sayısını tam olarak**
buluyor.

**Bu kesirli bir bit değil, tam kardinalite.** Bir pencerede yabancı koşum sayısı
`0..N` arasındaysa abone o sayıyı **kesin** öğreniyor (~log₂(N+1) bit), artı her
geçişin **anı**. Sayısı ve zamanlaması bir olay grafiği çiziyor: hangi ekip ne
sıklıkta alarm alıyor, bir olay ne zaman başladı, ne kadar sürdü.

K17 kapsamı **satırları** korumak için var; bu grafik satır okumadan çıkıyordu.
Yani süzgeç **yazılmalıydı**, ve gerekçesi bu sayı — *"muhtemelen küçük"* değil.

## 2 · İşleyici DEVRALINMADI — filtre yoluyla girildi

İlk plan `Handlers.SubscriptionsListenHandler`'ı sahiplenmekti (`McpRequestHandler
<SubscriptionsListenRequestParams, EmptyResult>` — imzası ölçüldü). **Elendi:**
SDK'nın kendi işleyicisi aynı akışta `*/list_changed` yayılımını da taşıyor
(`ActiveSubscription`, `GrantsListChanged`, `SendListChangedNotificationAsync`) ve
o yüzeyler `internal`. Devralmak katalog bildirimlerinin yayılımını **ikinci kez
yazmak** demekti (§9) — ve o kopya sessizce ayrışırdı: abonelik kazanılırken
`tools/list_changed` kaybolurdu.

Yerine kullanılan şey bir **mesaj filtresi** (`Filters.Message.IncomingFilters`):
istek SDK'nın işleyicisine gidiyor, biz yalnızca yanından okuyoruz — isteğin
**kimliğini**, istediği adresleri ve çağıranın **kapsamını**. Mekanizma bir tane
kaldı.

**Bedeli yazılı:** `subscriptions/listen` için SDK **istek filtresi** sunmuyor
(`McpRequestFilters`'ta karşılığı yok), dolayısıyla filtre **mesaj** düzeyinde ve
JSON-RPC şeklini kendisi okumak zorunda. Tip güvenliği bir basamak düşüyor;
karşılığında ikinci bir mekanizma yazılmıyor. Ayrıştırma yine SDK'nın kendi
tipine yapılıyor (`SubscriptionsListenRequestParams`), yani istenen adreslerin
okunuşu SDK'nınkinden ayrışamıyor.

**Devralmanın bedeli ödenmediği ölçülüyor:** `Katalog_bildirimi_yayilimi_korunuyor`
istemcinin `toolsListChanged` isteğini sunucunun onayında geri verdiğini
gösteriyor.

## 3 · Etiketleme — ve iki aboneliğin ayırt edilebildiğinin ölçümü

Her bildirim artık `_meta/io.modelcontextprotocol/subscriptionId` taşıyor; anahtar
SDK'nın kendi sabitinden (`MetaKeys.SubscriptionId`), dizge elle yazılmadı.

Etiketin **var olması** ile **işe yaraması** ayrı iki şey, ikisi ayrı ölçülüyor:
aynı kanalda iki `subscriptions/listen` açılıyor (biri `rca-runs`'a, biri başka
adrese), yayın `rca-runs`'a yapılıyor ve gelen **tek** bildirimin etiketi
**birinci** aboneliğin kimliğini taşıyor. Aynı ölçüm ikinci bir şeyi de söylüyor:
**istenmeyen adres için bildirim gitmiyor** — spesifikasyonun kuralı.

## 4 · Ölçüm bir test kusurunu değil bir KARARI görünür yaptı

Bekçiler ilk koşumda hiç bildirim almadı. Sebep: filtre kapsamı çözmek için
**kimlik** istiyor ve bellek içi taşımada `User` yoktu, dolayısıyla abonelik
deftere **hiç girmiyordu**.

Bu bir test kusuru değil ölçümün kendisi: **kimliksiz bir abonelik kapalı
sayılıyor.** `AccessScope.Denied` verip deftere yazmak da "kapalı" olurdu ama bir
kayıt bırakırdı; hiç yazmamak süzgecin varsayılanını kapalı tutmanın en dar hâli.
İstek yine SDK'ya gidiyor — reddetmek filtrenin işi değil, ve reddetseydi
`*/list_changed` aboneliği de ölürdü.

## 5 · SDK'nın istemci tarafı sunucu tarafını izlemiyor — kalıcı not

`McpClient.SubscribeToResourceAsync` hâlâ **kaldırılmış** `resources/subscribe`
RPC'sini çağırıyor ve `subscriptions/listen` için **hiçbir istemci API'si yok**.
Aynı paket, aynı sürüm.

İki sonucu var: zincir SDK'nın kendi istemcisiyle **ölçülemiyor** (bekçiler ham
JSON-RPC yazıyor — zahmet değil, kanıtın kendisi), ve devralma kararı verilirken
*"SDK zaten yapıyor"* varsayımı **yapılamaz**.

## 6 · Kapatılmayan tek şey: HTTP taşıması

Abonelik yeteneği hâlâ yalnızca stdio'da ilan ediliyor
(`subscriptionsDeliverable`). Sebep SEP-2567: aynı revizyon `Mcp-Session-Id`'yi
kaldırdı, yani akışlanabilir HTTP'de **oturum yok**. Bildirimin gideceği yer
`subscriptions/listen` isteğinin **kendi yanıt akışı** ve o akışa yazmanın yolu
SDK'da `internal` (`ActiveSubscription.RelatedTransport`).

**Ölçülmedi:** HTTP'de bildirimin gerçekten hiçbir yere gitmediği ölçülmedi;
SDK'nın kendi belgesine dayanıldı (*"a stateless HTTP server has no session-wide
channel"*). Bu bir açık kalem ve ayrı bir ticket'a değer.

## 7 · Bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `Yabanci_grubun_kosum_sayisi_bildirimlerden_cikarilabiliyor` | Yan kanalın büyüklüğü (süzgeçsiz hâl) |
| `Suzgec_yabanci_grubu_elemekle_kanali_kapatiyor` | Süzgecin kararı, saf fonksiyon olarak |
| `Grubu_bilinmeyen_degisiklik_gonderilmiyor` | Bilinmeyen grup = kapalı |
| `Bildirim_abonelik_kimligiyle_etiketli` | Etiket var |
| `Iki_abonelik_etiketle_ayirt_edilebiliyor` | Etiket işe yarıyor + istenmeyen adrese gitmiyor |
| `Kapsam_disi_grubun_degisikligi_aboneye_gitmiyor` | Süzgeç akışa bağlı |
| `Kopru_degisikligin_grubunu_tasiyor` | Köprü grubu geçiriyor |
| `Katalog_bildirimi_yayilimi_korunuyor` | Devralma bedeli ödenmedi |
| `Akis_kapaninca_abonelik_defterden_dusuyor` | Sızıntı yok |
