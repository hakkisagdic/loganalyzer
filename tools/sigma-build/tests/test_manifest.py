"""Manifest, takaslı yazım ve sürüklenme kapısı testleri (T32)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from sigma_build.gate import KIND_UNKNOWN_COLUMN, REMEDY_UPSTREAM, Blocker
from sigma_build.manifest import (
    MANIFEST_NAME,
    STATUS_FAILED,
    STATUS_GATED,
    STATUS_WRITTEN,
    RuleOutcome,
    RunHeader,
    build_manifest,
    check_output,
    rule_file_text,
    transition_summary,
    write_output,
)

HEADER = RunHeader(
    ruleset_commit="a1b2c3d4",
    pipeline_version="bizigo-events-ocsf/1",
    pipeline_sha="sha256:deadbeef",
    pysigma_version="1.5.0",
    backend_version="1.1.1",
)

BLOCKER = Blocker(
    kind=KIND_UNKNOWN_COLUMN,
    column="url",
    message="kolon yok: `url` (events_ocsf)",
    remedy="pipeline_or_schema",
)


def written(rule_id: str = "rule-a", sql: str = "SELECT * FROM events_ocsf WHERE a=1") -> RuleOutcome:
    return RuleOutcome(
        rule_id=rule_id,
        title="Bir kural",
        source_path=f"rules/{rule_id}.yml",
        source_sha="sha256:aaaa",
        status=STATUS_WRITTEN,
        logsource={"category": "firewall", "product": "fortigate"},
        sql=sql,
    )


def gated(rule_id: str = "rule-b") -> RuleOutcome:
    return RuleOutcome(
        rule_id=rule_id,
        title="Takılan kural",
        source_path=f"rules/{rule_id}.yml",
        source_sha="sha256:bbbb",
        status=STATUS_GATED,
        logsource={"category": "firewall", "product": "nginx"},
        gate="column_existence",
        blockers=(BLOCKER,),
    )


# --------------------------------------------------------------------------- #
# Değişmezler — tip düzeyinde zorlanıyor
# --------------------------------------------------------------------------- #

def test_gated_kural_sql_tasiyamaz():
    """Taşısaydı dosya yazılırdı ve var olan bir dosya "bu kural koşuyor" iddiasıdır."""
    with pytest.raises(ValueError, match="iddiasıdır"):
        RuleOutcome(
            rule_id="x",
            title="t",
            source_path="p",
            source_sha="s",
            status=STATUS_GATED,
            gate="column_existence",
            blockers=(BLOCKER,),
            sql="SELECT 1",
        )


def test_gated_kural_sebepsiz_olamaz():
    with pytest.raises(ValueError, match="sebep yok"):
        RuleOutcome(rule_id="x", title="t", source_path="p", source_sha="s",
                    status=STATUS_GATED, gate="column_existence")


def test_gated_kural_kapisiz_olamaz():
    with pytest.raises(ValueError, match="hangi kapı"):
        RuleOutcome(rule_id="x", title="t", source_path="p", source_sha="s",
                    status=STATUS_GATED, blockers=(BLOCKER,))


def test_written_kural_sqlsiz_olamaz():
    with pytest.raises(ValueError, match="SQL yok"):
        RuleOutcome(rule_id="x", title="t", source_path="p", source_sha="s", status=STATUS_WRITTEN)


def test_failed_kural_hata_metnisiz_olamaz():
    with pytest.raises(ValueError, match="hata metni yok"):
        RuleOutcome(rule_id="x", title="t", source_path="p", source_sha="s", status=STATUS_FAILED)


def test_bilinmeyen_durum_reddediliyor():
    with pytest.raises(ValueError, match="bilinmeyen durum"):
        RuleOutcome(rule_id="x", title="t", source_path="p", source_sha="s", status="pasif")


@pytest.mark.parametrize("rule_id", ["../kacis", "a/b", "", ".gizli"])
def test_tehlikeli_kural_kimligi_reddediliyor(rule_id):
    """Kimlik dosya adı oluyor; yol ayracı çıktıyı hedef dizinin dışına yazdırırdı."""
    with pytest.raises(ValueError, match="güvenli değil"):
        RuleOutcome(rule_id=rule_id, title="t", source_path="p", source_sha="s",
                    status=STATUS_WRITTEN, sql="SELECT 1")


# --------------------------------------------------------------------------- #
# Manifest içeriği
# --------------------------------------------------------------------------- #

def test_sayilar_durumlara_gore_bolunuyor():
    document = json.loads(build_manifest([written(), gated()], HEADER))
    assert document["counts"] == {
        "total": 2, "written": 1, "gated": 1,
        "gated_closeable": 1, "gated_upstream": 0, "failed": 0,
    }


def test_kapanabilir_ve_kapanamaz_ayri_sayiliyor():
    """"Liste boşaldı mı" sorusu, içinde hiç kapanmayacak kalem varken asla
    evet olamaz — `Pending` ile `Exempt` tek listede durunca ne oluyorsa o (§8)."""
    kapanamaz = RuleOutcome(
        rule_id="rule-c", title="Yukarı akış", source_path="rules/c.yml", source_sha="sha256:cccc",
        status=STATUS_GATED, gate="explain",
        blockers=(Blocker(kind="unsupported_construct", message="backend desteklemiyor",
                          remedy=REMEDY_UPSTREAM),),
    )
    counts = json.loads(build_manifest([gated(), kapanamaz], HEADER))["counts"]
    assert counts["gated"] == 2
    assert counts["gated_closeable"] == 1
    assert counts["gated_upstream"] == 1


def test_tek_kapanamaz_engel_kurali_kapanamaz_yapiyor():
    """Kural açılmak için engellerinin HEPSİNİN kapanmasını istiyor.

    "En az biri kapanabiliyor" demek iyimser tarafa yanılmak olurdu ve o sayı
    yol haritası olarak kullanılacak.
    """
    karma = RuleOutcome(
        rule_id="rule-d", title="Karma", source_path="rules/d.yml", source_sha="sha256:dddd",
        status=STATUS_GATED, gate="explain",
        blockers=(BLOCKER, Blocker(kind="unsupported_construct", message="backend",
                                   remedy=REMEDY_UPSTREAM)),
    )
    counts = json.loads(build_manifest([karma], HEADER))["counts"]
    assert counts["gated_closeable"] == 0
    assert counts["gated_upstream"] == 1


def test_gated_kayit_eyleme_cevrilebilir_sebep_tasiyor():
    document = json.loads(build_manifest([gated()], HEADER))
    (rule,) = document["rules"]
    assert rule["status"] == STATUS_GATED
    assert rule["gate"] == "column_existence"
    assert rule["blockers"] == [
        {
            "kind": "unknown_column",
            "message": "kolon yok: `url` (events_ocsf)",
            "remedy": "pipeline_or_schema",
            "column": "url",
        }
    ]
    # Kaynak sürümü de taşınıyor — gereksinimin parçası.
    assert rule["source_sha"] == "sha256:bbbb"


def test_manifestte_derleme_tarihi_yok():
    """Tarih olsaydı manifest'in kendi sürüklenme kapısı imkânsız olurdu."""
    document = json.loads(build_manifest([written()], HEADER))
    assert set(document["run"]) == {
        "view", "ruleset_commit", "pipeline_version", "pipeline_sha",
        "pysigma_version", "backend_version",
    }
    assert "compiled_at" not in json.dumps(document)


def test_ayni_girdi_ayni_manifest():
    """Kabul kriteri A, manifest tarafı."""
    assert build_manifest([written(), gated()], HEADER) == build_manifest([gated(), written()], HEADER)


def test_tekrarlanan_kimlik_reddediliyor():
    with pytest.raises(ValueError, match="birden fazla"):
        build_manifest([written("a"), written("a")], HEADER)


def test_kural_dosyasinda_tarih_yok_kaynak_surumu_var():
    text = rule_file_text(written(), HEADER)
    assert "sha256:aaaa" in text
    assert "a1b2c3d4" in text
    assert "bizigo-events-ocsf/1" in text
    assert "SELECT * FROM events_ocsf WHERE a=1" in text


def test_gated_kural_dosya_metni_uretmiyor():
    with pytest.raises(ValueError, match="yalnızca `written`"):
        rule_file_text(gated(), HEADER)


# --------------------------------------------------------------------------- #
# Takaslı yazım — clicksiem tuzağı
# --------------------------------------------------------------------------- #

def test_yalnizca_written_kurallar_dosya_uretiyor(tmp_path):
    target = tmp_path / "sigma"
    write_output(target, [written("a"), gated("b")], HEADER)
    assert sorted(p.name for p in target.iterdir()) == ["a.sql", MANIFEST_NAME]


def test_kapiya_takilan_kuralin_eski_dosyasi_siliniyor(tmp_path):
    """`clicksiem/sigma_rules`'ta ölçülen tuzak: başarısız dönüşüm eski dosyayı bırakıyor.

    Burada bırakabileceği bir kod yolu yok — çıktı takas ediliyor.
    """
    target = tmp_path / "sigma"
    write_output(target, [written("a"), written("b")], HEADER)
    assert (target / "b.sql").is_file()

    write_output(target, [written("a"), gated("b")], HEADER)
    assert not (target / "b.sql").exists()
    assert (target / "a.sql").is_file()

    document = json.loads((target / MANIFEST_NAME).read_text(encoding="utf-8"))
    kayip = next(rule for rule in document["rules"] if rule["rule_id"] == "b")
    assert kayip["status"] == STATUS_GATED
    assert kayip["blockers"][0]["column"] == "url"


def test_takas_yarida_kalirsa_eski_cikti_geri_geliyor(tmp_path, monkeypatch):
    """Takas sırasında bir istisna, üretilmiş çıktıyı silip yerine bir şey koymamalı."""
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    onceki = (target / "a.sql").read_text(encoding="utf-8")

    gercek_rename = __import__("os").rename
    cagri = {"n": 0}

    def patlayan_rename(src, dst):
        cagri["n"] += 1
        if cagri["n"] == 2:  # eski dizin yana alındıktan sonra
            raise OSError("takas ortasında disk hatası")
        return gercek_rename(src, dst)

    monkeypatch.setattr("sigma_build.manifest.os.rename", patlayan_rename)

    with pytest.raises(OSError, match="disk hatası"):
        write_output(target, [written("a", sql="SELECT 2")], HEADER)

    assert (target / "a.sql").read_text(encoding="utf-8") == onceki


def test_yazim_gecici_dizin_birakmiyor(tmp_path):
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    write_output(target, [written("a")], HEADER)
    artiklar = [p.name for p in tmp_path.iterdir() if p.name.startswith(".sigma-build")]
    assert artiklar == []


# --------------------------------------------------------------------------- #
# Sürüklenme kapısı
# --------------------------------------------------------------------------- #

def test_kapi_ayni_ciktida_sessiz(tmp_path):
    target = tmp_path / "sigma"
    write_output(target, [written("a"), gated("b")], HEADER)
    assert check_output(target, [written("a"), gated("b")], HEADER) == []


def test_kapi_hedefe_dokunmuyor(tmp_path):
    """Üzerine yazsaydı, düşen bir kapıdan sonra ikinci koşum sebepsiz geçerdi."""
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    onceki = (target / "a.sql").read_text(encoding="utf-8")

    assert check_output(target, [written("a", sql="SELECT 99")], HEADER) != []
    assert (target / "a.sql").read_text(encoding="utf-8") == onceki


def test_kapi_degisen_sqli_yakaliyor(tmp_path):
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    assert check_output(target, [written("a", sql="SELECT 99")], HEADER) == ["farklı: a.sql", "farklı: manifest.json"]


def test_kapi_yeni_kurali_yakaliyor(tmp_path):
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    problems = check_output(target, [written("a"), written("b")], HEADER)
    assert "eksik (üretiliyor ama depoda yok): b.sql" in problems


def test_kapi_bayat_dosyayi_yakaliyor(tmp_path):
    """Elle bırakılmış bir dosya da sürüklenmedir."""
    target = tmp_path / "sigma"
    write_output(target, [written("a")], HEADER)
    (target / "hayalet.sql").write_text("SELECT 1", encoding="utf-8")
    assert "fazla (depoda var ama üretilmiyor): hayalet.sql" in check_output(target, [written("a")], HEADER)


def test_kapi_pipeline_degisimini_yakaliyor(tmp_path):
    """Pipeline sürümü değişince bütün kural dosyaları ve manifest değişiyor."""
    target = tmp_path / "sigma"
    write_output(target, [written("a"), written("b")], HEADER)

    yeni = RunHeader(
        ruleset_commit=HEADER.ruleset_commit,
        pipeline_version="bizigo-events-ocsf/2",
        pipeline_sha="sha256:cafe",
        pysigma_version=HEADER.pysigma_version,
        backend_version=HEADER.backend_version,
    )
    problems = check_output(target, [written("a"), written("b")], yeni)
    assert sorted(problems) == ["farklı: a.sql", "farklı: b.sql", "farklı: manifest.json"]


def test_bos_hedef_her_seyi_eksik_gosteriyor(tmp_path):
    problems = check_output(tmp_path / "yok", [written("a")], HEADER)
    assert problems == ["eksik (üretiliyor ama depoda yok): a.sql",
                        "eksik (üretiliyor ama depoda yok): manifest.json"]


# --------------------------------------------------------------------------- #
# Geçiş — iki koşum arasındaki fark
# --------------------------------------------------------------------------- #

def test_acilan_kural_gorunuyor():
    once = build_manifest([gated("a"), written("b")], HEADER)
    sonra = build_manifest([written("a"), written("b")], HEADER)
    assert transition_summary(once, sonra)["opened"] == ["a"]


def test_kapanan_kural_gorunuyor():
    once = build_manifest([written("a")], HEADER)
    sonra = build_manifest([gated("a")], HEADER)
    summary = transition_summary(once, sonra)
    assert summary["closed"] == ["a"]
    assert summary["opened"] == []


def test_eklenen_ve_kaybolan_kural_ayri_sayiliyor():
    """Yeni bir kural "açıldı" değildir; kaybolan bir kural "kapandı" değildir."""
    once = build_manifest([written("a")], HEADER)
    sonra = build_manifest([written("b")], HEADER)
    summary = transition_summary(once, sonra)
    assert summary["added"] == ["b"]
    assert summary["removed"] == ["a"]
    assert summary["opened"] == []
    assert summary["closed"] == []


def test_bos_onceki_manifest_kaza_yapmiyor():
    """İlk koşumda karşılaştırılacak bir şey yok."""
    summary = transition_summary("", build_manifest([written("a")], HEADER))
    assert summary["added"] == ["a"]
    assert summary["opened"] == []
