"""Analiz sidecar'ı — Drain3 (template mining) + pySigma (kural derleme).

T01: yalnızca sağlık uçları. Gerçek uçlar T12'de.

Taşıyıcı kısıt (K14): bu servis SICAK YOLDA DEĞİL. Ölürse ingest çalışmaya devam
eder; yalnızca format keşfi devre dışı kalır. .NET tarafı buraya sert bağımlılık
kurmaz — sınırlı kuyruk, devre kesici, 2 sn zaman aşımı.
"""

import os

from fastapi import FastAPI

app = FastAPI(title="bizigo-sidecar", version="0.1.0")

REDIS_URL = os.environ.get("REDIS_URL", "redis://localhost:6379/0")


@app.get("/healthz")
def healthz() -> dict[str, str]:
    return {"status": "ok", "phase": "T01-iskelet"}


@app.get("/readyz")
def readyz() -> dict[str, str]:
    # T12: Redis bağlantısı ve miner durumu burada gerçekten kontrol edilecek.
    return {"status": "ok", "redis": REDIS_URL}
