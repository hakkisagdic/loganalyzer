---
title: Duvar saati ölçmek istediğin şeyi ölçmez
category: concepts
tags: [test, surec, kavram, bizigo]
aliases: [matchTimeout, zaman aşımı tuzağı, kararsız test]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[skills/f1-deklaratif-parser-motoru]]"
    type: constrains
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: related_to
sources:
  - docs/epic/f1-kapanis/index.md
  - docs/epic/t08-motor-geri-beslemesi/index.md
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/tickets/parser-motoru/index.md
  - docs/epic/tickets/sidecar/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=dd011df5a833 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=c9cae79ad905 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/t08-motor-geri-beslemesi/index.md=5ce87e36f831 docs/epic/tickets/parser-motoru/index.md=aeac91406893 docs/epic/tickets/sidecar/index.md=b9ae8ba0558e"
summary: F1'de aynı hata sınıfı beş yerde çıktı ve hepsinde belirti "test kararsız"dı. Zaman aşımı pattern'in karmaşıklığını değil işlemin beklediği süreyi ölçüyor; sonuç yüklü makinede sessizce yanlış veri.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.86
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Duvar saati ölçmek istediğin şeyi ölçmez

F1 kapanışı bu fazın en pahalı dersini tek cümleye indirmiş
(`docs/epic/f1-kapanis/index.md`):

> Yüklü bir makinede sağlıklı kod bütçeyi aşar; hızlı bir makinede bozuk kod
> bütçeye sığar.

Kural olarak `CLAUDE.md` §6'da duruyor. Bu sayfa kuralın **arkasındaki beş
olayı** birleştiriyor — üçü üretim kodunda, ikisi testlerde, ve hepsinin ilk
belirtisi aynıydı: *"test kararsız"*.

## Üretim kodundaki üç olay

| Nerede | Bütçe | Bütçe aşıldığında ne oluyordu |
| --- | --- | --- |
| Grok `matchTimeout` | 50 ms | Sonuç `parse_status=failed`; *"motor meşguldü"* ile *"bu satır uymuyor"* ayırt edilemiyor |
| `MaskCatalog` | 250 ms | `Signature()` **boş** dönüyor; olay sessizce etiketsiz kalıyor ve keşif kuyruğuna hiç girmiyor |
| `DiscoveryWorker` | — | Devre açılana kadar geri adım yok; ölü sidecar bağlantıyı mikrosaniyede reddedince işçi sıkı döngüye giriyor |

İkincisi bu deponun *sessiz yanlış davranış* tanımına birebir uyuyor: hata yok,
sayaç yok, belirti yok — format keşif döngüsü yüklü makinede fark edilmeden
bozuluyordu ([[concepts/sessiz-yanlis-davranis]]).

Üçüncüsünün maliyeti ölçüldü: canlı ölçüm etiketleme yolunun **2,7× yavaşladığını**
gösterdi, ve sebep etiketleme değil o döngünün çaldığı CPU'ydu.

### Grok tarafının ayrıntısı ölçümle geldi

T08'in motor geri beslemesi (`docs/epic/t08-motor-geri-beslemesi/index.md` §10)
olayı sayılarla yakaladı — makine swap %89'dayken, **aynı ikili ve aynı 87
satırlık örnek kümesiyle** iki ardışık koşum:

- biri `ok 83 / failed 1`, diğeri `ok 84 / failed 0`;
- düşen satırın parser'ı tek başına çağrıldığında sorunsuz ayrışıyor;
- zaman aşımına uğradığı bildirilen pattern düz bir **literal alternasyonu** —
  lookaround yok, `NonBacktracking` ile derleniyor, girdi uzunluğunda doğrusal.
  50 ms'i aşması imkânsız; aşan şey işlemin **bekleme** süresi.
- `dotnet test` bir koşumda dört test düşürdü, ardışık koşumda 301/301 geçti.

Üç sonucu var ve üçü de üretimi ilgilendiriyor: satır sessizce `failed` oluyor;
sağlıklı bir parser **karantinaya** girip sahibine uyarı gidebiliyor; ve
`parser coverage` CI kapısı yüklü bir runner'da rastgele kırılıyor.

## Testlerdeki iki olay

- **`VendorCatalogTests`** `legacy` pattern setini tek başına yüklüyordu — yani
  **sevk edilmeyen** bir yapılandırmayı sınıyordu, ve tam da geri izleyen oydu.
  Yeşil bir testin neyi ölçtüğü sorusunun cevabı "üretimin koştuğu şeyi değil"di.
- **`DiscoveryWorkerTests`** arka plan görevini başlatıp etkiyi **10 saniyelik
  duvar saati bütçesiyle** yokluyordu. Aynı commit CI'da 14 saniye, yerelde
  **6,5 dakika** sürüyordu ve her koşumda başka bir test düşüyordu. Çözüm
  zamanlamayı ayarlamak değil, denklemden çıkarmak oldu: `ExecuteAsync` artık
  `RunTurnAsync`'e devrediyor ve testler turu doğrudan çağırıyor. Paket **9
  saniyeye** indi, sekiz ardışık koşum temiz.

`CLAUDE.md` §6 bu ikinciyi ayrıca kayda geçmiş, çünkü *"kararsız test" diye
raporlanmıştı; değildi.*

## Çözüm her seferinde aynı üç yönden birine gitti

1. **Doğrusal zaman garantisi al, zaman aşımını kaldır.** Grok derleyicisinin
   `RegexOptions.NonBacktracking` tercihi zaten bunun için seçilmişti
   (`docs/epic/mimari-kararlar/index.md` §3.4). Geri besleme bunu bir adım öteye
   taşıyor: `CompiledGrok.IsLinearTime` doğruysa zaman aşımına **hiç gerek yok**.
2. **Mutlak bütçe yerine uzunluk sınırı.** `MaskCatalog`'un 250 ms'i kaldırıldı,
   yerine 16 KB girdi uzunluğu sınırı geldi. Ölçüldü ki geri izlemede kalan dört
   maskede sınırsız iç içe niceleyici yok — hepsi sınırlı tekrar ya da tek düzey
   `+` — yani korunması gereken tek şey dev bir satır ve onu uzunluk
   **deterministik** olarak durduruyor. Sınırı aşan satır **sayılıyor**
   (`SkippedTooLong`); sessizce boş dönmek kaldırılan zaman aşımının en kötü
   yanıydı.
3. **Ölçüyü aynı süreçte alınan bir tabana göreli yap**, ya da beklemeyi bir
   sinyalle değiştir.

Bir de karar düzeyinde öneri var, henüz uygulanmamış: karantina kararı ardışık
timeout sayısına değil **başarılı eşleşmelere oranına** baksın — yük altında
oran bozulmaz, bozuk pattern'de bozulur
(`docs/epic/t08-motor-geri-beslemesi/index.md` §10). ^[inferred: geri besleme
belgesinde öneri olarak duruyor; uygulandığına dair kayıt bulunamadı]

## Ölçülen kazanç

| Değişiklik | Önce | Sonra |
| --- | --- | --- |
| Kaplamanın varsayılan yola bağlanması | 45–75 sn | **11 sn** |
| `DiscoveryWorkerTests`'in turu doğrudan çağırması | 6,5 dk (yerel) | **9 sn** |

İlkinin sebebi beklenmedik ama net: geri izlemeye düşen pattern'ler
`matchTimeout` ödüyordu ve o timeout pattern davranışını değil **duvar saatini**
ölçüyordu (`docs/epic/f1-kapanis/index.md`).

## Uygulanabilir ölçüt

Bir bütçe yazmadan önce sorulacak soru: **bu test neyi ölçmek istiyor?**
Duvar saati değilse süreyi denklemden çıkar — büyütme. Ölçüyorsa mutlak bütçe
yerine aynı süreçte alınan bir tabana **oran** kullan (`CLAUDE.md` §6).

Aynı ölçüt benchmark'lara da uzanıyor: yüklü makine yanlış sayı üretiyor ve
`-c Release` şart. O tarafın kaydı [[skills/paralel-ajan-koordinasyonu]]
sayfasında.

## Kaynaklar

- `docs/epic/f1-kapanis/index.md` — "Bu fazın en pahalı dersi", beş olay
- `docs/epic/t08-motor-geri-beslemesi/index.md` — §10, iki koşumluk ölçüm
- `docs/epic/mimari-kararlar/index.md` — §3.4, `NonBacktracking` gerekçesi
- `docs/epic/f1-teknik-plan/index.md` — §4.1, üç kademeli ReDoS stratejisi
- `docs/epic/tickets/parser-motoru/index.md` — linter şiddet ayrımı, karantina
- `docs/epic/tickets/sidecar/index.md` — devre kesici, 2 sn zaman aşımı
- `CLAUDE.md` §6 — ölçüm kültürü
