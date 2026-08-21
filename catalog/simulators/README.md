# Cihaz simülatör profilleri (FS · S01)

Bir **profil**, simüle edilen tek bir cihazı tarif eder. Üç sadakat seviyesi de
(N1 süreç içi sahte, N2 gerçek SSH sunucusu, N3 CLI öykünmesi) **aynı profili**
okur.

Gerekçe §9'un kuralı: *ikinci kopya yazma*. Üç seviye kendi tanımını taşısaydı
ayrıştıkları gün hangisinin doğru olduğu bilinemezdi — ve ayrışma sessiz
olurdu: SSH tarafı `fw-ankara-01` derken syslog tarafı `fw-ankara-1` basar,
envanterde iki yarım cihaz görünür.

## Profilde ne var

| Alan | Ne için |
| --- | --- |
| `id` | Dosya adıyla aynı olmak zorunda; envanterdeki `source_id` bundan türüyor |
| `vendor` · `product` | Envanter satırı ve ekrandaki sütunlar |
| `hostname` | Cihazın kendi adı; syslog gövdesinde de geçiyor |
| `owner_group` | **Kapsam.** Filonun birden çok gruba yayılması bilinçli — bkz. aşağısı |
| `parser_id` | Envanter bağı (dispatcher kademe 1). Boş bırakılabilir |
| `encoding` | Cihazın tel kodlaması; `bozuk-kodlama` senaryosunun girdisi |
| `ssh` | N2/N3'ün açacağı sunucunun portu ve kimliği |
| `config` | Taban config dosyası ve adlandırılmış senaryo geçişleri |
| `syslog` | Hangi örnek dosyalardan, hangi hızda, hangi taşımayla basılacağı |

## Profilde bilerek OLMAYAN üç şey

**Vendor komutları.** `show`, `more system:running-config`, `/export terse` —
bunlar ürünün toplayıcısında (`src/Bizigo.Devices/ConfigCollector.cs`) yazılı ve
tek kaynak orası. Profilde tekrarlansaydı, ürün komutu değiştirdiği gün
simülatör eski komuta cevap vermeye devam ederdi: test yeşil kalır, üretim
kırılır.

**Örnek log satırları.** `catalog/parsers/<id>/samples/` altında zaten duruyorlar
ve parser testlerinin girdisi de onlar. Profil onlara **işaret ediyor**,
kopyalamıyor. Böylece bir örnek düzeltildiğinde simülatör de düzelmiş oluyor.

**Maskeleme sözlüğü.** `catalog/masks/`, ürünle ortak.

## Filo neden birden çok `owner_group`'a yayılıyor

Tek gruba toplanmış bir filo, ürünün en pahalı hata sınıfının (K17 — kapsam)
ekranda görünmesini imkânsız kılar: analist her şeyi görür ve kapsamın
uygulandığını hiçbir görüntü göstermez.

Bugünkü filo Keycloak realm'inde **zaten var olan** gruplarla hizalı:

| Profil | Vendor | `owner_group` | Neyi görünür kılıyor |
| --- | --- | --- | --- |
| `fw-ankara-01` | FortiGate | `network/core` | `analyst.core`'un gördüğü |
| `asa-dc-01` | Cisco ASA | `network/core` | Aynı grupta ikinci vendor |
| `lb-web-01` | nginx | `network/core` | Cihaz değil ama aynı yolu kullanıyor |
| `fw-izmir-01` | FortiGate | `network/edge` | `analyst.core`'un **görmediği** — negatif kanıt |
| `rb-sube-07` | MikroTik | `network/edge` | Üçüncü vendor, ikinci grup |

`fw-izmir-01` bu listenin en önemli satırı: iki analistin ekranının **farklı**
olduğunu ve farkın kaynağının `idp_group_mapping` olduğunu gösteren tek şey o.

## Senaryolar

Varsayılan **statik** — tekrarlanabilirlik testin şartı. Değişim bir bayrakla
geliyor ve her senaryo ürünün belirli bir iddiasını hedefliyor; "genel olarak
değişsin" diye bir senaryo yok. Tam liste ve hangi iddiayı sınadıkları
`docs/epic/fs-simulatorler/index.md` §7'de.
