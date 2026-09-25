"""Synthetic generator failure fixture; invoked as an alternative interpreter."""
from pathlib import Path
import subprocess
import sys
import time

manifest = Path(sys.argv[sys.argv.index("--manifest") + 1])
root = manifest.parents[3]
if (root / "fail-generator").exists():
    sys.exit(17)
if (root / "bad-manifest").exists():
    manifest.write_text("{broken-json")
    sys.exit(0)
descendant = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(90)"])
(root / "generator-descendant").write_text(str(descendant.pid))
time.sleep(90)
