#!/usr/bin/env python3
"""Supervise the Web10 stream capture and Liquidsoap runtime.

This module deliberately uses only the Python standard library.  The public
objects below are intentionally small so the runtime can be tested without
starting its operating-system children.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import re
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from enum import Enum
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from types import FrameType
from typing import Any, Callable, Mapping, Sequence


_API_TIMEOUT_SECONDS = 5.0
_CALLBACK_TIMEOUT_SECONDS = 2.0
_CALLBACK_PORT = 18080
_CALLBACK_MAX_BYTES = 4 * 1024
_CONTROL_CADENCE_SECONDS = 2.0
_HEARTBEAT_CADENCE_SECONDS = 10.0
_START_CALLBACK_DEADLINE_SECONDS = 5.0
_PROCESS_STOP_GRACE_SECONDS = 10.0
_STORAGE_ROOT = Path("/var/lib/web10/storage")
_LIQUIDSOAP_SOCKET = "/run/web10/liquidsoap.sock"
_VALID_STATUSES = frozenset(
    {"starting", "live", "degraded", "restarting", "failed", "offline"}
)
_BEARER_TOKEN = re.compile(r"^[A-Za-z0-9._~+/-]+={0,2}$")
_POSITIVE_DECIMAL = re.compile(r"^[1-9][0-9]*$")
_DISPLAY = re.compile(r"^:([0-9]+)$")


class _ConfigurationError(ValueError):
    """A safe configuration error whose text is always an environment key."""


class _BackendError(RuntimeError):
    pass


class _BackendTransportError(_BackendError):
    pass


class _BackendHttpError(_BackendError):
    def __init__(self, status: int) -> None:
        super().__init__(str(status))
        self.status = status


@dataclass(frozen=True, repr=False)
class Config:
    """Validated, immutable runtime configuration.

    The representation is deliberately redacted: process supervisors are often
    included in exception context and neither stream credentials nor stage URLs
    may become logs by accident.
    """

    api_base_url: str
    callback_token: str
    stage_url: str
    rtmp_url: str
    rtmp_key: str
    display: str
    width: int
    height: int
    framerate: int
    bitrate_kbps: int

    @classmethod
    def from_environ(cls, environ: Mapping[str, str]) -> "Config":
        api_base_url = _required(environ, "WEB10_API__BASE_URL")
        callback_token = _required(environ, "WEB10_STREAM__CALLBACK_TOKEN")
        stage_url = _required(environ, "WEB10_STREAM__STAGE_URL")
        rtmp_url = _required(environ, "WEB10_STREAM__RTMP_URL")
        rtmp_key = _required(environ, "WEB10_STREAM__RTMP_KEY")

        _validate_http_url("WEB10_API__BASE_URL", api_base_url, allow_query=False)
        _validate_http_url("WEB10_STREAM__STAGE_URL", stage_url, allow_query=True)
        _validate_rtmp_url(rtmp_url)
        if len(callback_token) < 24 or _BEARER_TOKEN.fullmatch(callback_token) is None:
            raise _ConfigurationError("WEB10_STREAM__CALLBACK_TOKEN")
        if len(rtmp_key) < 16 or any(character.isspace() for character in rtmp_key):
            raise _ConfigurationError("WEB10_STREAM__RTMP_KEY")

        display = _optional(environ, "WEB10_STREAM__DISPLAY", ":99")
        if _DISPLAY.fullmatch(display) is None:
            raise _ConfigurationError("WEB10_STREAM__DISPLAY")

        width = _positive_int(environ, "WEB10_STREAM__WIDTH", 1280)
        height = _positive_int(environ, "WEB10_STREAM__HEIGHT", 720)
        framerate = _positive_int(environ, "WEB10_STREAM__FRAMERATE", 30)
        bitrate_kbps = _positive_int(environ, "WEB10_STREAM__BITRATE_KBPS", 192)

        return cls(
            api_base_url=api_base_url.rstrip("/"),
            callback_token=callback_token,
            stage_url=stage_url,
            rtmp_url=rtmp_url,
            rtmp_key=rtmp_key,
            display=display,
            width=width,
            height=height,
            framerate=framerate,
            bitrate_kbps=bitrate_kbps,
        )

    def __repr__(self) -> str:
        return (
            "Config("
            f"display={self.display!r}, width={self.width}, height={self.height}, "
            f"framerate={self.framerate}, bitrate_kbps={self.bitrate_kbps})"
        )


@dataclass(frozen=True)
class ControlState:
    desired_state: str
    restart_generation: int


@dataclass(frozen=True, repr=False)
class Assignment:
    queue_item_id: str
    claim_owner: str
    claim_attempt: int
    cache_path: str
    title: str
    artist: str

    @property
    def identity(self) -> tuple[str, str, int]:
        return (self.queue_item_id, self.claim_owner, self.claim_attempt)

    def __repr__(self) -> str:
        return "Assignment(<redacted>)"


class CallbackResult(str, Enum):
    accepted = "accepted"
    stale = "stale"
    unauthorized = "unauthorized"
    transient_error = "transient_error"


@dataclass(frozen=True)
class RestartDecision:
    allowed: bool
    attempt: int
    delay_seconds: int


class RestartBudget:
    """A five-restart rolling budget with the specified exponential backoff."""

    def __init__(self, *, max_restarts: int = 5, window_seconds: float = 300.0) -> None:
        self._max_restarts = max_restarts
        self._window_seconds = window_seconds
        self._restarts: list[float] = []

    def record(self, now_monotonic: float) -> RestartDecision:
        cutoff = now_monotonic - self._window_seconds
        self._restarts = [restart for restart in self._restarts if restart > cutoff]
        attempt = len(self._restarts) + 1
        if attempt > self._max_restarts:
            return RestartDecision(allowed=False, attempt=attempt, delay_seconds=0)
        self._restarts.append(now_monotonic)
        return RestartDecision(
            allowed=True,
            attempt=attempt,
            delay_seconds=2 ** (attempt - 1),
        )

    def reset(self) -> None:
        self._restarts.clear()


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(
        self,
        req: urllib.request.Request,
        fp: Any,
        code: int,
        msg: str,
        headers: Any,
        newurl: str,
    ) -> None:
        # Redirecting would risk forwarding the bearer token to another host.
        return None


class BackendClient:
    """Authenticated, bounded HTTP client for the stream-node contract."""

    def __init__(self, config: Config) -> None:
        self._base_url = config.api_base_url
        self._callback_token = config.callback_token
        self._bitrate_kbps = config.bitrate_kbps
        self._opener = urllib.request.build_opener(_NoRedirect())

    def get_control(self) -> ControlState:
        status, body = self._request("GET", "/api/v0/stream-node/control")
        if status != 200:
            raise _BackendHttpError(status)
        payload = _json_object(body)
        desired_state = payload.get("desiredState")
        generation = payload.get("restartGeneration")
        if desired_state not in {"running", "stopped"} or not _is_nonnegative_int(generation):
            raise _BackendError()
        return ControlState(desired_state=desired_state, restart_generation=generation)

    def get_assignment(self) -> Assignment | None:
        status, body = self._request("GET", "/api/v0/stream-node/playback/current")
        if status == 204:
            return None
        if status != 200:
            raise _BackendHttpError(status)
        payload = _json_object(body)
        queue_item_id = payload.get("queueItemId")
        claim_owner = payload.get("claimOwner")
        claim_attempt = payload.get("claimAttempt")
        cache_path = payload.get("cachePath")
        title = payload.get("title")
        artist = payload.get("artist")
        if (
            not _is_nonempty_string(queue_item_id)
            or not _is_nonempty_string(claim_owner)
            or not _is_positive_int(claim_attempt)
            or not _is_nonempty_string(cache_path)
            or not isinstance(title, str)
            or not isinstance(artist, str)
        ):
            raise _BackendError()
        return Assignment(
            queue_item_id=queue_item_id,
            claim_owner=claim_owner,
            claim_attempt=claim_attempt,
            cache_path=cache_path,
            title=title,
            artist=artist,
        )

    def post_heartbeat(
        self,
        status: str,
        failure_reason: str | None,
        restart_attempt: int | None,
        active_queue_item_id: str | None,
    ) -> None:
        if status not in _VALID_STATUSES:
            raise ValueError("status")
        if failure_reason is not None:
            failure_reason = _bounded_reason(failure_reason)
        payload = {
            "status": status,
            "failureReason": failure_reason,
            "metadata": {
                "bitrateKbps": self._bitrate_for_status(status),
                "restartAttempt": restart_attempt,
                "activeQueueItemId": active_queue_item_id,
            },
        }
        response_status, _ = self._request(
            "POST", "/api/v0/stream-node/heartbeat", payload
        )
        if response_status != 204:
            raise _BackendHttpError(response_status)

    def renew_lease(self, assignment: Assignment) -> CallbackResult:
        try:
            status, _ = self._request(
                "POST",
                f"/api/v0/stream-node/playback/{urllib.parse.quote(assignment.queue_item_id, safe='')}/lease",
                {"claimOwner": assignment.claim_owner, "claimAttempt": assignment.claim_attempt},
            )
        except _BackendError:
            return CallbackResult.transient_error
        return _callback_result(status)

    def complete(
        self,
        assignment: Assignment,
        status: str,
        failure_reason: str | None = None,
    ) -> CallbackResult:
        if status not in {"played", "failed"}:
            raise ValueError("status")
        if status == "failed":
            failure_reason = _bounded_reason(failure_reason or "Stream playback failed")
        payload: dict[str, Any] = {
            "claimOwner": assignment.claim_owner,
            "claimAttempt": assignment.claim_attempt,
            "status": status,
        }
        if status == "failed":
            payload["failureReason"] = failure_reason
        try:
            response_status, _ = self._request(
                "POST",
                f"/api/v0/stream-node/playback/{urllib.parse.quote(assignment.queue_item_id, safe='')}/completion",
                payload,
            )
        except _BackendError:
            return CallbackResult.transient_error
        return _callback_result(response_status)

    def _bitrate_for_status(self, status: str) -> int | None:
        return self._bitrate_kbps if status != "offline" else None

    def _request(
        self, method: str, path: str, payload: Mapping[str, Any] | None = None
    ) -> tuple[int, bytes]:
        data: bytes | None = None
        headers = {
            "Accept": "application/json",
            "Authorization": f"Bearer {self._callback_token}",
        }
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"
        request = urllib.request.Request(
            f"{self._base_url}{path}", data=data, headers=headers, method=method
        )
        try:
            with self._opener.open(request, timeout=_API_TIMEOUT_SECONDS) as response:
                return response.status, response.read(64 * 1024)
        except urllib.error.HTTPError as error:
            # Do not parse or retain an error body: it might contain upstream
            # diagnostics that must not enter runtime logs.
            return error.code, b""
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise _BackendTransportError() from error


class _CallbackServer(ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, supervisor: "Supervisor", port: int) -> None:
        self.supervisor = supervisor
        super().__init__(("127.0.0.1", port), _CallbackRequestHandler)


class _CallbackRequestHandler(BaseHTTPRequestHandler):
    server: _CallbackServer

    def log_message(self, format: str, *args: object) -> None:
        # Liquidsoap callbacks contain fenced media metadata.  The stock HTTP
        # server logger would expose request paths and must stay disabled.
        return

    def do_GET(self) -> None:
        if self.path == "/healthz" and self.server.supervisor._control_alive.is_set():
            self.send_response_only(204)
            self.end_headers()
            return
        self.send_response_only(404)
        self.end_headers()

    def do_POST(self) -> None:
        path = self.path
        if path not in {
            "/callbacks/started",
            "/callbacks/ended",
            "/callbacks/output-failed",
        }:
            self.send_response_only(404)
            self.end_headers()
            return

        body = self._read_json_object()
        if body is None:
            self.server.supervisor._record_callback_protocol_error()
            self.send_response_only(400)
            self.end_headers()
            return

        callback = path.rsplit("/", 1)[-1]
        if not _callback_payload_is_well_formed(callback, body):
            self.server.supervisor._record_callback_protocol_error()
            self.send_response_only(400)
            self.end_headers()
            return
        accepted = self.server.supervisor._accept_callback(callback, body)
        self.send_response_only(204 if accepted else 409)
        self.end_headers()

    def _read_json_object(self) -> dict[str, Any] | None:
        raw_length = self.headers.get("Content-Length")
        try:
            length = int(raw_length) if raw_length is not None else -1
        except ValueError:
            return None
        if length < 0 or length > _CALLBACK_MAX_BYTES:
            return None
        try:
            raw = self.rfile.read(length)
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError, OSError):
            return None
        return value if isinstance(value, dict) else None


class Supervisor:
    """Own Xvfb, Chromium, Liquidsoap, callbacks, and backend coordination."""

    def __init__(
        self,
        config: Config,
        backend_client: BackendClient | None = None,
        *,
        callback_port: int = _CALLBACK_PORT,
        monotonic: Callable[[], float] = time.monotonic,
        popen: Callable[..., subprocess.Popen[bytes]] = subprocess.Popen,
    ) -> None:
        self._config = config
        self._backend = backend_client or BackendClient(config)
        self._callback_port = callback_port
        self._monotonic = monotonic
        self._popen = popen
        self._runtime_root = Path(__file__).resolve().parents[1]
        self._shutdown = threading.Event()
        self._control_alive = threading.Event()
        self._lock = threading.RLock()
        self._callback_events: queue.SimpleQueue[tuple[str, Assignment]] = queue.SimpleQueue()
        self._server: _CallbackServer | None = None
        self._server_thread: threading.Thread | None = None
        self._xvfb: subprocess.Popen[bytes] | None = None
        self._chromium: subprocess.Popen[bytes] | None = None
        self._liquidsoap: subprocess.Popen[bytes] | None = None
        self._desired_state = "running"
        self._generation: int | None = None
        self._assignment: Assignment | None = None
        self._assignment_path: Path | None = None
        self._callback_identity: tuple[str, str, int] | None = None
        self._callback_started = False
        self._callback_ended = False
        self._callback_output_failed = False
        self._pushed_at: float | None = None
        self._completion_pending: tuple[Assignment, str, str | None] | None = None
        self._next_completion_retry_at = 0.0
        self._restart_budget = RestartBudget()
        self._restart_at: float | None = None
        self._restart_attempt: int | None = None
        self._restart_visual = False
        self._terminal_failure = False
        self._failure_reason: str | None = None
        self._degraded_until = 0.0
        self._stable_since: float | None = None
        self._next_control_at = 0.0
        self._next_assignment_at = 0.0
        self._next_heartbeat_at = 0.0
        self._next_lease_at = 0.0
        self._previous_signal_handlers: dict[int, Any] = {}

    def request_shutdown(self) -> None:
        self._shutdown.set()

    def run(self) -> int:
        self._install_signal_handlers()
        try:
            self._start_callback_server()
            self._control_alive.set()
            try:
                self._start_visual()
            except _BackendError:
                self._schedule_restart("Visual pipeline failed", visual=True)
            except (OSError, subprocess.SubprocessError):
                self._schedule_restart("Visual pipeline failed", visual=True)

            while not self._shutdown.is_set():
                now = self._monotonic()
                self._drain_callback_events(now)
                self._observe_children(now)
                self._poll_control_if_due(now)
                self._apply_desired_state(now)
                self._poll_assignment_if_due(now)
                self._service_pending_completion(now)
                self._renew_lease_if_due(now)
                self._check_start_deadline(now)
                self._reset_stable_budget_if_due(now)
                self._heartbeat_if_due(now)
                self._shutdown.wait(0.1)
        finally:
            self._control_alive.clear()
            self._terminate_all()
            self._post_offline_best_effort()
            self._stop_callback_server()
            self._restore_signal_handlers()
        return 0

    def _install_signal_handlers(self) -> None:
        def _handle_signal(signum: int, frame: FrameType | None) -> None:
            del signum, frame
            self.request_shutdown()

        for signum in (signal.SIGTERM, signal.SIGINT):
            try:
                self._previous_signal_handlers[signum] = signal.getsignal(signum)
                signal.signal(signum, _handle_signal)
            except ValueError:
                # Unit tests can run Supervisor in a non-main thread.
                pass

    def _restore_signal_handlers(self) -> None:
        for signum, handler in self._previous_signal_handlers.items():
            try:
                signal.signal(signum, handler)
            except ValueError:
                pass
        self._previous_signal_handlers.clear()

    def _start_callback_server(self) -> None:
        if self._server is not None:
            return
        self._server = _CallbackServer(self, self._callback_port)
        self._server_thread = threading.Thread(
            target=self._server.serve_forever,
            name="web10-loopback-callbacks",
            daemon=True,
        )
        self._server_thread.start()

    def _stop_callback_server(self) -> None:
        server, thread = self._server, self._server_thread
        self._server = None
        self._server_thread = None
        if server is not None:
            server.shutdown()
            server.server_close()
        if thread is not None:
            thread.join(timeout=_CALLBACK_TIMEOUT_SECONDS)

    def _start_visual(self) -> None:
        if not _process_alive(self._xvfb):
            display_number = _DISPLAY.fullmatch(self._config.display)
            assert display_number is not None
            self._xvfb = self._spawn(
                [
                    "Xvfb",
                    self._config.display,
                    "-screen",
                    "0",
                    f"{self._config.width}x{self._config.height}x24",
                    "-nolisten",
                    "tcp",
                ]
            )
            self._wait_for_x11_socket(display_number.group(1))
        if not _process_alive(self._chromium):
            profile = Path("/tmp/web10-chromium")
            shutil.rmtree(profile, ignore_errors=True)
            environment = self._process_environment()
            environment["DISPLAY"] = self._config.display
            self._chromium = self._spawn(
                [
                    "chromium",
                    "--kiosk",
                    "--no-sandbox",
                    "--enable-unsafe-swiftshader",
                    "--autoplay-policy=no-user-gesture-required",
                    f"--window-size={self._config.width},{self._config.height}",
                    "--user-data-dir=/tmp/web10-chromium",
                    _capture_stage_url(self._config.stage_url),
                ],
                environment=environment,
            )

    def _wait_for_x11_socket(self, display_number: str) -> None:
        socket_path = Path("/tmp/.X11-unix") / f"X{display_number}"
        deadline = self._monotonic() + _PROCESS_STOP_GRACE_SECONDS
        while self._monotonic() < deadline:
            if socket_path.exists():
                return
            if not _process_alive(self._xvfb):
                raise OSError("Xvfb exited")
            if self._shutdown.wait(0.05):
                raise OSError("shutdown")
        raise OSError("Xvfb readiness timeout")

    def _start_media(self) -> None:
        if _process_alive(self._liquidsoap):
            return
        self._liquidsoap = self._spawn(
            ["liquidsoap", "./liquidsoap/web10.liq"],
            environment=self._process_environment(),
            stream_output=True,
        )
        self._stable_since = self._monotonic()

    def _spawn(
        self,
        command: Sequence[str],
        *,
        environment: Mapping[str, str] | None = None,
        stream_output: bool = False,
    ) -> subprocess.Popen[bytes]:
        return self._popen(
            list(command),
            cwd=str(self._runtime_root),
            env=dict(environment or self._process_environment()),
            stdin=subprocess.DEVNULL,
            stdout=None if stream_output else subprocess.DEVNULL,
            stderr=None if stream_output else subprocess.DEVNULL,
            start_new_session=True,
        )

    def _process_environment(self) -> dict[str, str]:
        environment = os.environ.copy()
        environment.update(
            {
                "WEB10_API__BASE_URL": self._config.api_base_url,
                "WEB10_STREAM__CALLBACK_TOKEN": self._config.callback_token,
                "WEB10_STREAM__STAGE_URL": self._config.stage_url,
                "WEB10_STREAM__RTMP_URL": self._config.rtmp_url,
                "WEB10_STREAM__RTMP_KEY": self._config.rtmp_key,
                "WEB10_STREAM__DISPLAY": self._config.display,
                "WEB10_STREAM__WIDTH": str(self._config.width),
                "WEB10_STREAM__HEIGHT": str(self._config.height),
                "WEB10_STREAM__FRAMERATE": str(self._config.framerate),
                "WEB10_STREAM__BITRATE_KBPS": str(self._config.bitrate_kbps),
            }
        )
        return environment

    def _poll_control_if_due(self, now: float) -> None:
        if now < self._next_control_at:
            return
        self._next_control_at = now + _CONTROL_CADENCE_SECONDS
        try:
            control = self._backend.get_control()
        except _BackendHttpError as error:
            if error.status in {401, 403}:
                self._set_terminal_failure("Backend authorization failed")
            else:
                self._mark_degraded("Backend control request failed", now)
            return
        except _BackendError:
            self._mark_degraded("Backend control request failed", now)
            return

        previous_generation = self._generation
        self._generation = control.restart_generation
        self._desired_state = control.desired_state
        if previous_generation is not None and control.restart_generation > previous_generation:
            self._controlled_restart(now)

    def _apply_desired_state(self, now: float) -> None:
        if self._desired_state == "stopped":
            self._stop_media()
            self._clear_assignment()
            return
        if self._terminal_failure:
            self._stop_media()
            return
        if self._restart_at is not None:
            if now < self._restart_at:
                return
            restart_visual = self._restart_visual
            self._restart_at = None
            self._restart_visual = False
            try:
                if restart_visual:
                    self._start_visual()
                self._start_media()
            except (OSError, subprocess.SubprocessError):
                self._schedule_restart("Pipeline restart failed", visual=restart_visual)
            return
        try:
            if not self._visual_alive():
                self._start_visual()
            self._start_media()
        except (OSError, subprocess.SubprocessError):
            self._schedule_restart("Pipeline startup failed", visual=True)

    def _poll_assignment_if_due(self, now: float) -> None:
        if self._desired_state != "running" or self._terminal_failure:
            return
        if now < self._next_assignment_at:
            return
        self._next_assignment_at = now + _CONTROL_CADENCE_SECONDS
        if self._completion_pending is not None:
            return
        try:
            assignment = self._backend.get_assignment()
        except _BackendHttpError as error:
            if error.status in {401, 403}:
                self._set_terminal_failure("Backend authorization failed")
            else:
                self._mark_degraded("Backend playback request failed", now)
            return
        except _BackendError:
            self._mark_degraded("Backend playback request failed", now)
            return

        if assignment is None:
            if self._assignment is not None:
                self._stop_media()
                self._clear_assignment()
            return
        if self._assignment is not None and self._assignment.identity == assignment.identity:
            self._push_assignment_if_ready(now)
            return

        if self._assignment is not None:
            # A changed fence makes all local media stale.  Do not report a
            # completion for the old identity: the backend already replaced it.
            self._stop_media()
            self._clear_assignment()
            self._start_media()

        path = _assignment_path(assignment.cache_path)
        if path is None:
            self._assignment = assignment
            self._begin_completion(assignment, "failed", "Media file is unavailable", now)
            return
        self._assignment = assignment
        self._assignment_path = path
        self._push_assignment_if_ready(now)

    def _push_assignment_if_ready(self, now: float) -> None:
        assignment, media_path = self._assignment, self._assignment_path
        if (
            assignment is None
            or media_path is None
            or self._pushed_at is not None
            or self._completion_pending is not None
            or not _process_alive(self._liquidsoap)
        ):
            return
        if not Path(_LIQUIDSOAP_SOCKET).is_socket():
            return
        with self._lock:
            self._callback_identity = assignment.identity
            self._callback_started = False
            self._callback_ended = False
            self._callback_output_failed = False
        try:
            response = self._liquidsoap_command(
                f"web10.push {_annotated_file_uri(assignment, media_path)}"
            )
            if "error" in response.lower():
                raise OSError("Liquidsoap queue rejected media")
        except OSError:
            with self._lock:
                self._callback_identity = None
            self._begin_completion(assignment, "failed", "Media pipeline rejected assignment", now)
            return
        self._pushed_at = now
        self._next_lease_at = now + _HEARTBEAT_CADENCE_SECONDS

    def _renew_lease_if_due(self, now: float) -> None:
        assignment = self._assignment
        if (
            assignment is None
            or self._pushed_at is None
            or self._completion_pending is not None
            or self._desired_state != "running"
            or self._terminal_failure
            or now < self._next_lease_at
        ):
            return
        self._next_lease_at = now + _HEARTBEAT_CADENCE_SECONDS
        try:
            result = self._backend.renew_lease(assignment)
        except _BackendError:
            result = CallbackResult.transient_error
        if result == CallbackResult.accepted:
            return
        if result == CallbackResult.stale:
            self._handle_stale_lease(now)
            return
        if result == CallbackResult.unauthorized:
            self._set_terminal_failure("Backend authorization failed")
            return
        self._mark_degraded("Playback lease renewal failed", now)

    def _handle_stale_lease(self, now: float) -> None:
        # Stale media must not keep publishing RTMP while control catches up.
        self._stop_media()
        self._clear_assignment()
        self._next_assignment_at = now
        if self._desired_state == "running" and not self._terminal_failure:
            try:
                self._start_media()
                self._poll_assignment_if_due(now)
            except (OSError, subprocess.SubprocessError):
                self._schedule_restart("Media pipeline restart failed", visual=False)

    def _check_start_deadline(self, now: float) -> None:
        if (
            self._assignment is not None
            and self._pushed_at is not None
            and not self._callback_started
            and now - self._pushed_at >= _START_CALLBACK_DEADLINE_SECONDS
        ):
            self._begin_completion(
                self._assignment, "failed", "Playback start callback timed out", now
            )

    def _drain_callback_events(self, now: float) -> None:
        while True:
            try:
                callback, assignment = self._callback_events.get_nowait()
            except queue.Empty:
                return
            if self._assignment is None or assignment.identity != self._assignment.identity:
                continue
            if callback == "started":
                self._callback_started = True
            elif callback == "ended":
                self._begin_completion(assignment, "played", None, now)
            elif callback == "output-failed":
                self._begin_completion(assignment, "failed", "RTMP output failed", now)
            elif callback == "start-timeout":
                self._begin_completion(assignment, "failed", "Playback start callback timed out", now)
            else:
                self._begin_completion(assignment, "failed", "Callback protocol error", now)

    def _begin_completion(
        self,
        assignment: Assignment,
        status: str,
        failure_reason: str | None,
        now: float,
    ) -> None:
        if self._completion_pending is not None:
            return
        if status == "failed":
            failure_reason = _bounded_reason(failure_reason or "Stream playback failed")
            self._mark_degraded(failure_reason, now)
        self._completion_pending = (assignment, status, failure_reason)
        self._next_completion_retry_at = now
        if status == "failed":
            self._stop_media()
        with self._lock:
            self._callback_identity = None
        self._service_pending_completion(now)

    def _service_pending_completion(self, now: float) -> None:
        pending = self._completion_pending
        if pending is None or now < self._next_completion_retry_at:
            return
        assignment, status, failure_reason = pending
        try:
            result = self._backend.complete(assignment, status, failure_reason)
        except _BackendError:
            result = CallbackResult.transient_error
        if result == CallbackResult.accepted:
            self._completion_pending = None
            self._clear_assignment()
            self._next_assignment_at = now
            return
        if result == CallbackResult.stale:
            self._completion_pending = None
            self._clear_assignment()
            self._next_assignment_at = now
            self._poll_assignment_if_due(now)
            return
        if result == CallbackResult.unauthorized:
            self._completion_pending = None
            self._set_terminal_failure("Backend authorization failed")
            return
        self._mark_degraded("Playback completion failed", now)
        self._next_completion_retry_at = now + _HEARTBEAT_CADENCE_SECONDS

    def _observe_children(self, now: float) -> None:
        if self._desired_state == "stopped" or self._terminal_failure:
            return
        if self._restart_at is not None:
            return
        if self._xvfb is not None and not _process_alive(self._xvfb):
            self._xvfb = None
            self._handle_unexpected_exit("Visual pipeline exited", now, visual=True)
            return
        if self._chromium is not None and not _process_alive(self._chromium):
            self._chromium = None
            self._handle_unexpected_exit("Visual pipeline exited", now, visual=True)
            return
        if self._liquidsoap is not None and not _process_alive(self._liquidsoap):
            self._liquidsoap = None
            reason = "RTMP output failed" if self._assignment is not None else "Media pipeline exited"
            self._handle_unexpected_exit(reason, now, visual=False)

    def _handle_unexpected_exit(self, reason: str, now: float, *, visual: bool) -> None:
        assignment = self._assignment
        if assignment is not None and self._completion_pending is None:
            self._begin_completion(assignment, "failed", reason, now)
        if visual:
            # A display and kiosk browser are one visual unit, and their media
            # capture is inseparable from that X11 session.  Restart all three.
            self._stop_media()
            self._stop_visual()
        if reason == "RTMP output failed":
            # Retrying against an unavailable publisher hides the actionable
            # failure and spends the restart budget. An operator restart after
            # restoring the target performs the clean recovery.
            self._set_terminal_failure(reason)
            self._send_heartbeat("failed", reason, None, self._active_queue_item_id())
            return
        self._schedule_restart(reason, visual=visual)

    def _schedule_restart(self, reason: str, *, visual: bool) -> None:
        now = self._monotonic()
        reason = _bounded_reason(reason)
        self._failure_reason = reason
        self._degraded_until = max(self._degraded_until, now + _HEARTBEAT_CADENCE_SECONDS)
        # Degraded is observable before restarting, even when a normal
        # heartbeat is not due.  Both emissions use canonical metadata.
        self._send_heartbeat("degraded", reason, None, self._active_queue_item_id())
        decision = self._restart_budget.record(now)
        self._restart_attempt = decision.attempt
        if not decision.allowed:
            self._terminal_failure = True
            self._restart_at = None
            self._stop_media()
            if visual:
                self._stop_visual()
            self._send_heartbeat("failed", reason, decision.attempt, self._active_queue_item_id())
            return
        self._restart_visual = self._restart_visual or visual
        self._restart_at = now + decision.delay_seconds
        self._send_heartbeat("restarting", reason, decision.attempt, self._active_queue_item_id())

    def _controlled_restart(self, now: float) -> None:
        self._restart_budget.reset()
        self._terminal_failure = False
        self._restart_at = None
        self._restart_attempt = None
        self._restart_visual = False
        self._failure_reason = None
        self._degraded_until = 0.0
        self._completion_pending = None
        self._stop_media()
        self._clear_assignment()
        self._next_assignment_at = now
        self._next_lease_at = now + _HEARTBEAT_CADENCE_SECONDS

    def _set_terminal_failure(self, reason: str) -> None:
        self._terminal_failure = True
        self._restart_at = None
        self._failure_reason = _bounded_reason(reason)
        self._stop_media()

    def _mark_degraded(self, reason: str, now: float) -> None:
        self._failure_reason = _bounded_reason(reason)
        self._degraded_until = max(self._degraded_until, now + _HEARTBEAT_CADENCE_SECONDS)

    def _reset_stable_budget_if_due(self, now: float) -> None:
        if self._restart_at is not None or self._terminal_failure or not self._visual_alive():
            self._stable_since = None
            return
        if not _process_alive(self._liquidsoap):
            self._stable_since = None
            return
        if self._stable_since is None:
            self._stable_since = now
            return
        if now - self._stable_since >= 300.0:
            self._restart_budget.reset()
            self._restart_attempt = None
            self._stable_since = now

    def _heartbeat_if_due(self, now: float) -> None:
        if now < self._next_heartbeat_at:
            return
        self._next_heartbeat_at = now + _HEARTBEAT_CADENCE_SECONDS
        status, reason, restart_attempt = self._heartbeat_state(now)
        self._send_heartbeat(status, reason, restart_attempt, self._active_queue_item_id())

    def _heartbeat_state(self, now: float) -> tuple[str, str | None, int | None]:
        if self._desired_state == "stopped":
            return "offline", None, None
        if self._terminal_failure:
            return "failed", self._failure_reason or "Stream node failed", self._restart_attempt
        if self._restart_at is not None:
            return "restarting", self._failure_reason or "Restarting stream pipeline", self._restart_attempt
        if now < self._degraded_until:
            return "degraded", self._failure_reason or "Stream node degraded", None
        if (
            self._assignment is not None
            and self._callback_started
            and self._visual_alive()
            and _process_alive(self._liquidsoap)
            and self._pipeline_ready()
        ):
            return "live", None, None
        return "starting", None, None

    def _send_heartbeat(
        self,
        status: str,
        failure_reason: str | None,
        restart_attempt: int | None,
        active_queue_item_id: str | None,
    ) -> None:
        try:
            self._backend.post_heartbeat(
                status, failure_reason, restart_attempt, active_queue_item_id
            )
        except _BackendHttpError as error:
            if error.status in {401, 403}:
                self._set_terminal_failure("Backend authorization failed")
            elif status != "offline":
                self._mark_degraded("Backend heartbeat failed", self._monotonic())
        except _BackendError:
            # Heartbeat transport failures are retried at the normal cadence and
            # never consume restart budget.
            if status != "offline":
                self._mark_degraded("Backend heartbeat failed", self._monotonic())

    def _post_offline_best_effort(self) -> None:
        self._send_heartbeat("offline", None, None, None)

    def _pipeline_ready(self) -> bool:
        try:
            return _command_is_true(self._liquidsoap_command("web10-video.is_ready")) and _command_is_true(
                self._liquidsoap_command("web10-rtmp.is_started")
            )
        except OSError:
            return False

    def _liquidsoap_command(self, command: str) -> str:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(_CALLBACK_TIMEOUT_SECONDS)
            connection.connect(_LIQUIDSOAP_SOCKET)
            connection.sendall((command + "\n").encode("utf-8"))
            chunks: list[bytes] = []
            while True:
                try:
                    chunk = connection.recv(4096)
                except socket.timeout:
                    break
                if not chunk:
                    break
                chunks.append(chunk)
                response = b"".join(chunks)
                if response.endswith(b"\nEND\n"):
                    break
        response = b"".join(chunks).decode("utf-8", "replace")
        lines = response.splitlines()
        return "\n".join(lines[:-1]) if lines and lines[-1] == "END" else response

    def _visual_alive(self) -> bool:
        return _process_alive(self._xvfb) and _process_alive(self._chromium)

    def _active_queue_item_id(self) -> str | None:
        return self._assignment.queue_item_id if self._assignment is not None else None

    def _clear_assignment(self) -> None:
        self._assignment = None
        self._assignment_path = None
        self._pushed_at = None
        self._callback_started = False
        self._callback_ended = False
        self._callback_output_failed = False
        with self._lock:
            self._callback_identity = None

    def _stop_media(self) -> None:
        process, self._liquidsoap = self._liquidsoap, None
        _terminate_process_group(process, self._monotonic)

    def _stop_visual(self) -> None:
        chromium, self._chromium = self._chromium, None
        xvfb, self._xvfb = self._xvfb, None
        _terminate_process_groups((chromium, xvfb), self._monotonic)

    def _terminate_all(self) -> None:
        liquidsoap, self._liquidsoap = self._liquidsoap, None
        chromium, self._chromium = self._chromium, None
        xvfb, self._xvfb = self._xvfb, None
        _terminate_process_groups((liquidsoap, chromium, xvfb), self._monotonic)

    def _accept_callback(self, callback: str, payload: dict[str, Any]) -> bool:
        with self._lock:
            assignment = self._assignment
            identity = self._callback_identity
            if callback == "output-failed":
                if payload != {} or assignment is None or identity != assignment.identity:
                    return False
                if self._callback_output_failed:
                    return False
                self._callback_output_failed = True
                self._callback_events.put((callback, assignment))
                return True
            if set(payload) != {"queueItemId", "claimOwner", "claimAttempt"}:
                return False
            queue_item_id = payload.get("queueItemId")
            claim_owner = payload.get("claimOwner")
            raw_claim_attempt = payload.get("claimAttempt")
            claim_attempt = (
                int(raw_claim_attempt)
                if isinstance(raw_claim_attempt, str) and _POSITIVE_DECIMAL.fullmatch(raw_claim_attempt)
                else raw_claim_attempt
            )
            callback_identity = (queue_item_id, claim_owner, claim_attempt)
            if (
                assignment is None
                or identity != assignment.identity
                or callback_identity != assignment.identity
                or not isinstance(queue_item_id, str)
                or not isinstance(claim_owner, str)
                or not _is_positive_int(claim_attempt)
            ):
                return False
            if callback == "started":
                if self._callback_started:
                    return False
                if (
                    self._pushed_at is not None
                    and self._monotonic() - self._pushed_at >= _START_CALLBACK_DEADLINE_SECONDS
                ):
                    self._callback_events.put(("start-timeout", assignment))
                    return False
                self._callback_started = True
            elif callback == "ended":
                if self._callback_ended:
                    return False
                self._callback_ended = True
            else:
                return False
            self._callback_events.put((callback, assignment))
            return True

    def _record_callback_protocol_error(self) -> None:
        with self._lock:
            if self._assignment is not None and self._callback_identity == self._assignment.identity:
                self._callback_events.put(("protocol-error", self._assignment))


def _required(environ: Mapping[str, str], key: str) -> str:
    value = environ.get(key)
    if not isinstance(value, str) or not value or value != value.strip():
        raise _ConfigurationError(key)
    return value


def _optional(environ: Mapping[str, str], key: str, default: str) -> str:
    value = environ.get(key, default)
    if not isinstance(value, str) or not value or value != value.strip():
        raise _ConfigurationError(key)
    return value


def _positive_int(environ: Mapping[str, str], key: str, default: int) -> int:
    value = environ.get(key)
    if value is None:
        return default
    if not isinstance(value, str) or _POSITIVE_DECIMAL.fullmatch(value) is None:
        raise _ConfigurationError(key)
    try:
        return int(value)
    except ValueError as error:
        raise _ConfigurationError(key) from error


def _validate_http_url(key: str, value: str, *, allow_query: bool) -> None:
    try:
        parsed = urllib.parse.urlsplit(value)
        hostname = parsed.hostname
        _ = parsed.port
    except ValueError as error:
        raise _ConfigurationError(key) from error
    if (
        any(character.isspace() for character in value)
        or parsed.scheme not in {"http", "https"}
        or not hostname
        or parsed.username is not None
        or parsed.password is not None
        or (not allow_query and (parsed.query or parsed.fragment))
    ):
        raise _ConfigurationError(key)


def _validate_rtmp_url(value: str) -> None:
    key = "WEB10_STREAM__RTMP_URL"
    try:
        parsed = urllib.parse.urlsplit(value)
        hostname = parsed.hostname
        _ = parsed.port
    except ValueError as error:
        raise _ConfigurationError(key) from error
    if (
        any(character.isspace() for character in value)
        or parsed.scheme not in {"rtmp", "rtmps"}
        or not hostname
        or parsed.username is not None
        or parsed.password is not None
    ):
        raise _ConfigurationError(key)


def _capture_stage_url(stage_url: str) -> str:
    """Merge exactly one capture=1 parameter without losing fragment/query data."""
    parsed = urllib.parse.urlsplit(stage_url)
    query = [
        (key, value)
        for key, value in urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
        if key != "capture"
    ]
    query.append(("capture", "1"))
    return urllib.parse.urlunsplit(
        (parsed.scheme, parsed.netloc, parsed.path, urllib.parse.urlencode(query), parsed.fragment)
    )


def _callback_payload_is_well_formed(callback: str, payload: dict[str, Any]) -> bool:
    if callback == "output-failed":
        return payload == {}
    if callback not in {"started", "ended"}:
        return False
    return (
        set(payload) == {"queueItemId", "claimOwner", "claimAttempt"}
        and _is_nonempty_string(payload.get("queueItemId"))
        and _is_nonempty_string(payload.get("claimOwner"))
        and _is_positive_int(payload.get("claimAttempt"))
    )

def _json_object(body: bytes) -> dict[str, Any]:
    try:
        value = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise _BackendError() from error
    if not isinstance(value, dict):
        raise _BackendError()
    return value


def _callback_result(status: int) -> CallbackResult:
    if status == 204:
        return CallbackResult.accepted
    if status == 409:
        return CallbackResult.stale
    if status in {401, 403}:
        return CallbackResult.unauthorized
    return CallbackResult.transient_error


def _bounded_reason(reason: str) -> str:
    # Python slices Unicode code points, which is the API's stated bound.
    value = reason[:500]
    return value if value else "Stream playback failed"


def _is_nonempty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _is_positive_int(value: Any) -> bool:
    return type(value) is int and value > 0


def _is_nonnegative_int(value: Any) -> bool:
    return type(value) is int and value >= 0


def _assignment_path(cache_path: str) -> Path | None:
    try:
        storage_root = _STORAGE_ROOT.resolve(strict=True)
        resolved = Path(cache_path).resolve(strict=True)
        if os.path.commonpath((str(storage_root), str(resolved))) != str(storage_root):
            return None
        if resolved == storage_root or not resolved.is_file():
            return None
        with resolved.open("rb"):
            pass
        return resolved
    except (OSError, RuntimeError, ValueError):
        return None


def _annotation_value(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"').replace("\r", "\\r").replace("\n", "\\n")


def _annotated_file_uri(assignment: Assignment, media_path: Path) -> str:
    metadata = (
        f'web10_queue_item_id="{_annotation_value(assignment.queue_item_id)}",'
        f'web10_claim_owner="{_annotation_value(assignment.claim_owner)}",'
        f'web10_claim_attempt="{assignment.claim_attempt}",'
        f'title="{_annotation_value(assignment.title)}",'
        f'artist="{_annotation_value(assignment.artist)}"'
    )
    return f"annotate:{''.join(metadata)}:{media_path}"


def _process_alive(process: subprocess.Popen[bytes] | None) -> bool:
    return process is not None and process.poll() is None


def _terminate_process_group(
    process: subprocess.Popen[bytes] | None, monotonic: Callable[[], float]
) -> None:
    _terminate_process_groups((process,), monotonic)


def _terminate_process_groups(
    processes: Sequence[subprocess.Popen[bytes] | None], monotonic: Callable[[], float]
) -> None:
    alive = [process for process in processes if _process_alive(process)]
    for process in alive:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    deadline = monotonic() + _PROCESS_STOP_GRACE_SECONDS
    while alive and monotonic() < deadline:
        alive = [process for process in alive if _process_alive(process)]
        if alive:
            time.sleep(0.05)
    for process in alive:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    for process in alive:
        try:
            process.wait(timeout=1.0)
        except (subprocess.TimeoutExpired, OSError):
            pass


def _command_is_true(response: str) -> bool:
    return response.strip().lower() in {"true", "true\n"}


def _health_check() -> int:
    request = urllib.request.Request("http://127.0.0.1:18080/healthz", method="GET")
    try:
        with urllib.request.urlopen(request, timeout=_CALLBACK_TIMEOUT_SECONDS) as response:
            return 0 if response.status == 204 else 1
    except (urllib.error.URLError, TimeoutError, OSError):
        return 1


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="supervisor.py")
    parser.add_argument("command", choices=("run", "validate-config", "health-check"))
    arguments = parser.parse_args(argv)
    if arguments.command == "health-check":
        return _health_check()
    try:
        config = Config.from_environ(os.environ)
    except _ConfigurationError as error:
        # The exception only contains an environment key.  Never echo the
        # rejected value because it can be a callback token or RTMP key.
        print(f"invalid configuration: {error}", file=sys.stderr)
        return 1
    if arguments.command == "validate-config":
        return 0
    return Supervisor(config).run()


if __name__ == "__main__":
    raise SystemExit(main())
