#!/bin/sh
set -eu

if [ "$#" -ne 2 ] || [ -z "$1" ] || [ -z "$2" ]; then
    echo "start-chromium: expected <profile-path> <stage-url>" >&2
    exit 2
fi

profile_path="$1"
stage_url="$2"
backend="${WEB10_STREAM__GRAPHICS_BACKEND-swiftshader}"
width="${WEB10_STREAM__WIDTH:-1280}"
height="${WEB10_STREAM__HEIGHT:-720}"
crash_report_dir="${HOME:-/home/web10}/.config/chromium/Crash Reports/pending"

mkdir -p "$crash_report_dir"
for report in "$crash_report_dir"/*.dmp "$crash_report_dir"/*.meta; do
    if [ -f "$report" ]; then
        mv "$report" "${report}.handled"
    fi
done

set -- \
    --kiosk \
    --no-sandbox \
    --autoplay-policy=no-user-gesture-required \
    "--window-size=${width},${height}" \
    "--user-data-dir=${profile_path}" \
    --enable-logging=stderr \
    --log-level=1

case "$backend" in
    swiftshader)
        set -- "$@" --enable-unsafe-swiftshader
        ;;
    vulkan)
        export VK_DRIVER_FILES=/usr/share/vulkan/icd.d/radeon_icd.json
        if ! vulkan_summary="$(vulkaninfo --summary 2>&1)"; then
            printf '%s\n' "$vulkan_summary" >&2
            echo "start-chromium: RADV Vulkan device vendor 0x1002 unavailable" >&2
            exit 1
        fi
        if ! printf '%s\n' "$vulkan_summary" | grep -Eq 'vendorID[[:space:]]*=[[:space:]]*0x1002'; then
            printf '%s\n' "$vulkan_summary" >&2
            echo "start-chromium: RADV Vulkan device vendor 0x1002 unavailable" >&2
            exit 1
        fi
        set -- "$@" \
            --enable-gpu \
            --ignore-gpu-blocklist \
            --use-gl=angle \
            --use-angle=vulkan \
            --disable-software-rasterizer
        ;;
    *)
        echo "start-chromium: invalid WEB10_STREAM__GRAPHICS_BACKEND: ${backend}" >&2
        exit 2
        ;;
esac

chromium_pid=""

stop_chromium() {
    if [ -n "$chromium_pid" ] && kill -0 "$chromium_pid" 2>/dev/null; then
        kill "$chromium_pid" 2>/dev/null || true
        wait "$chromium_pid" 2>/dev/null || true
    fi
}

on_signal() {
    stop_chromium
    exit 143
}

trap on_signal HUP INT TERM

chromium "$@" "$stage_url" &
chromium_pid=$!

while kill -0 "$chromium_pid" 2>/dev/null; do
    for report in "$crash_report_dir"/*.dmp; do
        if [ -f "$report" ]; then
            echo "start-chromium: child crash report detected: ${report}" >&2
            stop_chromium
            exit 70
        fi
    done
    sleep 1
done

if wait "$chromium_pid"; then
    exit 0
else
    exit $?
fi
