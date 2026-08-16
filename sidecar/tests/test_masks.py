"""Maskeleme sözlüğü — tek kaynağın Python tarafı.

Buradaki `golden` koşusunun ikizi .NET'te (`MaskCatalogTests`). Aynı dosya,
aynı girdi, aynı beklenen çıktı. Bir regex iki motorda ayrışırsa ikisinden
biri kırmızıya döner; sessizce sürüklenemez — ki sürüklenirse .NET'in ürettiği
imza Drain3'ün kümesiyle örtüşmez ve `template_id` yanlış olur.
"""

from pathlib import Path

import pytest
from drain3.masking import LogMasker

from app.masks import load_masks
from conftest import MASKS_PATH


@pytest.fixture(scope="module")
def catalog():
    return load_masks(MASKS_PATH)


def test_sozluk_yuklendi(catalog):
    assert catalog.version >= 1
    assert len(catalog.masks) >= 8
    assert catalog.mask_prefix == "<"
    assert catalog.mask_suffix == ">"


def test_golden_ornekleri_beklenen_ciktiyi_veriyor(catalog):
    assert catalog.golden, "golden bölümü boş — çapraz dil güvencesi kalmaz."

    for sample in catalog.golden:
        assert catalog.mask(sample["input"]) == sample["masked"], sample["input"]


def test_drain3_maskeleyicisi_ayni_sonucu_veriyor(catalog):
    """`MaskCatalog.mask` Drain3'ün `LogMasker`'ının kopyası olmalı.

    Kopya olmasaydı `/v1/mine/*` yanıtındaki `masked` alanı Drain3'ün gerçekte
    kümelediği metinden farklı olurdu ve .NET'in sapma sayacı yalan söylerdi.
    """
    masker = LogMasker(catalog.instructions(), catalog.mask_prefix, catalog.mask_suffix)

    for sample in catalog.golden:
        assert masker.mask(sample["input"]) == catalog.mask(sample["input"])


def test_maske_adlari_grok_kutuphanesinde_var(catalog):
    """`name` bir grok pattern adı olmak zorunda (K14 maskeleme sinerjisi)."""
    pattern_root = Path(MASKS_PATH).parents[1] / "patterns" / "legacy"
    known: set[str] = set()

    for path in pattern_root.rglob("*"):
        if not path.is_file() or path.suffix in {".md", ".txt"} or path.name == "LICENSE":
            continue
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if line and not line.startswith("#") and " " in line:
                known.add(line.split(" ", 1)[0])

    assert known, "grok pattern kütüphanesi okunamadı"

    missing = [name for name in catalog.names if name not in known]
    assert not missing, (
        f"Grok kütüphanesinde karşılığı olmayan maske adı: {missing}. "
        "Mask adı doğrudan grok taslağına dönüşüyor; karşılığı yoksa köprü kopuk."
    )


def test_sayilar_token_ici_maskelenmiyor(catalog):
    """`eth0`, `sda1`, `v2` şablonun bilgi taşıyan parçası — maskelenmemeli."""
    assert catalog.mask("link eth0 down on sda1 v2") == "link eth0 down on sda1 v2"


def test_saat_ipv6_sanilmiyor(catalog):
    assert "<IPV6>" not in catalog.mask("elapsed 10:11:12 total")
