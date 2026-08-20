"""Ölçüm aracının kendi bekçileri (T30).

Neden bir ölçüm aracının testi var
----------------------------------
Bu koşumun ürettiği sayı bir **kapsam kararı** verecek. Aracın kendisi sessizce
yanlış sayarsa, protokolün engellemek için var olduğu hatayı ölçüm aracı
üretmiş olur — ve o hata, ölçülen şeyin kusuru gibi görünür.

En pahalısı şu: `events_ocsf` boşken koşarsan her kural `matches=false` verir.
O tablo "kapsam düşük" diye okunur ve F3'ün kapsamı yüklenmemiş bir fixture'a
göre daraltılır.

Koşum (pySigma GEREKMİYOR — bu dosya yalnızca saf mantığı sınıyor):

    python3.13 test_measure.py
"""

from __future__ import annotations

import sys

import measure


def test_gorunum_yoksa_olcum_reddediliyor() -> None:
    """Sorgu hatası ile boş tablo AYRI: birincisi kurulum, ikincisi veri sorunu."""
    measure.run_on_clickhouse = lambda *a, **k: (0, "Code: 60. Table events_ocsf does not exist")

    result = measure.preflight("http://x", "u", "p", 1.0)

    assert not result.ok
    assert "sorgulanamadı" in result.reason
    assert "BOŞ" not in result.reason


def test_bos_gorunumde_olcum_reddediliyor() -> None:
    """Ve reddederken statik kipi öneriyor — kullanıcı çıkmaz sokakta kalmasın."""
    measure.run_on_clickhouse = lambda *a, **k: (0, "")

    result = measure.preflight("http://x", "u", "p", 1.0)

    assert not result.ok
    assert "BOŞ" in result.reason
    assert "statik kip" in result.reason


def test_veri_varken_vendor_dagilimi_raporlaniyor() -> None:
    """Yalnızca bir vendor yüklüyse diğerlerinin sıfırı eşlemenin kusuru değil."""

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        if "device_vendor_name = 'Cisco'" in sql:
            return 0, ""
        if "metadata_product_name = 'nginx'" in sql:
            return 7, ""
        if "WHERE" in sql:
            return 12, ""
        return 31, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert result.ok
    assert result.rows == 31
    assert result.vendors["Cisco"] == 0
    assert result.vendors["nginx"] == 7


def test_verisiz_kural_orandan_dusuluyor() -> None:
    """**Bu testin sayıları iki farklı kapsam kararına denk geliyor.**

    24 kuralın 6'sının vendor'ı yüklü değilse: paydayı 24 almak %38, doğru
    payda olan 18'i almak %50 veriyor. Kapsam iskeletinde birincisi "yalnızca
    firewall + network_connection", ikincisi "dört vendor da girsin" dalı.
    Yani bu bir yuvarlama farkı değil, farklı bir F3 kapsamı.
    """
    report = measure.Report(rules=24, no_data=6, matches=9)

    assert report.measurable == 18
    assert abs(report.match_ratio - 0.50) < 1e-9

    naive = report.matches / report.rules
    assert abs(naive - 0.375) < 1e-9
    assert report.match_ratio > naive


def test_olculebilir_kural_yoksa_oran_sifir() -> None:
    """Sıfıra bölme yerine sıfır — ve sıfır oran zaten ölçüm yapılmadı demek."""
    assert measure.Report(rules=0).match_ratio == 0.0


def test_tablo_adi_ikamesi_raporlaniyor() -> None:
    """Sessiz düzeltme, ölçümün 'SQL koşuyor' sonucunu kendi eliyle üretmek olurdu."""
    sql, rewritten = measure.rewrite_table("SELECT * FROM logs WHERE x=1", "events_ocsf")

    assert rewritten
    assert "FROM events_ocsf" in sql
    assert "FROM logs" not in sql

    untouched, flag = measure.rewrite_table("SELECT * FROM events_ocsf", "events_ocsf")
    assert not flag
    assert untouched == "SELECT * FROM events_ocsf"


def main() -> int:
    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    failed = 0

    for test in tests:
        try:
            test()
            print(f"✓ {test.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"✗ {test.__name__}: {exc or 'assertion failed'}")

    print(f"\n{len(tests) - failed}/{len(tests)} geçti")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
