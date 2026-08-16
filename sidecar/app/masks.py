"""Maskeleme sözlüğünün Python tarafı.

Tek kaynak `catalog/masks/bizigo-masks.yaml` (K14 maskeleme sinerjisi).
Burada yapılan tek iş onu Drain3'ün `MaskingInstruction` listesine çevirmek —
regex'ler burada **tanımlanmaz**, aksi halde .NET tarafıyla sessizce ayrışır.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

import yaml
from drain3.masking import MaskingInstruction


@dataclass(frozen=True)
class MaskDefinition:
    name: str
    regex: str
    compiled: re.Pattern[str]


@dataclass(frozen=True)
class MaskCatalog:
    version: int
    mask_prefix: str
    mask_suffix: str
    masks: Sequence[MaskDefinition]
    golden: Sequence[dict[str, str]]
    source_path: str

    @property
    def names(self) -> list[str]:
        return [m.name for m in self.masks]

    def instructions(self) -> list[MaskingInstruction]:
        return [MaskingInstruction(m.regex, m.name) for m in self.masks]

    def mask(self, text: str) -> str:
        """Drain3'ün `LogMasker`'ıyla birebir aynı işlem: sırayla `re.sub`.

        Ayrı bir uygulama değil, aynı davranışın test edilebilir kopyası —
        `LogMasker.mask` de tam olarak bunu yapıyor (masking.py).
        """
        for definition in self.masks:
            text = definition.compiled.sub(
                f"{self.mask_prefix}{definition.name}{self.mask_suffix}", text
            )
        return text


def load_masks(path: Path) -> MaskCatalog:
    if not path.is_file():
        raise FileNotFoundError(
            f"Maskeleme sözlüğü bulunamadı: {path}. "
            "İmaja `catalog/masks/` kopyalanmamış olabilir (BIZIGO_MASKS_PATH)."
        )

    document: Any = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError(f"{path}: kök öğe sözlük olmalı.")

    entries = document.get("masks") or []
    if not entries:
        raise ValueError(f"{path}: `masks` boş — maskesiz mining şablonları işe yaramaz.")

    masks: list[MaskDefinition] = []
    seen: set[str] = set()

    for index, entry in enumerate(entries):
        name = str(entry.get("name", "")).strip()
        regex = entry.get("regex")

        if not name:
            raise ValueError(f"{path}: {index}. maskenin `name` alanı boş.")
        if not regex:
            raise ValueError(f"{path}: '{name}' maskesinin `regex` alanı boş.")
        if name in seen:
            # Drain3 aynı `mask_with` ile birden fazla talimatı kabul ediyor ama
            # parametre çıkarımı belirsizleşiyor (extract_parameters uyarısı).
            raise ValueError(f"{path}: '{name}' maskesi iki kez tanımlı.")

        seen.add(name)
        masks.append(MaskDefinition(name=name, regex=regex, compiled=re.compile(regex)))

    return MaskCatalog(
        version=int(document.get("version", 0)),
        mask_prefix=str(document.get("mask_prefix", "<")),
        mask_suffix=str(document.get("mask_suffix", ">")),
        masks=masks,
        golden=list(document.get("golden") or []),
        source_path=str(path),
    )
