"""Kural seti çivisi testleri — hepsi **ağsız** (T32)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from sigma_build.ruleset import (
    CATALOG_DIR,
    PIN_NAME,
    RULES_SUBDIR,
    Pin,
    load_pin,
    pin_text,
    verify,
)
from sigma_build.view_columns import repo_root

REPO_CATALOG = repo_root() / CATALOG_DIR


def make_catalog(tmp_path: Path, files: dict[str, str], *, commit: str | None = "abc123") -> Path:
    catalog = tmp_path / "sigma"
    rules = catalog / RULES_SUBDIR
    rules.mkdir(parents=True)

    import hashlib

    entries: dict[str, str] = {}
    for name, body in files.items():
        path = rules / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
        entries[name] = "sha256:" + hashlib.sha256(body.encode("utf-8")).hexdigest()

    pin = Pin(source="https://github.com/SigmaHQ/sigma", commit=commit, license="DRL-1.1", rules=entries)
    (catalog / PIN_NAME).write_text(pin_text(pin), encoding="utf-8")
    return catalog


def test_uyan_agac_sessiz(tmp_path):
    catalog = make_catalog(tmp_path, {"net/a.yml": "title: A\n", "net/b.yml": "title: B\n"})
    assert verify(catalog) == []


def test_eksik_dosya_yakalaniyor(tmp_path):
    catalog = make_catalog(tmp_path, {"net/a.yml": "title: A\n"})
    (catalog / RULES_SUBDIR / "net" / "a.yml").unlink()
    assert verify(catalog) == ["eksik (çivide var, diskte yok): net/a.yml"]


def test_fazla_dosya_yakalaniyor(tmp_path):
    """Çiviye girmemiş bir kural derlenir ama **nereden geldiği kayıtsız** olur."""
    catalog = make_catalog(tmp_path, {"net/a.yml": "title: A\n"})
    (catalog / RULES_SUBDIR / "net" / "kacak.yml").write_text("title: Kaçak\n", encoding="utf-8")
    assert verify(catalog) == ["fazla (diskte var, çivide yok): net/kacak.yml"]


def test_degismis_icerik_yakalaniyor(tmp_path):
    catalog = make_catalog(tmp_path, {"net/a.yml": "title: A\n"})
    (catalog / RULES_SUBDIR / "net" / "a.yml").write_text("title: Başkası\n", encoding="utf-8")
    assert verify(catalog) == ["değişmiş: net/a.yml"]


def test_uc_yon_ayri_raporlaniyor(tmp_path):
    """Üçünün cevabı farklı: yarım kopyalama, kayıtsız kural, elle düzenleme."""
    catalog = make_catalog(tmp_path, {"a.yml": "A\n", "b.yml": "B\n", "c.yml": "C\n"})
    (catalog / RULES_SUBDIR / "a.yml").unlink()
    (catalog / RULES_SUBDIR / "b.yml").write_text("başka\n", encoding="utf-8")
    (catalog / RULES_SUBDIR / "d.yml").write_text("D\n", encoding="utf-8")
    assert verify(catalog) == [
        "eksik (çivide var, diskte yok): a.yml",
        "fazla (diskte var, çivide yok): d.yml",
        "değişmiş: b.yml",
    ]


def test_civi_dosyasi_yoksa_hata(tmp_path):
    (tmp_path / "bos").mkdir()
    with pytest.raises(FileNotFoundError, match="Çivi dosyası yok"):
        load_pin(tmp_path / "bos")


def test_civi_metni_belirlenimci(tmp_path):
    """Çivi de bir üretilmiş dosya; sırası girdiye bağlı olamaz."""
    pin_a = Pin(source="s", commit="c", license="DRL-1.1", rules={"b.yml": "sha256:2", "a.yml": "sha256:1"})
    pin_b = Pin(source="s", commit="c", license="DRL-1.1", rules={"a.yml": "sha256:1", "b.yml": "sha256:2"})
    assert pin_text(pin_a) == pin_text(pin_b)


def test_civide_tarih_yok():
    """Manifest ve kural dosyalarıyla aynı gerekçe: kapı bayt karşılaştırıyor."""
    document = json.loads(pin_text(Pin(source="s", commit="c", license="DRL-1.1", rules={})))
    assert set(document) == {"_comment", "source", "commit", "license", "rules"}


# --------------------------------------------------------------------------- #
# Depodaki gerçek çivi
# --------------------------------------------------------------------------- #

def test_depodaki_civi_tutarli():
    """Bugün boş — ama doğrulama yine de koşuyor.

    Çivi mekanizması kapsam kararından bağımsız; kapsam da çiviyi beklemesin diye
    bugünden yerinde duruyor.
    """
    assert verify(REPO_CATALOG) == []


def test_civi_kaynagini_dogru_soyluyor():
    """Korpus **SigmaHQ'dan gelmiyor** ve çivi bunu söylemek zorunda.

    24 kural T30 prototipinden terfi ettirildi (koordinatör kararı). Çivi
    `source` alanında SigmaHQ yazsaydı, kapsamı okuyan biri bunları yukarı
    akıştan gelmiş sanır ve "SigmaHQ'nun %x'ini kapsıyoruz" diye okurdu —
    örneklem SigmaHQ'nun dağılımını temsil etmiyor (T30 bunu açıkça yazıyor).

    T30'un kapsam kararı geldiğinde çivi değişecek, kapı değişmeyecek.
    """
    pin = load_pin(REPO_CATALOG)
    assert "SigmaHQ" not in pin.source
    assert "bizigo" in pin.source
    assert pin.is_pinned
    assert len(pin.rules) == 24
