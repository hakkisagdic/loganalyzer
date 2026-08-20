"""Sidecar ayarları — hepsi ortam değişkeninden, hepsi görünür.

`max_clusters` **zorunlu** (K14): sınırsız bırakılan Drain3 ağ loglarında
bellek sızıntısı gibi davranır. Bu yüzden geçersiz bir değer sessizce
varsayılana düşmüyor, açılışta patlıyor.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

# Sözleşme sürümü (F1 §9). .NET istemcisi bunu `/healthz`'de görüp
# uyuşmazsa devre kesiciyi açıyor — yanlış sürümle konuşmak, hiç
# konuşmamaktan kötü.
API_VERSION = "v1"

DEFAULT_MASKS_PATH = "/app/masks/bizigo-masks.yaml"

#: `catalog/mappings/` — Sigma değer dönüşümleri buradan okunuyor, KOPYALANMIYOR.
#: Maskelerle aynı gerekçe: tablo iki yerde tutulsaydı ingest ile sessizce
#: ayrışırdı ve `proto: 6` içeren kural, ingest'in `tcp` yazdığı satırı bulamazdı.
DEFAULT_MAPPINGS_PATH = "/app/mappings"


def _int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return int(raw)
    except ValueError as exc:
        raise ValueError(f"{name} tam sayı olmalı, görülen: {raw!r}") from exc


def _float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return float(raw)
    except ValueError as exc:
        raise ValueError(f"{name} ondalık sayı olmalı, görülen: {raw!r}") from exc


@dataclass(frozen=True)
class Settings:
    redis_url: str = ""
    redis_key_prefix: str = "bizigo:drain3:"
    masks_path: Path = field(default_factory=lambda: Path(DEFAULT_MASKS_PATH))

    # Drain3
    max_clusters: int = 5_000
    sim_th: float = 0.4
    depth: int = 4
    max_children: int = 100
    snapshot_interval_minutes: int = 5

    # Sidecar
    max_miners: int = 64
    max_batch: int = 500

    # Sigma derlemesi — backend'in varsayılanları (`logs` / `full_log`) bizim
    # şemamıza uymuyor (db/clickhouse/0001_events.sql).
    sigma_table: str = "events_ocsf"
    sigma_full_log_column: str = "raw_data"
    sigma_mappings_path: Path = field(default_factory=lambda: Path(DEFAULT_MAPPINGS_PATH))

    @staticmethod
    def from_env() -> "Settings":
        settings = Settings(
            redis_url=os.environ.get("REDIS_URL", "redis://localhost:6379/0"),
            redis_key_prefix=os.environ.get("REDIS_KEY_PREFIX", "bizigo:drain3:"),
            masks_path=Path(os.environ.get("BIZIGO_MASKS_PATH", DEFAULT_MASKS_PATH)),
            max_clusters=_int("DRAIN_MAX_CLUSTERS", 5_000),
            sim_th=_float("DRAIN_SIM_TH", 0.4),
            depth=_int("DRAIN_DEPTH", 4),
            max_children=_int("DRAIN_MAX_CHILDREN", 100),
            snapshot_interval_minutes=_int("DRAIN_SNAPSHOT_INTERVAL_MINUTES", 5),
            max_miners=_int("SIDECAR_MAX_MINERS", 64),
            max_batch=_int("SIDECAR_MAX_BATCH", 500),
            sigma_table=os.environ.get("SIGMA_TABLE", "events_ocsf"),
            sigma_full_log_column=os.environ.get("SIGMA_FULL_LOG_COLUMN", "raw_data"),
            sigma_mappings_path=Path(
                os.environ.get("BIZIGO_MAPPINGS_PATH", DEFAULT_MAPPINGS_PATH)
            ),
        )
        settings.validate()
        return settings

    def validate(self) -> None:
        # K14: sınırsız küme yasak. 0/negatif "sınırsız" demek olurdu.
        if self.max_clusters <= 0:
            raise ValueError(
                "DRAIN_MAX_CLUSTERS pozitif olmalı. Sınırsız küme (K14) yasak: "
                "ağ loglarında bellek sızıntısı gibi davranıyor."
            )
        if not 0.0 < self.sim_th <= 1.0:
            raise ValueError("DRAIN_SIM_TH (0, 1] aralığında olmalı.")
        if self.depth < 3:
            # Drain'in kendi alt sınırı; altında ağaç anlamsızlaşıyor.
            raise ValueError("DRAIN_DEPTH en az 3 olmalı.")
        if self.max_miners <= 0:
            raise ValueError("SIDECAR_MAX_MINERS pozitif olmalı.")
        if self.max_batch <= 0:
            raise ValueError("SIDECAR_MAX_BATCH pozitif olmalı.")
