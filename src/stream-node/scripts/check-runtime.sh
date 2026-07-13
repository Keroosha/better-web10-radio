#!/bin/sh
set -eu

for command in liquidsoap Xvfb chromium ffmpeg ffprobe unclutter fc-match; do
    command -v "$command" >/dev/null
done

japanese_font="$(fc-match --format='%{family}' 'sans-serif:lang=ja')"
case "$japanese_font" in
    *"Noto Sans CJK"*) ;;
    *)
        echo "Japanese font fallback is unavailable: ${japanese_font}" >&2
        exit 1
        ;;
esac

# Validate the same required configuration that the F# runtime uses.
./Web10.Radio.StreamNode validate-config
liquidsoap --check ./liquidsoap/web10.liq

width="${WEB10_STREAM__WIDTH:-1280}"
height="${WEB10_STREAM__HEIGHT:-720}"
framerate="${WEB10_STREAM__FRAMERATE:-30}"
display_number=$((100 + ($$ % 800)))
display=":${display_number}"
runtime_dir=$(mktemp -d)
chromium_profile="${runtime_dir}/chromium"
flv_output="${runtime_dir}/capture.flv"
unclutter_pid=""
chromium_pid=""

cleanup() {
    if [ -n "$chromium_pid" ] && kill -0 "$chromium_pid" 2>/dev/null; then
        kill "$chromium_pid" 2>/dev/null || true
        wait "$chromium_pid" 2>/dev/null || true
    fi
    if [ -n "$unclutter_pid" ] && kill -0 "$unclutter_pid" 2>/dev/null; then
        kill "$unclutter_pid" 2>/dev/null || true
        wait "$unclutter_pid" 2>/dev/null || true
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
DISPLAY="$display" unclutter --timeout 0 --start-hidden >/dev/null 2>&1 &
unclutter_pid=$!
sleep 0.2
if ! kill -0 "$unclutter_pid" 2>/dev/null; then
    echo "unclutter exited during X11 capability check" >&2
    exit 1
fi

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

cue_source="${runtime_dir}/cue-source.flac"
cue_segment="${runtime_dir}/cue-segment.flac"
ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i sine=frequency=1000:sample_rate=48000 \
    -t 2 -map 0:a:0 -vn -sn -dn -c:a flac "$cue_source"
ffmpeg -hide_banner -loglevel error -y \
    -i "$cue_source" -ss 0.500 -t 1.000 \
    -map 0:a:0 -vn -sn -dn -c:a flac "$cue_segment"

cue_duration="$(ffprobe -v error -show_entries format=duration -of default=nokey=1:noprint_wrappers=1 "$cue_segment")"
if ! awk -v duration="$cue_duration" 'BEGIN { exit !(duration >= 0.90 && duration <= 1.10) }'; then
    echo "FFmpeg did not produce a 0.90-1.10 second FLAC CUE segment" >&2
    exit 1
fi
