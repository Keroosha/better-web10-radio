"""Regression coverage for the persistent RTMP publisher across tracks."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
import unittest


_SUPERVISOR_SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "supervisor.py"
_SPEC = importlib.util.spec_from_file_location(
    "supervisor_for_persistent_rtmp_tests", _SUPERVISOR_SCRIPT
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
        raise AssertionError("completion handling must not launch a process")


class _Backend:
    def __init__(self, result: object) -> None:
        self._result = result
        self.completions: list[tuple[object, str, str | None]] = []
        self.heartbeats: list[tuple[str, str | None, int | None, str | None]] = []

    def complete(
        self, assignment: object, status: str, failure_reason: str | None = None
    ) -> object:
        self.completions.append((assignment, status, failure_reason))
        return self._result

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


def _config() -> object:
    return _supervisor.Config(
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
    )


def _assignment() -> object:
    return _supervisor.Assignment(
        queue_item_id="queue-item-1",
        claim_owner="stream-node-1",
        claim_attempt=1,
        cache_path="/var/lib/web10/storage/track.wav",
        title="",
        artist="",
    )


class PersistentRtmpTests(unittest.TestCase):
    def _make(self) -> tuple[object, _Backend, list[bool], object, float]:
        now = 100.0
        backend = _Backend(_supervisor.CallbackResult.accepted)
        supervisor = _supervisor.Supervisor(
            _config(), backend, monotonic=_Clock(now), popen=_Popen()
        )
        stops: list[bool] = []
        sentinel = object()
        supervisor._liquidsoap = sentinel

        def fake_stop() -> None:
            stops.append(True)
            supervisor._liquidsoap = None

        supervisor._stop_media = fake_stop  # type: ignore[method-assign]
        return supervisor, backend, stops, sentinel, now

    def test_played_completion_keeps_media_alive(self) -> None:
        supervisor, backend, stops, sentinel, now = self._make()
        assignment = _assignment()
        supervisor._assignment = assignment
        supervisor._pushed_at = now
        supervisor._callback_started = True

        supervisor._begin_completion(assignment, "played", None, now)

        self.assertEqual(stops, [], "a played track must not stop the media pipeline")
        self.assertIs(
            supervisor._liquidsoap,
            sentinel,
            "Liquidsoap stays alive to hold the persistent RTMP connection",
        )
        self.assertEqual(backend.completions, [(assignment, "played", None)])
        self.assertIsNone(supervisor._completion_pending)
        self.assertIsNone(supervisor._assignment)

    def test_failed_completion_stops_media(self) -> None:
        supervisor, backend, stops, _sentinel, now = self._make()
        assignment = _assignment()
        supervisor._assignment = assignment
        supervisor._pushed_at = now
        supervisor._callback_started = True

        supervisor._begin_completion(assignment, "failed", "boom", now)

        self.assertEqual(stops, [True], "a failed track must stop the media pipeline")
        self.assertIsNone(supervisor._liquidsoap)
        self.assertEqual(backend.completions, [(assignment, "failed", "boom")])


if __name__ == "__main__":
    unittest.main()
