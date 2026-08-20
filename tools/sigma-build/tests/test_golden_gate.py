"""Kapı 3'ün **ClickHouse gerektirmeyen** yarısı (T32).

Kapının kendisi canlı ClickHouse ve yüklü altın örnek istiyor; koordinatörde
koşuyor (§2). Burada sınanan şey kapının **karar mantığı**: ön kontrol neyi
reddediyor, beyan listesi kapının ayırt edebildiğini gösteriyor mu, ve bir
beklenti tutmadığında kapı gerçekten kırmızı yanıyor mu.

ClickHouse yerine sahte bir `post_sql` konuyor. Sahteyle ölçülen şey harness;
verinin gerçekten ne döndürdüğü koordinatörün koşumunda ölçülecek.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from sigma_build.golden_gate import (
    EXPECT_AT_LEAST_ONE,
    EXPECT_NONE,
    Expectation,
    GoldenResult,
    check_corpus_shape,
    count_rows,
    evaluate,
    expectations_text,
    load_expectations,
    precheck,
)

CONN = {"url": "http://yok", "user": "u", "password": "p", "database": "d"}


def beklenti(rule_id="a", file_name="a.sql", expect=EXPECT_AT_LEAST_ONE, why="altın örnekte bu olay var") -> Expectation:
    return Expectation(rule_id=rule_id, file_name=file_name, expect=expect, why=why)


def sahte_post(cevaplar: dict[str, str], *, reddedilenler: frozenset[str] = frozenset()):
    """Sorgunun içindeki bir parçaya göre cevap veren sahte ClickHouse."""
    sorulanlar: list[str] = []

    def sahte(sql, **kwargs):
        sorulanlar.append(sql)
        for parca in reddedilenler:
            if parca in sql:
                return False, f"Code: 47. DB::Exception: {parca} reddedildi"
        for parca, cevap in cevaplar.items():
            if parca in sql:
                return True, cevap
        return True, "0\n"

    return sahte, sorulanlar


# --------------------------------------------------------------------------- #
# Beklenti tipi
# --------------------------------------------------------------------------- #

def test_bilinmeyen_beklenti_reddediliyor():
    with pytest.raises(ValueError, match="bilinmeyen beklenti"):
        beklenti(expect="belki")


def test_gerekcesiz_beklenti_reddediliyor():
    """Gerekçesiz bir beklenti, kırıldığı gün "herhâlde veri değişmiştir" diye gevşetilir."""
    with pytest.raises(ValueError, match="gerekçesi yok"):
        beklenti(why="   ")


@pytest.mark.parametrize(
    ("expect", "rows", "gecti"),
    [
        (EXPECT_AT_LEAST_ONE, 1, True),
        (EXPECT_AT_LEAST_ONE, 0, False),
        (EXPECT_NONE, 0, True),
        (EXPECT_NONE, 1, False),
    ],
)
def test_sonuc_degerlendirmesi(expect, rows, gecti):
    assert GoldenResult(file_name="a.sql", expect=expect, rows=rows).passed is gecti


# --------------------------------------------------------------------------- #
# Beyan listesi kapının ayırt edebildiğini gösteriyor mu
# --------------------------------------------------------------------------- #

def test_bos_beyan_listesi_reddediliyor():
    """Boş liste kapıyı sıfır sorgu sorup geçen bir şeye çevirirdi.

    Kapı 2'de `EXPLAIN SYNTAX` kusurunun kural seti üretime çıkana kadar
    görünmemesinin sebebi tam olarak buydu.
    """
    (problem,) = check_corpus_shape([])
    assert "beyan listesi boş" in problem


def test_yalnizca_eslesme_beklentisi_yetmiyor():
    """Her şeyi eşleştiren bozuk bir kapı da yalnızca `at_least_one`'ları geçerdi."""
    (problem,) = check_corpus_shape([beklenti()])
    assert EXPECT_NONE in problem


def test_yalnizca_eslesmeme_beklentisi_yetmiyor():
    (problem,) = check_corpus_shape([beklenti(expect=EXPECT_NONE, why="bu vendor'da bu olay yok")])
    assert EXPECT_AT_LEAST_ONE in problem


def test_iki_yon_de_varsa_sessiz():
    assert check_corpus_shape(
        [beklenti(), beklenti(rule_id="b", file_name="b.sql", expect=EXPECT_NONE, why="yanlış pozitif bekçisi")]
    ) == []


def test_ayni_kural_icin_iki_beklenti_reddediliyor():
    problems = check_corpus_shape([beklenti(), beklenti()])
    assert any("birden fazla beklenti" in problem for problem in problems)


def test_beyan_dosyasi_tarihsiz_ve_sirali(tmp_path):
    metin = expectations_text(
        [beklenti(rule_id="z", file_name="z.sql"), beklenti(rule_id="a", file_name="a.sql")]
    )
    document = json.loads(metin)
    assert [e["rule_id"] for e in document["expectations"]] == ["a", "z"]
    assert set(document) == {"_comment", "expectations"}

    path = tmp_path / "expectations.json"
    path.write_text(metin, encoding="utf-8")
    assert [e.rule_id for e in load_expectations(path)] == ["a", "z"]


def test_olmayan_beyan_dosyasi_bos_liste(tmp_path):
    """Dosya yoksa liste boş — ve boş liste `check_corpus_shape` tarafından reddediliyor.

    "Dosya yok" ile "beyan yok" aynı sonuca çıkmalı: ikisi de kapıyı sessizce
    geçirmemeli.
    """
    assert load_expectations(tmp_path / "yok.json") == []


# --------------------------------------------------------------------------- #
# Ön kontrol — T30'un protokolü
# --------------------------------------------------------------------------- #

def test_sorgu_hata_verirse_kurulum_sorunu(monkeypatch):
    sahte, _ = sahte_post({}, reddedilenler=frozenset({"count()"}))
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuc = precheck(**CONN)
    assert sonuc.ok is False
    assert "sorgulanamadı" in sonuc.problems[0]


def test_bos_tablo_olcum_yaptirmiyor(monkeypatch):
    """Boş görünüme karşı koşulan kapı her kural için "eşleşme yok" üretir ve o
    tablo "kurallar bozuk" diye okunur. Oysa bozuk olan veri."""
    sahte, _ = sahte_post({"count() FROM events_ocsf": "0\n"})
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuc = precheck(**CONN)
    assert sonuc.ok is False
    assert "boş" in sonuc.problems[0]


def test_eksik_vendor_ayri_raporlaniyor(monkeypatch):
    """Bir vendor yüklenmemişse onun kurallarının sonucu "kural bozuk" diye okunmamalı.

    T30'un ölçtüğü hâl: tabloda önceki turdan kalma tek vendor'lı veri vardı,
    "boş mu" sorusunun cevabı hayırdı, ölçüm geçti ve %0 eşleşme üretti.
    """
    sahte, _ = sahte_post(
        {
            "'Cisco'": "10\n",
            "'Fortinet'": "20\n",
            "'MikroTik'": "0\n",
            "'nginx'": "5\n",
            "count() FROM events_ocsf": "35\n",
        }
    )
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuc = precheck(**CONN)
    assert sonuc.ok is False
    assert any("MikroTik" in problem for problem in sonuc.problems)
    assert sonuc.vendor_rows["Fortinet"] == 20


def test_dort_vendor_da_varsa_gecerli(monkeypatch):
    sahte, _ = sahte_post(
        {
            "'Cisco'": "7426\n",
            "'Fortinet'": "1078801\n",
            "'MikroTik'": "22343\n",
            "'nginx'": "11430\n",
            "count() FROM events_ocsf": "1120001\n",
        }
    )
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuc = precheck(**CONN)
    assert sonuc.ok is True
    assert sonuc.total_rows == 1120001
    assert sonuc.problems == ()


# --------------------------------------------------------------------------- #
# Sayım ve değerlendirme
# --------------------------------------------------------------------------- #

def test_sorgu_sarmalanip_sinirlaniyor(monkeypatch):
    """`LIMIT 1` bilerek: soru "hiç mi, en az bir mi", "kaç tane" değil.

    Tam sayım 1,1 milyon satırlık bir görünümde ölçtüğümüz şeyi değil makinenin
    o anki yükünü ölçerdi (§6).
    """
    sahte, sorulanlar = sahte_post({"count()": "1\n"})
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    count_rows("SELECT * FROM events_ocsf WHERE a=1;", **CONN)
    (sorgu,) = sorulanlar
    assert sorgu.startswith("SELECT count() FROM (")
    assert "LIMIT 1)" in sorgu
    assert ";" not in sorgu  # sondaki noktalı virgül sarmalamayı bozardı


def test_reddedilen_sorgu_kapi_ikiye_isaret_ediyor(monkeypatch):
    """Kapı 3'e gelen bir SQL zaten Kapı 2'den geçmiş olmalı.

    Burada red almak, bir önceki kapının kaçırdığı anlamına gelir ve hata mesajı
    bunu söylüyor — yoksa arayan kişi kuralı suçlar.
    """
    sahte, _ = sahte_post({}, reddedilenler=frozenset({"count()"}))
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    with pytest.raises(RuntimeError, match="Kapı 2"):
        count_rows("SELECT * FROM events_ocsf", **CONN)


def test_beyani_olup_dosyasi_olmayan_kural_hata(tmp_path, monkeypatch):
    """Kural kapıya takıldıysa beyanı da kalkmalı.

    Kalırsa beyan listesi **var olmayan bir kapsamı** iddia eder — ve bu tam
    olarak `gated` kuralların üründe görünmemesi sorununun beyan tarafındaki
    hâli.
    """
    sahte, _ = sahte_post({"count()": "1\n"})
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    with pytest.raises(FileNotFoundError, match="beyan var ama üretilmiş SQL yok"):
        evaluate(tmp_path, [beklenti()], **CONN)


def test_beklenti_tutmayinca_kirmizi(tmp_path, monkeypatch):
    (tmp_path / "a.sql").write_text("SELECT * FROM events_ocsf WHERE a=1", encoding="utf-8")
    (tmp_path / "b.sql").write_text("SELECT * FROM events_ocsf WHERE b=2", encoding="utf-8")

    # İki sorgu da satır döndürüyor; `none` bekleyen kural bunu geçmemeli.
    sahte, _ = sahte_post({"count()": "1\n"})
    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuclar = evaluate(
        tmp_path,
        [beklenti(), beklenti(rule_id="b", file_name="b.sql", expect=EXPECT_NONE, why="yanlış pozitif bekçisi")],
        **CONN,
    )
    assert [r.passed for r in sonuclar] == [True, False]


def test_beklentiler_tutunca_yesil(tmp_path, monkeypatch):
    (tmp_path / "a.sql").write_text("SELECT * FROM events_ocsf WHERE a=1", encoding="utf-8")
    (tmp_path / "b.sql").write_text("SELECT * FROM events_ocsf WHERE b=2", encoding="utf-8")

    def sahte(sql, **kwargs):
        return True, ("1\n" if "WHERE a=1" in sql else "0\n")

    monkeypatch.setattr("sigma_build.golden_gate.post_sql", sahte)

    sonuclar = evaluate(
        tmp_path,
        [beklenti(), beklenti(rule_id="b", file_name="b.sql", expect=EXPECT_NONE, why="yanlış pozitif bekçisi")],
        **CONN,
    )
    assert all(r.passed for r in sonuclar)


# --------------------------------------------------------------------------- #
# Kapı bugünden koşuyor ama ilk gün kırmızı yanmıyor
# --------------------------------------------------------------------------- #

def test_sifir_kural_uretiliyorsa_bos_beyan_sorun_degil():
    """Koşul "üretilen her kural beyanlı olmalı" — sıfır kural, sıfır beyan.

    Bu bir gevşetme değil koşulun kendisi, ve iki tuzağın arasından geçiyor:
    kapıyı "kural seti gelince bağlarız" diye ertelemek **hazırlanmış ama
    bağlanmamış** desenini kurardı; bugün koşulsuz zorlamak ise ilgisiz bir işi
    bekleyen ve **ilk günden kırmızı** yanan bir kapı yaratırdı — o kapı da
    gevşetilir ya da devre dışı bırakılır (`ci.yml`, yamllint notu).
    """
    assert check_corpus_shape([], produced_rules=0) == []


def test_kural_uretiliyorsa_bos_beyan_kirmizi():
    """İlk kural üretildiği anda kapı kendiliğinden diş kazanıyor."""
    (problem,) = check_corpus_shape([], produced_rules=24)
    assert "24 kural üretiliyor ama beyan listesi boş" in problem
