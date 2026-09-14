---
title: Elle tutulan liste bekçiyi körleştirir
category: concepts
tags: [test, surec, kavram, bizigo]
aliases: [manuel liste kalıbı, yansımayla denetim]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[references/f2-kapanis]]"
    type: derived_from
sources: [docs/epic/f2-kapanis/index.md, docs/epic/tickets-f3/produces-kapisi-bagi/index.md, docs/epic/tickets-f3/kompozisyon-koku-bagi/index.md, CLAUDE.md]
source_digest: "sha256-12/v1 CLAUDE.md=d270c2c035f1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/tickets-f3/kompozisyon-koku-bagi/index.md=4f32c80bd6b5 docs/epic/tickets-f3/produces-kapisi-bagi/index.md=67247a04a205"
summary: Denetlenen kümeyi elle tutulan bir listeden toplayan bekçi, listede olmayanı hiç görmez ve yine de yeşil yanar. Çözüm kümeyi yansımayla bulmak — ama bir kapıdaki elle listeyi kaldırmak, o kapıda başka elle liste kalmadığını göstermiyor.
provenance:
  extracted: 0.8
  inferred: 0.2
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-09-05T00:00:00Z
---

# Elle tutulan liste bekçiyi körleştirir

[[concepts/sessiz-yanlis-davranis]] sınıfının bekçi katmanındaki somut hâli.
Bir kapı, **denetleyeceği kümeyi elle yazılmış bir listeden** topluyorsa,
listede olmayan hiçbir şey kapıya görünmez — ve kapı yine yeşil yanar.
Yeşilliği hiçbir şey ifade etmez.

## Ölçülen tekrar sayısı: beş

F2 kapanışı (`docs/epic/f2-kapanis/index.md` §2) aynı kalıbın **beş kez**
tekrarlandığını ve her seferinde bekçinin yeşil yandığını kaydediyor:

| Nerede | Ne göremedi |
| --- | --- |
| `Produces<T>` kapısı | Elle `Map*` listesi — **16 uç** kapıya hiç görünmedi, üç test de geçti |
| Ömür bekçisi | Elle `Add*` listesi — kanıt katmanı görünmedi |
| Ömür bekçisi | Aynı liste **doğduğu gün** eksikti: kimlik grafiği hiç denetlenmemişti |
| Ömür bekçisi | `AddBizigoDiscovery` başka bir uzantının içinden çağrılıyor; elle listeye girmesi akla gelmezdi |
| Kapı, ikinci yarı | Uç bulunuyordu ama **çağrılamıyordu**; bağımlılığı kayıtlı olmayan `Map*` patlıyor ve o dosya yine denetlenmemiş kalıyordu |

Dördüncü satır kalıbın neden disiplinle çözülemeyeceğini gösteriyor: liste
eksikti çünkü çağrı **başka bir uzantının içinden** geliyordu. Yazan kişi
dikkatsiz değildi; liste yanlış mekanizmaydı. ^[inferred]

## Çözüm hep aynı yöne gitti

**Denetlenen kümeyi yansımayla bul.** Elle kalması gereken tek şey *denetlenen*
küme değil, *beklenen* küme — `ExpectedExemptCount` gibi tek bir sayı.

Bu, `CLAUDE.md` §8'in muafiyet kuralıyla aynı fikir: muafiyet eklemek **iki
ayrı bilinçli hareket** gerektirsin diye sayı ayrı tutuluyor.

## Ama bir liste kaldırmak, listenin tek olduğunu göstermiyor

Yukarıdaki cümle F2 kapanışında yazıldığında **eksikti**, ve eksikliği dört
tekrar daha üretti. `Produces<T>` kapısı denetlenen kümeyi yansımayla buluyordu
— ama uçları kaydedebilmek için **ikinci** bir elle liste tutuyordu: minimal
API'nin "bu parametre servis mi gövde mi" ayrımını yapabilmesi için gereken
asgari servis kayıtları. Bir uç dosyası listede olmayan bir servis enjekte
ettiğinde o dosyanın uçları kapıdan düşüyordu.

Bu ikinci liste `AlertPreview`, `CatalogCoverageCache`, `ParserPublishGate` ve
`RcaAdmission` ile **dört kez** eksik kaldı ve her seferinde bulan kişi
farklıydı (`docs/epic/tickets-f3/produces-kapisi-bagi/index.md`).

Kalıbın buradaki dersi birincisinden ayrı: **bir kapıdaki elle listeyi
kaldırmak, o kapıda başka elle liste kalmadığını göstermiyor.** İlk liste
"neyi denetliyorum" sorusunun cevabıydı ve görünürdü; ikincisi "denetleyebilmek
için neye ihtiyacım var" sorusununkiydi ve bir kurulum ayrıntısı gibi
duruyordu. Görünmeyen liste, görünen listeden daha uzun yaşadı. ^[inferred]

T48 ikinciyi de türetilmiş hâle getirdi: kaydedilecek servis kümesi uç
dosyasının kendi metot imzalarından çıkarılıyor, ve neyin zaten servis olduğu
`IServiceProviderIsService`'e **soruluyor** — üçüncü bir elle liste doğmasın
diye.

### Ölçülen ölçüt: "kaç test düştü" değil

Dördüncü tekrarda 15 test birden düştü ve bu bir iyileşme sanıldı. Değildi:
eksik servis gövde parametresi gibi görünecek bir **şekle** sahipti ve yansıma
patladı. Şekli biraz farklı olsaydı uçlar sessizce kaybolurdu.

Doğru soru **kapı ucu görüyor mu**, ve o soru ancak ölçülerek cevaplanıyor:
eski kapıya adı `Map` ile başlamayan gerçek bir ürün ucu eklendiğinde on altı
testin **on altısı yeşil** kaldı. Bir bekçinin kapsamını ölçmenin yolu, kapsamı
dışında bir şey yaratıp bekçinin sustuğunu görmek.

## Üçüncü kılık: konvansiyona bakan keşif

Yansıma bir listeyi kaldırıyor ama yerine bir **konvansiyon** koyuyor, ve
konvansiyon da elle tutulur — yalnızca başka bir dosyada.

T50 bunun ölçülmüş örneğini buldu: bu depodaki yansıma keşifleri ürün
derlemelerini `Bizigo.` **önekinden** tanıyor. `src/Bizigo.Cli` ise derlemesini
`<AssemblyName>bizigo</AssemblyName>` ile yeniden adlandırıyor, yani önekli
keşiflerin **hepsine görünmez**. Delik `CLAUDE.md` §4'ün *"dizin adına
güvenme, `git worktree list` çıktısına bak"* dersiyle aynı biçimde: ad bir
kimlik değil bir alışkanlık.

Kaçınma yolu aynı yöne gidiyor — **türetilmiş kaynağa bak**: T50'nin kapısı
derleme adı önekine değil, diskteki proje dosyalarına ve onların gerçek
`<AssemblyName>` değerine bakıyor.

Kapsam notu, çünkü kapsamsız doğru işaret de yanıltıyor: bu kör noktanın
**bugün kurbanı yok** — CLI derlemesi tek bir kayıt/uç uzantısı bildirmiyor,
ölçüldü. Gerçek bir delik, henüz düşen bir şey yok. ^[extracted]

## Kardeş kalıp: doğrulama listesinden düşen kapı

Aynı belge üçüncü bir kalıp daha sayıyor: T26 indikten sonra
`api:generate`/`api:check` kırıldı ve kimse görmedi. Belge üretimi `Main`'i
gerçekten çalıştırdığı için **tek gerçek DI doğrulaması oydu**; kusur ancak
başka bir dalın birleştirmesinde, ona hiç dokunmamış birinin gözünde görüldü.

Ders şu cümleyle kayıtlı:

> Bir kusurun yalnızca tesadüfen görülebilir olması, kusurun kendisi kadar ciddi.

`CLAUDE.md` §5'in "bir kapının kırmızı yanması ile o kırmızının okunması ayrı
olaylardır" maddesi aynı gözlemin merge tarafındaki karşılığı — bkz.
[[skills/paralel-ajan-koordinasyonu]].

## Açık sorular

- Yansımayla denetim her kapıya uygulanabilir mi, yoksa bazı kapılarda
  (örneğin YAML yapılandırma dosyaları) elle liste kaçınılmaz mı? ^[ambiguous]

## Kaynaklar

- [[references/f2-kapanis]] — §2, "İkinci kalıp" ve "Üçüncü kalıp"
- `docs/epic/tickets-f3/produces-kapisi-bagi/index.md` — dört tekrarın kaydı,
  seçilen çözüm, kaçırdığı hâller ve ölçülen kırmızılar
- `docs/epic/tickets-f3/kompozisyon-koku-bagi/index.md` — konvansiyona bakan
  keşfin ölçülmüş örneği
- `CLAUDE.md` §7 ve §8
