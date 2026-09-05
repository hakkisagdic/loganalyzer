"""Hattın maliyeti kural sayısıyla nasıl büyüyor — **duvar saati olmadan**.

Neden saat yok
--------------
`CLAUDE.md` §6: *"Bir testin geçme sebebinin duvar saatiyle ilgisi olmamalı …
test neyi ölçmek istiyor? Duvar saati değilse süreyi denklemden çıkar."*

Buradaki soru "ne kadar sürüyor" değil **"kural başına iş korpus boyuyla
büyüyor mu"**. O soru karşılaştırma sayarak cevaplanıyor ve sayı makinenin o
anki yükünden bağımsız: yüklü makinede de sessiz makinede de aynı çıkıyor. Bu
turda makine gerçekten thrash'teydi (2583–3580 swap-in/sn) ve bu bekçi yine de
ölçülebildi — sürelerin ölçümü ise koordinatöre bırakıldı
(`python -m sigma_build.cost`).

`GrokPropertyTests`'in 2 saniyelik bütçesi ve `DiscoveryWorkerTests`'in 200 ms'si
bu deponun bu dersi iki kez ödediği yerler. Üçüncüsünü yazmıyoruz.

Ne sabitleniyor
---------------
Kural **başına** kimlik dokunuşu, korpus boyu 8 katına çıktığında sabit
kalmalı. Karesel bir uygulamada bu sayı korpusla birlikte büyüyor — 24 kuralda
24, 269 kuralda 269 — ve testi düşüren şey tam olarak o büyüme.

Ölçülen kırmızı: karesel hâl geri konduğunda üç testin ikisi düşüyor
(`test_manifest_kural_basina_isi_korpusla_buyutmuyor`,
`test_beyan_sekli_kontrolu_beyan_sayisiyla_karesel_degil`); üçüncüsü —
`test_duplicate_values_tekrari_buluyor` — davranışı sınadığı için yeşil kalıyor,
ve bu ayrım kasıtlı: davranış testi hızın bekçisi değildir, o yüzden ayrı.
"""

from __future__ import annotations

import pytest

from sigma_build.duplicates import duplicate_values
from sigma_build.golden_gate import (
    CLASS_CORPUS_GAP,
    EXPECT_AT_LEAST_ONE,
    EXPECT_NONE,
    Expectation,
    check_corpus_shape,
)
from sigma_build.manifest import STATUS_WRITTEN, RuleOutcome, RunHeader, build_manifest

#: Korpus boyu bu kat kadar büyüyor. Sekiz kat, doğrusal ile karesel arasındaki
#: farkı gürültü payının çok üstünde bırakıyor: karesel bir uygulamada kural
#: başına iş de sekiz katına çıkar.
BUYUME_KATI = 8
KUCUK_KORPUS = 25
BUYUK_KORPUS = KUCUK_KORPUS * BUYUME_KATI

#: Kural başına işin izinli büyümesi.
#:
#: **Ölçülmüş iki uç** — eşik bir tahmin değil, aralığın ortası:
#:
#: | Hâl | 25 kuralda | 200 kuralda | Oran |
#: | --- | --- | --- | --- |
#: | doğrusal (bugünkü) | 1,0 dokunuş | 1,0 dokunuş | **1,00×** |
#: | karesel (geri konup ölçüldü) | 25,0 | 200,0 | **8,00×** |
#:
#: Eşiği okuyan kişinin görmesi gereken şey sayı değil **aralık**: 1,00 ile 8,00
#: arasında dört kat pay var. Dar bir eşik (ör. 1,2×) `Counter`'ın iç ayrıntısı
#: dokunuşu 1'den 2'ye çıkardığı gün sebepsiz kırmızı yanardı; gevşek bir eşik
#: (ör. 6×) karesele yaklaşan bir gerilemeyi geçirirdi.
#:
#: ⚠️ `CLAUDE.md` §8'in uyarısı burada da geçerli: bir eşik zamanla kimsenin
#: bakmadığı bir rakama dönüşebilir. Sabit bir sayıya çevrilmedi çünkü
#: dokunuş sayısı uygulama ayrıntısına bağlı ve sabit bir değer **her** iç
#: değişiklikte kırmızı yanardı — o kapı da gevşetilirdi. Eşiğin alternatifi
#: daha iyi bir yargı değil, bekçinin yokluğu.
IZINLI_BUYUME = 2.0


class SayanKimlik(str):
    """`__eq__` ve `__hash__` çağrılarını sayan kural kimliği.

    Neden ikisi birden: karesel hâl `==` ile tarıyor, doğrusal hâl `Counter` ile
    **hash**liyor. Yalnızca birini saymak, ölçümü uygulamanın seçtiği yönteme
    bağlar — ve o zaman test "iş büyüdü mü" sorusunu değil "hangi yöntem
    kullanıldı" sorusunu sorar. İkisinin toplamı yönteme bakmadan **iş
    miktarını** ölçüyor.

    `__lt__` sayılmıyor: sıralama zaten `N log N` ve o bu testin konusu değil.
    """

    sayac = [0]

    def __eq__(self, other: object) -> bool:
        SayanKimlik.sayac[0] += 1
        return str.__eq__(self, other)

    def __hash__(self) -> int:
        SayanKimlik.sayac[0] += 1
        return str.__hash__(self)


def _kimlik(index: int) -> SayanKimlik:
    return SayanKimlik(f"{index:08d}-0000-4000-8000-000000000000")


def _dokunus_sayisi(is_yapan) -> int:
    SayanKimlik.sayac[0] = 0
    is_yapan()
    return SayanKimlik.sayac[0]


def _kural_basina_dokunus(is_yapan_uretici) -> tuple[float, float]:
    """(küçük korpusta kural başına, büyük korpusta kural başına)."""
    kucuk = _dokunus_sayisi(is_yapan_uretici(KUCUK_KORPUS)) / KUCUK_KORPUS
    buyuk = _dokunus_sayisi(is_yapan_uretici(BUYUK_KORPUS)) / BUYUK_KORPUS
    return kucuk, buyuk


def _header() -> RunHeader:
    return RunHeader(
        view="events_ocsf",
        ruleset_commit="olcum",
        pipeline_version="bizigo-events-ocsf/0000000000",
        pipeline_sha="sha256:0",
        pysigma_version="1.5.0",
        backend_version="1.1.1",
    )


# --------------------------------------------------------------------------- #
# Kural başına iş — korpusla büyümemeli
# --------------------------------------------------------------------------- #

def test_manifest_kural_basina_isi_korpusla_buyutmuyor() -> None:
    """`build_manifest` hem `--write` hem `--check` yolunda.

    Karesel olduğunda kural sayısı büyüdükçe **CI kapısı kendi kendini
    yavaşlatıyor** — kapının maliyeti koruduğu şeyle birlikte artıyor, ki bu
    kapının bir gün kaldırılmasının en olağan sebebi.
    """
    header = _header()

    def uretici(n: int):
        outcomes = [
            RuleOutcome(
                rule_id=_kimlik(i),
                title=f"kural {i}",
                source_path=f"catalog/sigma/rules/k{i}.yml",
                source_sha="sha256:0",
                status=STATUS_WRITTEN,
                sql="SELECT * FROM events_ocsf WHERE class_uid = 4001",
            )
            for i in range(n)
        ]
        return lambda: build_manifest(outcomes, header)

    kucuk, buyuk = _kural_basina_dokunus(uretici)

    assert buyuk <= kucuk * IZINLI_BUYUME, (
        f"Kural başına iş korpusla büyüyor: {KUCUK_KORPUS} kuralda {kucuk:.1f}, "
        f"{BUYUK_KORPUS} kuralda {buyuk:.1f} dokunuş. Korpus {BUYUME_KATI}× büyüdü, "
        f"kural başına maliyet {buyuk / max(kucuk, 1e-9):.1f}× arttı — yani toplam "
        "maliyet karesel. Aranacak yer: liste içinde listeyi tarayan bir "
        "`sum(1 for … )`; doğrusal karşılığı `sigma_build.duplicates`."
    )


def test_beyan_sekli_kontrolu_beyan_sayisiyla_karesel_degil() -> None:
    """`check_corpus_shape` CI'nın ağsız kapısı — beyan sayısı kuralla birlikte büyüyor."""

    def uretici(n: int):
        expectations = [
            Expectation(
                rule_id=_kimlik(i),
                file_name=f"{i:08d}.sql",
                expect=EXPECT_AT_LEAST_ONE if i % 2 else EXPECT_NONE,
                why="ölçüm için üretilmiş beyan",
                kind=None if i % 2 else CLASS_CORPUS_GAP,
            )
            for i in range(n)
        ]
        return lambda: check_corpus_shape(expectations, produced_rules=n)

    kucuk, buyuk = _kural_basina_dokunus(uretici)

    assert buyuk <= kucuk * IZINLI_BUYUME, (
        f"Beyan başına iş listeyle büyüyor: {kucuk:.1f} → {buyuk:.1f} dokunuş. "
        "Beyan sayısı üretilen kural sayısıyla birlikte büyüyor, yani bu da "
        "korpus eksenli bir maliyet."
    )


# --------------------------------------------------------------------------- #
# Ortak yardımcının davranışı — hızdan ayrı, bilerek
# --------------------------------------------------------------------------- #

def test_duplicate_values_tekrari_buluyor() -> None:
    assert duplicate_values(["a", "b", "a", "c", "b", "a"]) == ["a", "b"]
    assert duplicate_values(["a", "b", "c"]) == []
    assert duplicate_values([]) == []


def test_duplicate_values_sirali_donuyor() -> None:
    """Sıra bir konfor değil: dönen liste hata mesajına giriyor.

    Sırasız bir küme aynı girdide farklı metin üretirdi ve bu araçtaki her şey —
    üretilen SQL, manifest, çivi — tekrarlanabilir olmak zorunda.
    """
    assert duplicate_values(["z", "z", "a", "a", "m", "m"]) == ["a", "m", "z"]


def test_duplicate_values_tekrar_sayisini_degil_degeri_donduruyor() -> None:
    """Üç kez geçen bir değer listede **bir kez** duruyor."""
    assert duplicate_values(["a", "a", "a"]) == ["a"]


@pytest.mark.parametrize("bozuk", [None, 42])
def test_sayan_kimlik_str_gibi_davraniyor(bozuk: object) -> None:
    """Ölçüm aracının kendisi de sınanıyor — §6.

    `SayanKimlik` gerçekten bir `str` gibi davranmazsa üstteki iki test yanlış
    şeyi ölçer ve **yeşil** yanar. Sayaçlı bir kimlik, eşitliği bozarsa
    `build_manifest` her kuralı tekrar sanır ya da hiç sanmaz; ikisi de ölçümü
    sessizce anlamsız kılar.
    """
    kimlik = _kimlik(7)
    assert kimlik == str(kimlik)
    assert kimlik != bozuk
    assert hash(kimlik) == hash(str(kimlik))
    assert len({kimlik, SayanKimlik(str(kimlik))}) == 1
