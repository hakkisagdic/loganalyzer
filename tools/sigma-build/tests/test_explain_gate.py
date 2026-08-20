"""Kapı 2'nin **ClickHouse gerektirmeyen** yarısı: hata sınıflandırması.

Kapının kendisi canlı ClickHouse istiyor ve koordinatörde koşuyor (§2). Burada
sınanan şey, o koşum bir hata döndürdüğünde onun eyleme çevrilebilir bir engele
dönüşüp dönüşmediği — ve en önemlisi, **tanınmayan bir hatanın yutulmadığı**.

⚠️ Buradaki hata metinleri ClickHouse'un bilinen biçimlerinden yazıldı,
canlı koşumdan alınmadı. Yani bu testler sınıflandırıcının **davranışını**
çiviliyor, desenlerin ClickHouse'un gerçek metinleriyle örtüştüğünü değil.
Örtüşme Kapı 2 ilk koşturulduğunda ölçülecek; örtüşmezse bedeli kaba bir `kind`,
kaçırılmış bir kural değil.
"""

from __future__ import annotations

import pytest

from sigma_build.explain_gate import classify_error, explain_sql
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
