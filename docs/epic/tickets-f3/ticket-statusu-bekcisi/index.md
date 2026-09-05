---
title: "T53 — Ticket status alanlarını gerçekle tutan bekçi"
kind: ticket
status: 2
---

# T53 — Statünün dört gösterimi birbirini yalanlamasın

## 1 · Sorun deponun kendi kaydında yazılı

`kalan-is-raporu` §6:

> **Ticket `status` alanları bayatlıyor.** Bu raporu yazarken FS'in beş
> ticket'ının durumu gerçekle uyuşmuyordu — S04 ve S05 kapanmıştı, `0`
> görünüyorlardı. Düzelttim, ama **mekanizma yok**: alanları güncel tutan bir
> bekçi olmadığı sürece bir sonraki rapor da aynı düzeltmeyi yapacak.

Öngörü tuttu. Bu hafta T38'in brief'i **bayat bir statüden** çıktı: aynı commit
(`7723f81`) hem ticket'ı `1 → 2` yaptı hem de kalan iş raporunda *"T38 açık"*
yazdı; brief ikincisinden türedi ve ajan **zaten yazılmış bir ticket'ı** açmak
üzere gönderildi.

Raporun kendi cümlesi hedefi de veriyor: *bu, `WikiSourceDigestTests`'in vault
için çözdüğü sorunun ticket katmanındaki karşılığı ve **çözülmedi**.*

## 2 · Seçilen olgu ve iki adayın neden elendiği

Ticket üç aday veriyordu. Seçim **üçüncüsü**, ve gerekçesi ölçüldü.

| Aday | Karar | Gerekçe |
| --- | --- | --- |
| **`git log`'da ticket atfı** | **Elendi** | Bu depodaki commit'lerin çoğu ticket kimliği taşımıyor: son yirmi beş commit'in yalnızca yedisinde `(T4x)` ya da `T48:` biçiminde bir atıf var; gerisi `Merge branch …`, `Restamp the two pages …`. Yani *"atıf yok"* ile *"iş yok"* ayırt edilemiyordu ve bekçi ya sürekli yanlış pozitif üretirdi ya hiçbir şey söylemezdi. |
| **Kabul kriterine karşılık gelen test/dosyanın varlığı** | **Elendi** | Kriter metnini makineye okutmayı gerektiriyor. Ölçtüğü şeyden **daha kırılgan bir tahmin** üretirdi: kriteri karşılamayan bir dosyanın varlığı "bitti" diye okunurdu. |
| **Toplayıcı belgelerle tutarlılık** | **Seçildi** | Bu hafta kırılan şey tam olarak buydu ve ayrışma **tamamen mekanik**. |

### Statü bu depoda dört yerde yazılı

1. Ticket dosyasının `status:` alanı.
2. Yol haritası tablosunun o ticket'a **bağ vermesi** (`tickets-*/index.md`).
3. `tickets-fs` tablosunun **`Durum` sütunundaki işaret** (⬜ · 🔄 · ✅).
4. `kalan-is-raporu`'nun **"açık ticket'lar"** tablosu.

Bekçi bu dördünün birbirini yalanlamamasını sınıyor.

## 3 · Ne sınamıyor — beyan

**Bekçi statünün gerçeğe uyduğunu sınamıyor**, çünkü *"iş gerçekten bitti mi"*
gözlenebilir bir olgu değil. Sınadığı şey, statünün **kendi dört gösteriminin
birbirini yalanlamaması**. Dördü birden yanlış olabilir ve bekçi yeşil yanar.

Bu bir eksiklik değil bir **sınır** — ama yazılmazsa okuyan kişi bekçinin
*"ticket'lar doğru"* dediğini sanar, ve bu deponun beş kez adını koyduğu şeye
dönüşür.

İkinci sınır: `Durum` sütunundaki işaret eşlemesi (⬜=0 · 🔄=1 · ✅=2) bir
**varsayım**. Kodda yazılı duruyor ki tablo başka bir anlam kastediyorsa
sessizce yanlış hizalanmasın.

Üçüncü sınır: girdi **markdown ayrıştırması**. Bir başlık yeniden adlandırılırsa
okunan küme sessizce boşalır ve bütün kapılar yeşil yanardı — kapsamını yitirmiş
bir bekçi, olmayan bekçiyle aynı sonucu verir. `Bekci_bos_kume_uzerinde_donmuyor`
bu yüzden ayrı bir kapı, ve kırmızı yanabildiği ölçüldü (§5, kusur H).

## 4 · Bugün ayrışmış olanlar — bu ticket'ın çıktısı

Bekçi listeler boşken **3 kapıda kırmızı** yanıyor ve **on altı bulgu**
sayıyor. Düzeltme koordinatörde; bulgular `KnownDivergence`'ta ticket atfıyla
duruyor ve düzeltilen satır `Listeler_bayat_giris_tasimiyor` ile silinmeye
zorlanıyor.

### 4.1 · Yol haritası tablosunda hiç görünmeyen ticket dosyaları (7)

`tickets-f2/ui-container` (T49) · `tickets-f3/produces-kapisi-bagi` (T48) ·
`tickets-f3/kompozisyon-koku-bagi` (T50) · `tickets-f3/specificity-olcutu` ·
`tickets-f4/model-saglayicisi` (T42) · `tickets-f4/senaryo-plugin-cekirdegi`
(T43) · `tickets/ham-arsiv-kurtarma`

Görünmeyen bir ticket, faz sayılarının ("9/12") onu saymaması demek — ve
kimliği hiçbir yere bağlanmadığı için rapor ondan söz ettiğinde ticket
dosyasına ulaşılamıyor. §4.3'teki iki "eşlenemiyor" satırı bunun doğrudan
sonucu.

### 4.2 · `tickets-fs` `Durum` sütunu ticket dosyasıyla çelişiyor (4)

| # | Tablo | Dosya |
| --- | --- | --- |
| S02 | 🔄 | `status: 2` |
| S03 | 🔄 | `status: 2` |
| S04 | ⬜ | `status: 2` |
| S05 | ⬜ | `status: 2` |

**S04 ve S05 tam olarak `kalan-is-raporu` §6'nın bir kez düzelttiği ikili.**
Mekanizma konulmadığı için yeniden ayrıştılar — raporun kendi öngörüsü, kendi
belgesinde gerçekleşmiş hâlde.

### 4.3 · `kalan-is-raporu` "açık" diyor, ticket dosyası aksini söylüyor (5)

| # | Bulgu |
| --- | --- |
| **T38** | Raporda **AÇIK**, `tickets-f3/altin-kume` `status: 2`. **Bu haftaki yanlış brief'in tam kaynağı.** |
| T44 | Raporda açık, ama hiçbir ticket **dosyası** yok |
| T47 | Raporda açık, ama hiçbir ticket **dosyası** yok |
| T48 | Ticket dosyası var, yol haritasında olmadığı için eşlenemiyor (§4.1) |
| T49 | Aynı |

T44/T47 satırları `kalan-is-raporu` §6'nın *"T45/T46'nın ticket dosyası hiç
yazılmadı, kararlar ajan raporlarında kaldı"* şikâyetinin devam ettiğini
gösteriyor: rapor, **ticket belgesi olmayan işi** takip ediyor.

### 4.4 · Bu ticket'ın kendi eklediği borç

`tickets-f3/ticket-statusu-bekcisi` de yol haritası tablosunda **yok** ve
listede sekizinci satır olarak duruyor. Tabloya eklemedim çünkü F3'ün
"Ticket listesi" tablosu T29–T38 arasında **kürate edilmiş bir anlatı** ve
T48/T50/T53'ün oraya nasıl yerleşeceği koordinatörün kararı — ayrıca o dosyayı
düzenlemek `references/f3-detection-ve-rca-kaniti` vault sayfasının damgasını
bayatlatıyor (§11). Borcu **görünür bıraktım**, sessizce üstlenmedim.

## 5 · Ölçülen kırmızılar

`CLAUDE.md` §6'nın iddia adımı her kusurda uygulandı.

| # | Kusur | Sonuç |
| --- | --- | --- |
| **Kontrol** | Kusur yok — listeler boşaltıldı | **KIRMIZI** — 8 testin 3'ü, **16 bulgu**, §4'teki listeyle birebir |
| H | `kalan-is-raporu` başlığı yeniden adlandırıldı (bekçi kör kalsın) | **KIRMIZI** — `Bekci_bos_kume_uzerinde_donmuyor`: `Assert.NotEmpty() Failure`, ve `Listeler_bayat_giris_tasimiyor` da yanıyor |

**Kontrol satırı ticket'ın istediği ölçüt:** kapı bugün kırmızı yanıyor, yani
kapsamı doğru ve boş küme üzerinde dönmüyor.

**Kusur H ikinci yarısı:** kapının kendi kör kalması da kırmızı yanıyor. Bir
bekçinin sessizce kapsamını yitirmesi, bu depoda bekçinin kendisinden tehlikeli
sayılıyor (§7) — ölçtüm.

## 6 · Kapsam

**İçinde** — sekiz kapı: yol haritası üyeliği · kırık bağ · `Durum` işareti ↔
`status` · rapor ↔ `status` · tamamlanmış story'nin yarım ticket taşımaması ·
boş küme bekçisi · liste bayatlığı · muafiyet sabiti.

**Dışında**

- **Ticket statülerini düzeltmek.** Koordinatörün kararı; bazı düzeltmeler bu
  ajanın ölçmediği şeylere bağlı (T32 kendi ticket'ını `1`'de bıraktı ve doğru
  yaptı).
- Vault katmanı — `WikiSourceDigestTests` zaten orada, ve emsal o.

### Asimetrik bırakılan bir kural

**Tamamlanmış bir story yarım ticket taşıyamaz** sınanıyor; tersi
**bilerek sınanmıyor**. Bütün çocukları `2` olan bir story `2` olmak zorunda
değil — henüz yazılmamış ticket'ları olabilir, ve o yönü sınamak yazılmamış işi
"yok" saymak olurdu. `tickets-f4` ve `tickets-mcp` bugün tam olarak bu hâlde:
story `0`, yazılmış çocukların hepsi `2`.

## 7 · Ne ölçüldü, ne arandı

**İlk bakılacak yer:** `EpicStatusTests.LiveDivergenceKeys` — üç kapının
ölçüm tabanı orada tek yerde toplanıyor.

**Aradım ve elemedim:** 53 ticket belgesinin tamamının `kind`/`status` alanı ·
46 yol haritası satırı (kırık bağ **yok**) · `Durum` sütununun yalnızca
`tickets-fs`'te olduğu · `kalan-is-raporu`'nun açık dediği sekiz kimliğin
tamamı · son yirmi beş commit'in ticket atfı taşıyıp taşımadığı.

**Aramadım:** ticket statülerinin **gerçeğe** uyup uymadığı (§3'ün beyanı) ·
`docs/epic` dışındaki statü gösterimleri — `README`, `docs/wiki` ve UI tarafına
**bakmadım** · `tickets-mcp`'nin sekiz M-ticket'ının neden hiç dosyası olmadığı.
