"""Sigma canlı beklenti kümesinin boyutu sessizce sürüklenmesin (T32)."""

from __future__ import annotations

import collections
import json

from sigma_build.golden_gate import EXPECTATIONS_PATH
from sigma_build.view_columns import repo_root

EXPECTED_TOTAL = 19
EXPECTED_AT_LEAST_ONE = 7
EXPECTED_NONE = 12
EXPECTED_UNDECLARED = 1


def _document() -> dict:
    return json.loads((repo_root() / EXPECTATIONS_PATH).read_text(encoding="utf-8"))


def test_beyan_sayisi_civili():
    entries = _document()["expectations"]
    assert len(entries) == EXPECTED_TOTAL, (
        f"Beyan kümesi {len(entries)} kalem taşıyor, çivili sayı {EXPECTED_TOTAL}. "
        "Küme bilerek büyüdüyse bu sabiti de güncelleyin."
    )


def test_iki_sinifin_dagilimi_civili():
    distribution = collections.Counter(e["expect"] for e in _document()["expectations"])
    assert distribution["at_least_one"] == EXPECTED_AT_LEAST_ONE
    assert distribution["none"] == EXPECTED_NONE
    assert set(distribution) == {"at_least_one", "none"}


def test_beyansiz_kural_sayisi_civili():
    undeclared = _document()["undeclared"]
    assert len(undeclared) == EXPECTED_UNDECLARED
    for entry in undeclared:
        assert entry.get("why", "").strip(), f"`{entry['rule_id']}` gerekçesiz beyansız."


def test_beyan_dosyasi_uretilen_sqlle_ortusuyor():
    root = repo_root()
    missing = [
        e["file_name"]
        for e in _document()["expectations"]
        if not (root / "detections" / "sigma" / e["file_name"]).is_file()
    ]
    assert not missing, "Beyan var ama üretilen SQL yok:\n  " + "\n  ".join(missing)
