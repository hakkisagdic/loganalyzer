import os
import sys
from pathlib import Path

SIDECAR_DIR = Path(__file__).resolve().parents[1]
REPO_ROOT = SIDECAR_DIR.parent
MASKS_PATH = REPO_ROOT / "catalog" / "masks" / "bizigo-masks.yaml"

# İmajda `/app/masks/...`, testte repo kökü. Tek kaynak aynı dosya.
os.environ.setdefault("BIZIGO_MASKS_PATH", str(MASKS_PATH))
# Testlerde Redis yok: sidecar'ın "kalıcılık olmadan da çalış" yolu da
# böylece her koşuda sınanmış oluyor.
os.environ.setdefault("REDIS_URL", "redis://127.0.0.1:1/0")

sys.path.insert(0, str(SIDECAR_DIR))
