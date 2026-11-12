#!/usr/bin/env bash
# Installs ffmpeg and ffprobe on a CI runner. The runners' Ubuntu mirror sometimes stalls for many minutes, so try apt once
# with a short bound and fall back to a stable-release static build from GitHub; fail loudly if neither works.
set -uo pipefail

if ! command -v ffprobe >/dev/null; then
  timeout 120 sudo apt-get -o Acquire::Retries=1 -o Acquire::http::Timeout=15 update \
    && timeout 240 sudo apt-get -o Acquire::Retries=1 -o Acquire::http::Timeout=15 install -y --no-install-recommends ffmpeg \
    || echo "apt did not work; using a static build instead"
fi

if ! command -v ffprobe >/dev/null; then
  tmp=$(mktemp -d)
  curl -fsSL --retry 3 "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz" -o "$tmp/ffmpeg.tar.xz"
  tar -xJf "$tmp/ffmpeg.tar.xz" -C "$tmp"
  sudo cp "$tmp"/*/bin/ffmpeg "$tmp"/*/bin/ffprobe /usr/local/bin/
fi

ffmpeg -version | head -1
command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null
