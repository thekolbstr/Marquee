@echo off
setlocal
cd /d "%~dp0"
title Marquee
rem Everything Marquee stores (database, artwork, recordings, secret key) lives in the "data" folder next to this file.
if not defined ASPNETCORE_URLS set "ASPNETCORE_URLS=http://0.0.0.0:8080"
if not defined Database__Path set "Database__Path=%~dp0data\mediaserver.db"
if not defined Images__CacheDir set "Images__CacheDir=%~dp0data\images"
if exist "%~dp0ffmpeg\ffmpeg.exe" (
  if not defined Tools__FfmpegPath set "Tools__FfmpegPath=%~dp0ffmpeg\ffmpeg.exe"
  if not defined Tools__FfprobePath set "Tools__FfprobePath=%~dp0ffmpeg\ffprobe.exe"
)
echo Marquee is starting at http://localhost:8080  (close this window to stop it)
start "" "http://localhost:8080"
"%~dp0MediaServer.Api.exe"
pause
