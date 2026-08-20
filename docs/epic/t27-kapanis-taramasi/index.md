---
kind: review
title: "T27 — kabul kriterlerinin taraması"
---

# T27 — kabul kriterlerinin taraması

`main` = `d034f9d` üstünde tarandı. [Ticket](../tickets-f2/f2-dogrulamasi/index.md) ·
[F2 kapanışı](../f2-kapanis/index.md)

Üç kalem tarandı ve §10 gereği üç ayrı sonuç var: **aradım/var**,
**aradım/yok**, **aramadım**. Aşağıda hangisinin hangisi olduğu tek tek yazılı.

## Özet

| Kabul kriteri | Sonuç |
| --- | --- |
| `Pending` boş, `Exempt` sabit | ✅ aradım, var — `Pending` boş, `Exempt` 6, 16/16 test geçti |
| Replay sırasında canlı ingest bozulmuyor | ✅ aradım, var — **ve iddiadan güçlü** (§3) |
| Kuru koşu = gerçek koşu | ✅ aradım, var — `Kuru_kosu_gercek_kosuyla_ayni_sonucu_veriyor` |
| Dört akış otomatik koşuyor | ⚠️ **ikisi var, ikisi akış değil** (§1) |
| Kapsam ayrışması bekçisi | ⚠️ API'de var, **ekran katmanında yok** (§2) |
| Token sızıntısı bekçisi | ⚠️ yanıt/çerez taranıyor, **`localStorage` taranmıyor** (§2) |

## 1 · Dört akış — ikisi akış, ikisi parça

**Aradım.** `F2FlowTests` (5 test), `AlertingTests` (6), `RawEventLocatorTests`
(4), ve `ui/tests/` altındaki 24 dosya.

| Ticket'ın akışı | Bugün | Not |
| --- | --- | --- |
| 2 · Parser yaz → dene → yayınla → etkisini gör | ✅ akış | `Yayinlanan_parser_sonraki_olayi_ayristiriyor` |
| 4 · Değişiklik üç kaynaktan düşüyor | ✅ akış | `Uc_degisiklik_kaynagi_ayni_zaman_cizelgesinde_bulusuyor` |
| 1 · Giriş → arama → detay → ham bayt | ❌ **akış değil** | aşağıda |
| 3 · Cihaz sussun → alarm → bildirim → bağlantı | ❌ **akış değil** | aşağıda |

### 1.1 · "Ham bayt" testi adının söylediğini yapmıyor

`Ham_bayt_sadakati_zincir_boyunca_korunuyor` adında **zincir** geçiyor. Gövdesi
şunu yapıyor: bir olay yazıyor, ClickHouse'tan `body`'nin SHA-256'sını okuyor,
yazdığıyla karşılaştırıyor.

Yani ne giriş var, ne arama, ne olay detayı, ne `GET /v1/events/{id}/raw`, ne de
arşivden okuma. Ölçtüğü şey **tek bir yazma-okuma turunda gövdenin bozulmaması**
— değerli ama ticket'ın istediği şey değil.

Bu, bu deponun tanıdığı bir sınıf: **adı iddiasından geniş olan test.** Yeşil
yanıyor ve okuyan kişi "zincir doğrulandı" diye anlıyor.

Akışın parçaları ayrı ayrı **var** ama hiç buluşmuyorlar:

| Parça | Nerede | Katman |
| --- | --- | --- |
| Giriş, token izolasyonu | `ui/tests/token-isolation.test.ts` | BFF |
| Arama ekranı | `ui/tests/events-screen.test.tsx` | ekran |
| Olay detayı, ham görünüm | `ui/tests/events-raw.test.ts` | ekran |
| Ham indirme | `ui/tests/raw-download.test.ts` | BFF |
| Ham baytın kapsamla korunması | `RawEventLocatorTests.Kapsam_disi_olay_nesneyi_hic_actirmiyor` | depolama |

F1'in dersi tam buydu: *"tek tek doğru olan parçaların birlikte doğru olduğunu
göstermek"* — ticket'ın kendi cümlesi. Bugün gösterilen, parçaların tek tek
doğru olduğu.

### 1.2 · Alarm akışı da parça hâlinde

| Parça | Nerede |
| --- | --- |
| Susan kaynak yakalanıyor | `AlertingTests.Sessizlik_alarmi_susan_kaynagi_gercek_veriyle_yakaliyor` |
| Tetiklenme ve teslim yazılıyor | `AlertingTests.Zamanlayici_turu_gercek_semada_tetiklenme_ve_teslim_yaziyor` |
| Mesajdaki bağlantı doğru aramayı açıyor | `AlertLinkTargetTests` (**birim**, dosya sisteminden rota okuyor) |

Üçü de sağlam. Eksik olan, **birinin çıktısının ötekinin girdisi olduğunun**
gösterilmesi: bugün susan kaynağın ürettiği tetiklenmenin, kanala giden
mesajın, ve o mesajdaki bağlantının aynı kaynağı işaret ettiğini sınayan bir
test yok.

## 2 · İki çapraz doğrulama — biri yarım, biri eksik

### 2.1 · Token sızıntısı: `localStorage` taranmıyor

**Aradım, yok.** `ui/tests/` altında `localStorage` ya da `sessionStorage`
geçen **tek bir iddia yok**.

Ticket üç yer sayıyor: *"hiçbir tarayıcı yanıtında, çerezde veya
`localStorage`'da"*. İlk ikisi `token-isolation.test.ts`'te her baytı taranarak
karşılanıyor. Üçüncüsü hiç sınanmıyor.

Bu boş bir risk değil: `ui/src/app/olaylar/SavedSearches.tsx` gerçekten
`localStorage`'a yazıyor (kayıtlı aramalar, T15/T21 kararı). Bugün oraya
yazılanın içinde token yok — ama **bunu tutan hiçbir şey yok**, ve kayıtlı
aramaya bir gün "bu aramayı kim kaydetti" gibi bir alan eklenmesi yeterli.

### 2.2 · Kapsam ayrışması ekran katmanında sınanmıyor

**Aradım, yok.** `ScopeNegativeTests` (12 test, entegrasyon) API tarafını
kapsıyor ve sağlam: arama, tekil okuma, envanter, değişiklik yazma, kapsam
daraltmasının genişletememesi.

Ekran tarafında ise testler veriyi **sabit** veriyor (`owner_group:
"network/edge"` gibi) ve o verinin çizildiğini sınıyor. `analyst.core` ile
`analyst.edge`'in **aynı ekranda farklı veri gördüğünü** sınayan bir test yok.

Neden önemli: aynı ayrım bu turda başka bir yerde gerçek bir açık üretti —
kanıt paketinin **okuma yolunda** kapsam kapısı yoktu, toplama tarafında vardı.
"API'de var" ile "kullanıcının gördüğü yerde var" farklı iddialar ve F2 ikisini
de söylüyor.

## 3 · Replay / canlı ingest — kapanmış, ve F1'in iddiası yanlış çıkmış

**Aradım, var — ticket'ın istediğinden iyi bir hâlde.**

F1 bunu *"`REPLACE PARTITION` atomik olduğu için beklenen doğru davranış"* diye
bırakmıştı, ölçmeden. Ölçülünce iddia **yanlış** çıkmış ve sebebi kayıtlı:
motor önce mevcut satırları okuyup gölge tabloyu kuruyor, sonra bölümü
değiştiriyor; o iki adım arasında canlı ingest'in aynı bölüme yazdığı her satır
gölgede yok ve değiştirme onu **sessizce siliyor**. Atomiklik "yarım bölüm
görünmez" diyor, "anlık görüntüden sonra gelen korunur" demiyor.

Kapatma biçimi bir yük testi değil, **tehlikenin kaldırılması**: açık bölüm
(bugünün bölümü) varsayılan olarak replay'in dışında, atlandığı rapora
yazılıyor, ve `ReplayPlan.AllowOpenPartition` bunu bilerek açmak isteyene
bırakılıyor. `ReplayOpenPartitionTests` sınıyor.

Bu, yük altında ölçmekten **daha iyi** bir cevap: yük testi "bu koşulda
bozulmadı" der, buradaki çözüm bozulabileceği durumu ortadan kaldırıyor ve
kalan tek yolu görünür bir karara bağlıyor.

## 4 · Aramadım

Dürüstlük için ayrı: aşağıdakilere **bakmadım**, dolayısıyla haklarında bir şey
söylemiyorum.

- `AlertingTests`'in altı testinin hangi kabul kriterine denk düştüğünü dosya
dosya eşlemedim; yalnızca akışla ilgili üçünü okudum.
- `ui/tests/` altındaki 24 dosyanın tamamını okumadım — kapsam ayrışması ve
`localStorage` için hedefli arama yaptım.
- CI'da dört akışın **gerçekten koştuğunu** koşum çıktısından doğrulamadım;
`integration` işinin paketi koşturduğunu okudum, tek tek testleri değil.
- `f2-kapanis`'in §1'deki ölçümlerini (Keycloak, K35, Sigma) yeniden ölçmedim.

## 5 · Sonuç: F2 kapanmadı

Ticket'ın kendi şartı: dört akış otomatik koşacak ve iki çapraz doğrulama bekçi
olarak duracak. Bugün **iki akış ve bir buçuk çapraz doğrulama** var.

`Pending` boş — ama o, bitiş şartlarından **biriydi**, tamamı değil.
`f2-kapanis` belgesi §6'da yalnızca `Pending`'i bitiş şartı olarak anlatıyor ve
dört akışa hiç değinmiyor; belge bu yüzden eksik, yanlış değil.

Ticket `status` **1'de kalıyor**.
