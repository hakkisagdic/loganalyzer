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
sources: [docs/epic/f2-kapanis/index.md, CLAUDE.md]
source_digest: "sha256-12/v1 CLAUDE.md=3984257f89e8 docs/epic/f2-kapanis/index.md=c701d88f78fd"
summary: Denetlenen kümeyi elle tutulan bir listeden toplayan bekçi, listede olmayanı hiç görmez ve yine de yeşil yanar. Çözüm kümeyi yansımayla bulmak.
provenance:
  extracted: 0.8
  inferred: 0.2
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
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

**Denetlenen kümeyi yansımayla bul.** Elle kalan tek şey artık *denetlenen*
küme değil, *beklenen* küme — `ExpectedExemptCount` gibi tek bir sayı.

Bu, `CLAUDE.md` §8'in muafiyet kuralıyla aynı fikir: muafiyet eklemek **iki
ayrı bilinçli hareket** gerektirsin diye sayı ayrı tutuluyor.

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
- `CLAUDE.md` §7 ve §8
