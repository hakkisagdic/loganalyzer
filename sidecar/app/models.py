"""HTTP sözleşmesi (F1 §9). Alan adları .NET istemcisiyle birebir."""

from __future__ import annotations

from typing import Literal, Optional

from pydantic import BaseModel, Field


class MineMessage(BaseModel):
    id: str = Field(min_length=1, max_length=128)
    text: str


class MineRequest(BaseModel):
    source_key: str = Field(min_length=1, max_length=128)
    messages: list[MineMessage]


class ExtractedParam(BaseModel):
    value: str
    mask: str


class MineResult(BaseModel):
    id: str
    template_id: Optional[str]
    template: Optional[str]
    params: list[ExtractedParam] = []
    is_new: bool = False

    # Sözleşmede yok, bilinçli ek: .NET tarafı imzayı **yerel** olarak da
    # hesaplıyor (aynı maske dosyası). İkisi ayrışırsa `template_id` sessizce
    # yanlış olurdu; istemci bu alanı kendi çıktısıyla karşılaştırıp sapmayı
    # sayaca yazıyor.
    masked: str


class MineResponse(BaseModel):
    api_version: str
    source_key: str
    masks_version: int
    cluster_count: int
    results: list[MineResult]


class ClusterEntry(BaseModel):
    template_id: str
    cluster_id: int
    template: str
    size: int
    mask_names: list[str]


class ClustersResponse(BaseModel):
    api_version: str
    source_key: str
    masks_version: int
    cluster_count: int
    max_clusters: int
    clusters: list[ClusterEntry]


class SigmaCompileRequest(BaseModel):
    rule_yaml: str = Field(min_length=1)
    target: Literal["clickhouse"] = "clickhouse"
    # F3'te önizleme bir görünüm üzerinde koşabilir; varsayılan gerçek tablo.
    table: Optional[str] = Field(default=None, max_length=128)


class SigmaCompileResponse(BaseModel):
    api_version: str
    target: str
    table: str
    sql: str
    queries: list[str]
    warnings: list[str]


class HealthResponse(BaseModel):
    status: str
    api_version: str
    masks_version: int
    mask_names: list[str]


class ReadyResponse(BaseModel):
    status: str
    api_version: str
    masks_version: int
    redis: str
    redis_error: Optional[str] = None
    max_clusters: int
    max_miners: int
    loaded_miners: int
    evicted_miners: int
    sigma_backend: str
