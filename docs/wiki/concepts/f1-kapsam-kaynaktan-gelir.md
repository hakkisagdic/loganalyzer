---
title: Kapsam kaynaktan gelir, tek kapıdan geçer
category: concepts
tags: [guvenlik, mimari, kavram, bizigo]
aliases: [owner_group, kapsam kapısı, IScopedQuery]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: related_to
  - target: "[[concepts/f1-sema-karari-bir-kez-verilir]]"
    type: constrains
  - target: "[[concepts/f1-ham-sadakat-zinciri]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: part_of
sources:
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/f1-kapanis/index.md
  - docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md
  - docs/epic/tickets/dispatcher-ve-envanter/index.md
  - docs/epic/tickets/ham-arsiv/index.md
  - docs/epic/tickets/kimlik/index.md
  - docs/epic/tickets/api-uclari/index.md
  - docs/epic/tickets/normalizasyon/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=d1f9862fea36 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=1c733a465f06 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/tickets/api-uclari/index.md=4e2624c6db9a docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md=7a1070c1c1da docs/epic/tickets/dispatcher-ve-envanter/index.md=5b0c998c5ca7 docs/epic/tickets/ham-arsiv/index.md=f7b6e1d7e3a0 docs/epic/tickets/kimlik/index.md=1777b62193e0 docs/epic/tickets/normalizasyon/index.md=74d881911e1b"
summary: K17'nin iki cümlesi — kapsam olayın değil kaynağın özelliğidir ve tek bir kapıda uygulanır — F1'in yedi ticket'ına yayılıyor; şemadan nesne anahtarına, claim sözleşmesinden 404 kararına kadar.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Kapsam kaynaktan gelir, tek kapıdan geçer

K17 tek satırlık bir karar gibi görünüyor: *"kaynak/cihaz grubu bazlı erişim"*
(`docs/epic/mimari-kararlar/index.md` §1). Ama F1'in yedi ticket'ında iz bıraktı,
çünkü iki ayrı iddia taşıyor ve ikisi de yapısal:

1. **Kapsam olayın değil kaynağın özelliğidir.**
2. **Zorlama tek bir yerde olur.**

Bu sayfa o iki iddianın kodda nereye dokunduğunu birleştiriyor; parçaları plan,
kapanış ve ticket belgelerine dağılmış durumda.

## 1 · Grup olaydan değil kaynaktan gelir

Atama zinciri (`docs/epic/f1-teknik-plan/index.md` §8):

```
kaynak kimliği (syslog peer IP / hostname / cihaz etiketi)
      → sources tablosu (Postgres) → owner_group
```

Parser'ın işi değil, envanterin işi. Bunun üç ölçülebilir sonucu var:

- **Eşleşmeyen kaynak reddedilmiyor**, `_unassigned` grubuna düşüyor ve bir sağlık
  uyarısı üretiyor. Gerekçe ticket'ta açıkça yazılı: *veri kaybı, eksik
  envanterden kötüdür* (`docs/epic/tickets/dispatcher-ve-envanter/index.md`).
- **Ham arşivin nesne anahtarı `owner_group` taşıyor**
  (`raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/…`), çünkü ham okuma da aynı filtreden
  geçmek zorunda. Bu, T04'ün kapsamını T06'nın alanına taşıdı: `SourceDirectory`
  biçimsel olarak T06'nın kalemiydi ama anahtar bir kez yazılıp değişmediği için
  yüklemeden **önce** çözülmesi gerekti
  (`docs/epic/tickets/ham-arsiv/index.md`).
- **`ORDER BY` ön ekinin ilk kolonu `owner_group`** — kapsam filtresi her sorguda
  bulunduğu için sıralama anahtarı ona göre seçildi. Ayrıntı:
  [[concepts/f1-sema-karari-bir-kez-verilir]].

## 2 · Tek kapı: `IScopedQuery`

Zorlama noktası ClickHouse row policy değil, sorgu API'si
(`docs/epic/f1-teknik-plan/index.md` §10.2). Gerekçe geleceğe bakıyor: F3'ün kanıt
toplayıcısı ve F4'ün MCP sunucusu da aynı API'den geçecek, dolayısıyla kapı bir
tane olmalı.

Kapının kendisi bir bekçiyle korunuyor: **NetArchTest**, `Bizigo.Query.Internal`
dışındaki hiçbir tipin `ClickHouse.Driver`'a referans vermesine izin vermiyor —
yani ihlal derleme zamanında kırmızı yanıyor. Ticket bunun sebebini tek cümleyle
yazmış: *bu kural olmadan kapsam ayrımı ilk aceleci PR'da delinir*
(`docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md`).

Kapının **iki kez tanımlanmaya** eğilimli olduğu T10'da ölçüldü: envanter listesi
ilk yazımda kapsam filtresini uç katmanında elle uyguluyordu. Düzeltme filtreyi
uçtan alıp `IScopedQuery.SearchSourcesAsync`'e taşımak oldu
(`docs/epic/tickets/api-uclari/index.md`). Aynı disiplin normalizasyonda da
görünüyor: OCSF/OTel görünümlerine kapsam filtresi **gömülmedi**, `owner_group`
kolonu aynen taşınıyor — görünüme filtre koymak kapsamı iki yerde tanımlamak
olurdu (`docs/epic/tickets/normalizasyon/index.md`).

### Kapıdan geçen şey yalnızca okuma değil

`IScopedQuery.WriteChangeAsync` çağıranın yalnızca kendi kapsamındaki bir gruba
yazabildiğini doğruluyor. Sebep RCA'ya bakıyor: aksi hâlde bir ekip başka bir
ekibin zaman çizelgesine olay düşürebilir ve F3'ün RCA'sı **yanlış kanıtla**
çalışırdı (`docs/epic/tickets/api-uclari/index.md`).

## 3 · Kapsamın kenar kararları

Bu dört karar aynı ilkeden çıkıyor ama kolayca kaçırılır; hepsi
`docs/epic/tickets/api-uclari/index.md` ve `docs/epic/tickets/kimlik/index.md`
içinde:

| Karar | Gerekçe |
| --- | --- |
| Kapsam dışı olay **404**, 403 değil | 403 *"böyle bir olay var ama göremezsin"* bilgisini sızdırır |
| Serbest SQL kabul eden uç **yok** | Tek zorlama noktası sorgu API'si; serbest SQL kapsamı arka kapıdan deler |
| Katalog uçlarına kapsam **uygulanmıyor** | Katalog veri değil yapılandırma; hangi parser'ların var olduğunu görmek kimsenin logunu görmek değil |
| `Auth:Enabled=false` iken bile `AccessScope` **`Denied`** başlıyor | *"Kimlik yoksa her şeyi gör"* varsayılanı bu üründe yapılabilecek en pahalı hata olurdu |
| CSV envanter yüklemesi **ya hep ya hiç** | Yarı yüklenmiş envanter, hangi cihazın hangi gruba düştüğünü belirsiz bırakır — belirsizlik doğrudan kapsam hatasına döner |

## 4 · Kimlik tarafı: dört claim'lik sözleşme

Ürünün IdP'den beklediği tek şey `sub`, `preferred_username`, `roles`, `groups`
(`docs/epic/f1-teknik-plan/index.md` §10.1.1). Sözleşmenin dar tutulmasının
karşılığı ölçülebilir: Keycloak → Entra ID geçişi IdP tarafında mapper ayarı +
`idp_group_mapping` satırı demek, **kodda değişiklik yok**.

İki tuzak kayda geçmiş:

- Keycloak Group Membership mapper'ı tam yolu **başında eğik çizgiyle** basıyor
  (`/network/core`). Tam yol açık bırakıldı, eşleme tablosu tam yolu saklıyor,
  giriş `TrimStart('/')` ile normalize ediliyor.
- Claim'i doğrudan `owner_group` saymak reddedildi: bir ekibin kapsamını
  değiştirmek için IdP'ye dokunmak gerekirdi. Eşleme Postgres'te kalıyor.

### En sessiz kırık buradaydı

F1'in doğrulama turunda çıkan hata bu sayfanın konusunun tam kalbinde:
realm JSON'unda `clientScopes` dizisi verildiği için Keycloak yerleşik scope'ları
hiç oluşturmadı; `sub` ve `preferred_username` hiçbir token'da yoktu. **Ama
yetkilendirme çalışmaya devam ediyordu** — roller yerindeydi — dolayısıyla hiçbir
belirti üretmedi. Kırılan şey *"bu isteği kim yaptı"* sorusunun cevabıydı, yani
audit (`docs/epic/f1-kapanis/index.md`).

Bu, [[concepts/sessiz-yanlis-davranis]] sayfasının anlattığı sınıfın kimlik
katmanındaki örneği: kapsam doğru çalışırken denetim sessizce boşalmıştı.

Çözüm yerleşik scope'ları kopyalamak değil, ihtiyaç duyulan claim'leri kendi
scope'umuza yazmak oldu — Keycloak sürümünden bağımsız. Bunun bugünkü izi
`CLAUDE.md` §12'de duruyor: realm'de `bizigo-claims` var ve
`scope=openid profile email` canlıda `invalid_scope` alıyor.

> ⚠️ Bu cümlenin *"realm'de **yalnızca** `bizigo-claims` var"* hâli bugün
> yanlıştı: M09 ikinci bir client scope ekledi (`bizigo-mcp`, isteğe bağlı,
> RFC 8707 kaynak kimliği için). Aynı bayat iddia **iki** vault sayfasında
> duruyordu; M17 birini düzeltti, ikincisi bu sayfaydı ve damgası düşene kadar
> görünmedi. Bir iddianın tek yerde düzeltilmesi, düzeltildiği anlamına
> gelmiyor.

## Açık kalan

- Kapsamın **ölçülmüş** kanıtı F1 kapanışında yalnızca giriş akışı düzeyinde:
  `analyst.core` → `/network/core`, `analyst.edge` → `/network/edge`, collector
  yalnızca `ingest`. Her uç için negatif test var (T10'da sekiz tane).

- **"Hiçbir yoldan" iddiası ÖLÇÜLDÜ ve YANLIŞ ÇIKTI** (M18). Bu sayfanın bir
  önceki hâli tam bunu bekliyordu — *"tam kanıtı yolların tamamının sayılmasına
  bağlı"* — ve sayıldığında kriterin **kendisi** yanlış çıktı: F1'in kabul
  kriteri üç yol sayıyordu (REST, replay okuma, CLI) ve **yalnızca REST** kapsam
  kapısından geçiyor. `ReplayEngine` `AccessScope.System("replay")` ile okuyor,
  CLI `IScopedQuery`'yi hiç anmıyor.

  İkisinin kapsam dışı olması **doğru** ve ayrı sebeplerle (replay bir sistem
  işlemi ve kapsamlanmış bir kurtarma arşivin yalnızca bir kısmını geri
  yükleyebilirdi; CLI'da kimlik yok, çağıran zaten veritabanı erişimine sahip).
  Yanlış olan **kriterin metni**: *"bütün okuma yolları"* ile *"kullanıcıya açık
  sorgu yolları"* karıştırılmış. Kriter düzeltildi ve gerekçeleri
  `docs/epic/f1-kapsam-kriteri-duzeltmesi/index.md`'de.

  Ve asıl bulgu, iddianın yanlış olması değil: **F1 kapandığından beri hiçbir
  koşum onu üç yol için sınamadı.** Bugün sınayan şey M18'in türetilmiş tüketici
  kümesi (`ScopedQueryConsumerTests`) — elle liste değil, meta veriden.

## Kaynaklar

- `docs/epic/mimari-kararlar/index.md` — K16, K17, §6, §6.1
- `docs/epic/f1-teknik-plan/index.md` — §8, §10.1, §10.1.1, §10.2
- `docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md` — `IScopedQuery`, NetArchTest
- `docs/epic/tickets/dispatcher-ve-envanter/index.md` — `_unassigned`, kaynak→grup
- `docs/epic/tickets/ham-arsiv/index.md` — nesne anahtarında `owner_group`
- `docs/epic/tickets/kimlik/index.md` — claim sözleşmesi, `GroupMapping`
- `docs/epic/tickets/api-uclari/index.md` — tek kapı düzeltmesi, 404/403, CSV
- `docs/epic/tickets/normalizasyon/index.md` — görünümlere filtre gömülmemesi
- `docs/epic/f1-kapanis/index.md` — denetim kimliğinin sessiz kaybı
