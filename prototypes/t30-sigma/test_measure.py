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


def test_veri_var_ama_altin_ornek_yoksa_reddediliyor() -> None:
    """**Bu testin karşılığı gerçek bir koşumda yaşandı.**

    Tabloda önceki bir turdan kalma 1.000.000 satırlık tek-vendor'lu sentetik
    benchmark verisi vardı. Ön kontrol "boş mu" diye sordu, cevap hayırdı,
    geçirdi — ve ölçüm `%0` eşleşme üretti. O sıfır eşlemenin değil verinin
    sonucuydu.

    "Boş değil" ile "doğru veri" aynı şey değil; kontrol artık bir YOKLUK
    kanıtı değil VARLIK kanıtı arıyor.
    """

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        # Bol satır var, ama hiçbir altın örnek sondası tutmuyor.
        if "position(raw_data" in sql:
            return 0, ""
        return 1_000_001, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert not result.ok
    assert result.rows == 1_000_001
    assert "altın örnek değil" in result.reason
    assert not any(result.golden.values())


def test_altin_ornek_bulununca_geciyor() -> None:
    """Tek bir vendor'ın örneği bile bulunursa ölçüm anlamlı olabilir."""
    probes = measure.golden_probes()
    assert probes, "altın örnek sondaları türetilemedi — dosyalar taşınmış olabilir"

    fortinet = probes["Fortinet"]

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        if "position(raw_data" in sql:
            return (1 if fortinet[:20] in sql else 0), ""
        return 87, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert result.ok
    assert result.golden["Fortinet"]
    assert not result.golden["Cisco"]


def test_sondalar_ayirt_edici_uzunlukta() -> None:
    """Kısa ya da jenerik sonda işe yaramaz: sentetik veri de aynı söz dizimini
    taşıyor ve `level="notice"` gibi bir parça onda da bulunur."""
    probes = measure.golden_probes()

    assert set(probes) == {"Fortinet", "Cisco", "MikroTik", "nginx"}

    for vendor, probe in probes.items():
        assert len(probe) == 60, f"{vendor} sondası {len(probe)} karakter"


def test_reddedilen_kolonlar_uc_hata_bicimini_de_okuyor() -> None:
    """ClickHouse bilinmeyen kolonu üç ayrı cümleyle anlatıyor."""
    assert measure.rejected_columns(
        "Code: 47. DB::Exception: Unknown expression identifier 'type_uid' in scope SELECT"
    ) == ["type_uid"]

    assert measure.rejected_columns(
        "Code: 47. DB::Exception: Missing columns: 'dns_query_name' 'answer' while processing"
    ) == ["dns_query_name", "answer"]

    assert measure.rejected_columns(
        "Code: 47. DB::Exception: Unknown identifier: process_name; there are columns: time"
    ) == ["process_name"]

    assert measure.rejected_columns("") == []


def test_eslemesiz_alanlar_statik_olarak_sayiliyor() -> None:
    """**Bu boşluk prototipin kendi kusuruydu ve ölçümü çarpıtıyordu.**

    `UNMAPPED_FIELDS` dokuz alan tanımlıyor ama hiçbir dönüşüme bağlı değil;
    `unmapped_expression()` de tanımlı ve hiçbir yerden çağrılmıyor. Sonuç:
    o alanlara giden kurallar ham Sigma adıyla SQL'e iniyor ve ClickHouse
    reddediyor.

    Yani `runs < compiled` farkının bir kısmı ŞEMANIN değil PROTOTİPİN
    eksikliği — ve ayrı sayılmazsa kapsam kararı yanlış sebebe dayanırdı.
    Sayı statik: ClickHouse koşmadan da biliniyor.
    """
    field_map = {"srcip": "src_endpoint_ip", "action": "activity_name"}

    # Görünümde olan alan: boşluk yok.
    assert measure.unhandled_fields(
        "detection:\n  selection:\n    srcip: 10.0.0.1\n  condition: selection", field_map
    ) == []

    # Görünümde olmayan alan: boşluk var ve adıyla raporlanıyor.
    assert measure.unhandled_fields(
        "detection:\n  selection:\n    url|contains: '/admin'\n  condition: selection", field_map
    ) == ["url"]

    # Operatör eki alan adının parçası değil.
    assert measure.unhandled_fields(
        "detection:\n  selection:\n    action|startswith: 'blo'\n  condition: selection", field_map
    ) == []


def test_ornekleme_gercek_bosluk_sayisi() -> None:
    """Örneklemin bugünkü hâli: 24 kuralın 8'i eşleme dalı olmayan alana gidiyor.

    Sayı değişirse ya kurallar ya pipeline değişmiş demektir; ikisi de ölçümü
    etkiliyor ve fark edilmeden geçmemeli.
    """
    import ast
    from pathlib import Path

    source = Path(__file__).parent / "bizigo_pipeline.py"
    tree = ast.parse(source.read_text(encoding="utf-8"))
    field_map: dict[str, str] = {}

    for node in ast.walk(tree):
        if isinstance(node, ast.AnnAssign) and getattr(node.target, "id", "") == "FIELD_MAP":
            field_map = {k.value: v.value for k, v in zip(node.value.keys, node.value.values)}

    assert field_map, "FIELD_MAP okunamadı"

    rules = sorted((Path(__file__).parent / "rules").glob("*.yml"))
    affected = [p.name for p in rules if measure.unhandled_fields(p.read_text(encoding="utf-8"), field_map)]

    assert len(rules) == 24
    assert len(affected) == 8, f"beklenen 8, ölçülen {len(affected)}: {affected}"


def test_esleyen_kural_yoksa_inf_yerine_sifir() -> None:
    """`inf` bir ölçüm gibi görünüyor ama ölçüm yapılamadığını anlatıyor."""
    report = measure.Report(rules=24, matches=0, pipeline_lines=111)

    assert report.mapping_lines_per_rule == 0.0


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
