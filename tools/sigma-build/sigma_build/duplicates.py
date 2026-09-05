"""Tekrarlanan değeri bulan **tek** yer (T32).

Neden ayrı bir modül
--------------------
Aynı iş bu araçta üç ayrı dosyada üç kez yazılmıştı ve üçü de **karesel**di:

```python
{x for x in items if sum(1 for y in items if y == x) > 1}
```

İç `sum(...)` her öğe için bütün listeyi yeniden tarıyor, yani **öğe başına**
maliyet listenin boyuyla büyüyor ve toplam maliyet öğe sayısının karesi oluyor.
Ölçüldü (karşılaştırma sayarak, duvar saati olmadan):

| Kural | Kimlik karşılaştırması | Kural başına |
| --- | --- | --- |
| 24 | 576 | 24 |
| 269 | 72.361 | 269 |
| 7.400 (SigmaHQ'nun tamamı) | ~55.000.000 | 7.400 |

24 kuralda görünmüyor; çivinin bugün hedeflediği 269'da görünmeye başlıyor.
İkisi `build_manifest` ve `check_corpus_shape` içindeydi — yani hem `--write`
hem **`--check`** yolunda: kural sayısı büyüdükçe CI kapısı kendi kendini
yavaşlatıyordu.

Üç kopyanın üçü de aynı deyimden çıktı, ve deyimin ikinci kez yazılması
üçüncüsünü ucuzlattı. `CLAUDE.md` §9: *"İkinci kopya yazma."* Tek yerde durması,
bir daha üçe çıkmamasının tek mekanik yolu — burada bir kez düzeltilen şey üç
çağrı yerinde birden düzeliyor.

Bekçisi `tests/test_cost.py`: kural başına kimlik dokunuşunun korpus boyuyla
**büyümediğini** sabitliyor. Sayı duvar saatine değil karşılaştırma sayısına
bağlı, yani yüklü makinede de sessiz makinede de aynı çıkıyor (`CLAUDE.md` §6:
*"duvar saati değilse süreyi denklemden çıkar"*).
"""

from __future__ import annotations

from collections import Counter
from typing import Hashable, Iterable, TypeVar

__all__ = ["duplicate_values"]

T = TypeVar("T", bound=Hashable)


def duplicate_values(values: Iterable[T]) -> list[T]:
    """Birden fazla geçen değerler, sıralı ve tekilleştirilmiş.

    Maliyet öğe sayısıyla **doğrusal**: her değere bir kez dokunuluyor.

    Sıralı dönmesi bir konfor değil bir gereklilik: dönen liste hata mesajına
    giriyor ve bu araçtaki bütün üretilen çıktı gibi mesajın da tekrarlanabilir
    olması gerekiyor — sırasız bir küme, aynı girdide farklı metin üretirdi.
    """
    counts = Counter(values)
    return sorted(value for value, count in counts.items() if count > 1)
