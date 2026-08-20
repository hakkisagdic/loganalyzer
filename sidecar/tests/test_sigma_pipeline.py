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
        namespace, dot, rest = key.partition(".")

        # Öneksiz anahtar (`fw_chain`) `partition` ile bozuluyordu: `rest` boş
        # kalıyor, aranan dizge `":"` oluyor ve **her dosyada** bulunuyordu.
        # Yani bekçi öneksiz anahtarlar için sessizce her şeyi geçiriyordu —
        # ölçüldü: sahte bir anahtar konduğunda düzeltme olmadan yakalanmıyor.
        if not dot:
            namespace, rest = "", key

        needle = f'"{rest}":' if namespace in {"otel", "ocsf"} else f"{rest}:"

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

def _corpus() -> Path:
    """Kural korpusu — **T32'nin sabitinden**, elle yazılmadan.

    Bu sabit eskiden `prototypes/t30-sigma/rules`'u gösteriyordu ve korpus
    `catalog/sigma/rules/`'a taşındığında **CI'yı kırdı**. Kırması iyi oldu:
    sessizce boş bir liste okusaydı testler "atlandı" diye yeşil kalırdı.

    Asıl ders taşımanın kendisi değil: iki aracı tek kaynağa çevirirken
    *"her iki tüketici de"* denmişti — ama tüketiciler **sayılmamış,
    hatırlanmıştı**. Üçüncüsü buydu ve aynı pakette duruyordu.
    `rg -n "t30-sigma"` bir saniyelik iş.
    """
    import sys

    root = Path(__file__).resolve().parents[2]
    tools = str(root / "tools" / "sigma-build")

    if tools not in sys.path:
        sys.path.insert(0, tools)

    from sigma_build.ruleset import CATALOG_DIR, RULES_SUBDIR

    return root / CATALOG_DIR / RULES_SUBDIR


RULES = _corpus()

#: Korpusta gerçekten kural var mı — atlama ölçütü **dizin varlığı değil**.
#:
#: Eski ölçüt `RULES.is_dir()` idi ve tam da bu yüzden CI'yı kırdı: emekli
#: dizin bir README ile duruyordu, yani `is_dir()` doğruydu, testler atlanmadı
#: ve dosya okumada patladılar. Dizinin var olması kuralların var olması
#: demek değil.
HAS_RULES = RULES.is_dir() and any(RULES.glob("*.yml"))


def _sql(rule_name: str) -> str:
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection

    backend = sp.bizigo_backend(mappings_path=CATALOG / "mappings")
    path = RULES / rule_name

    # Adı geçen kural yoksa bu bir ATLAMA sebebi değil, bir arıza: korpus
    # yerinde ama beklenen kural gitmiş demektir ve testin sessizce
    # geçmesi o kaybı gizlerdi.
    assert path.is_file(), f"korpusta beklenen kural yok: {path}"

    return backend.convert(
        SigmaCollection.from_yaml(path.read_text(encoding="utf-8"))
    )[0]


@pytest.mark.skipif(not HAS_RULES, reason="kural korpusu bu ağaçta yok (dağıtılmış imaj)")
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


@pytest.mark.skipif(not HAS_RULES, reason="kural korpusu bu ağaçta yok (dağıtılmış imaj)")
def test_ip_joker_karsilastirmasi_metne_ceviriliyor() -> None:
    """`src_endpoint_ip ILIKE '203.0.113.%'` iki kere yanlıştı.

    Ham hâli tip hatası veriyordu (`IPv6` vs `ILIKE`). Düz `toString()` ise
    hata VERMEZ ama `'::ffff:203.0.113.7'` üretip hiçbir zaman tutmaz — sessiz
    sıfır. Doğrusu öneki sökmek.
    """
    sql = _sql("fortigate_admin_from_wan.yml")

    assert "replaceRegexpOne(toString(src_endpoint_ip), '^::ffff:', '') ILIKE '203.0.113.%'" in sql


@pytest.mark.skipif(not HAS_RULES, reason="kural korpusu bu ağaçta yok (dağıtılmış imaj)")
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


@pytest.mark.skipif(not HAS_RULES, reason="kural korpusu bu ağaçta yok (dağıtılmış imaj)")
def test_proto_sayisi_kolonun_metnine_ceviriliyor() -> None:
    """`proto: 6` kolonda `tcp` arıyor; sayı olarak bıraksak tip hatası olurdu."""
    sql = _sql("fortigate_high_port_scan.yml")

    assert "connection_info_protocol_name='tcp'" in sql
    assert "connection_info_protocol_name=6" not in sql


@pytest.mark.skipif(not HAS_RULES, reason="kural korpusu bu ağaçta yok (dağıtılmış imaj)")
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


# ---------------------------------------------------------------------------
# T32 ile sözleşme
# ---------------------------------------------------------------------------

TOOLS = Path(__file__).resolve().parents[2] / "tools" / "sigma-build"


def test_remedy_sozlugu_T32_ile_ayni() -> None:
    """**İki ayrı ajanın aynı sözlüğü kullandığı, ölçülerek biliniyor olmalı.**

    T32'nin manifesti engelleri `remedy`'ye göre kapanabilir/kapanamaz diye
    ayırıyor ve `gated_closeable` / `gated_upstream` sayılarını oradan
    türetiyor. Bizim yazdığımız değer o sözlükte YOKSA, `CLOSEABLE_REMEDIES`
    üyelik kontrolü sessizce `False` döner ve engel **kapanamaz** tarafına
    yazılır — yani "liste boşaldı mı" sorusunun cevabı asla evet olmaz.

    Hatanın tamamı bir yazım farkında: `"schema"` yerine `"schemas"`. Hiçbir
    yerde hata yok, sayaç yok, belirti yok.

    Sidecar imajı `tools/` taşımıyor, dolayısıyla değerler orada yeniden
    yazılıyor. Yeniden yazmanın bedeli ayrışma; bu test o bedeli ödüyor.
    """
    if not TOOLS.is_dir():
        # Dizin yoksa (dağıtılmış imaj) sınanacak bir şey de yok. Ama dizin
        # VARSA import başarısız olmamalı: "koşuma giriyor ama ortam hazır
        # değil" hâli, §7'nin sessizce kırmızı yanan CI'sının ta kendisi.
        pytest.skip("tools/sigma-build bu ağaçta yok — imaj koşumu")

    import sys

    if str(TOOLS) not in sys.path:
        sys.path.insert(0, str(TOOLS))

    from sigma_build.gate import CLOSEABLE_REMEDIES, REMEDY_SCHEMA, REMEDY_UNKNOWN

    assert sp.REMEDY_SCHEMA == REMEDY_SCHEMA
    assert sp.REMEDY_UNKNOWN == REMEDY_UNKNOWN

    for field, gap in sp.SCHEMA_GAPS.items():
        assert gap.remedy in CLOSEABLE_REMEDIES, (
            f"{field}: `{gap.remedy}` T32'nin sözlüğünde yok ya da muafiyete "
            "yazılmış. Muafiyet iki bilinçli hareket gerektiriyor ve ikincisi "
            "T32'nin sahibinde."
        )


def test_hicbir_bosluk_tek_tarafli_MUAFIYET_almiyor() -> None:
    """`upstream` "kimsenin yapamayacağı iş" demek ve sayısı ayrıca sabitleniyor.

    Buradan tek taraflı verilirse T32'nin `gated_upstream` sabiti sessizce
    kayar — muafiyet eklemenin iki ayrı bilinçli hareket olmasının sebebi tam
    olarak buydu (§8).
    """
    assert all(gap.remedy != "upstream" for gap in sp.SCHEMA_GAPS.values())


def test_her_bosluk_kapatan_isi_ADLANDIRYOR() -> None:
    """Gerekçe, "neyin kapatacağını" söylemiyorsa liste bir çöp kutusudur."""
    for field, gap in sp.SCHEMA_GAPS.items():
        assert len(gap.reason) > 30, f"{field}: gerekçe yok"
        assert gap.remedy, f"{field}: remedy yok"


def test_routeros_action_kolonu_SESSIZCE_bos_donmuyor() -> None:
    """**Dördüncü boşluk sınıfı: logsource'a bağlı boş kolon.**

    `activity_name` üç vendor'da dolu, RouterOS'ta boş — ve boşluk kaza değil,
    parser'ın bilinçli kararı: *"RouterOS firewall kaydı kuralın ne verdiğini
    içermiyor; `accept` ya da `drop` yazmak uydurma olurdu."*

    Üç sınıflı model bunu ifade edemiyordu. `FIELD_MAP` küresel, dolayısıyla
    `action` eşlemesi diğer vendor'lar için doğru ve `SCHEMA_GAPS`'e konamaz.
    Ama RouterOS kuralı `action` kullandığında sorgu koşar, VAR OLAN bir kolona
    bakar ve sonsuza kadar sıfır döner.

    Bu kusur ne sözlüğe ne üretilen SQL'e bakarak görülebilirdi — yalnızca
    veriye sorularak. T32'nin 3. kapısı canlı ClickHouse'ta yakaladı.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection
    from sigma.exceptions import SigmaTransformationError

    rule = (
        "title: t\nid: 44444444-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  category: network_connection\n  product: routeros\n"
        "detection:\n  selection:\n    action: 'forward'\n  condition: selection\nlevel: low\n"
    )

    with pytest.raises(SigmaTransformationError) as caught:
        sp.bizigo_backend(mappings_path=CATALOG / "mappings").convert(
            SigmaCollection.from_yaml(rule)
        )

    # Mesaj alternatifi ADLANDIRIYOR: "burada boş" tek başına ne yapılacağını
    # söylemiyor ve kullanıcı aynı yanlışı başka bir alanla tekrarlar.
    assert "fw_chain" in str(caught.value)


def test_bekci_diger_vendorlarda_action_i_ENGELLEMIYOR() -> None:
    """Bekçinin ölçüsü. Küresel olsaydı üç vendor'ın kuralları da düşerdi.

    Bu, düzeltmenin kendi bedelini sınırlayan bekçi: `activity_name`'i topluca
    yasaklamak kolaydı ve ölçülmeden fark edilmezdi.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection

    rule = (
        "title: t\nid: 55555555-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  category: firewall\n  product: fortigate\n"
        "detection:\n  selection:\n    action: 'blocked'\n  condition: selection\nlevel: low\n"
    )
    sql = sp.bizigo_backend(mappings_path=CATALOG / "mappings").convert(
        SigmaCollection.from_yaml(rule)
    )[0]

    assert "activity_name='blocked'" in sql


def test_zincir_adi_attrs_tarafina_gidiyor() -> None:
    """`forward` bir zincir adı; grok onu `fw_chain`'e yakalıyor, `fields:`
    öneksiz iniyor, yani `unmapped['fw_chain']`."""
    assert sp.ATTRS_MAP["fw_chain"] == "fw_chain"
    assert sp.unmapped_expression(sp.ATTRS_MAP["fw_chain"]) == "unmapped['fw_chain']"


def test_bilinmeyen_product_ta_bekci_YASAKLAMIYOR() -> None:
    """**`unknown` ile `empty` farklı şeyler** ve bu ayrım burada tutuyor.

    `VENDOR_EMPTY_COLUMNS` `LogsourceCondition(product=...)` ile koşullu,
    yani tanımadığımız bir product'ta (ör. `linux`) hiç uygulanmıyor. Doğru
    davranış: o vendor'da kolonun boş olduğunu **bilmiyoruz**, ve bilmediğimiz
    bir şeyi yasaklamak kuralı sebepsiz düşürürdü.

    Bekçinin dar olması bir eksiklik değil ölçüsü: `activity_name`'i her
    logsource'ta yasaklamak kolaydı ve tanımadığımız her vendor'ın kurallarını
    sessizce öldürürdü.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from sigma.collection import SigmaCollection

    rule = (
        "title: t\nid: 66666666-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  product: linux\n"
        "detection:\n  selection:\n    action: 'denied'\n    user_name: 'admin'\n"
        "  condition: selection\nlevel: low\n"
    )
    sql = sp.bizigo_backend(mappings_path=CATALOG / "mappings").convert(
        SigmaCollection.from_yaml(rule)
    )[0]

    assert "activity_name='denied'" in sql
    assert "actor_user_name='admin'" in sql


def test_sozlukler_okunamazsa_KURULUM_hatasi_kullanici_hatasi_degil() -> None:
    """**Yanlış yapılandırılmış bir dağıtım, kullanıcıya "kuralın bozuk" demez.**

    `/v1/sigma/compile` sözlükleri imaj yolundan okuyor. Yol yanlışsa
    `FileNotFoundError` geliyordu ve ucun genel `except`'i onu **422**'ye
    çeviriyordu — yani kurulum hatası, kural hatası gibi raporlanıyordu ve
    kimse imaja bakmazdı.

    Ölçüldü: CI'da tam olarak bu oldu ve teşhis bir tur sürdü.
    """
    pytest.importorskip("sigma.backends.clickhouse.clickhouse")
    from app.sigma_compile import SigmaBackendUnavailable, compile_rule

    rule = (
        "title: t\nid: 77777777-0000-4000-8000-000000000000\nstatus: experimental\n"
        "logsource:\n  product: fortigate\n"
        "detection:\n  selection:\n    action: 'blocked'\n  condition: selection\nlevel: low\n"
    )

    with pytest.raises(SigmaBackendUnavailable) as caught:
        compile_rule(rule, "clickhouse", mappings_path="/bu/yol/yok")

    # Mesaj NEREYE bakılacağını söylüyor: "okunamadı" tek başına bir sonraki
    # kişiyi kuralın içinde arattırırdı.
    assert "kurulum sorunu" in str(caught.value)
    assert "BIZIGO_MAPPINGS_PATH" in str(caught.value)
