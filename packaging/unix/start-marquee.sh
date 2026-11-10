#!/usr/bin/env bash
# Starts Marquee. Everything it stores (database, artwork, recordings, secret key) lives in the "data" folder next to this file.
cd "$(dirname "$0")" || exit 1
HERE="$PWD"
export PATH="$PATH:/opt/homebrew/bin:/usr/local/bin"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:8080}"
export Database__Path="${Database__Path:-$HERE/data/mediaserver.db}"
export Images__CacheDir="${Images__CacheDir:-$HERE/data/images}"
if [ -x "$HERE/ffmpeg/ffmpeg" ]; then
  export Tools__FfmpegPath="${Tools__FfmpegPath:-$HERE/ffmpeg/ffmpeg}"
  export Tools__FfprobePath="${Tools__FfprobePath:-$HERE/ffmpeg/ffprobe}"
elif ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg was not found. Install it first (macOS: brew install ffmpeg; Debian/Ubuntu: sudo apt install ffmpeg)."; exit 1
fi
echo "Marquee is starting at http://localhost:8080  (press Ctrl+C to stop)"
( sleep 3; { open "http://localhost:8080" || xdg-open "http://localhost:8080"; } >/dev/null 2>&1 ) &
exec "$HERE/MediaServer.Api"
