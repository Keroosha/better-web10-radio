#!/bin/sh
set -eu

command -v Xvfb
command -v chromium
command -v ffmpeg
command -v liquidsoap

chromium --version
ffmpeg -version
liquidsoap --version
