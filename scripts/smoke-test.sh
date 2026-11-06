#!/usr/bin/env bash
# Smoke-tests a built image the way the cluster runs it: non-root, read-only root filesystem, read-only library.
# Usage: scripts/smoke-test.sh <image>
set -euo pipefail
IMG="${1:?usage: smoke-test.sh <image>}"
WORK="$(mktemp -d)"; NAME="ms-smoke-$$"
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; docker volume rm "$NAME-data" "$NAME-tx" >/dev/null 2>&1 || true; rm -rf "$WORK"; }
trap cleanup EXIT
fail() { echo "SMOKE FAIL: $*" >&2; docker logs "$NAME" 2>&1 | tail -30 >&2 || true; exit 1; }

echo "== ffmpeg has NVENC + libx264"
ENC="$(docker run --rm --entrypoint ffmpeg "$IMG" -hide_banner -encoders)"
grep -q h264_nvenc <<<"$ENC" || fail "h264_nvenc encoder missing"
grep -q libx264 <<<"$ENC" || fail "libx264 encoder missing"

echo "== runs as non-root"
[ "$(docker run --rm --entrypoint id "$IMG" -u)" != 0 ] || fail "container runs as root"

echo "== test library: browser-unplayable 4:4:4 H.264 (forces a real transcode) + a normal MP4"
mkdir -p "$WORK/lib/Movies"
ffmpeg -v error -y -f lavfi -i testsrc=d=4:s=320x240:r=25 -f lavfi -i sine=d=4 -c:v libx264 -pix_fmt yuv444p -c:a aac "$WORK/lib/Movies/Odd Clip (2020).mp4"
ffmpeg -v error -y -f lavfi -i testsrc=d=4:s=320x240:r=25 -f lavfi -i sine=d=4 -c:v libx264 -pix_fmt yuv420p -c:a aac "$WORK/lib/Movies/Normal Clip (2021).mp4"
chmod -R a+rX "$WORK/lib"

docker volume create "$NAME-data" >/dev/null; docker volume create "$NAME-tx" >/dev/null
docker run -d --name "$NAME" -p 18080:8080 --read-only --tmpfs /tmp -e Library__AutoScan=false \
  -v "$NAME-data:/data" -v "$NAME-tx:/transcode" -v "$WORK/lib:/media:ro" "$IMG" >/dev/null

for i in $(seq 40); do curl -fsS localhost:18080/health >/dev/null 2>&1 && break; sleep 1; [ "$i" = 40 ] && fail "/health never became ready"; done
echo "== health ok"
curl -fsS localhost:18080/ | grep -q "MediaServer" || fail "web client not served"

echo "== sign-in: closed until the first account exists, then open to that session only"
JAR="$WORK/cookies.txt"
[ "$(curl -s -o /dev/null -w '%{http_code}' localhost:18080/api/items)" = 401 ] || fail "the API answered without sign-in"
[ "$(curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-9' localhost:18080/stream/2)" = 401 ] || fail "a stream was served without sign-in"
curl -fsS -c "$JAR" -H 'content-type: application/json' -d '{"username":"smoke","password":"smoke-test-password-1"}' localhost:18080/api/auth/setup >/dev/null || fail "first-run setup failed"
[ "$(curl -s -o /dev/null -w '%{http_code}' -H 'content-type: application/json' -d '{"username":"intruder","password":"another-password-1"}' localhost:18080/api/auth/setup)" = 409 ] || fail "a second account could be created"
curl -fsS -b "$JAR" localhost:18080/api/items >/dev/null || fail "signed-in request was refused"

SCAN="$(curl -fsS -b "$JAR" -X POST localhost:18080/api/library/scan)"; echo "scan: $SCAN"
grep -q '"added":2' <<<"$SCAN" || fail "scan did not add 2 files"
grep -q '"probeErrors":0' <<<"$SCAN" || fail "ffprobe failed inside the container"

echo "== direct-play range request"
curl -fsS -b "$JAR" -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' localhost:18080/stream/2 | grep -q 206 || fail "range request not 206"

echo "== HLS transcode of the unplayable file (libx264, read-only rootfs)"
S="$(curl -fsS -b "$JAR" -X POST -H 'content-type: application/json' -d '{"mediaId":1}' localhost:18080/api/hls/start)" || fail "hls start failed"
SID="$(sed -E 's/.*"sessionId":"([0-9a-f]+)".*/\1/' <<<"$S")"
for i in $(seq 40); do curl -fsS -b "$JAR" "localhost:18080/api/hls/$SID/index.m3u8" | grep -q ENDLIST && break; sleep 0.5; done
curl -fsS -b "$JAR" "localhost:18080/api/hls/$SID/index.m3u8" | grep -q ENDLIST || fail "playlist never completed"
curl -fsS -b "$JAR" -o /dev/null "localhost:18080/api/hls/$SID/seg00000.ts" || fail "segment not served"
curl -fsS -b "$JAR" -o /dev/null -X POST "localhost:18080/api/hls/$SID/stop"
echo "SMOKE OK"
