import os
import sys
from pathlib import Path

SIDECAR_DIR = Path(__file__).resolve().parents[1]
REPO_ROOT = SIDECAR_DIR.parent
MASKS_PATH = REPO_ROOT / "catalog" / "masks" / "bizigo-masks.yaml"
MAPPINGS_PATH = REPO_ROOT / "catalog" / "mappings"

# İmajda `/app/masks/...`, testte repo kökü. Tek kaynak aynı dosya.
os.environ.setdefault("BIZIGO_MASKS_PATH", str(MASKS_PATH))
# Aynısı Sigma değer sözlükleri için (T31). Bu satır **eksikti** ve CI'yı
# kırdı: `/v1/sigma/compile` `ip_proto_name.yaml`'ı imaj yolundan okuyor,
# testte o yol yok, ve uç 422 dönüyordu.
#
# Yerelde görünmedi çünkü paketi elle `BIZIGO_MAPPINGS_PATH=... pytest` diye
# koşturuyordum — yani ortamı komut satırında kurup koşum düzenine
# yazmamıştım. "Bende yeşildi" tam olarak bu.
os.environ.setdefault("BIZIGO_MAPPINGS_PATH", str(MAPPINGS_PATH))
# Testlerde Redis yok: sidecar'ın "kalıcılık olmadan da çalış" yolu da
# böylece her koşuda sınanmış oluyor.
os.environ.setdefault("REDIS_URL", "redis://127.0.0.1:1/0")

sys.path.insert(0, str(SIDECAR_DIR))
