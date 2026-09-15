---
title: Kapanacak ile kapanmayacağı ayırmak
category: skills
tags: [surec, test, yordam, bizigo]
aliases: [Pending Exempt ayrımı, borç listesi disiplini]
relationships:
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: related_to
  - target: "[[concepts/kapsam-olcumun-sinirini-yazmak]]"
    type: related_to
  - target: "[[references/f2-kapanis]]"
    type: derived_from
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: related_to
sources:
  - docs/epic/is-envanteri/index.md
  - docs/epic/fs-simulatorler/index.md
  - docs/epic/t36-devir-notu/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=377d7b2c42fb docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048 docs/epic/t36-devir-notu/index.md=5027982dbb85"
summary: Bir açık liste, kapanacak kalemle hiç kapanmayacak kalemi bir arada tutarsa asla boşalmaz ve "bitti mi" sorusu cevapsız kalır. Ayrımı yapmanın, gerekçelendirmenin ve muafiyeti pahalı kılmanın yordamı.
provenance:
  extracted: 0.8
  inferred: 0.2
  ambiguous: 0.0
base_confidence: 0.76
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Kapanacak ile kapanmayacağı ayırmak

Bu deponun en çok yerde tekrarlanan kapsam kuralı, `CLAUDE.md` §8'de tek
cümle: *"Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz.*
İkisi tek listedeyken **"liste boşaldı mı" sorusunun cevabı asla evet
olamıyor** — ve boşalmayan bir liste okunmayı bırakıyor.

Kuralın envanterdeki karşılığı doğrudan yazılı
(`docs/epic/is-envanteri/index.md` §2): *"Ayrım olmadan liste hiç
boşalmıyordu: kapanacak bir kalemle kapanmayacak bir kalem yan yana durunca
ikisi de 'açık borç' gibi okunuyor ve kapanmayanlar listeyi sonsuza kadar dolu
gösteriyordu."*

## Yordam

### 1 · Listeyi üçe böl

Envanterin kullandığı biçim: **2.1 Kapanacak** · **2.2 Kapanmayacak —
gerekçesiyle kabul edilmiş** · **2.3 Bu turda kapanan** (üstü çizili). Üçüncü
bölüm bir tarih değil bir **kanıt** taşır: nasıl kapandığı.

### 2 · Gerekçe, alternatifin maliyeti olsun

Kabul edilen risk, unutulan risk değildir. İki örnek, ikisi de "alternatifi
daha sessiz bir kusur üretiyordu" diyor:

- **B9** — `GetDocument.Insider` adına bağımlılık. Ayrımı ortam değişkenine
  bağlamak daha kötü olurdu: bayrak üretime taşınabilir ve göçleri sessizce
  atlayan bir API bırakırdı. Araç adı değişirse belge üretimi **kırmızı
  yanıyor** — hatanın doğru yönü.
- **B16** — worktree `node_modules` bayatlığı. Ajanlar ayrı worktree'lerde
  çalıştığı sürece **yapısal**; azaltılabilir, yok edilemez.

### 3 · Kapsam engelini borç sanma

`docs/epic/fs-simulatorler/index.md` bunu fazın var olma sebebi olarak
yazıyor: ekip gerçek cihazlara erişmiyor, dolayısıyla erişim olmadan
kapanmayacak kalemler var ve onları "yapılacak iş" listesinde tutmak *"hiç
kapanmayacak bir listeyi kapanacakmış gibi göstermek"* olur. Aynı belgenin
§11'i o kalemleri ayrı bir başlıkta sayıyor: vendor sürüm farkları, ölçek, ağ
gerçekliği.

Simülatör bunları **kapatmıyor**, yalnızca sınırını görünür kılıyor — ve bu
ayrım [[concepts/kapsam-olcumun-sinirini-yazmak]] ile aynı disiplinin liste
üzerindeki hâli.

### 4 · Muafiyeti iki bilinçli hareket yap

`ExpectedExemptCount` bir sayıyı sabitliyor; muafiyet eklemek hem listeye
yazmayı hem sayıyı artırmayı gerektiriyor. Aynı kalıp kanıt paketinde de var:
`CurrentSchemaVersion` ile `MinReadableSchemaVersion` ayrı sabitler ve
"sürüm artırmak iki ayrı bilinçli hareket gerektiriyor"
(`docs/epic/t36-devir-notu/index.md` §9).

Not: elle tutulan bir sayı normalde
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]'in tam tarifi. Burada
kabul edilebilir olmasının sebebi, listenin *denetlenen* kümeyi değil
**beklenen** kümeyi tutması — F2 kapanışında ölçülen ayrım budur.

### 5 · Bitiş şartını listeye bağla, ama listeyi bitiş şartı sanma

İki uyarı, ikisi de yaşanmış:

- **Bağlamak işe yarıyor:** `Pending` listesi 21 satırdan bire indi ve fazın
  kapanış ölçütü ona bağlıydı.
- **Yetmiyor:** F2 kapanışında `Pending` bitiş şartlarından **biriydi, tamamı
  değil**; dört uçtan uca akış ve iki çapraz doğrulama da isteniyordu ve
  belge onlara hiç değinmedi. Ayrıntı: [[references/f2-kapanis]].

Simülatör fazının kapanış ölçütü aynı biçimde tek ve **silinebilir** bir şeye
bağlanmış: uçtan uca harness'taki elle tohumlama silinebildiği gün faz bitmiş
sayılıyor (`fs-simulatorler` §6), ve bunu bir bekçi tutuyor — S05 sonrası
harness'ta kalan bir `INSERT` varsa düşüyor.

### 6 · Boş listenin kendisi de bilgidir

`t36-devir-notu` §1: `NotConsulted` listesinin boş olması *"her şeye bakıldı"*
demek ve **gösterilmeye değer**. Bugün F5'in üç türü her zaman `NotRegistered`
döndüğü için liste hiç boşalmıyor; o gün geldiğinde bölümün sessizce
kaybolması yanlış olur.

Kardeş kural: her raporda duran bir uyarı bilgi taşımıyor — `OutOfScopeCount`
sıfırken satır hiç yazılmıyor.

## Nerede uygulanmış (kontrol listesi)

| Liste | Kapanacak tarafı | Kapanmayacak tarafı |
| --- | --- | --- |
| Uç sözleşmeleri | `ProducesContractTests.Pending`, ticket atfıyla | `Exempt` + `ExpectedExemptCount` |
| Teknik borç | Envanter §2.1 | Envanter §2.2, gerekçeleriyle |
| Doğrulanmamışlar | D listesi (D3, D5, D7) | — kapsam engelleri ayrı yazılmış |
| Simülatör fazı | S01–S07 bitti ölçütleri | §11 "bu belgenin bilmediği" |
| Kanıt paketi | `Silent` (bakıldı, çıkmadı) | `NotConsulted` (bakılamadı) |

## Kaynaklar

- `CLAUDE.md` — §8 sözleşme ve kapsam disiplini
- `docs/epic/is-envanteri/index.md` — §2 üç bölümlü borç tablosu
- `docs/epic/fs-simulatorler/index.md` — giriş kısıtı, §6 kapanış ölçütü,
  §10 bekçiler, §11 kapanmayacaklar
- `docs/epic/t36-devir-notu/index.md` — §1 boş liste, §9 sürüm sabitleri
