"""Sigma kural korpusunun **tek** kopyası var mı (T32).

Bu bekçi ölçülmüş bir olaydan doğdu. Korpus `catalog/sigma/rules/`'a terfi
ettirildi; aynı sırada başka bir ajan `routeros_forward_new.yml`'i eski yerinde
(`prototypes/t30-sigma/rules/`) düzeltti — `action` yerine `fw_chain`. İkisi de
doğru davrandı, kimse diğerinin dizinini bilmiyordu.

Sonuç ölçüldü: derleme hattı **düzeltilmemiş** kopyayı derledi, Kapı 3 iki koşum
boyunca eski SQL'i sınadı, ve hiçbir şey bunu söylemedi. `measure.py` de hâlâ
eski dizini okuyordu, yani "aynı kural setinin iki ölçümü" diye okunan sayılar
aslında **iki farklı korpusun** ölçümüydü.

`CLAUDE.md` §9 bunu zaten yasaklıyor ("ikinci kopya yazma"), ama yasak bir
cümleydi ve cümleler sessizce ihlal edilir. Bu test onu kırmızı yanabilir hâle
getiriyor.

Neden `.yml` uzantısına değil **içeriğe** bakıyor: kopya bir gün başka bir adla
başka bir dizine düşerse, ada bakan bir bekçi onu görmez. Sigma kuralının
tanımı `detection:` + `logsource:` taşıyan bir YAML; ölçüt bu.
"""

from __future__ import annotations

import re
from pathlib import Path

from sigma_build.ruleset import CATALOG_DIR, RULES_SUBDIR
from sigma_build.view_columns import repo_root

#: Taranmayan dizinler — üretilen çıktı, bağımlılıklar, git.
SKIP = {".git", "node_modules", ".venv", "obj", "bin", "detections", ".pytest_cache"}

_DETECTION = re.compile(r"^detection:", re.MULTILINE)
_LOGSOURCE = re.compile(r"^logsource:", re.MULTILINE)


def sigma_rule_files(root: Path) -> list[Path]:
    """`detection:` VE `logsource:` taşıyan bütün YAML'lar."""
    found: list[Path] = []
    for path in root.rglob("*.y*ml"):
        if any(part in SKIP for part in path.parts):
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        if _DETECTION.search(text) and _LOGSOURCE.search(text):
            found.append(path)
    return found


def test_sigma_kurallari_yalnizca_katalogda():
    """Korpusun ikinci bir kopyası olamaz.

    Kırmızı yandığında yapılacak şey kopyayı **silmek**, bekçiyi genişletmek
    değil: iki kopya bir gün ayrışır ve ayrıştıkları gün hiçbir şey söylemez.
    """
    root = repo_root()
    corpus = (root / CATALOG_DIR / RULES_SUBDIR).resolve()

    disarida = sorted(
        path.relative_to(root).as_posix()
        for path in sigma_rule_files(root)
        if corpus not in path.resolve().parents
    )
    assert disarida == [], (
        f"Korpus dışında Sigma kuralı bulundu: {disarida}. "
        f"Tek kaynak {CATALOG_DIR / RULES_SUBDIR}; kopyayı silin."
    )


def test_korpus_bos_degil():
    """Bekçinin kendisi boş bir depoda da yeşil yanardı.

    "Korpus dışında kural yok" iddiası, hiç kural olmadığında da doğru. O yüzden
    korpusun dolu olduğu ayrıca sınanıyor — yoksa bu dosya bir gün hiçbir şey
    ölçmeyen bir test olur ve yeşilliği bir şey ifade etmez.
    """
    corpus = repo_root() / CATALOG_DIR / RULES_SUBDIR
    assert len(sigma_rule_files(corpus)) >= 20
