#!/usr/bin/env python3
"""Bounded Compose smoke runner for the documented admin and RTMP contracts.

The runner deliberately treats all server payloads as private input: it uses the
values required to drive the smoke flow but never writes response data,
credentials, cookies, CSRF tokens, stream names, or RTMP keys to stdout/stderr.
"""

from __future__ import annotations

import argparse
import http.cookiejar
import json
import math
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as element_tree
from dataclasses import dataclass
from typing import Any, Callable


API_PREFIX = "/api/v0/admin"
SMOKE_TRACK_TITLE = "web10-compose-smoke"
REQUEST_TIMEOUT_SECONDS = 5.0
POLL_INTERVAL_SECONDS = 1.0
MAX_RESPONSE_BYTES = 2 * 1024 * 1024


@dataclass(frozen=True)
class Deadline:
    """A monotonic deadline shared by a complete smoke mode."""

    end_monotonic: float

    @classmethod
    def from_timeout(cls, timeout_seconds: float) -> "Deadline":
        return cls(time.monotonic() + timeout_seconds)

    def remaining(self) -> float:
        return max(0.0, self.end_monotonic - time.monotonic())

    def request_timeout(self) -> float:
        remaining = self.remaining()
        if remaining <= 0.0:
            raise OperationError("deadline", "timeout")
        return min(REQUEST_TIMEOUT_SECONDS, remaining)

    def sleep_until_next_poll(self) -> None:
        remaining = self.remaining()
        if remaining > 0.0:
            time.sleep(min(POLL_INTERVAL_SECONDS, remaining))


class OperationError(Exception):
    """A deliberately redacted failure: callers may report only these fields."""

    def __init__(self, operation: str, status: str) -> None:
        super().__init__(operation, status)
        self.operation = operation
        self.status = status


@dataclass(frozen=True)
class Response:
    status: int | None
    body: bytes | None

    @property
    def status_text(self) -> str:
        return "unavailable" if self.status is None else str(self.status)


class AdminHttpClient:
    """Small stdlib-only client for the cookie and synchronizer-token contract."""

    def __init__(self, base_url: str, rtmp_stat_url: str) -> None:
        self._base_url = validate_base_url(base_url, "base-url")
        self._rtmp_stat_url = validate_http_url(rtmp_stat_url, "rtmp-stat-url")
        self._cookies = http.cookiejar.CookieJar()
        self._opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self._cookies))
        self._csrf_token: str | None = None

    def login(self, username: str, password: str, deadline: Deadline) -> None:
        response = self._request_json(
            "login",
            "POST",
            "/api/v0/admin/auth/login",
            {"username": username, "password": password},
            deadline,
            include_csrf=False,
        )
        payload = require_json_object("login", response, 200)
        csrf_token = payload.get("csrfToken")
        if not isinstance(csrf_token, str) or not csrf_token:
            raise OperationError("login", "invalid-response")
        if not self._has_admin_session_cookie():
            raise OperationError("login", "invalid-response")
        self._csrf_token = csrf_token

    def get_admin(self, operation: str, path: str, deadline: Deadline) -> Response:
        return self._request("GET", operation, path, None, deadline, include_csrf=False)

    def post_admin(self, operation: str, path: str, payload: dict[str, Any], deadline: Deadline) -> Response:
        return self._request_json(operation, "POST", path, payload, deadline, include_csrf=True)

    def get_rtmp_stat(self, deadline: Deadline) -> Response:
        return self._request_url("GET", "rtmp-stat", self._rtmp_stat_url, None, deadline, {})

    def _request_json(
        self,
        operation: str,
        method: str,
        path: str,
        payload: dict[str, Any],
        deadline: Deadline,
        *,
        include_csrf: bool,
    ) -> Response:
        return self._request(method, operation, path, payload, deadline, include_csrf=include_csrf)

    def _request(
        self,
        method: str,
        operation: str,
        path: str,
        payload: dict[str, Any] | None,
        deadline: Deadline,
        *,
        include_csrf: bool,
    ) -> Response:
        if not path.startswith("/"):
            raise OperationError(operation, "invalid-path")
        headers: dict[str, str] = {"Accept": "application/json", "User-Agent": "web10-backend-smoke"}
        if include_csrf:
            if self._csrf_token is None:
                raise OperationError(operation, "not-authenticated")
            headers["X-CSRF-Token"] = self._csrf_token
        return self._request_url(method, operation, self._base_url + path, payload, deadline, headers)

    def _request_url(
        self,
        method: str,
        operation: str,
        url: str,
        payload: dict[str, Any] | None,
        deadline: Deadline,
        headers: dict[str, str],
    ) -> Response:
        data: bytes | None = None
        request_headers = dict(headers)
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
            request_headers["Content-Type"] = "application/json; charset=utf-8"

        request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
        try:
            with self._opener.open(request, timeout=deadline.request_timeout()) as response:
                return Response(response.status, read_limited(response))
        except urllib.error.HTTPError as error:
            # Do not read error bodies: problem-details payloads are intentionally never displayed.
            error.close()
            return Response(error.code, None)
        except (OSError, TimeoutError, urllib.error.URLError):
            return Response(None, None)

    def _has_admin_session_cookie(self) -> bool:
        return any(cookie.name == "web10_admin_session" and bool(cookie.value) for cookie in self._cookies)


class SmokeRunner:
    def __init__(self, client: AdminHttpClient, deadline: Deadline) -> None:
        self._client = client
        self._deadline = deadline

    def authenticate(self, username: str, password: str) -> None:
        self._client.login(username, password, self._deadline)

    def run_live(self) -> None:
        track_id = self._scan_and_find_track()
        self._enqueue_track(track_id)
        self._wait_for_live_rtmp()

    def run_restart_live(self) -> None:
        self._stream_control("restart")
        track_id = self._find_smoke_track()
        self._enqueue_track(track_id)
        self._wait_for_live_rtmp()

    def run_expect_output_failure(self) -> None:
        def output_failed() -> tuple[bool, str | None]:
            snapshot, http_status = self._get_stream_status()
            if snapshot is None:
                return False, http_status
            accepted_statuses = {"degraded", "restarting", "failed"}
            return (
                snapshot.get("status") in accepted_statuses
                and snapshot.get("failureReason") == "RTMP output failed",
                None,
            )

        self._wait_for("output-failure", output_failed)

    def run_recover(self) -> None:
        first_restart = self._stream_control("restart")
        track_id = self._find_smoke_track()
        self._enqueue_track(track_id)
        self._wait_for_live_rtmp()

        stopped = self._stream_control("stop")
        self._wait_for_offline("stream-stop", required_generation=stopped, require_stopped=True)

        second_restart = self._stream_control("restart")
        if second_restart <= first_restart:
            raise OperationError("stream-restart", "invalid-response")
        self._wait_for_resumed(second_restart)

    def run_expect_offline(self) -> None:
        self._wait_for_offline("offline")

    def _scan_and_find_track(self) -> str:
        response = self._client.post_admin("library-scan", f"{API_PREFIX}/library/scan", {}, self._deadline)
        payload = require_json_object("library-scan", response, 202)
        scan_job_id = require_nonempty_string("library-scan", payload, "scanJobId")
        self._wait_for_scan(scan_job_id)
        return self._find_smoke_track()

    def _wait_for_scan(self, scan_job_id: str) -> None:
        path = f"{API_PREFIX}/library/scan/{urllib.parse.quote(scan_job_id, safe='')}"

        def scan_completed() -> tuple[bool, str | None]:
            response = self._client.get_admin("library-scan", path, self._deadline)
            if response.status != 200:
                return False, response.status_text
            payload = require_json_object("library-scan", response, 200)
            scan_status = payload.get("status")
            if scan_status == "failed":
                raise OperationError("library-scan", "failed")
            if not isinstance(scan_status, str):
                raise OperationError("library-scan", "invalid-response")
            return scan_status == "completed", None

        self._wait_for("library-scan", scan_completed)

    def _find_smoke_track(self) -> str:
        query = urllib.parse.urlencode({"query": SMOKE_TRACK_TITLE, "limit": "100"})
        response = self._client.get_admin("track-lookup", f"{API_PREFIX}/tracks?{query}", self._deadline)
        if response.status != 200:
            raise OperationError("track-lookup", response.status_text)
        tracks = require_json_array("track-lookup", response, 200)
        matches: list[dict[str, Any]] = []
        for candidate in tracks:
            if isinstance(candidate, dict) and candidate.get("title") == SMOKE_TRACK_TITLE:
                matches.append(candidate)

        if not matches:
            raise OperationError("track-lookup", "not-found")
        if len(matches) != 1:
            raise OperationError("track-lookup", "ambiguous")

        track_id = matches[0].get("id")
        if not isinstance(track_id, str) or not track_id or matches[0].get("hasCachedFile") is not True:
            raise OperationError("track-lookup", "invalid-response")
        return track_id

    def _enqueue_track(self, track_id: str) -> None:
        response = self._client.post_admin(
            "playback-queue",
            f"{API_PREFIX}/playback/queue",
            {"trackId": track_id},
            self._deadline,
        )
        payload = require_json_object("playback-queue", response, 202)
        require_nonempty_string("playback-queue", payload, "queueItemId")

    def _stream_control(self, action: str) -> int:
        response = self._client.post_admin(
            f"stream-{action}",
            f"{API_PREFIX}/stream-node/{action}",
            {},
            self._deadline,
        )
        payload = require_json_object(f"stream-{action}", response, 202)
        generation = payload.get("restartGeneration")
        if isinstance(generation, bool) or not isinstance(generation, int) or generation < 0:
            raise OperationError(f"stream-{action}", "invalid-response")
        return generation

    def _get_stream_status(self) -> tuple[dict[str, Any] | None, str | None]:
        response = self._client.get_admin("stream-status", f"{API_PREFIX}/stream-node/status", self._deadline)
        if response.status != 200:
            return None, response.status_text
        return require_json_object("stream-status", response, 200), None

    def _wait_for_live_rtmp(self) -> None:
        def live_rtmp() -> tuple[bool, str | None]:
            stream_status, stream_http_status = self._get_stream_status()
            if stream_status is None:
                return False, stream_http_status

            heartbeat_is_live = (
                stream_status.get("status") == "live"
                and stream_status.get("bitrateKbps") == 192
                and isinstance(stream_status.get("lastHeartbeatUtc"), str)
                and bool(stream_status["lastHeartbeatUtc"])
                and stream_status.get("failureReason") is None
            )
            if not heartbeat_is_live:
                return False, None

            rtmp_response = self._client.get_rtmp_stat(self._deadline)
            if rtmp_response.status != 200 or rtmp_response.body is None:
                return False, rtmp_response.status_text
            return rtmp_stat_has_active_stream(rtmp_response.body), None

        self._wait_for("live", live_rtmp)

    def _wait_for_offline(
        self,
        operation: str,
        required_generation: int | None = None,
        require_stopped: bool = False,
    ) -> None:
        def offline() -> tuple[bool, str | None]:
            snapshot, http_status = self._get_stream_status()
            if snapshot is None:
                return False, http_status
            if required_generation is not None and snapshot.get("restartGeneration") != required_generation:
                return False, None
            return (
                snapshot.get("status") == "offline"
                and (not require_stopped or snapshot.get("desiredState") == "stopped"),
                None,
            )

        self._wait_for(operation, offline)

    def _wait_for_resumed(self, required_generation: int) -> None:
        def resumed() -> tuple[bool, str | None]:
            snapshot, http_status = self._get_stream_status()
            if snapshot is None:
                return False, http_status
            generation = snapshot.get("restartGeneration")
            if isinstance(generation, bool) or not isinstance(generation, int):
                raise OperationError("stream-restart", "invalid-response")
            return (
                generation >= required_generation
                and snapshot.get("desiredState") == "running"
                and snapshot.get("status") in {"starting", "live"},
                None,
            )

        self._wait_for("stream-restart", resumed)

    def _wait_for(self, operation: str, probe: Callable[[], tuple[bool, str | None]]) -> None:
        last_http_status: str | None = None
        while self._deadline.remaining() > 0.0:
            complete, observed_http_status = probe()
            if complete:
                return
            if observed_http_status is not None:
                last_http_status = observed_http_status
            self._deadline.sleep_until_next_poll()
        raise OperationError(operation, last_http_status or "timeout")


def read_limited(response: Any) -> bytes:
    body = response.read(MAX_RESPONSE_BYTES + 1)
    if len(body) > MAX_RESPONSE_BYTES:
        return b""
    return body


def decode_json(operation: str, response: Response, expected_status: int) -> Any:
    if response.status != expected_status:
        raise OperationError(operation, response.status_text)
    if response.body is None or response.body == b"":
        raise OperationError(operation, "invalid-response")
    try:
        return json.loads(response.body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise OperationError(operation, "invalid-response") from None


def require_json_object(operation: str, response: Response, expected_status: int) -> dict[str, Any]:
    payload = decode_json(operation, response, expected_status)
    if not isinstance(payload, dict):
        raise OperationError(operation, "invalid-response")
    return payload


def require_json_array(operation: str, response: Response, expected_status: int) -> list[Any]:
    payload = decode_json(operation, response, expected_status)
    if not isinstance(payload, list):
        raise OperationError(operation, "invalid-response")
    return payload


def require_nonempty_string(operation: str, payload: dict[str, Any], field: str) -> str:
    value = payload.get(field)
    if not isinstance(value, str) or not value:
        raise OperationError(operation, "invalid-response")
    return value


def rtmp_stat_has_active_stream(document: bytes) -> bool:
    try:
        root = element_tree.fromstring(document)
    except element_tree.ParseError:
        raise OperationError("rtmp-stat", "invalid-response") from None

    # Debian's nginx-rtmp 1.1.4 reports an active publisher through its root
    # connection counters, but does not emit a <stream> node when no viewers
    # are attached. Both values are session-scoped and reset to zero after the
    # publisher disconnects, so they prove an active ingress without logging
    # the private stream key.
    counters = {
        local_xml_name(element.tag): (element.text or "").strip()
        for element in root
        if local_xml_name(element.tag) in {"naccepted", "bytes_in"}
    }
    try:
        return int(counters.get("naccepted", "0")) > 0 and int(counters.get("bytes_in", "0")) > 0
    except ValueError:
        return False


def local_xml_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def validate_http_url(value: str, argument: str) -> str:
    try:
        parsed = urllib.parse.urlsplit(value)
    except ValueError:
        raise OperationError("arguments", "invalid") from None
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise OperationError("arguments", "invalid")
    if parsed.username is not None or parsed.password is not None:
        raise OperationError("arguments", "invalid")
    return value


def validate_base_url(value: str, argument: str) -> str:
    normalized = validate_http_url(value, argument)
    parsed = urllib.parse.urlsplit(normalized)
    if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
        raise OperationError("arguments", "invalid")
    return normalized.rstrip("/")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run the bounded backend/RTMP Compose smoke modes without displaying private payload data."
    )
    parser.add_argument(
        "--mode",
        choices=("live", "restart-live", "expect-output-failure", "recover", "expect-offline"),
        required=True,
        help="Smoke phase to execute.",
    )
    parser.add_argument("--base-url", required=True, help="API origin, for example http://localhost:8080.")
    parser.add_argument("--rtmp-stat-url", required=True, help="Local RTMP sink status endpoint.")
    parser.add_argument("--username", required=True, help="Admin username used only for the login request.")
    parser.add_argument("--password", required=True, help="Admin password used only for the login request.")
    parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=60.0,
        metavar="SECONDS",
        help="Monotonic deadline for the complete mode (default: 60).",
    )
    return parser


def validate_arguments(args: argparse.Namespace) -> None:
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0.0:
        raise OperationError("arguments", "invalid")
    if not isinstance(args.username, str) or not isinstance(args.password, str):
        raise OperationError("arguments", "invalid")


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        validate_arguments(args)
        deadline = Deadline.from_timeout(args.timeout_seconds)
        client = AdminHttpClient(args.base_url, args.rtmp_stat_url)
        runner = SmokeRunner(client, deadline)
        runner.authenticate(args.username, args.password)

        if args.mode == "live":
            runner.run_live()
        elif args.mode == "restart-live":
            runner.run_restart_live()
        elif args.mode == "expect-output-failure":
            runner.run_expect_output_failure()
        elif args.mode == "recover":
            runner.run_recover()
        else:
            runner.run_expect_offline()
    except OperationError as failure:
        print(f"{failure.operation} status={failure.status}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("main status=interrupted", file=sys.stderr)
        return 130
    except Exception:
        # Keep unexpected runtime failures redacted just like HTTP failures.
        print("main status=failed", file=sys.stderr)
        return 1

    print(f"{args.mode} status=passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
