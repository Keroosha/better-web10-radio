#!/usr/bin/env python3
"""Faked stream-node — a LOCAL DEMO AID, NOT the real B5 stream-node.

The real stream-node (src/stream-node/, phase B5) runs Xvfb + kiosk Chromium +
Liquidsoap + FFmpeg and pushes RTMP to Telegram. This script does none of that. It
only speaks the backend's stream-node callback contract so the public stage flips to
LIVE and (optionally) the playback queue advances while you develop the frontend.

It:
  * POSTs a `live` heartbeat every WEB10_FAKE_HEARTBEAT_SECONDS so /player/state reports
    stream.status = "live" (the backend treats a heartbeat fresh within ~30s as live);
  * reads the current fenced assignment from GET /stream-node/playback/current and, when
    present, renews its lease and reports `played` after the (capped) track duration so
    NOW PLAYING / QUEUE keep moving;
  * on Ctrl-C / SIGTERM sends a best-effort `offline` heartbeat.

Only the Python 3 standard library is used (no jq, no pip). Configure via env:

  WEB10_API__BASE_URL             backend base URL           (default http://localhost:8080)
  WEB10_STREAM__CALLBACK_TOKEN    bearer token               (REQUIRED — same value as the API)
  WEB10_FAKE_HEARTBEAT_SECONDS    heartbeat/poll cadence     (default 10)
  WEB10_FAKE_MAX_TRACK_SECONDS    cap before advancing       (default 30)
  WEB10_FAKE_BITRATE_KBPS         reported bitrate           (default 192)
  WEB10_FAKE_STATUS               live|starting              (default live)
  WEB10_FAKE_ADVANCE              true|false advance queue   (default true)

Usage:
  WEB10_STREAM__CALLBACK_TOKEN=... python3 scripts/fake-stream-node.py
"""

import json
import os
import signal
import sys
import time
import urllib.error
import urllib.request

BASE_URL = os.environ.get("WEB10_API__BASE_URL", "http://localhost:8080").rstrip("/")
TOKEN = os.environ.get("WEB10_STREAM__CALLBACK_TOKEN", "")
HEARTBEAT_SECONDS = float(os.environ.get("WEB10_FAKE_HEARTBEAT_SECONDS", "10"))
MAX_TRACK_SECONDS = float(os.environ.get("WEB10_FAKE_MAX_TRACK_SECONDS", "30"))
BITRATE_KBPS = int(os.environ.get("WEB10_FAKE_BITRATE_KBPS", "192"))
STATUS = os.environ.get("WEB10_FAKE_STATUS", "live")
ADVANCE = os.environ.get("WEB10_FAKE_ADVANCE", "true").lower() != "false"

STREAM_NODE = BASE_URL + "/api/v0/stream-node"
_running = True


def _request(method, path, body=None):
    """Return (status_code, parsed_json_or_None). Never raises for HTTP status codes."""
    url = STREAM_NODE + path
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", "Bearer " + TOKEN)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read()
            if response.status == 204 or not raw:
                return response.status, None
            return response.status, json.loads(raw.decode("utf-8"))
    except urllib.error.HTTPError as error:
        return error.code, None
    except urllib.error.URLError as error:
        print(f"[fake-stream-node] {method} {path} failed: {error.reason}", file=sys.stderr)
        return 0, None


def _heartbeat(status, active_queue_item_id, restart_attempt=0):
    payload = {
        "status": status,
        "failureReason": None,
        "metadata": {
            "bitrateKbps": BITRATE_KBPS,
            "restartAttempt": restart_attempt,
            "activeQueueItemId": active_queue_item_id,
        },
    }
    code, _ = _request("POST", "/heartbeat", payload)
    if code != 204:
        print(f"[fake-stream-node] heartbeat status={status} -> HTTP {code}", file=sys.stderr)


def _renew_lease(assignment):
    _request(
        "POST",
        f"/playback/{assignment['queueItemId']}/lease",
        {"claimOwner": assignment["claimOwner"], "claimAttempt": assignment["claimAttempt"]},
    )


def _complete(assignment):
    code, _ = _request(
        "POST",
        f"/playback/{assignment['queueItemId']}/completion",
        {"claimOwner": assignment["claimOwner"], "claimAttempt": assignment["claimAttempt"], "status": "played"},
    )
    label = f"{assignment.get('artist', '?')} - {assignment.get('title', '?')}"
    print(f"[fake-stream-node] completed {label} -> HTTP {code}")


def _stop(_signum, _frame):
    global _running
    _running = False


def main():
    if not TOKEN:
        print("[fake-stream-node] WEB10_STREAM__CALLBACK_TOKEN is required.", file=sys.stderr)
        return 2

    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)

    print(f"[fake-stream-node] simulating a LIVE node against {BASE_URL} (Ctrl-C to stop).")
    current_id = None
    started_at = 0.0

    while _running:
        code, assignment = _request("GET", "/playback/current", None)

        if code == 200 and assignment:
            queue_item_id = assignment["queueItemId"]
            if queue_item_id != current_id:
                current_id = queue_item_id
                started_at = time.monotonic()
                print(f"[fake-stream-node] now playing {assignment.get('artist', '?')} - {assignment.get('title', '?')}")

            _heartbeat(STATUS, queue_item_id)
            _renew_lease(assignment)

            duration_seconds = max(1.0, assignment.get("durationMs", 0) / 1000.0)
            play_for = min(duration_seconds, MAX_TRACK_SECONDS)
            if ADVANCE and (time.monotonic() - started_at) >= play_for:
                _complete(assignment)
                current_id = None
        else:
            # No assignment yet (queue empty). Still report the node so the stage renders LIVE.
            current_id = None
            _heartbeat(STATUS, None)

        for _ in range(int(max(1, HEARTBEAT_SECONDS))):
            if not _running:
                break
            time.sleep(1)

    _heartbeat("offline", None)
    print("[fake-stream-node] sent offline heartbeat, exiting.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
