---
title: Değişiklik beslemesi — üç kaynak, üç ayrı büyüklük
category: concepts
tags: [mimari, guvenlik, kavram, bizigo]
aliases: [K34, change feed, change_events, cihaz config farkı]
relationships:
  - target: "[[concepts/f2-kapsam-tek-kapi]]"
    type: uses
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[references/f2-kapanis]]"
    type: derived_from
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/change-webhook/index.md
  - docs/epic/tickets-f2/change-connector/index.md
  - docs/epic/tickets-f2/change-config-diff/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/change-config-diff/index.md=f1936ded8c04 docs/epic/tickets-f2/change-connector/index.md=7155ed5a4924 docs/epic/tickets-f2/change-webhook/index.md=5e50834cbe21"
summary: K34 üç kaynağı da istedi ama kapsamı daraltmak yerine sıraladı, çünkü üçüncüsü diğer ikisinin toplamından büyük. Değeri bugün değil F3'te görünüyor ve geçmişe dönük üretilemiyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:45:00Z
updated: 2026-08-24T17:45:00Z
---

# Değişiklik beslemesi — üç kaynak, üç ayrı büyüklük

K34: **üç kaynak birden, ekrandan yapılandırılabilir.** Teknik plan kapsamı
daraltmıyor, **sıralıyor** — çünkü üçüncüsü diğer ikisinin toplamından büyük.

| Kaynak | İş | Ticket |
| --- | --- | --- |
| Elle giriş + API | Küçük — `POST /v1/changes` zaten var | T24 |
| CI/CD webhook | Orta — imzalı alıcı, sağlayıcı başına yük eşlemesi | T24 |
| Cihaz config fark tespiti | **Büyük** — kendi başına bir alt sistem | T26 |

Sıralamanın gerekçesi bir teslim edilebilirlik güvencesi: T26 F2'nin **sonuna**
konuldu ki ondan önceki her şey kendi başına çalışır durumda olsun — *"bu
ticket kayarsa F2 yine de teslim edilebilir."*

## Değeri bugün değil F3'te

T24'ün notu bu ticket'ın neden şimdi yapıldığını anlatıyor: `change_events`
tablosu F1'de **kasten** kuruldu ve boş duruyor. F3'ün RCA'sı "ne değişti"
verisi olmadan "ne oldu"nun ötesine geçemiyor, ve o veri **geçmişe dönük
üretilemiyor**.

> Ham arşivle aynı mantık — geç eklenen şey geçmişi kurtarmıyor.

## Webhook: imza, eşleme, idempotans

- **İmza doğrulaması olmadan kayıt kabul edilmiyor.**
- Sağlayıcı başına yük eşlemesi (GitHub Actions, Jenkins, GitLab CI): her
  gövde farklı, hepsi aynı `change_events` şekline düşüyor. Bilinmeyen
  sağlayıcı için JSON yol ifadeleriyle genel eşleme.
- Aynı webhook iki kez gelirse **ikinci kayıt oluşmuyor** (idempotans anahtarı).

Kapanışta çıkan altı kusurdan biri tam burada: *"iki Jenkins fazı aynı olguyu
bildiriyor"* diye varsayılmıştı; post-build adımı durumu **çevirebiliyor** ve
erken, **yanlış** durum kalıcı oluyordu. Kayıt var, hata yok, sonuç yanlış —
[[concepts/sessiz-yanlis-davranis]].

## Connector: ürünün ilk şifreli deposu

T25 modeli: tip (webhook / cihaz config / elle), hedef, zamanlama, kimlik
bilgisi referansı, etkin-pasif, sahip grubu. CRUD API + yönetim ekranı.

İki karar ağır basıyor:

- **Şifreli saklama bu üründe ilk kez burada gerekiyor.** Anahtarın nerede
  duracağı (ortam değişkeni, dosya, dış KMS) **bu ticket'ta** karara bağlanmalı;
  sonradan değiştirmek saklanmış her kaydı yeniden şifrelemek demek.
- **Kimlik bilgisi hiçbir yanıtta, log'da veya hata mesajında görünmüyor** —
  testle sabitlenmiş. Bağlantı testi başarısız olduğunda bile hata mesajı
  sızdırmıyor.

Kapanış §2'nin üçüncü kusuru bu kuralın ne kadar ince olduğunu gösteriyor:
T26'da gizli değerler maskeleniyordu, ama sır **bölüm adının içinde** kalıyordu.
Maskeleme çalışıyordu; maskelenen şey eksikti. Bugünkü bekçi
(`changes-screen` kimlik bilgisi maskesi) **saf fonksiyon** ve kırmızı
yanabildiği ölçüldü.

Connector kapsamı da tek kapıdan geçiyor: yalnızca sahibinin kapsamındaki
kaynaklara bağlanabiliyor ([[concepts/f2-kapsam-tek-kapi]]).

## Cihaz config farkı: okuma amaçlı da olsa ilk cihaz bağlantısı

T26, ürünün cihazlara bağlanan **ilk** parçası — ve okuma amaçlı olması
güvenlik incelemesi ihtiyacını kaldırmıyor. Kimlik bilgileri T25'in şifreli
deposunda ve **en dar yetkiyle** (salt okuma hesabı).

Vendor sırası tesadüf değil: **FortiGate, Cisco ASA, MikroTik** — parser'ları
zaten var, yani cihazları zaten tanıyoruz.

İki kabul kriteri fark algoritmasının bütün mantığını taşıyor:

- **Gürültü elenmesi sınanmış:** yalnızca zaman damgası değişen iki çekim
  **değişiklik üretmiyor.** Bu olmadan tablo işe yaramaz gürültüyle dolar.
- **Çekim döngüsü ölmüyor:** cihaza erişilemediğinde connector hata kaydediyor,
  döngü sürüyor. Yüzlerce cihazda eşzamanlılık limiti var.

## Bilerek yapılmayanlar

Kapanış §7'nin beş kaleminden dördü bu alt sisteme ait ve hepsinin gerekçesi
yazılı:

| Ne | Neden |
| --- | --- |
| REST/SNMP taşıması | Üç vendor da SSH konuşuyor; üç yöntemi tek soyutlamaya baştan sıkıştırmak **erken genelleme** olurdu. Yüzey iki somut vendor yazıldıktan sonra çıkarıldı |
| Ham config yedekleme | Kopya tutulduğu an saklama, erişim ve **sızıntı sorumluluğu** doğar; RCA'nın ihtiyacı "ne değişti" |
| Cihaz connector'ı için ayrı ekran | T25'in formu tipi zaten sunuyor; ikinci ekran aynı alanları iki yerde tutmak olurdu |
| Bölüm içi satır taşınmasını fark saymak | Bildirimsel config'te sıra anlam taşımıyor ve cihazlar yeniden yazımda sırayı değiştiriyor; LCS her `write mem`'de **yüzlerce sahte değişiklik** üretirdi |

Son satır fark algoritmasının felsefesini özetliyor: bir farkın *tespit
edilebilir* olması onu **anlamlı** yapmıyor. ^[inferred]

## Bir yan etki: T26 indikten sonra kırılan kapı

T26 birleştikten sonra `api:generate`/`api:check` **kırıldı ve kimse
görmedi** — kusur ancak başka bir dalın birleştirmesinde, ona hiç dokunmamış
birinin gözünde göründü. Kalıbın kaydı [[references/f2-kapanis]] §2'de;
buradaki payı, F2'nin en büyük ticket'ının aynı zamanda doğrulama listesinden
düşen kapıyı ortaya çıkaran ticket olması.

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — K34 ve "Change feed — üç kaynak, üç ayrı büyüklük"
- `docs/epic/tickets-f2/change-webhook/index.md` — T24 imza, eşleme, idempotans, F3 gerekçesi
- `docs/epic/tickets-f2/change-connector/index.md` — T25 şifreli saklama, bağlantı testi
- `docs/epic/tickets-f2/change-config-diff/index.md` — T26 vendor sırası, gürültü elenmesi
- `docs/epic/f2-kapanis/index.md` — §2 Jenkins ve maskeleme kusurları, §4 maske bekçisi, §7 yapılmayanlar
