"""Kapı 2'nin **ClickHouse gerektirmeyen** yarısı: hata sınıflandırması.

Kapının kendisi canlı ClickHouse istiyor ve koordinatörde koşuyor (§2). Burada
sınanan şey, o koşum bir hata döndürdüğünde onun eyleme çevrilebilir bir engele
dönüşüp dönüşmediği — ve en önemlisi, **tanınmayan bir hatanın yutulmadığı**.

Metinlerin bir kısmı **ölçüldü**: `MEASURED_*` ile başlayanlar canlı ClickHouse
26.7.3'ten, Kapı 2'nin ilk koşumundan geldi. Kalanlar hâlâ bilinen biçimlerden
yazıldı ve doğrulanmış sayılmazlar.

O ilk koşum iki şey buldu. Birincisi kapının kendi kusuru: `EXPLAIN SYNTAX` tip
denetimi yapmıyor, yani kapı bütün tip uyuşmazlıklarını geçiriyordu. İkincisi
sınıflandırmada bir boşluk: `NO_COMMON_TYPE`'ın metni hiçbir desene uymuyordu ve
tip uyuşmazlığı `remedy: unknown` diye etiketleniyordu — kapanabilir bir iş
kalemi "kapanır mı bilmiyoruz" kutusunda duruyordu.
"""

from __future__ import annotations

import pytest

from sigma_build.explain_gate import (
    CANDIDATE_FORMS,
    DEFAULT_EXPLAIN_FORM,
    classify_error,
    explain_sql,
    run_self_test,
)
from sigma_build.gate import (
    KIND_TYPE_MISMATCH,
    KIND_UNKNOWN_COLUMN,
    KIND_UNSUPPORTED_CONSTRUCT,
)


@pytest.mark.parametrize(
    "message",
    [
        "Code: 47. DB::Exception: Missing columns: 'url' while processing query",
        "Code: 47. DB::Exception: Unknown expression identifier 'url' in scope",
        "DB::Exception: There is no column with name 'url' in table",
    ],
)
def test_olmayan_kolon_taniniyor(message):
    blocker = classify_error(message)
    assert blocker.kind == KIND_UNKNOWN_COLUMN
    assert blocker.column == "url"
    assert blocker.detail  # ham metin kayboluyor mu — kaybolmamalı


@pytest.mark.parametrize(
    "message",
    [
        "Code: 43. DB::Exception: Illegal type IPv6 of argument of function ilike",
        "Code: 43. DB::Exception: Illegal types LowCardinality(String) and UInt8 of arguments of function equals",
        "DB::Exception: Cannot convert String to UInt16",
    ],
)
def test_tip_uyusmazligi_taniniyor(message):
    blocker = classify_error(message)
    assert blocker.kind == KIND_TYPE_MISMATCH
    assert blocker.remedy == "pipeline"
    assert blocker.detail


def test_taninmayan_hata_yutulmuyor():
    """Sınıflandıramamak, kuralı kapıdan geçirmemeli.

    Bu testin çivilediği şey sınıflandırma değil **sessiz kayıp olmadığı**:
    desenler ClickHouse sürümüyle birlikte bayatlayabilir, ve bayatladıkları gün
    kapının davranışı "çözünürlük kaybı" olmalı, "kural kabul edildi" değil.
    """
    blocker = classify_error("Code: 999. DB::Exception: Tamamen yeni bir hata biçimi")
    assert blocker.kind == KIND_UNSUPPORTED_CONSTRUCT
    assert "Tamamen yeni bir hata biçimi" in (blocker.detail or "")


def test_hata_metni_tek_satira_indiriliyor():
    blocker = classify_error("Code: 43.\n  DB::Exception: Illegal type IPv6 of argument\n\n  (version 26.2)")
    assert "\n" not in (blocker.detail or "")


def test_http_disi_adres_reddediliyor():
    """Yanlışlıkla bir dosya yolu ya da `clickhouse://` verilirse sessizce denenmesin."""
    with pytest.raises(ValueError, match="http"):
        explain_sql("SELECT 1", url="clickhouse://localhost:9000")


def test_ulasilamayan_clickhouse_kural_kusuru_sayilmiyor():
    """Ortam bozukken "bütün kurallar kırık" yazdırmak ölçüm aracının kendi sessiz yanlışı olurdu.

    Bağlantı hatası bir `Blocker` değil, bir istisna: kurulum sorunu ile kural
    sorunu farklı cevaplar hak ediyor (T30'un ön kontrol protokolüyle aynı ayrım).
    """
    with pytest.raises(ConnectionError, match="ulaşılamadı"):
        explain_sql("SELECT 1", url="http://127.0.0.1:1/", timeout=1.0)


# --------------------------------------------------------------------------- #
# Canlı koşumdan gelen GERÇEK metinler (ClickHouse 26.7.3)
# --------------------------------------------------------------------------- #

MEASURED_ILIKE_IPV6 = (
    "Code: 43. DB::Exception: Illegal type IPv6 of argument of function ilike: "
    "In scope SELECT * FROM events_ocsf WHERE src_endpoint_ip ILIKE '203.0.113.%'. "
    "(ILLEGAL_TYPE_OF_ARGUMENT) (version 26.7.3)"
)

MEASURED_NO_COMMON_TYPE = (
    "Code: 386. DB::Exception: There is no supertype for types String, UInt8 because some of "
    "them are String/FixedString/Enum and some of them are not: "
    "__table1.connection_info_protocol_name LowCardinality(String) : 1, 6_UInt8 UInt8 : 3. "
    "(NO_COMMON_TYPE) (version 26.7.3)"
)


def test_olculen_ilike_ipv6_tip_uyusmazligi_olarak_taniniyor():
    blocker = classify_error(MEASURED_ILIKE_IPV6)
    assert blocker.kind == KIND_TYPE_MISMATCH
    assert blocker.remedy == "pipeline"
    assert "IPv6" in blocker.message


def test_olculen_no_common_type_tip_uyusmazligi_olarak_taniniyor():
    """Bu metin ilk yazılan desenlerin HİÇBİRİNE uymuyordu.

    Güvenli tarafa bozulma çalışıyordu — kural yine engelleniyordu — ama `remedy`
    `unknown` çıkıyordu, yani **kapanabilir bir iş kalemi "kapanır mı bilmiyoruz"
    diye görünüyordu.** Sessiz kayıp değil, ama yol haritasında yanlış kutu.
    """
    blocker = classify_error(MEASURED_NO_COMMON_TYPE)
    assert blocker.kind == KIND_TYPE_MISMATCH
    assert blocker.remedy == "pipeline"
    assert "NO_COMMON_TYPE" in (blocker.detail or "")


# --------------------------------------------------------------------------- #
# Sınavın kendisi kırmızı yanabiliyor mu — ClickHouse'suz
# --------------------------------------------------------------------------- #

def test_sinav_yanlis_sonucta_kirmizi_yaniyor(monkeypatch):
    """`--self-test`'in kendi kırmızı yanabilirliği.

    Canlı koşumda ölçmek ClickHouse istiyor; burada `explain_sql` yerine bilerek
    yanlış cevap veren bir sahte konuyor. Sınav "her şey kabul" diyen bir kapıyı
    yakalayabiliyor mu — `EXPLAIN SYNTAX` kusurunun yakalandığı yol tam olarak bu.
    """
    from sigma_build.gate import GATE_EXPLAIN, GateVerdict

    monkeypatch.setattr(
        "sigma_build.explain_gate.explain_sql",
        lambda sql, **kwargs: GateVerdict(gate=GATE_EXPLAIN, blockers=()),
    )
    problems = run_self_test(url="http://yok")
    assert len(problems) == 2
    assert all("beklenen red, alınan kabul" in problem for problem in problems)


def test_sinav_dogru_kapida_sessiz(monkeypatch):
    from sigma_build.gate import GATE_EXPLAIN, Blocker, GateVerdict

    def sahte(sql, **kwargs):
        bozuk = "connection_info_protocol_name=6" in sql or "ILIKE" in sql
        return GateVerdict(
            gate=GATE_EXPLAIN,
            blockers=(Blocker(kind="type_mismatch", message="tip", remedy="pipeline"),) if bozuk else (),
        )

    monkeypatch.setattr("sigma_build.explain_gate.explain_sql", sahte)
    assert run_self_test(url="http://yok") == []


def test_explain_disi_bicim_reddediliyor():
    """`--explain-form SELECT` gibi bir yanlışlık sorguyu ÇALIŞTIRIRDI."""
    with pytest.raises(ValueError, match="EXPLAIN ile başlamalı"):
        explain_sql("SELECT 1", url="http://localhost:8123", form="SELECT")


def test_aday_bicimler_arasinda_explain_syntax_duruyor():
    """"Denedik, olmadı" bilgisi listeden silinmemeli.

    Silinirse bir sonraki kişi aynı seçimi aynı gerekçeyle yeniden yapar — ve o
    seçim kapıyı sessizce yeşil bırakıyordu.
    """
    assert "EXPLAIN SYNTAX" in CANDIDATE_FORMS
    assert DEFAULT_EXPLAIN_FORM == "EXPLAIN"
