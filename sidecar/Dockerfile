# ============================================================================
# Analiz sidecar'ı — Drain3 + pySigma (K14)
#
# ⚠️ BUILD CONTEXT REPO KÖKÜ. Çünkü imaja iki ayrı dizinden dosya giriyor:
#      sidecar/        → uygulama
#      catalog/masks/  → maskeleme sözlüğü (tek kaynak, F1 §9)
#    Maskeleri sidecar'ın içine kopyalamak "tek kaynak" kuralını bozardı:
#    .NET tarafı aynı dosyayı okuyor ve ikisi ayrışırsa `template_id` sessizce
#    yanlış olur.
#
#    deploy/docker-compose.yml:
#      build:
#        context: ..
#        dockerfile: sidecar/Dockerfile
# ============================================================================

FROM python:3.13-slim AS builder

# drain3 git SHA'sından kuruluyor (bkz. requirements.txt) — pip'in git'e
# ihtiyacı var. Yalnızca builder katmanında; çalışma imajına geçmiyor.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

COPY sidecar/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt


FROM python:3.13-slim

WORKDIR /app
ENV PATH="/opt/venv/bin:$PATH" \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    BIZIGO_MASKS_PATH=/app/masks/bizigo-masks.yaml

COPY --from=builder /opt/venv /opt/venv

COPY sidecar/app ./app
COPY catalog/masks ./masks

# Kök olmayan kullanıcı: sidecar ayrıştırılamamış, yani güvenilmeyen metin
# işliyor. Yüzey ne kadar dar olursa o kadar iyi.
RUN useradd --system --uid 10002 --create-home sidecar && chown -R sidecar:sidecar /app
USER sidecar

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
