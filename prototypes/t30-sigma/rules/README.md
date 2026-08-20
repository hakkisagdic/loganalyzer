# Bu korpus taşındı — buradaki kopya **yoktur**

Sigma kural korpusunun tek yeri:

    catalog/sigma/rules/

Buradaki 24 `.yml` **silindi**, dizin yalnızca bu notu taşımak için duruyor.

## Neden bir not, neden sessizce silinmedi

İki kopya bir gün ayrıştı ve bir günümüzü aldı. T32 korpusu `catalog/`'a terfi
ettirdi; aynı sırada başka bir ajan `routeros_forward_new.yml`'i **burada**
düzeltti (`action` → `fw_chain`). İkisi de doğru davrandı — kimse diğerinin
dizinini bilmiyordu. Sonuç: derleme hattı düzeltilmemiş kopyayı derledi, Kapı 3
iki koşum boyunca **eski SQL'i** sınadı, ve hiçbir şey bunu söylemedi.

`CLAUDE.md` §9: *"İkinci kopya yazma. Ortak yüzey varsa genişlet, kopyalama."*

## Buraya bakan bir araç varsa

Kırılması **doğru davranış**. Sessizce boş liste okuyup "0 kural ölçüldü" demek,
tam da bu ayrışmanın ilk seferde görünmemesini sağlayan şeydi. Kırıldıysa
`catalog/sigma/rules/` yolunu kullanın.

Bir bekçi de var: `tools/sigma-build/tests/test_corpus_single_source.py` depoda
`catalog/sigma/rules/` dışında Sigma kuralı bulursa kırmızı yanıyor.
