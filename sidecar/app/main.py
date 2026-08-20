"""Analiz sidecar'ı — Drain3 (template mining) + pySigma (kural derleme).

Taşıyıcı kısıt (K14): bu servis **SICAK YOLDA DEĞİL**. Ölürse ingest çalışmaya
devam eder; yalnızca format keşfi devre dışı kalır. .NET tarafı buraya sert
bağımlılık kurmaz — sınırlı kuyruk, devre kesici, 2 sn zaman aşımı
(bkz. `src/Bizigo.Ingest/Discovery/`).

Bu dosyanın tek işi HTTP sözleşmesi (F1 §9). Mining durumu `miners.py`'de,
maskeleme sözlüğü `catalog/masks/bizigo-masks.yaml`'de.
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager
from typing import Optional

import redis
from fastapi import FastAPI, HTTPException, Request

from .masks import MaskCatalog, load_masks
from .miners import MinerRegistry, template_id_for
from .models import (
    ClusterEntry,
    ClustersResponse,
    ExtractedParam,
    HealthResponse,
    MineRequest,
    MineResponse,
    MineResult,
    ReadyResponse,
    SigmaCompileRequest,
    SigmaCompileResponse,
)
from .settings import API_VERSION, Settings
from .sigma_compile import (
    SigmaBackendUnavailable,
    UnsupportedTarget,
    backend_name,
    compile_rule,
)

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
logger = logging.getLogger("bizigo.sidecar")


class SidecarState:
    settings: Settings
    catalog: MaskCatalog
    registry: MinerRegistry
    redis_client: Optional[redis.Redis]
    redis_error: Optional[str]


state = SidecarState()


def _connect_redis(url: str) -> tuple[Optional[redis.Redis], Optional[str]]:
    """Redis yoksa açılışı **düşürmüyoruz**.

    Kalıcılık kaybı, servisin hiç açılmamasından iyi: mining bellek içinde
    çalışmaya devam eder, `/readyz` durumu görünür kılar. Ayakta olan ama
    unutkan bir sidecar, olmayan bir sidecar'dan fazlasını veriyor.
    """
    try:
        client = redis.Redis.from_url(url, socket_connect_timeout=2, socket_timeout=2)
        client.ping()
        return client, None
    except Exception as exc:  # noqa: BLE001
        logger.warning("Redis'e bağlanılamadı (%s): %s — bellek içi devam ediliyor.", url, exc)
        return None, str(exc)


@asynccontextmanager
async def lifespan(app: FastAPI):
    state.settings = Settings.from_env()
    state.catalog = load_masks(state.settings.masks_path)
    state.redis_client, state.redis_error = _connect_redis(state.settings.redis_url)
    state.registry = MinerRegistry(state.settings, state.catalog, state.redis_client)

    logger.info(
        "Sidecar hazır: api=%s masks=v%d (%d maske, %s) max_clusters=%d max_miners=%d",
        API_VERSION,
        state.catalog.version,
        len(state.catalog.masks),
        state.catalog.source_path,
        state.settings.max_clusters,
        state.settings.max_miners,
    )

    yield

    state.registry.save_all()


app = FastAPI(title="bizigo-sidecar", version="1.0.0", lifespan=lifespan)


@app.middleware("http")
async def api_version_header(request: Request, call_next):
    """Sürüm her yanıtta. İstemci `/healthz`'i beklemeden uyuşmazlığı görebilsin."""
    response = await call_next(request)
    response.headers["X-Bizigo-Sidecar-Api"] = API_VERSION
    return response


# ---------------------------------------------------------------------------
# Sağlık
# ---------------------------------------------------------------------------


@app.get("/healthz", response_model=HealthResponse)
def healthz() -> HealthResponse:
    return HealthResponse(
        status="ok",
        api_version=API_VERSION,
        masks_version=state.catalog.version,
        mask_names=state.catalog.names,
    )


@app.get("/readyz", response_model=ReadyResponse)
def readyz() -> ReadyResponse:
    client = state.redis_client
    redis_status = "down"
    redis_error = state.redis_error

    if client is not None:
        try:
            client.ping()
            redis_status = "up"
            redis_error = None
        except Exception as exc:  # noqa: BLE001
            redis_error = str(exc)

    # `degraded` bilinçli: Redis yokken servis **hazır**, yalnızca unutkan.
    # 503 dönmek orkestratöre konteyneri yeniden başlattırır ve öğrenilenin
    # tamamı da o sırada gider.
    return ReadyResponse(
        status="ok" if redis_status == "up" else "degraded",
        api_version=API_VERSION,
        masks_version=state.catalog.version,
        redis=redis_status,
        redis_error=redis_error,
        max_clusters=state.settings.max_clusters,
        max_miners=state.settings.max_miners,
        loaded_miners=len(state.registry.loaded_miners),
        evicted_miners=state.registry.evicted_miners,
        sigma_backend=backend_name(),
    )


# ---------------------------------------------------------------------------
# Mining
# ---------------------------------------------------------------------------


def _check_batch(request: MineRequest) -> None:
    if not request.messages:
        raise HTTPException(status_code=400, detail="messages boş.")
    if len(request.messages) > state.settings.max_batch:
        raise HTTPException(
            status_code=413,
            detail=f"Toplu istek sınırı {state.settings.max_batch}; "
            f"{len(request.messages)} mesaj gönderildi.",
        )


def _params(miner, template: str, text: str) -> list[ExtractedParam]:
    extracted = miner.extract_parameters(template, text, exact_matching=True)
    if not extracted:
        return []
    return [ExtractedParam(value=p.value, mask=p.mask_name) for p in extracted]


@app.post("/v1/mine/batch", response_model=MineResponse)
def mine_batch(request: MineRequest) -> MineResponse:
    """Öğrenerek eşleştirir (`add_log_message`). Keşif turunun ucu."""
    _check_batch(request)

    handle = state.registry.get(request.source_key)
    results: list[MineResult] = []

    with handle.lock:
        for message in request.messages:
            outcome = handle.miner.add_log_message(message.text)
            template = str(outcome["template_mined"])
            cluster_id = int(outcome["cluster_id"])

            results.append(
                MineResult(
                    id=message.id,
                    template_id=template_id_for(request.source_key, cluster_id),
                    template=template,
                    params=_params(handle.miner, template, message.text),
                    is_new=outcome["change_type"] == "cluster_created",
                    masked=state.catalog.mask(message.text),
                )
            )

        cluster_count = len(handle.miner.drain.clusters)

    return MineResponse(
        api_version=API_VERSION,
        source_key=request.source_key,
        masks_version=state.catalog.version,
        cluster_count=cluster_count,
        results=results,
    )


@app.post("/v1/mine/match", response_model=MineResponse)
def mine_match(request: MineRequest) -> MineResponse:
    """Öğrenmeden eşleştirir (`match`). Yeni küme yaratmaz, sayaç değiştirmez."""
    _check_batch(request)

    handle = state.registry.get(request.source_key)
    results: list[MineResult] = []

    with handle.lock:
        for message in request.messages:
            # "fallback": ağaç araması boş dönerse aynı token sayısındaki
            # kümelerde doğrusal arama. "never" hızlı ama yanlış negatif
            # üretiyor ve burada yanlış negatif = gereksiz keşif turu.
            cluster = handle.miner.match(message.text, full_search_strategy="fallback")
            template = cluster.get_template() if cluster else None

            results.append(
                MineResult(
                    id=message.id,
                    template_id=(
                        template_id_for(request.source_key, cluster.cluster_id)
                        if cluster
                        else None
                    ),
                    template=template,
                    params=_params(handle.miner, template, message.text) if template else [],
                    is_new=False,
                    masked=state.catalog.mask(message.text),
                )
            )

        cluster_count = len(handle.miner.drain.clusters)

    return MineResponse(
        api_version=API_VERSION,
        source_key=request.source_key,
        masks_version=state.catalog.version,
        cluster_count=cluster_count,
        results=results,
    )


@app.get("/v1/clusters/{source_key}", response_model=ClustersResponse)
def clusters(source_key: str) -> ClustersResponse:
    handle = state.registry.get(source_key)

    with handle.lock:
        entries = [
            ClusterEntry(
                template_id=template_id_for(source_key, cluster.cluster_id),
                cluster_id=cluster.cluster_id,
                template=cluster.get_template(),
                size=cluster.size,
                # Şablonda geçen maske adları — F4'te grok taslağının iskeleti
                # bu listeden çıkıyor.
                mask_names=[
                    name
                    for name in state.catalog.names
                    if f"{state.catalog.mask_prefix}{name}{state.catalog.mask_suffix}"
                    in cluster.get_template()
                ],
            )
            for cluster in handle.miner.drain.clusters
        ]

    entries.sort(key=lambda e: e.size, reverse=True)

    return ClustersResponse(
        api_version=API_VERSION,
        source_key=source_key,
        masks_version=state.catalog.version,
        cluster_count=len(entries),
        max_clusters=state.settings.max_clusters,
        clusters=entries,
    )


# ---------------------------------------------------------------------------
# Sigma
# ---------------------------------------------------------------------------


@app.post("/v1/sigma/compile", response_model=SigmaCompileResponse)
def sigma_compile(request: SigmaCompileRequest) -> SigmaCompileResponse:
    table = request.table or state.settings.sigma_table
    try:
        compiled = compile_rule(
            request.rule_yaml,
            request.target,
            table_name=table,
            full_log_column=state.settings.sigma_full_log_column,
            mappings_path=str(state.settings.sigma_mappings_path),
        )
    except SigmaBackendUnavailable as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    except UnsupportedTarget as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:  # noqa: BLE001 — kural hataları kullanıcı hatası
        raise HTTPException(
            status_code=422, detail=f"Kural derlenemedi: {type(exc).__name__}: {exc}"
        ) from exc

    return SigmaCompileResponse(
        api_version=API_VERSION,
        target=request.target,
        table=table,
        sql=";\n\n".join(compiled.queries),
        queries=compiled.queries,
        warnings=compiled.warnings,
    )
