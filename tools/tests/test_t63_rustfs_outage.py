from __future__ import annotations

import importlib.util
import struct
import sys
import unittest
import zlib
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "t63-rustfs-outage-olcumu.py"
SPEC = importlib.util.spec_from_file_location("t63_rustfs_outage", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class T63RustFsOutageTests(unittest.TestCase):
    def test_only_new_or_grown_segments_belong_to_the_run(self) -> None:
        before = {"wal-0000000001.log": 100, "wal-0000000002.log": 200}
        after = {
            "wal-0000000001.log": 100,
            "wal-0000000002.log": 260,
            "wal-0000000003.log": 90,
        }

        self.assertEqual(
            ["wal-0000000002.log", "wal-0000000003.log"],
            MODULE.degisen_segmentler(before, after),
        )

    def test_unrelated_verified_manifest_cannot_satisfy_the_run(self) -> None:
        # Sorgu artık yalnız bu koşumun segmentlerini özetlediği için ilgisiz
        # küresel +1 burada sıfır görünür ve kapı kırmızı yanar.
        unrelated = MODULE.ManifestOzeti(0, 0, 0, 0, 0)

        problems = MODULE.toparlanma_sorunlari(
            ["wal-0000000042.log"],
            12,
            unrelated,
        )

        self.assertTrue(problems)
        self.assertTrue(any("segment eşleşmesi" in problem for problem in problems))
        self.assertTrue(any("WAL 12" in problem for problem in problems))

    def test_exact_segment_and_event_count_pass(self) -> None:
        summary = MODULE.ManifestOzeti(1, 1, 1, 0, 12)

        self.assertEqual(
            [],
            MODULE.toparlanma_sorunlari(["wal-0000000042.log"], 12, summary),
        )

    def test_wal_frames_are_counted_from_their_real_payloads(self) -> None:
        payloads = [b'{"id":1}\n{"id":2}\n', b'{"id":3}\n']
        frames = []
        for payload in payloads:
            frames.append(
                struct.pack(">III", MODULE.WAL_MAGIC, len(payload), zlib.crc32(payload) & 0xFFFFFFFF)
                + payload
            )

        self.assertEqual(3, MODULE.wal_verisi_kayit_sayisi(b"".join(frames)))

    def test_corrupt_wal_frame_is_not_accepted_as_evidence(self) -> None:
        payload = b'{"id":1}\n'
        frame = struct.pack(">III", MODULE.WAL_MAGIC, len(payload), 0) + payload

        with self.assertRaisesRegex(ValueError, "CRC"):
            MODULE.wal_verisi_kayit_sayisi(frame)


if __name__ == "__main__":
    unittest.main()
