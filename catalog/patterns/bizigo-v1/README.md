# `bizigo-v1` — lookaround'suz temel pattern seti

Bu dizin **tam bir set değil, bir kaplama (overlay)**. `legacy` setinin üzerine
bindirilir ve yalnızca adı geçen pattern'leri değiştirir:

```csharp
var library = GrokPatternLibrary
    .LoadFromDirectory(".../catalog/patterns/legacy")
    .With(overlay);   // bizigo-v1
```

`legacy` ve `ecs-v1` upstream'in birebir kopyası ve öyle kalmalı — yükseltme
`cp -R` ile yapılabilsin diye. Kaplama, upstream'e dokunmadan tek tek pattern
düzeltmenin yolu.

## Neden

`GrokCompiler` önce `RegexOptions.NonBacktracking` ile derlemeyi deniyor
(F1 §4.1): doğrusal zaman garantisi, katastrofik geri izleme imkânsız. Ama
NonBacktracking lookaround, geri referans ve atomik grup desteklemiyor; böyle
bir pattern geri izlemeli motora düşüyor ve `parser lint` GROK003 uyarısı
veriyor.

T08 kataloğunda **21 GROK003** ölçüldü ve kökenleri birkaç TEMEL pattern'di —
kataloğun kendi ifadeleri değil. Ağ cihazı logunda IP ya da saat geçmeyen
pattern neredeyse yok, dolayısıyla doğrusal motor pratikte hiç devreye
girmiyordu.

## Sınır davranışı — bu işin asıl zorluğu

`IPV4`'ün `(?<![0-9])` / `(?![0-9])` sınırları gerçek bir işe yarıyor:
`1.2.3.45` içinde `1.2.3.4` yakalanmasın. Lookaround'suz karşılık `\b`
(kelime sınırı) — NonBacktracking `\b`'yi destekliyor.

`\b`, yerine geçtiği lookaround'dan **daha katı**:

| Konum öncesi/sonrası | `(?<![0-9])` / `(?![0-9])` | `\b` |
| --- | --- | --- |
| satır başı/sonu | geçer | geçer |
| `=`, `:`, `/`, boşluk (kelime dışı) | geçer | geçer |
| rakam | **engeller** | **engeller** |
| harf veya `_` | geçer | **engeller** |

Yani `\b` hiçbir zaman lookaround'un yakalamayacağı bir şeyi yakalamıyor —
**yanlış pozitif üretmesi imkânsız**. Tek fark, harfe bitişik IP'leri
kaçırması: `host1.2.3.4` ve `1.2.3.4abc` legacy'de eşleşir, burada eşleşmez.

Bu bilinçli kabul edilen tek sapma ve testlerle sabitlendi
(`BizigoV1PatternTests`). Log alanlarında IP'ler `=`, `:`, `/` ya da boşlukla
sınırlanıyor; harfe bitişik bir rakam dizisi zaten IP alanı değil.

## Kapsam

Şimdilik iki pattern. Kalanlar ölçülüp ayrı ayrı ele alınacak — hepsini birden
yeniden yazmak, sınır davranışını tek tek doğrulamadan değiştirmek olurdu.

| Pattern | Kaldırılan | Yerine |
| --- | --- | --- |
| `IPV4` | `(?<![0-9])` … `(?![0-9])` | `\b` … `\b` |
| `TIME` | `(?!<[0-9])` … `(?![0-9])` | `\b` … `\b` |

`TIME`'ın baştaki `(?!<[0-9])` ifadesi upstream'de bir **yazım hatası**:
"`<` ve ardından rakam gelmesin" diyor, oysa niyet `(?<![0-9])` (rakamla
başlamasın). Pratikte etkisiz — ama etkisiz olduğu hâlde pattern'i geri
izlemeli motora düşürüyordu.
