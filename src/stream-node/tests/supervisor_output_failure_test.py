"""Regression coverage for terminal RTMP output failures."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
import unittest


_SUPERVISOR_SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "supervisor.py"
_SPEC = importlib.util.spec_from_file_location(
    "supervisor_for_output_failure_tests", _SUPERVISOR_SCRIPT
)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError(f"Unable to load supervisor module at {_SUPERVISOR_SCRIPT}")
_supervisor = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = _supervisor
_SPEC.loader.exec_module(_supervisor)


class _Clock:
    def __init__(self, now: float) -> None:
        self.now = now

    def __call__(self) -> float:
        return self.now


class _Popen:
    def __call__(self, *args: object, **kwargs: object) -> object:
        raise AssertionError("terminal output failure must not launch a process")


class _Backend:
    def __init__(self) -> None:
        self.completions: list[tuple[object, str, str | None]] = []
        self.heartbeats: list[tuple[str, str | None, int | None, str | None]] = []

    def complete(
        self, assignment: object, status: str, failure_reason: str | None = None
    ) -> object:
        self.completions.append((assignment, status, failure_reason))
        return _supervisor.CallbackResult.accepted

    def post_heartbeat(
        self,
        status: str,
        failure_reason: str | None,
        restart_attempt: int | None,
        active_queue_item_id: str | None,
    ) -> None:
        self.heartbeats.append(
            (status, failure_reason, restart_attempt, active_queue_item_id)
        )


class TerminalRtmpOutputFailureTests(unittest.TestCase):
    def test_fails_active_assignment_without_scheduling_restart(self) -> None:
        reason = "RTMP output failed"
        now = 123.0
        assignment = _supervisor.Assignment(
            queue_item_id="queue-item-1",
            claim_owner="stream-node-1",
            claim_attempt=1,
            cache_path="/var/lib/web10/storage/track.wav",
            title="",
            artist="",
        )
        backend = _Backend()
        supervisor = _supervisor.Supervisor(
            _supervisor.Config(
                api_base_url="http://api.example",
                callback_token="callback-token-1234567890",
                stage_url="http://stage.example/",
                rtmp_url="rtmp://rtmp.example/s/",
                rtmp_key="rtmp-key-1234567890",
                display=":99",
                width=1280,
                height=720,
                framerate=30,
                bitrate_kbps=192,
            ),
            backend,
            monotonic=_Clock(now),
            popen=_Popen(),
        )
        supervisor._assignment = assignment

        supervisor._handle_unexpected_exit(reason, now, visual=False)

        self.assertEqual(backend.completions, [(assignment, "failed", reason)])
        self.assertTrue(supervisor._terminal_failure)
        self.assertIsNone(supervisor._restart_at)
        self.assertIn(
            ("failed", reason),
            [(status, failure_reason) for status, failure_reason, _, _ in backend.heartbeats],
        )


if __name__ == "__main__":
    unittest.main()
