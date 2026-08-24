---
title: Hot Cache
updated: 2026-08-24T18:20:00Z
---

# Hot Cache

*Vault'un bugünkü hâlinin anlık görüntüsü. Her büyük yazma turundan sonra
yeniden yazılıyor; kataloğun kendisi [[index]], işlem kaydı [[log]].*

## Ne var

**40 içerik sayfası, 313 iç bağlantı, kırık bağ yok.** Dağılım: 24 kavram,
10 yordam, 5 kaynak özeti, 1 proje genel bakışı. Hepsi `lifecycle: draft`;
38'i `core`, 2'si `supporting`. `entities/`, `synthesis/` ve `journal/` boş —
vault bugüne kadar yalnızca **deponun kendi belgelerinden** damıtıldı.

Kaynak yüzeyi: `docs/epic/` altındaki 40'tan fazla dizin, `CLAUDE.md`,
`README.md`, `docs/graphify.md`, `docs/ekran-goruntuleri/BENIOKU.md`. Her sayfa
`sources:` ile bunlara bağlı ve bir `source_digest` taşıyor; bayatlama bekçisi
(`tests/Bizigo.UnitTests/WikiSourceDigestTests.cs`) damgayı yeniden hesaplayıp
karşılaştırıyor. Ayrıntı ve kırmızı yandığında ne yapılacağı [[README]]'de.

## En bağlantılı düğümler

| Sayfa | Gelen bağ |
| --- | --- |
| [[concepts/sessiz-yanlis-davranis]] | 32 |
| [[concepts/elle-tutulan-liste-bekciyi-korlestirir]] | 17 |
| [[references/f2-kapanis]] | 15 |
| [[projects/bizigo-loganalyzer/bizigo-loganalyzer]] | 14 |
| [[skills/paralel-ajan-koordinasyonu]] | 13 |

Grafın şekli tek başına bir bulgu: en çok bağlanan beş düğümün dördü bir **hata
sınıfı ya da çalışma protokolü** sayfası, yalnızca biri bir faz özeti. Vault
ürünün ne yaptığından çok **ne yanlış gidebileceği ve bunun nasıl ölçüldüğü**
etrafında kümelenmiş durumda. ^[inferred]

## Kümeler

- **Ölçüm kültürü** — beş kavram (`olcum-*`) ve iki yordam. Ortak omurga: bir
  sayı ancak paydası, eşiği ve onu üreten komutla birlikte bir şey söylüyor.
  Merkez: [[concepts/olcum-kirmizi-yanamayan-sayi]],
  [[skills/olcum-protokolu-sonuctan-once]].
- **Faz dilimleri** — F1 (5 kavram + 2 yordam) ham veriyi ve şema kararlarını,
  F2 (5 + 2) görünürlüğü, F3 (4 + 2) detection ile kanıtı taşıyor. Üç dilim
  birbirine faz sınırından değil **hata sınıfından** bağlanıyor.
- **Kapsam ve durum** — üç kavram, bir yordam, iki referans: neyin yazılmadığı,
  sınırın nereye kaydığı ve bugün nerede kalındığı.

Etiket dağılımı aynı şeyi söylüyor: `test` 21 sayfada, `mimari` ve `log-analiz`
16'şar, `surec` 14. `arayuz` yalnızca 3 sayfada — F2'nin yedi ekranı vault'ta
ekran olarak değil, **karar** olarak duruyor.

## Açık uçlar

- **Hepsi hâlâ `draft`.** Hiçbir sayfa gözden geçirilip `stable`'a alınmadı.
- **`entities/` boş.** ClickHouse, Keycloak, RustFS, Sigma ve graphify sayfalar
  boyunca geçiyor ama hiçbirinin kendi sayfası yok.
- **`synthesis/` boş.** İki aday görünüyor: ölçüm kültürü sayfalarını tek bir
  yordama bağlayan bir sentez, ve T30/T39 ölçümünden ayrı ayrı beslenen
  [[concepts/olcum-capraz-eksen]] ile [[skills/f3-eslesmeyen-kural-teshisi]]
  arasındaki ortak kural.
- **F3 kapanmadı.** [[references/f3-detection-ve-rca-kaniti]] ve
  [[references/kapsam-nerede-kalindi]] devreden borcu taşıyor; faz ilerledikçe
  bu iki sayfa ilk bayatlayacak olanlar.

## Okuma yolu

Vault'a soğuktan girenin üç adımı: önce
[[projects/bizigo-loganalyzer/bizigo-loganalyzer]] (ürün ne, hangi faz kapandı),
sonra [[concepts/sessiz-yanlis-davranis]] (geri kalan 39 sayfanın etrafında
döndüğü hata sınıfı), sonra ilgilenilen fazın referans sayfası —
[[references/f2-kapanis]] ya da [[references/f3-detection-ve-rca-kaniti]].
Bir karara değil bir **yordama** ihtiyaç varsa giriş [[index]]'in Skills
bölümü; oradaki on sayfanın her biri kendi kontrol listesiyle bitiyor.

## Flagged Contradictions

*Yok.* Bu turda sayfalar arasında çelişen bir iddia bulunmadı — arandı.
