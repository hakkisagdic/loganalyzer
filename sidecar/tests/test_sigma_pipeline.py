"""Sigma eşleme tablolarının bekçileri (T31).

**pySigma GEREKTİRMİYOR.** Bu bilinçli: eşleme tablolarının doğruluğu backend
sürümünden bağımsız bir olgu ve CI'da backend kurulu olmadan da sınanmalı.
`sigma_pipeline` pySigma'yı yalnızca `bizigo_pipeline()` içinde import ediyor,
dolayısıyla buradaki her şey saf Python.

Ölçülen şey: bir Sigma alanının hangi **sınıfa** düştüğü.

* kolonu var        → `FIELD_MAP`
* `attrs`'ta var     → `ATTRS_MAP`, ad alanlı anahtarla
* hiç üretilmiyor   → `SCHEMA_GAPS`, derleme düşüyor

Üçünü karıştırmak ölçümü tek yönde bozuyor ve bu bir kez yaşandı: T30
prototipi ikinci sınıfı hiç bağlamamıştı, sekiz kural ham adla SQL'e iniyordu
ve fark "şemamız yetersiz" diye okunuyordu.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from app import sigma_pipeline as sp

CATALOG = Path(__file__).resolve().parents[2] / "catalog"


def test_esleme_tablolari_tutarli() -> None:
    """Görünümde olmayan bir kolona eşlemek derlemeyi GEÇER, SQL'i kırar.

    Bu yüzden tutarlılık ayrı bir bekçi: hata sessiz olduğu için testin
    kendisinden başka onu yakalayacak bir şey yok.
    """
    assert sp.validate_tables() == []


def test_bir_alan_yalnizca_bir_sinifa_dusuyor() -> None:
    """Aynı alan iki sınıfta olursa hangi dalın kazandığı sıraya bağlı kalır."""
    assert not (set(sp.FIELD_MAP) & set(sp.ATTRS_MAP))
    assert not (set(sp.FIELD_MAP) & set(sp.SCHEMA_GAPS))
    assert not (set(sp.ATTRS_MAP) & set(sp.SCHEMA_GAPS))


def test_attrs_anahtarlari_ad_alanli() -> None:
    """**Bu testin karşılığı gerçek bir hatadır.**

    T30 prototipi `unmapped['url']` üretmeyi planlıyordu. `EventNormalizer`
    parser'ın `otel:` bloğunu `otel.` önekiyle yazıyor, yani gerçek anahtar
    `otel.url.path`. Düz ad yazılsaydı SQL derlenir, koşar ve SONSUZA KADAR
    sıfır satır döndürürdü — hata yok, sayaç yok, belirti yok.

    Önekli olması tek başına doğruluğu kanıtlamıyor; bir sonraki test
    anahtarların parser kataloğunda gerçekten durduğunu ölçüyor.
    """
    assert sp.ATTRS_MAP["url"] == "otel.url.path"
    assert sp.ATTRS_MAP["user_agent"] == "otel.user_agent.original"

    for field, key in sp.ATTRS_MAP.items():
        assert key.startswith(("otel.", "ocsf.", "fields.")) or "." not in key, (
            f"{field!r} → {key!r}: ad alanı belirsiz"
        )


@pytest.mark.skipif(not CATALOG.is_dir(), reason="katalog bu ağaçta yok")
def test_attrs_anahtarlarinin_karsiligi_parser_katalogunda_var() -> None:
    """Her `attrs` anahtarı bir parser tarafından GERÇEKTEN yazılıyor mu.

    Sözlüğü elle doğru yazmak yetmiyor: parser bir gün `otel."url.path"`
    yazmayı bırakırsa eşleme sessizce boşa düşer. Bu bekçi o günü yakalıyor.

    Karşılaştırma metin üzerinden — `otel.url.path` anahtarı parser YAML'ında
    `otel:` bloğu altında `"url.path"` olarak duruyor.
    """
    corpus = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((CATALOG / "parsers").rglob("*.yaml"))
    )

    missing = []

    for field, key in sp.ATTRS_MAP.items():
        namespace, _, bare = key.partition(".")
        needle = f'"{bare}":' if namespace in {"otel", "ocsf"} else f"{bare}:"

        if needle not in corpus:
            missing.append(f"{field} → {key} (aranan: {needle})")

    assert not missing, "parser kataloğunda karşılığı olmayan attrs anahtarı:\n  " + "\n  ".join(missing)


def test_dns_alanlari_semada_yok_ve_eslenmiyor() -> None:
    """**En önemli ayrım.** `dns_query_name` bir prototip boşluğu DEĞİL.

    Hiçbir parser DNS sorgu adı üretmiyor. `unmapped['dns_query_name']`
    yazılsaydı ClickHouse hata vermezdi — var olmayan Map anahtarı boş dizge
    döndürür — sorgu koşar, sıfır satır gelir, ve bu "kural eşleşmedi" diye
    okunur. Bu yüzden eşlenmiyor, derleme düşürülüyor.
    """
    for field in ("dns_query_name", "query", "QueryName", "answer"):
        assert field in sp.SCHEMA_GAPS
        assert field not in sp.ATTRS_MAP

    # Gerekçe boş bırakılamaz: liste bir gün kapanacaksa neyin kapatacağı yazılı olmalı.
    for field, reason in sp.SCHEMA_GAPS.items():
        assert len(reason) > 30, f"{field}: gerekçe yok"


def test_unmapped_ifadesi_kose_parantez() -> None:
    """Nokta erişimi Tuple/Nested içindir; bizim kolonumuz `Map`."""
    assert sp.unmapped_expression("otel.url.path") == "unmapped['otel.url.path']"

    # Tırnak taşıyan anahtar SQL'i kırardı; sessizce kaçırmak yerine reddediliyor.
    with pytest.raises(ValueError):
        sp.unmapped_expression("o'brien")


def test_ip_ifadesi_ipv4_mapped_onekini_sokuyor() -> None:
    """**Düz `toString()` sessizce sıfır döndürürdü.**

    ClickHouse IPv4'ü `::ffff:a.b.c.d` olarak saklıyor. `toString()` sonucu
    `'::ffff:203.0.113.7'`; `ILIKE '203.0.113.%'` hiçbir zaman tutmaz. Sorgu
    koşar, sonuç boş, hiçbir şey kırmızı yanmaz.
    """
    expression = sp.ip_text_expression("src_endpoint_ip")

    assert "toString(src_endpoint_ip)" in expression
    assert "::ffff:" in expression
    assert expression.startswith("replaceRegexpOne(")


@pytest.mark.skipif(not CATALOG.is_dir(), reason="katalog bu ağaçta yok")
def test_proto_tablosu_katalogdan_okunuyor_kopyalanmiyor() -> None:
    """Tablo burada yeniden yazılsaydı ingest'le sessizce ayrışırdı.

    Ayrışmanın sonucu somut: FortiGate `proto=6` yazıyor, ingest onu `tcp`'ye
    çeviriyor. Sigma tarafı farklı bir sözlük kullansaydı `proto: 6` içeren
    kural, ingest'in `tcp` yazdığı satırı bulamazdı.
    """
    table = sp.load_proto_table(CATALOG / "mappings")

    assert table["6"] == "tcp"
    assert table["17"] == "udp"
    assert table["TCP"] == "tcp"


@pytest.mark.skipif(not CATALOG.is_dir(), reason="katalog bu ağaçta yok")
def test_proto_degeri_kolonun_tuttugu_metne_ceviriliyor() -> None:
    """`proto: 6` ile `proto: TCP` aynı satırı bulmalı; kolon `tcp` tutuyor."""
    table = sp.load_proto_table(CATALOG / "mappings")

    assert sp.proto_to_text(6, table) == "tcp"
    assert sp.proto_to_text("6", table) == "tcp"
    assert sp.proto_to_text("TCP", table) == "tcp"
    assert sp.proto_to_text("tcp", table) == "tcp"
    assert sp.proto_to_text(17, table) == "udp"

    # Tanınmayan değer UYDURULMUYOR: yanlış eşleşme, eşleşmemekten kötü.
    assert sp.proto_to_text(999, table) is None
    assert sp.proto_to_text("", table) is None


def test_kural_alanlari_operator_eki_olmadan_okunuyor() -> None:
    rule = (
        "logsource:\n  category: firewall\n"
        "detection:\n  selection:\n"
        "    srcip|startswith: '10.'\n"
        "    url|contains: '/admin'\n"
        "    dstport: 443\n"
        "  condition: selection\n"
        "level: high\n"
    )

    assert sp.rule_fields(rule) == ["srcip", "url", "dstport"]
    assert sp.unsupported_fields(rule) == []


def test_taninmayan_alan_gorunur_kaliyor() -> None:
    """Tanınmayan alan sessizce geçmemeli — T32'nin kapısı buna dayanacak."""
    rule = (
        "detection:\n  selection:\n"
        "    some_vendor_specific_field: 1\n"
        "  condition: selection\n"
    )

    assert sp.unsupported_fields(rule) == ["some_vendor_specific_field"]


# ---------------------------------------------------------------------------
# Buradan aşağısı pySigma gerektiriyor — üretilen SQL'in KENDİSİ ölçülüyor.
#
# Yukarıdaki tablolar doğru olsa bile backend onları beklenmedik biçimde
# yazabilir; bu bölüm o boşluğu kapatıyor. Üçü de "yazdım" ile "ölçtüm"
# arasındaki farkı somutlaştıran, gerçekten yaşanmış hatalar.
# ---------------------------------------------------------------------------

RULES = Path(__file__).resolve().parents[2] / "prototypes" / "t30-sigma" / "rules"


def _sql(rule_name: str) -> str:
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection

    backend = sp.bizigo_backend(mappings_path=CATALOG / "mappings")

    return backend.convert(
        SigmaCollection.from_yaml((RULES / rule_name).read_text(encoding="utf-8"))
    )[0]


@pytest.mark.skipif(not RULES.is_dir(), reason="T30 örneklemi bu ağaçta yok")
def test_attrs_erisimi_backtick_ILE_SARILMIYOR() -> None:
    """**Bu hata ancak üretilen SQL okununca görüldü.**

    `ClickhouseBackend.field_quote_pattern` varsayılanı `^[a-zA-Z0-9_]*$` ve
    eşleşmeyen her adı backtick'liyor. Sonuç
    `` `unmapped['otel.url.path']` `` oluyordu: ClickHouse bunu bir KOLON ADI
    sanar ve "böyle kolon yok" der.

    Tablolar doğruydu, eşleme doğruydu, SQL kırıktı. Aradaki farkı yalnızca
    çıktının kendisi gösteriyor.
    """
    sql = _sql("nginx_admin_path.yml")

    assert "unmapped['otel.url.path'] ILIKE '/admin%'" in sql
    assert "`" not in sql, f"backtick sızdı: {sql}"


@pytest.mark.skipif(not RULES.is_dir(), reason="T30 örneklemi bu ağaçta yok")
def test_ip_joker_karsilastirmasi_metne_ceviriliyor() -> None:
    """`src_endpoint_ip ILIKE '203.0.113.%'` iki kere yanlıştı.

    Ham hâli tip hatası veriyordu (`IPv6` vs `ILIKE`). Düz `toString()` ise
    hata VERMEZ ama `'::ffff:203.0.113.7'` üretip hiçbir zaman tutmaz — sessiz
    sıfır. Doğrusu öneki sökmek.
    """
    sql = _sql("fortigate_admin_from_wan.yml")

    assert "replaceRegexpOne(toString(src_endpoint_ip), '^::ffff:', '') ILIKE '203.0.113.%'" in sql


@pytest.mark.skipif(not RULES.is_dir(), reason="T30 örneklemi bu ağaçta yok")
def test_ip_esitligi_indeksli_kolonda_kaliyor() -> None:
    """Joker yoksa metne çevrilmiyor: en sık IP koşulu indeksten düşmemeli.

    Bu, düzeltmenin kendi bedelini sınırlayan bekçi. Hepsini metne çevirmek
    kolaydı ve ölçülmeden fark edilmezdi.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection

    rule = (
        "title: t\nid: 11111111-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  category: firewall\n  product: fortigate\n"
        "detection:\n  selection:\n    srcip: '10.0.0.1'\n  condition: selection\nlevel: low\n"
    )
    sql = sp.bizigo_backend(mappings_path=CATALOG / "mappings").convert(
        SigmaCollection.from_yaml(rule)
    )[0]

    assert "src_endpoint_ip='10.0.0.1'" in sql
    assert "replaceRegexpOne" not in sql


@pytest.mark.skipif(not RULES.is_dir(), reason="T30 örneklemi bu ağaçta yok")
def test_proto_sayisi_kolonun_metnine_ceviriliyor() -> None:
    """`proto: 6` kolonda `tcp` arıyor; sayı olarak bıraksak tip hatası olurdu."""
    sql = _sql("fortigate_high_port_scan.yml")

    assert "connection_info_protocol_name='tcp'" in sql
    assert "connection_info_protocol_name=6" not in sql


@pytest.mark.skipif(not RULES.is_dir(), reason="T30 örneklemi bu ağaçta yok")
def test_sema_bosluklu_kural_SESSIZCE_gecmiyor() -> None:
    """T31'in kabul kriteri: eşlenemeyen kural derleme hattında işaretleniyor.

    Alternatif `unmapped['dns_query_name']` üretmekti — derlenir, koşar, sonsuza
    kadar sıfır döndürür ve "kural eşleşmedi" diye okunur.
    """
    from sigma.exceptions import SigmaTransformationError

    with pytest.raises(SigmaTransformationError) as caught:
        _sql("fortigate_dns_tunnel.yml")

    assert "dns_query_name" in str(caught.value)
    # Sebep de taşınıyor: "eşlenemedi" tek başına ne yapılacağını söylemiyor.
    assert "parser" in str(caught.value)


def test_hic_taninmayan_alan_da_derlemeyi_dusuruyor() -> None:
    """**`SCHEMA_GAPS` yalnızca ÖNGÖRÜLEN alanları kapsıyordu.**

    Bu bekçi öngörülmeyenleri kapsıyor ve gerekçesi somut: `user_name`
    eşlemesi eksikti, ham adıyla SQL'e iniyordu, `events_ocsf`'te öyle bir
    kolon yok — ve hiçbir şey kırmızı yanmıyordu. Onu bir API testi tesadüfen
    yakaladı. Tesadüfe bırakılacak bir sınıf değil.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection
    from sigma.exceptions import SigmaTransformationError

    rule = (
        "title: t\nid: 33333333-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  category: firewall\n  product: fortigate\n"
        "detection:\n  selection:\n    ThisFieldDoesNotExist: 'x'\n"
        "  condition: selection\nlevel: low\n"
    )

    with pytest.raises(SigmaTransformationError) as caught:
        sp.bizigo_backend(mappings_path=CATALOG / "mappings").convert(
            SigmaCollection.from_yaml(rule)
        )

    # Mesaj ne yapılacağını söylüyor: hangi sözlüğe eklenmesi gerektiği yazılı.
    assert "FIELD_MAP" in str(caught.value)
    assert "SCHEMA_GAPS" in str(caught.value)


def test_bekci_gercek_kolonlari_gecirıyor() -> None:
    """Bekçi fazla hevesli olsaydı her kuralı düşürürdü; ölçüsü budur."""
    pattern = sp.known_field_pattern()
    import re as _re

    for column in ("src_endpoint_ip", "activity_name", "raw_data"):
        assert _re.match(pattern, column), f"{column} gerçek kolon, geçmeli"

    assert _re.match(pattern, "unmapped['otel.url.path']")
    assert _re.match(pattern, sp.ip_text_expression("src_endpoint_ip"))
    assert not _re.match(pattern, "srcip"), "ham Sigma adı geçmemeli"
