Marquee for macOS and Linux
===========================
1. Unpack this folder anywhere you can write to.
2. Run ./start-marquee.sh (macOS: you can also double-click Start-Marquee.command).
   macOS only: ffmpeg is not bundled. Install it once with "brew install ffmpeg". The first launch may be blocked
   because the app is unsigned: right-click Start-Marquee.command, choose Open, or run: xattr -dr com.apple.quarantine <this folder>
   Linux: the bundled ffmpeg is used; if there is none, install ffmpeg from your package manager.
3. Your browser opens http://localhost:8080. Create the first account, then add your media folders.

Data is kept in the "data" folder next to the launcher. To update, unpack the new version over the old one and keep "data".
