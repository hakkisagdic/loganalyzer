"""Derleme adımının T31'e bağlanması (T32, sıra §5 madde 6).

Buradaki testler pySigma **gerektirmiyor**: pipeline ve backend yerine sahte
konuyor. Sınanan şey `collect_outcomes()`'in sınıflandırması — hangi sonucun
`gated`, hangisinin `failed` olduğu.

Ayrım ölçümle bulundu. İlk yazımda T31'in **bilinçli** reddi (`
SigmaTransformationError`) `failed` sayılıyordu ve bu yanlıştı: `failed`
"pipeline kırık" demek ve sabit sıfır olması bekleniyor. Eşlenemeyen bir alan
kırıklık değil bir **iş kalemi**.
"""

from __future__ import annotations

import sys
import types
from pathlib import Path

import pytest

from sigma_build import compile as compile_module
from sigma_build.manifest import STATUS_FAILED, STATUS_GATED, STATUS_WRITTEN

KURAL = """title: Deneme
id: 11111111-0000-4000-8000-000000000000
logsource:
  category: firewall
  product: fortigate
detection:
  selection:
    dstport: 445
  condition: selection
"""


def sahte_pipeline(*, hata: BaseException | None = None, sql: str = "SELECT * FROM events_ocsf WHERE dst_endpoint_port=445"):
    """T31 yerine geçen sahte modül."""

    class Backend:
        def convert(self, collection):
            if hata is not None:
                raise hata
            return [sql]

    module = types.SimpleNamespace(
        bizigo_backend=lambda **kwargs: Backend(),
        # T31 `schema_gaps`'i **sözlük** olarak veriyor: `{alan: {remedy, reason}}`.
        # Düz liste hâli remedy'yi bize tahmin ettiriyordu; artık beyan eden o.
        describe=lambda: {
            "schema_gaps": {
                "dns_query_name": {"remedy": "schema", "reason": "hiçbir parser üretmiyor"},
                "query": {"remedy": "schema", "reason": "hiçbir parser üretmiyor"},
            }
        },
        FIELD_MAP={"action": "activity_name"},
        VENDOR_EMPTY_COLUMNS={"routeros": {"activity_name": "parser bilerek boş bırakıyor"}},
        rule_fields=lambda text: ["dns_query_name"] if "dns_query_name" in text else ["dstport"],
    )
    return module


def hazirla(monkeypatch, tmp_path: Path, *, kural: str = KURAL, pipeline=None):
    rules = tmp_path / "catalog" / "sigma" / "rules"
    rules.mkdir(parents=True)
    (rules / "deneme.yml").write_text(kural, encoding="utf-8")

    monkeypatch.setattr(compile_module, "_rule_files", lambda: [rules / "deneme.yml"])
    monkeypatch.setattr(compile_module, "_assert_environment_matches_pin", lambda: None)
    monkeypatch.setattr(compile_module, "_load_pipeline", lambda: pipeline or sahte_pipeline())


def test_derlenen_kural_written(monkeypatch, tmp_path):
    hazirla(monkeypatch, tmp_path)
    (outcome,) = compile_module.collect_outcomes()
    assert outcome.status == STATUS_WRITTEN
    assert outcome.rule_id == "11111111-0000-4000-8000-000000000000"
    assert outcome.title == "Deneme"
    assert outcome.logsource == {"category": "firewall", "product": "fortigate"}


def test_bilincli_red_gated_failed_degil(monkeypatch, tmp_path):
    """T31'in `SigmaTransformationError`'ı bir iş kalemi, kırıklık değil.

    `failed` sabit sıfır olmalı; eşlenemeyen bir alanı oraya yazmak o sabiti
    anlamsızlaştırır ve yol haritasından bir kalem siler.
    """
    from sigma.exceptions import SigmaTransformationError

    kural = KURAL.replace("dstport: 445", "dns_query_name: '.tunnel.'")
    hazirla(
        monkeypatch, tmp_path, kural=kural,
        pipeline=sahte_pipeline(hata=SigmaTransformationError("`dns_query_name` eşlenemiyor")),
    )

    (outcome,) = compile_module.collect_outcomes()
    assert outcome.status == STATUS_GATED
    assert [b.column for b in outcome.blockers] == ["dns_query_name"]
    # `remedy` T31'in beyanından geliyor, bizim tahminimizden değil.
    assert outcome.blockers[0].remedy == "schema"


def test_gercek_hata_failed(monkeypatch, tmp_path):
    """Bilinçli red dışındaki her şey pipeline kırıklığı."""
    hazirla(monkeypatch, tmp_path, pipeline=sahte_pipeline(hata=RuntimeError("beklenmedik")))

    (outcome,) = compile_module.collect_outcomes()
    assert outcome.status == STATUS_FAILED
    assert "beklenmedik" in (outcome.error or "")


def test_alan_cikarilamazsa_sebep_yutulmuyor(monkeypatch, tmp_path):
    """Hangi alan olduğu bilinmiyorsa ham metin engelin içinde kalıyor.

    Çözünürlük kaybı kabul edilebilir; sessiz kayıp değil.
    """
    from sigma.exceptions import SigmaTransformationError

    pipeline = sahte_pipeline(hata=SigmaTransformationError("tanınmayan bir sebep"))
    pipeline.describe = lambda: {"schema_gaps": {}}
    pipeline.VENDOR_EMPTY_COLUMNS = {}
    hazirla(monkeypatch, tmp_path, pipeline=pipeline)

    (outcome,) = compile_module.collect_outcomes()
    assert outcome.status == STATUS_GATED
    assert outcome.blockers[0].kind == "unsupported_construct"
    assert "tanınmayan bir sebep" in (outcome.blockers[0].detail or "")


def test_vendorda_hep_bos_kolon_alan_adiyla_raporlaniyor(monkeypatch, tmp_path):
    """`VENDOR_EMPTY_COLUMNS` **eşlenmiş kolon adıyla** anahtarlı (`activity_name`),
    kural ise Sigma adını taşıyor (`action`). Eşleme yapılmadan kesişim boş çıkar
    ve engel sessizce `unknown`'a düşer — ölçüldü, `routeros_drop_input` tam
    olarak öyle düşüyordu.

    Raporlanan ad Sigma adı, çünkü kuralı düzeltecek kişinin değiştireceği o.
    """
    from sigma.exceptions import SigmaTransformationError

    kural = KURAL.replace("dstport: 445", "action: 'drop'")
    pipeline = sahte_pipeline(hata=SigmaTransformationError("`activity_name` her zaman BOŞ"))
    pipeline.rule_fields = lambda text: ["action"]
    hazirla(monkeypatch, tmp_path, kural=kural, pipeline=pipeline)

    (outcome,) = compile_module.collect_outcomes()
    assert outcome.status == STATUS_GATED
    assert [b.column for b in outcome.blockers] == ["action"]
    assert outcome.blockers[0].remedy == "schema"
    assert "activity_name" in outcome.blockers[0].message


def test_bos_korpus_bos_liste(monkeypatch):
    """Kural yoksa sıfır kural — doğru cevap, hata değil."""
    monkeypatch.setattr(compile_module, "_rule_files", lambda: [])
    assert compile_module.collect_outcomes() == []


def test_korpus_doluyken_eksik_kurulum_atiyor(monkeypatch, tmp_path):
    """Sessizce sıfır kural üretmek, `--write` koşturan birinin çıktıyı silmesi olurdu."""
    rules = tmp_path / "rules"
    rules.mkdir()
    (rules / "a.yml").write_text(KURAL, encoding="utf-8")
    monkeypatch.setattr(compile_module, "_rule_files", lambda: [rules / "a.yml"])
    monkeypatch.setattr(compile_module, "_installed_version", lambda name: None)

    with pytest.raises(RuntimeError, match="kurulu değil"):
        compile_module.collect_outcomes()


def test_bayat_civiyle_yazma_reddediliyor(monkeypatch, tmp_path, capsys):
    """Yarım hâl — kurallar yeni, çivi eski — bu turda CI'ı kırmızı yaktı.

    `--write` o gün hiçbir şey söylemeden yeni SQL üretti; tutarsızlığı ancak
    çivi kapısı, bir sonraki koşumda söyledi. Komut artık kendi girdisinin
    tutarlı olduğunu görmeden yazmıyor.
    """
    from sigma_build import compile as compile_module

    monkeypatch.setattr("sigma_build.ruleset.verify", lambda catalog: ["değişmiş: x.yml"])
    kod = compile_module.main(["--write", "--output", str(tmp_path / "cikti")])

    assert kod == 1
    assert not (tmp_path / "cikti").exists()
    assert "ruleset --refresh" in capsys.readouterr().err
