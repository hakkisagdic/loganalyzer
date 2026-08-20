import sys
from pathlib import Path

# `sigma_build` paketini kurulum gerektirmeden import edilebilir kıl.
# (`sidecar/tests/conftest.py` ile aynı desen.)
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
