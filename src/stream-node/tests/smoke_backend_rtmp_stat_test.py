"""Regression tests for nginx-rtmp publisher activity detection."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
import unittest


_SMOKE_SCRIPT = Path(__file__).resolve().parents[3] / "scripts" / "smoke-backend.py"
_SPEC = importlib.util.spec_from_file_location("smoke_backend_for_rtmp_stat_tests", _SMOKE_SCRIPT)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError(f"Unable to load smoke script at {_SMOKE_SCRIPT}")
_smoke_backend = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = _smoke_backend
_SPEC.loader.exec_module(_smoke_backend)


class RtmpStatHasActiveStreamTests(unittest.TestCase):
    def test_accepts_positive_root_counters_without_stream_node(self) -> None:
        document = b"""\
<rtmp>
  <naccepted>1</naccepted>
  <bytes_in>1024</bytes_in>
</rtmp>
"""

        self.assertTrue(_smoke_backend.rtmp_stat_has_active_stream(document))

    def test_rejects_non_positive_or_missing_root_counters(self) -> None:
        cases = (
            (
                "zero accepted connections",
                b"<rtmp><naccepted>0</naccepted><bytes_in>1024</bytes_in></rtmp>",
            ),
            (
                "zero ingress bytes",
                b"<rtmp><naccepted>1</naccepted><bytes_in>0</bytes_in></rtmp>",
            ),
            (
                "negative accepted connections",
                b"<rtmp><naccepted>-1</naccepted><bytes_in>1024</bytes_in></rtmp>",
            ),
            (
                "missing accepted connections",
                b"<rtmp><bytes_in>1024</bytes_in></rtmp>",
            ),
            (
                "missing ingress bytes",
                b"<rtmp><naccepted>1</naccepted></rtmp>",
            ),
        )

        for name, document in cases:
            with self.subTest(name=name):
                self.assertFalse(_smoke_backend.rtmp_stat_has_active_stream(document))

    def test_rejects_non_numeric_counter(self) -> None:
        document = b"<rtmp><naccepted>one</naccepted><bytes_in>1024</bytes_in></rtmp>"

        self.assertFalse(_smoke_backend.rtmp_stat_has_active_stream(document))

    def test_maps_malformed_xml_to_redacted_operation_error(self) -> None:
        with self.assertRaises(_smoke_backend.OperationError) as raised:
            _smoke_backend.rtmp_stat_has_active_stream(b"<rtmp><naccepted>1</naccepted>")

        self.assertEqual(raised.exception.operation, "rtmp-stat")
        self.assertEqual(raised.exception.status, "invalid-response")


if __name__ == "__main__":
    unittest.main()
