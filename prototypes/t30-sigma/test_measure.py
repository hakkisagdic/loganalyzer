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
    """Tek bir sonda tutarsa o vendor doğrulanmış sayılıyor."""
    probes = measure.golden_probes()
    assert probes, "altın örnek sondaları türetilemedi — dosyalar taşınmış olabilir"

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        if "position(raw_data" in sql:
            return (1 if any(x in sql for x in probes["Fortinet"]) else 0), ""
        if "device_vendor_name" in sql or "metadata_product_name" in sql:
            # Yalnızca Fortinet yüklü; diğerlerinin sıfırı yabancı veri değil.
            return (87 if "Fortinet" in sql else 0), ""
        return 87, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert result.ok
    assert result.golden["Fortinet"]
    assert not result.golden["Cisco"]


def test_sonda_turetilemezse_olcum_reddediliyor() -> None:
    """**Bu, ön kontrolün kendi eliyle kapandığı hâlin bekçisi.**

    Eski kapı `if probes and not any(golden.values())` yazıyordu. Sonda listesi
    boş olduğunda `probes and ...` her zaman False oluyor, ölçüm geçiyor — ve
    dört vendor da "altın örnek YOK" bayrağı alıyor, çünkü boş sözlükte
    `.get()` None dönüyor. Bekçi hem yanlış konuşuyor hem sözünü tutmuyordu.

    Gerçek bir koşumda tam olarak böyle oldu: veri doğruydu, ön kontrol dördü
    için de "YOK" dedi, ve reddetmesi gerekirken ölçümü yaptı.

    Boş sonda listesi bir CEVAP değil bir ARIZA: kurulum bozuk demek.
    """
    measure.run_on_clickhouse = lambda *a, **k: (1_120_001, "")
    original = measure.golden_probes
    measure.golden_probes = lambda: {}

    try:
        result = measure.preflight("http://x", "u", "p", 1.0)
    finally:
        measure.golden_probes = original

    assert not result.ok, "sonda yokken ölçüm GEÇMEMELİ"
    assert "TÜRETİLEMEDİ" in result.reason
    assert "KURULUM" in result.reason


def test_yabanci_verili_vendor_uyariyla_gecmiyor() -> None:
    """Satırı olup altın örneği olmayan vendor **reddediliyor**, uyarılmıyor.

    Eskiden yalnızca "hiçbiri bulunamadı" reddediliyordu; bir vendor'ın
    yabancı veriyle dolu olması uyarıyla geçiyordu. O uyarı tam da engellemek
    için yazıldığı şeyi üretir: o vendor'ın kuralları `matches=false` verir ve
    sıfır "kapsam düşük" diye okunur.
    """
    probes = measure.golden_probes()

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        if "position(raw_data" in sql:
            # Yalnızca Fortinet'in sondası tutuyor.
            return (1 if any(x in sql for x in probes["Fortinet"]) else 0), ""
        if "device_vendor_name" in sql or "metadata_product_name" in sql:
            # Cisco'nun satırı VAR ama altın örneği yok: yabancı veri.
            return (87 if "Fortinet" in sql or "Cisco" in sql else 0), ""
        return 174, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert not result.ok
    assert "Cisco" in result.reason
    assert "Fortinet" not in result.reason.split(":")[1].split(".")[0]
    assert result.probes, "reddederken aranan sondalar raporlanmalı"


def test_sonda_sorgusu_hata_verirse_bulunamadi_sayilmiyor() -> None:
    """Kırık sorgu ile yüklenmemiş veri aynı şey değil; eskiden hata yutuluyordu."""

    def stub(sql: str, *_args: object, **_kwargs: object) -> tuple[int, str]:
        if "position(raw_data" in sql:
            return 0, "Code: 62. DB::Exception: Syntax error"
        return 500, ""

    measure.run_on_clickhouse = stub
    result = measure.preflight("http://x", "u", "p", 1.0)

    assert not result.ok
    assert "SORGULANAMADI" in result.reason
    assert "Syntax error" in result.reason


def test_sondalar_damga_tasimiyor() -> None:
    """**Sondanın damgayla kesişmesi doğru veriyi reddettirir.**

    Yükleyici örneklerin 2015–2024 tarihlerini ölçüm penceresine taşımak için
    damgayı yeniden yazıyor. Damga taşıyan bir sonda, veri doğru yüklenmiş olsa
    bile tutmaz — ve bekçi doğru veriyi reddeder. Yanlış pozitiften daha sinsi,
    çünkü "veri yanlış" diye okunur.
    """
    probes = measure.golden_probes()

    assert set(probes) == {"Fortinet", "Cisco", "MikroTik", "nginx"}

    for vendor, candidates in probes.items():
        assert candidates, f"{vendor} için sonda türetilemedi"

        for probe in candidates:
            assert len(probe) == measure.PROBE_LENGTH, f"{vendor}: {len(probe)} karakter"
            assert not measure._VOLATILE.search(probe), f"{vendor} sondası damga taşıyor: {probe!r}"


def test_damga_filtresi_pencereyi_gercekten_kaydiriyor() -> None:
    """**Bu test, bir öncekinin ölçemediği şeyi ölçüyor.**

    `test_sondalar_damga_tasimiyor` bugünkü örneklerde damga filtresi
    kaldırılınca da yeşil kalıyor — çünkü o dosyalardaki en uzun satırların
    ortası zaten damga taşımıyor. Yani filtreyi kanıtlamıyor, yalnızca bugünkü
    verinin şanslı olduğunu söylüyor. Bu, deponun §6'sındaki "geçen test
    geçtiğini kanıtlamaz" durumunun ta kendisi.

    Burada satır, ortası damga OLACAK biçimde kuruluyor: naif orta-dilim
    seçimi damgaya düşerdi, filtre onu kaydırmak zorunda.
    """
    left = "srcip=10.1.100.11 srcport=54321 dstip=192.0.2.7 "
    stamp = "eventtime=1557513467369913239 date=2019-05-10 time=11:37:47 "
    right = "action=\"close\" policyid=1 sessionid=105048 proto=6 dstport=443"
    line = left + stamp + right

    naive = line[(len(line) - measure.PROBE_LENGTH) // 2:][:measure.PROBE_LENGTH]
    assert measure._VOLATILE.search(naive), "kurgu bozuk: naif dilim zaten damgasız"

    window = measure._stable_window(line, measure.PROBE_LENGTH)

    assert window, "damgasız pencere var ama bulunamadı"
    assert not measure._VOLATILE.search(window), f"pencere damga taşıyor: {window!r}"
    assert window in line


def test_vendor_basina_birden_cok_sonda() -> None:
    """Tek satıra bağlı sonda kırılgan: yükleyici o satırı yüklememiş olabilir.

    Farklı satırlardan birkaç sonda alıp "herhangi biri tutsun" demek, veri
    doğruyken çıkan yanlış negatifi kapatıyor.
    """
    probes = measure.golden_probes()

    for vendor, candidates in probes.items():
        assert len(candidates) > 1, f"{vendor} tek sondaya bağlı"
        assert len(set(candidates)) == len(candidates), f"{vendor} sondaları tekrar ediyor"


def test_depo_koku_bulunamazsa_sessizce_geri_cekilmiyor() -> None:
    """Eskiden `here.parent` dönüyordu; yanlış kök = boş sonda = kapalı bekçi."""
    assert measure._repo_root() is not None, "bu depoda kök bulunmalı"

    # Damgasız pencere bulunamayan satır için sonda üretilmiyor — uydurulmuyor.
    assert measure._stable_window("2026-08-13 01:02:03", 44) == ""


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
    """Örneklemin bugünkü hâli: **sıfır** kural sınıflandırılmamış alana gidiyor.

    T31 öncesi bu sayı **8**'di (`url` ×4, `dns_query_name` ×2, `query`,
    `http_method`, `user_agent`) ve o sekiz kural ham Sigma adıyla SQL'e
    iniyordu. Şimdi her alanın bir cevabı var:

    * `url`, `user_agent`  → `ATTRS_MAP`, ad alanlı `unmapped[...]` anahtarıyla
    * `http_method`        → `activity_name` kolonu (indeksli, Map'ten ucuz)
    * `dns_query_name`, `query` → `SCHEMA_GAPS`; derleme DÜŞÜYOR

    Sıfırdan sapma iki şeyden biri demek: ya örneklem büyüdü ya pipeline'da
    eksik bir satır var. İkisi de sessiz geçmemeli.
    """
    import importlib
    import sys
    from pathlib import Path

    root = measure._repo_root()
    assert root is not None, "depo kökü bulunamadı"
    sys.path.insert(0, str(root / "sidecar"))
    shipping = importlib.import_module("app.sigma_pipeline")

    rules = sorted((Path(__file__).parent / "rules").glob("*.yml"))
    affected = {
        path.name: shipping.unsupported_fields(path.read_text(encoding="utf-8"))
        for path in rules
    }
    open_gaps = {name: fields for name, fields in affected.items() if fields}

    assert len(rules) == 24
    assert open_gaps == {}, f"sınıflandırılmamış alan kaldı: {open_gaps}"


def test_semada_olmayan_alan_ESLENMIYOR_dusuruluyor() -> None:
    """`dns_query_name` bir eşleme boşluğu değil, bir ŞEMA boşluğu.

    Ayrım ölçüme giriyor: eşlenseydi `unmapped['dns_query_name']` üretilir,
    ClickHouse hata vermez (eksik Map anahtarı boş dizge döner), sorgu koşar ve
    sıfır satır döner. O sıfır "kural eşleşmedi" diye okunurdu.
    """
    import importlib
    import sys

    root = measure._repo_root()
    sys.path.insert(0, str(root / "sidecar"))
    shipping = importlib.import_module("app.sigma_pipeline")

    assert "dns_query_name" in shipping.SCHEMA_GAPS
    assert "dns_query_name" not in shipping.ATTRS_MAP
    assert "url" in shipping.ATTRS_MAP


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
