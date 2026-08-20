---
title: "T33 — Kural yönetimi ve alarm motoruna bağlama"
kind: ticket
status: 1
---

# T33 — Kural yönetimi ve alarm motoruna bağlama

**Bağımlılık:** T32 · **Sonraki:** T38

## Amaç

Derlenmiş Sigma SQL'inin **çalışması** ve yönetilebilmesi.

## Kapsam

### İçinde

- Kural kaydı: kimlik, kaynak sürümü, **durum**, kapsam, gürültü ayarı.

  **Durum ikili değil ÜÇLÜ** ([açık sorular §son](../../t32-t33-acik-sorular/index.md)):

  | Durum | Ne | Kimin kararı |
  | --- | --- | --- |
  | `etkin` | Koşuyor | Kullanıcı |
  | `pasif` | Koşmuyor, **koşabilirdi** | Kullanıcı |
  | `gated` | **Koşamaz** — SQL üretilemedi | Yetenek sınırı |

  `pasif` ile `gated`'i tek "kapalı" değerine indirgemek, *"kullanıcı istemedi"*
  ile *"biz yapamadık"*ı karıştırmak. Aynı ekranda ikisi tek renk olursa
  kullanıcı kapalı bir kuralı açmayı dener ve açılmaz — sebebini de göremez.
- **F2'nin alarm motoruna bağlama.** Ayrı bir çalıştırıcı yazılmıyor: eşik/oran/
sessizlik değerlendiricisi zaten bir sorgu koşturup sonucu eşikle
karşılaştırıyor; Sigma kuralı da bir sorgu.
- Kural yönetim ekranı — F2'nin alarm ekranına eklenen bir sekme, ayrı ürün
yüzeyi değil.
- Toplu etkinleştirme: 269 kuralı tek tek açmak kimse yapmaz.

### Dışında

- `failed` durumunu ekranda göstermek. `failed` bir kural durumu **değil**,
bizim build'imizin durumu: pipeline kırık demek, sabit sıfır bekleniyor ve CI
onu geçirmiyor. Gated bölümüne karıştırılırsa kullanıcı bizim hatamızı kapsam
sınırı sanır.

- Kural yazma/düzenleme. Sigma kuralları yukarı akıştan geliyor; bizde yazılmıyor.
- Sigma korelasyon kuralları — backend destekliyor ama önce tekiller otursun.

## Kabul kriterleri

- Bir Sigma kuralı etkinleştirildiğinde tetikleniyor ve bildirim gidiyor.
- Kural **sahibinin kapsamıyla** koşuyor; başka grubun verisini görmüyor.
- Pasif kural hiç sorgu üretmiyor — kapalı kuralın maliyeti sıfır olmalı.

  ⚠️ **Testte tuzak:** `gated` bir kural bu kriteri **tanım gereği** sağlıyor
  (zaten SQL'i yok). Bir test ikisini karıştırırsa `pasif` yolunun gerçekten
  sınandığı görünmez olur — bekçinin yanlış sebeple yeşil yanması. Test
  açıkça `pasif` bir kural kullanmalı.
- Eşzamanlılık limiti Sigma kurallarını da kapsıyor: 269 kural açıksa
ClickHouse'a atılan eşzamanlı sorgu sayısı sınırlı.
- **Kuralın ürettiği SQL değiştiğinde** kullanıcı bunu görüyor.

  Kriter eskiden *"kaynak kural sürümü değiştiğinde"* diyordu ve **iki
  değişiklik olayından yalnızca birini** kapsıyordu. T32'nin manifesti ikisini
  mekanik olarak ayırıyor:

  | Olay | Manifest'te | Eski kriter |
  | --- | --- | --- |
  | Kaynak kural değişti | `source_sha` **ve** `output_sha` değişir | ✅ |
  | **Pipeline değişti** | `output_sha` değişir, `source_sha` **aynı kalır** | ❌ |

  İkincisi şu demek: kullanıcının **etkin** bir kuralının ne yakaladığı oynadı
  ama kaynak kural hiç değişmedi — eski kriter sessiz kalıyor ve kullanıcı
  kuralın hâlâ aynı şeyi yakaladığını sanıyor. Bu depoda beş kez ısıran şeklin
  aynısı: bir şey değişti, hiçbir yerde hata yok, fark eden olmadı.

  Tek ölçüt `output_sha`; sebebin kaynak mı pipeline mı olduğu `source_sha`'nın
  değişip değişmediğinden okunuyor. Tek yerden akıyor, ikinci bir bildirim yolu
  gerekmiyor.

## Gated bölümü `remedy`'ye göre gruplanmalı

*"31'i şema bekliyor, 11'i asla derlenmeyecek"* **iki farklı cümle**. İkincisi
kullanıcıya bir **kapsam sınırı** olarak sunulmalı, iş kalemi olarak değil.

Veri hazır: T31'in `describe()["schema_gaps"]` çıktısı her boşluğu `remedy` ile
veriyor, T32'nin manifesti `gated_closeable` / `gated_upstream` sayıyor. Ekranın
gösterdiği sayı ile `ExpectedGatedCount` sabitinin **aynı kaynaktan** gelmesi
şart: ekran filtreleyip gösterirse çivilenmiş sabit ile kullanıcının gördüğü
sayı ayrışır ve bekçi artık kullanıcıya görünen iddiayı korumaz.

## Notlar

Gürültü F3'ün en büyük ürün riski: Sigma kuralları ağ evreninde yazılmadı ve
yanlış pozitif üretecekler. F2'nin alarm önizlemesi (T23) burada da geçerli —
kural açılmadan önce "son 24 saatte kaç kez tetiklenirdi" gösterilmeli.
