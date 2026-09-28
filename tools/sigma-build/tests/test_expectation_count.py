"""Beyan kümesinin **sayısı** çivili mi (T32).

Bu bekçi ölçülmüş bir sürüklenmeden doğdu. Kapı 3'ün canlı yarısı bir kez koştu
ve gerçek bir eşleme kusuru buldu (`routeros_forward_new`); o koşumun kaydı
vault sayfasına *"canlıda **sekiz** beyanın sekizini geçirdi"* diye girdi. Bugün
beyan dosyası **on dokuz** kalem taşıyor.

Yani sayı **sekizden on dokuza çıktı ve hiçbir yerde kırmızı yanmadı** — çünkü
canlı yarı bir kapı değil, bir kayıt; ve kaydın dayandığı sayıyı sayan hiçbir
şey yoktu. Sürüklenmenin kendisi sessiz bir yanlış: bir sonraki okuyan vault
cümlesini bugünün gerçeği sanıyor.

**Bu bekçi canlı yarının yerine geçmiyor.** Canlı koşum ClickHouse istiyor ve
faz kapanışında elle koşuyor (karar T32 ticket'ında yazılı). Buradaki soru daha
küçük ve konteynersiz: *beyan kümesi büyüdüyse birisi bunu bilerek mi yaptı?*

Sayı **üç yerden** çiviliyor, çünkü tek bir toplam iki farklı değişikliği aynı
görürdü: bir `at_least_one` eklemek ile bir `none` eklemek ölçüm açısından ayrı
şeyler — birincisi *"artık veri var"*, ikincisi *"bu vendor'da bu olay yok"*.
"""

from __future__ import annotations

import collections
import json
from pathlib import Path

from sigma_build.golden_gate import EXPECTATIONS_PATH
from sigma_build.view_columns import repo_root

#: Beyan kalemi sayısı. **Artırmak iki bilinçli hareket:** beyanı yazmak ve bu
#: sayıyı güncellemek. Kalıp `ProducesContractTests.ExpectedExemptCount`'un
#: aynısı; bu depoda muafiyet ve beyan kümeleri hep böyle çivili.
EXPECTED_TOTAL = 19

#: `at_least_one` — "bu kural bu korpusta eşleşmeli". Kapının DİŞİ bu tarafta:
#: yalnızca `none` beyanlarından oluşan bir liste, her şeyi eşleştiremeyen bozuk
#: bir SQL tarafından da geçilirdi.
EXPECTED_AT_LEAST_ONE = 7

#: `none` — "eşleşmemeli", ve her biri gerekçeli.
EXPECTED_NONE = 12

#: Beyanı bilerek YAZILMAYAN kural sayısı (`undeclared`). Ayrı bir sayı, çünkü
#: §8'in ayrımı: "bir gün yazılacak" ile "hiç yazılmayacak" aynı listede
#: duramaz.
EXPECTED_UNDECLARED = 1


def _document() -> dict:
    return json.loads((repo_root() / EXPECTATIONS_PATH).read_text(encoding="utf-8"))


def test_beyan_sayisi_civili():
    """Toplam kalem sayısı sabitle aynı."""
    entries = _document()["expectations"]

    assert len(entries) == EXPECTED_TOTAL, (
        f"Beyan kümesi {len(entries)} kalem taşıyor, çivili sayı {EXPECTED_TOTAL}.\n\n"
        "Bu bir hata değil bir KARAR NOKTASI: korpus ya da örneklem değiştiyse beyan "
        "eklemek doğru olabilir. Ama sayının sessizce oynaması, Kapı 3'ün canlı "
        "koşumunun kaydını (vault: 'sekiz beyanın sekizi') bugünün gerçeği sanan bir "
        "okuyucu üretiyor — bu bekçi tam olarak o sürüklenmeden doğdu."
    )


def test_iki_sinifin_dagilimi_civili():
    """İki yön ayrı ayrı sayılıyor — toplam ikisini birden gizler."""
    dagilim = collections.Counter(e["expect"] for e in _document()["expectations"])

    assert dagilim["at_least_one"] == EXPECTED_AT_LEAST_ONE, (
        f"`at_least_one` beyanı {dagilim['at_least_one']}, çivili sayı "
        f"{EXPECTED_AT_LEAST_ONE}. Kapının dişi bu taraf: eşleşme bekleyen beyan "
        "azaldıysa kapı daha az şey kanıtlıyor."
    )
    assert dagilim["none"] == EXPECTED_NONE, (
        f"`none` beyanı {dagilim['none']}, çivili sayı {EXPECTED_NONE}."
    )

    # Tanımsız bir üçüncü sınıf sessizce girmesin: `check_corpus_shape` yalnızca
    # bildiği iki değeri doğruluyor ve bilinmeyen bir değer oraya hiç gelmiyor.
    assert set(dagilim) == {"at_least_one", "none"}, (
        f"Beklenmeyen beyan sınıfı: {sorted(set(dagilim) - {'at_least_one', 'none'})}"
    )


def test_beyansiz_kural_sayisi_civili():
    """`undeclared` ayrı sayılıyor: beyansızlık da bir karar ve gerekçeli."""
    undeclared = _document()["undeclared"]

    assert len(undeclared) == EXPECTED_UNDECLARED, (
        f"Beyansız kural {len(undeclared)}, çivili sayı {EXPECTED_UNDECLARED}."
    )

    # Gerekçesiz beyansızlık, "unutuldu" ile "bilerek" arasındaki ayrımı silerdi.
    for entry in undeclared:
        assert entry.get("why", "").strip(), f"`{entry['rule_id']}` gerekçesiz beyansız."


def test_beyan_dosyasi_uretilen_sqlle_ortusuyor():
    """Her beyan gerçek bir SQL dosyasına bakıyor.

    Bu, sayının **anlamını** koruyan yarısı: sayı doğru kalıp beyanların
    silinmiş dosyalara bakması, kapının hiçbir şey ölçmediği hâl olurdu — ve
    `--shape-only` yolu bunu görmüyor, çünkü o yalnızca listenin ŞEKLİNE bakıyor.
    """
    root = repo_root()
    entries = _document()["expectations"]

    missing = [
        e["file_name"]
        for e in entries
        if not (root / "detections" / "sigma" / e["file_name"]).is_file()
    ]

    assert not missing, (
        "Beyan var ama üretilen SQL yok:\n  " + "\n  ".join(missing)
        + "\n\nBeyan silinmiş bir dosyaya bakıyorsa kapı o kural için hiçbir şey "
        "ölçmüyor ve sayı yine de doğru görünüyor."
    )
