"""Derleme hattının pySigma sürümleri sidecar'ınkilerle aynı mı (T32).

Bu test bir **kopyanın bekçisi**. `tools/sigma-build/requirements.txt`
sidecar'ınkine `-r` ile işaret etmiyor: o dosya drain3'ü bir git SHA'sından
çekiyor ve Sigma derleme hattını üçüncü taraf bir deponun erişilebilirliğine
bağlamak olurdu (`ci.yml`, sidecar işinin notu).

Bedeli iki dosyada aynı sürümü tutmak; karşılığı bu test. Ayrıştıkları gün
kırmızı yanan bir şey olmasaydı, sidecar'ın `/v1/sigma/compile` ucu UI'da bir
SQL gösterirken build-time başka bir SQL üretirdi ve **hiçbir şey bunu
söylemezdi.**
"""

from __future__ import annotations

import re
from pathlib import Path

from sigma_build.view_columns import repo_root

#: İki tarafta da aynı olması gereken paketler. Sidecar'ın geri kalanı
#: (drain3, fastapi, redis) derleme hattını ilgilendirmiyor.
SHARED = ("pySigma", "pysigma-backend-clickhouse", "PyYAML")


def pins(path: Path) -> dict[str, str]:
    found: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^([A-Za-z0-9_.-]+)==(\S+)\s*$", line.strip())
        if match:
            found[match.group(1)] = match.group(2)
    return found


def test_paylasilan_surumler_sidecar_ile_ayni():
    root = repo_root()
    ours = pins(root / "tools" / "sigma-build" / "requirements.txt")
    theirs = pins(root / "sidecar" / "requirements.txt")

    for package in SHARED:
        assert package in ours, f"{package} derleme hattında sabitlenmemiş"
        assert package in theirs, f"{package} sidecar'da sabitlenmemiş"
        assert ours[package] == theirs[package], (
            f"{package} ayrışmış: derleme hattı {ours[package]}, sidecar {theirs[package]}. "
            "UI'nın önizlediği SQL ile build-time üretilen SQL farklı olur."
        )


def test_derleme_hatti_drain3_cekmiyor():
    """Ağ, kapının gerekçesi olamaz — drain3 bir git SHA'sından iniyor.

    Ölçüt **satır düzeyinde**: yorumda drain3'ten söz etmek serbest, hatta
    gerekli (gerekçe orada yazılı). Yasak olan onu bir bağımlılık satırı olarak
    yazmak. İlk hâli bütün metne bakıyordu ve kendi gerekçe yorumunu bağımlılık
    sandı — testin kendisi tarafından yakalandı.
    """
    lines = [
        line.strip()
        for line in (repo_root() / "tools" / "sigma-build" / "requirements.txt")
        .read_text(encoding="utf-8")
        .splitlines()
        if line.strip() and not line.strip().startswith("#")
    ]
    assert not any("drain3" in line for line in lines), lines
    assert not any("git+" in line for line in lines), lines
    assert not any(line.startswith("-r ") for line in lines), lines


# --------------------------------------------------------------------------- #
# Manifest ortama değil çiviye bağlı
# --------------------------------------------------------------------------- #

def test_manifest_surumleri_cividen_okuyor():
    """Kurulu sürümü yazmak kriter A'yı kırardı.

    Manifest o zaman ortama duyarlı olur: pySigma'sız bir makinede `null`,
    başka sürümlü bir makinede başka şey — yani "aynı girdi, aynı SQL" iddiası
    **makineye** bağlanır. Girdinin tanımı çivi.
    """
    from sigma_build.compile import current_header

    ours = pins(repo_root() / "tools" / "sigma-build" / "requirements.txt")
    header = current_header()
    assert header.pysigma_version == ours["pySigma"]
    assert header.backend_version == ours["pysigma-backend-clickhouse"]


def test_ayrismis_ortam_derleme_aninda_atiyor(monkeypatch):
    """Kurulu sürüm çividen farklıysa üretilen SQL çivinin vaat ettiği değildir.

    Manifest bunu gösteremez — çünkü manifest çiviyi yazıyor. Tek doğru davranış
    derleme anında durmak.
    """
    import pytest

    from sigma_build import compile as compile_module

    monkeypatch.setattr(compile_module, "_installed_version", lambda name: "0.0.1")
    with pytest.raises(RuntimeError, match="ayrışmış"):
        compile_module._assert_environment_matches_pin()


def test_kurulu_olmayan_pysigma_atiyor(monkeypatch):
    """Sessizce sıfır kural üretmek, `--write` koşturan birinin çıktıyı silmesi olurdu."""
    import pytest

    from sigma_build import compile as compile_module

    monkeypatch.setattr(compile_module, "_installed_version", lambda name: None)
    with pytest.raises(RuntimeError, match="kurulu değil"):
        compile_module._assert_environment_matches_pin()
