#!/bin/sh
set -eu

exec curl \
    --proxy socks5h://127.0.0.1:1080 \
    --fail \
    --silent \
    --show-error \
    --output /dev/null \
    --connect-timeout 5 \
    --max-time 10 \
    https://api.telegram.org/
