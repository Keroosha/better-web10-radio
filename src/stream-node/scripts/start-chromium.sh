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

set -- \
    --kiosk \
    --no-sandbox \
    --autoplay-policy=no-user-gesture-required \
    "--window-size=${width},${height}" \
    "--user-data-dir=${profile_path}"

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

exec chromium "$@" "$stage_url"
