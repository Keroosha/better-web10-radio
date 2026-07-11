#!/bin/sh
set -eu

for command in python3 liquidsoap Xvfb chromium ffmpeg; do
    command -v "$command" >/dev/null
done

# Validate the same required configuration that the supervised runtime uses.
python3 ./scripts/supervisor.py validate-config
liquidsoap --check ./liquidsoap/web10.liq

width="${WEB10_STREAM__WIDTH:-1280}"
height="${WEB10_STREAM__HEIGHT:-720}"
framerate="${WEB10_STREAM__FRAMERATE:-30}"
display_number=$((100 + ($$ % 800)))
display=":${display_number}"
runtime_dir=$(mktemp -d)
chromium_profile="${runtime_dir}/chromium"
flv_output="${runtime_dir}/capture.flv"
xvfb_pid=""
chromium_pid=""

cleanup() {
    if [ -n "$chromium_pid" ] && kill -0 "$chromium_pid" 2>/dev/null; then
        kill "$chromium_pid" 2>/dev/null || true
        wait "$chromium_pid" 2>/dev/null || true
    fi

    if [ -n "$xvfb_pid" ] && kill -0 "$xvfb_pid" 2>/dev/null; then
        kill "$xvfb_pid" 2>/dev/null || true
        wait "$xvfb_pid" 2>/dev/null || true
    fi

    rm -rf "$runtime_dir"
}

trap cleanup 0
trap 'exit 1' HUP INT TERM

if [ -e "/tmp/.X11-unix/X${display_number}" ]; then
    echo "temporary X display is unavailable" >&2
    exit 1
fi

Xvfb "$display" -screen 0 "${width}x${height}x24" -nolisten tcp >/dev/null 2>&1 &
xvfb_pid=$!

attempt=0
while [ ! -S "/tmp/.X11-unix/X${display_number}" ]; do
    if ! kill -0 "$xvfb_pid" 2>/dev/null; then
        echo "Xvfb exited before creating its display socket" >&2
        exit 1
    fi

    attempt=$((attempt + 1))
    if [ "$attempt" -ge 50 ]; then
        echo "Xvfb did not create its display socket" >&2
        exit 1
    fi
    sleep 0.1
done

DISPLAY="$display" chromium \
    --kiosk \
    --no-sandbox \
    --enable-unsafe-swiftshader \
    --autoplay-policy=no-user-gesture-required \
    "--window-size=${width},${height}" \
    "--user-data-dir=${chromium_profile}" \
    about:blank >/dev/null 2>&1 &
chromium_pid=$!

sleep 1
if ! kill -0 "$chromium_pid" 2>/dev/null; then
    echo "Chromium exited during X11 capability check" >&2
    exit 1
fi

DISPLAY="$display" ffmpeg -hide_banner -loglevel error -y \
    -f x11grab -framerate "$framerate" -video_size "${width}x${height}" -i "$display" \
    -f lavfi -i anullsrc=r=48000:cl=stereo \
    -t 3 -map 0:v:0 -map 1:a:0 \
    -c:v libx264 -pix_fmt yuv420p -preset veryfast -b:v 2500k -g $((framerate * 2)) \
    -c:a aac -ar 48000 -b:a "${WEB10_STREAM__BITRATE_KBPS:-192}k" \
    -f flv "$flv_output"

if [ ! -s "$flv_output" ]; then
    echo "FFmpeg did not produce an FLV capture" >&2
    exit 1
fi
