"""Kapı 1 testleri — kolon varlığı kapısı.

Örneklem sentetik değil: `tests/fixtures/generated-sql-sample.json` T30
prototipinin 24 kuralının **gerçek backend çıktısı** (pySigma 1.5.0 +
`pysigma-backend-clickhouse` 1.1.1). Dondurulmuş olmasının iki sebebi var —
prototip dizini "atılabilir" işaretli ve testlerin pySigma kurulumuna
bağlanmaması gerekiyor. T31'in kalıcı pipeline'ı geldiğinde örneklem yenilenir.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from sigma_build.gate import (
    CLOSEABLE_REMEDIES,
    GATE_COLUMN_EXISTENCE,
    KIND_UNKNOWN_COLUMN,
    check_columns,
    referenced_columns,
)
from sigma_build.view_columns import repo_root, view_definition

FIXTURE = Path(__file__).parent / "fixtures" / "generated-sql-sample.json"
OCSF = view_definition("events_ocsf", repo_root() / "db" / "clickhouse").column_set


def sample_rules() -> list[dict[str, str]]:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))["rules"]


# --------------------------------------------------------------------------- #
# Ad çıkarımı
# --------------------------------------------------------------------------- #

def test_metin_sabitleri_kolon_sayilmiyor():
    columns = referenced_columns("SELECT * FROM events_ocsf WHERE activity_name='denied'")
    assert columns == ("activity_name",)


def test_tablo_adi_kolon_sayilmiyor():
    assert "events_ocsf" not in referenced_columns("SELECT * FROM events_ocsf WHERE a=1")


def test_fonksiyon_adi_kolon_sayilmiyor():
    columns = referenced_columns("SELECT * FROM events_ocsf WHERE toString(src_endpoint_ip) ILIKE '10.%'")
    assert "toString" not in columns
    assert "src_endpoint_ip" in columns


def test_anahtar_sozcukler_kolon_sayilmiyor():
    columns = referenced_columns(
        "SELECT * FROM events_ocsf WHERE a IS NOT NULL AND b IN (1, 2) ORDER BY c DESC LIMIT 10"
    )
    assert set(columns) == {"a", "b", "c"}


def test_map_erisimi_kolonu_veriyor_anahtari_vermiyor():
    """`unmapped['url']` → kolon `unmapped`; `url` bir metin sabiti."""
    columns = referenced_columns("SELECT * FROM events_ocsf WHERE unmapped['url'] ILIKE '%/admin%'")
    assert columns == ("unmapped",)


def test_kacirilmis_tirnak_metni_bitirmiyor():
    """`nginx_sqli_probe` gerçekten `'%'' OR ''1''=''1%'` üretiyor.

    Kaçış yanlış okunsaydı metnin içindeki `OR` ve `1` kolon sanılırdı.
    """
    columns = referenced_columns("SELECT * FROM events_ocsf WHERE url ILIKE '%'' OR ''1''=''1%'")
    assert columns == ("url",)


def test_tirnakli_ad_kolon_sayiliyor():
    assert referenced_columns('SELECT * FROM events_otel WHERE "host.name"=\'fw-01\'') == ("host.name",)
    assert referenced_columns("SELECT * FROM events_otel WHERE `host.name`='fw-01'") == ("host.name",)


def test_ad_tekrarlanmiyor():
    columns = referenced_columns("SELECT * FROM t WHERE url ILIKE 'a' OR url ILIKE 'b'")
    assert columns == ("url",)


def test_bilinmeyen_sozcuk_gurultulu_tarafa_yaniliyor():
    """Anahtar sözcük listesi eksikse sonuç fazladan bir engel — sessiz kayıp değil.

    Bu testin çivilediği şey davranış değil **yanılma yönü**: `SAMPLE` listede
    olmadığı için kolon sanılıyor ve rapora düşüyor. Fark edilir; ters yanılgı
    kuralı kapıdan geçirirdi.
    """
    verdict = check_columns("SELECT * FROM events_ocsf SAMPLE 0.1", OCSF, view="events_ocsf")
    assert [blocker.column for blocker in verdict.blockers] == ["SAMPLE"]


# --------------------------------------------------------------------------- #
# Kapı
# --------------------------------------------------------------------------- #

def test_gecen_kural_engel_uretmiyor():
    verdict = check_columns(
        "SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND dst_endpoint_port=445",
        OCSF,
        view="events_ocsf",
    )
    assert verdict.passed
    assert verdict.blockers == ()
    assert verdict.gate == GATE_COLUMN_EXISTENCE


def test_engel_eyleme_cevrilebilir():
    verdict = check_columns("SELECT * FROM events_ocsf WHERE url ILIKE '%/admin%'", OCSF, view="events_ocsf")
    (blocker,) = verdict.blockers
    assert blocker.kind == KIND_UNKNOWN_COLUMN
    assert blocker.column == "url"
    assert "url" in blocker.message
    assert blocker.as_dict()["remedy"] == "pipeline_or_schema"


def test_engeller_liste_tekil_alan_degil():
    """`nginx_large_upload` iki alan birden istiyor.

    Tekil bir sebep alanı olsaydı "`url` eklersek kaç kural açılır" sorusu
    **fazla** cevap verirdi: bu kural `url` gelince açılmıyor, `http_method` de
    gerekiyor. Yol haritası olacak bir sayı iyimser tarafa yanılmamalı.
    """
    verdict = check_columns(
        "SELECT * FROM events_ocsf WHERE http_method='POST' AND url ILIKE '%/upload%'",
        OCSF,
        view="events_ocsf",
    )
    assert sorted(blocker.column for blocker in verdict.blockers) == ["http_method", "url"]


# --------------------------------------------------------------------------- #
# Gerçek örneklem — 24 kural
# --------------------------------------------------------------------------- #

#: Bu örneklemde kapıya takılan kural sayısı — **iki sabit, tek değil.**
#:
#: Eşik değil sabit: artış da azalış da bu testi kırar ve incelemede tek başına
#: göze çarpar. Eşik olsaydı "şu kadara kadar normal" derdi ve bir gün kimsenin
#: bakmadığı bir rakama dönüşürdü (§8, `ExpectedExemptCount` deseni).
#:
#: İkiye bölünmesinin sebebi de §8: azalması beklenen kalemlerle hiç
#: kapanmayacak olanlar tek sayıda toplanırsa, "liste boşaldı mı" sorusunun
#: cevabı asla evet olamaz. Bugün ikincisi sıfır — Kapı 1'in ürettiği tek engel
#: türü `unknown_column` ve o kapanabilir bir iş kalemi.
EXPECTED_GATED_CLOSEABLE = 8
EXPECTED_GATED_UPSTREAM = 0
EXPECTED_GATED_COUNT = EXPECTED_GATED_CLOSEABLE + EXPECTED_GATED_UPSTREAM

#: Hangi kuralın hangi alan yüzünden takıldığı. Sayının yanına bu tablo
#: gerekiyor çünkü sayı sabit kalırken içeriği değişebilir.
EXPECTED_BLOCKERS = {
    "fortigate_blocked_category.yml": ["url"],
    "fortigate_dns_tunnel.yml": ["dns_query_name"],
    "nginx_admin_path.yml": ["url"],
    "nginx_dns_rebind.yml": ["query"],
    "nginx_large_upload.yml": ["http_method", "url"],
    "nginx_scanner_agent.yml": ["user_agent"],
    "nginx_sqli_probe.yml": ["url"],
    "routeros_dns_request.yml": ["dns_query_name"],
}


def test_orneklem_yirmi_dort_kural():
    assert len(sample_rules()) == 24


def test_orneklemde_gated_sayisi_sabit():
    gated = [rule for rule in sample_rules() if not check_columns(rule["sql"], OCSF, view="events_ocsf").passed]
    assert len(gated) == EXPECTED_GATED_COUNT


def test_orneklemde_kapanabilir_ve_kapanamaz_ayri_sabit():
    kapanabilir = kapanamaz = 0
    for rule in sample_rules():
        verdict = check_columns(rule["sql"], OCSF, view="events_ocsf")
        if verdict.passed:
            continue
        if all(blocker.remedy in CLOSEABLE_REMEDIES for blocker in verdict.blockers):
            kapanabilir += 1
        else:
            kapanamaz += 1
    assert (kapanabilir, kapanamaz) == (EXPECTED_GATED_CLOSEABLE, EXPECTED_GATED_UPSTREAM)


def test_orneklemde_hangi_kural_hangi_alan_yuzunden():
    actual = {}
    for rule in sample_rules():
        verdict = check_columns(rule["sql"], OCSF, view="events_ocsf")
        if not verdict.passed:
            actual[rule["rule_file"]] = sorted(blocker.column for blocker in verdict.blockers)
    assert actual == EXPECTED_BLOCKERS


def test_yol_haritasi_sorusu_mekanik_cevaplaniyor():
    """"`url` eklersek kaç kural açılır" — `group by column` ile.

    Cevap 4 değil **3**: `nginx_large_upload` `http_method`'u da bekliyor.
    Manifest'in yol haritası olma iddiası tam olarak bu ayrımda duruyor.
    """
    blocked: dict[str, set[str]] = {}
    for rule in sample_rules():
        verdict = check_columns(rule["sql"], OCSF, view="events_ocsf")
        if not verdict.passed:
            blocked[rule["rule_file"]] = {blocker.column for blocker in verdict.blockers}

    url_users = [name for name, columns in blocked.items() if "url" in columns]
    assert len(url_users) == 4
    opened_by_url_alone = [name for name in url_users if blocked[name] == {"url"}]
    assert len(opened_by_url_alone) == 3


# --------------------------------------------------------------------------- #
# Kapı 1'in yakalayamadıkları — Kapı 2'nin gerekçesi
# --------------------------------------------------------------------------- #

@pytest.mark.parametrize(
    ("rule_file", "why"),
    [
        ("fortigate_high_port_scan.yml", "connection_info_protocol_name LowCardinality(String), tamsayı ile karşılaştırılıyor"),
        ("fortigate_admin_from_wan.yml", "src_endpoint_ip IPv6, ILIKE String istiyor"),
    ],
)
def test_tip_uyusmazligi_kapi_birden_geciyor(rule_file, why):
    """Bu iki kural Kapı 1'i **geçiyor** ve geçmeleri doğru.

    Kolonları gerçekten var; kusur tipte. Kapı 1'in bunları yakalaması ancak
    kolon tiplerini de modellemesiyle olurdu — yani ClickHouse'un yarısını
    yeniden yazmakla. Yakalayan yer Kapı 2 (`EXPLAIN`).

    Test buradaki iddiayı çiviliyor: **tek kapı yetmiyor.** Biri diğerinin
    yerine konursa yakalanmayan sınıf sessiz kalır — ve bu iki kural,
    `compiled=24 / runs=14` farkının 8'i kolon yokluğuyla açıklandıktan sonra
    kalan ikisi.

    ⚠️ Bu iki tipin ClickHouse tarafından **gerçekten** reddedildiği burada
    ölçülmüyor; şema tiplerinden çıkarıldı (`0001_events.sql`: `proto
    LowCardinality(String)`, `src_ip IPv6`). Ölçümü Kapı 2 yapacak.
    """
    sql = next(rule["sql"] for rule in sample_rules() if rule["rule_file"] == rule_file)
    assert check_columns(sql, OCSF, view="events_ocsf").passed, why
