# Marquee

A self-hosted media server for your movies, shows and live TV: direct play first, transcoding last (.NET 10, Minimal APIs, SQLite, ffmpeg).

## Run (dev)

Needs the .NET 10 SDK and `ffmpeg`/`ffprobe` on PATH. Media is addressed by database ID, never by client-supplied path.
Point the server at a (read-only) library folder and scan it:

    export Library__Root=/path/to/media          # or: dotnet user-secrets set "Library:Root" ...
    dotnet run --project src/MediaServer.Api     # http://localhost:5087
    curl -X POST localhost:5087/api/library/scan # index new/changed/moved/deleted files (fast when nothing changed)

Open http://localhost:5087. The SQLite DB defaults to `data/mediaserver.db` (`Database:Path`); keep it on local disk.
`GET|HEAD /stream/{id}` supports single-range requests (`a-b`, `a-`, `-n`), `If-Range`, 200/206/416.

## Web UI

A dependency-free single-page app (`wwwroot/`): Home (hero, Continue Watching, Recently Added), Movies, TV Shows, search,
detail pages with season tabs, and a full-screen player (keyboard: space/k, j/l or arrows to seek, f, m, n, Esc).

- **Library scanning is automatic**: shortly after startup and every `Library:ScanIntervalMinutes` (default 360; `0` = startup only;
  `Library:AutoScan=false` disables). The refresh button in the header scans on demand. Scans run in the background, so closing
  the tab or a proxy timeout never aborts one.
- **Artwork works without TMDB**: when there's no TMDB match, cards and banners use a frame grabbed from the video
  (`GET /api/media/{id}/thumb?w=320|640|1280`, cached under `Images:CacheDir/thumbs`). With a TMDB token you get real posters/backdrops.
- **Watch progress lives on the server** (`POST /api/media/{id}/progress`), so Continue Watching and resume follow you between
  browsers. "Up next" appears when the latest episode of a show is finished.
- Safari plays HLS through its native player (HEVC in fMP4 needs it); other browsers use the bundled hls.js.

## Quick start (Docker)

    docker compose up -d --build   # see docker-compose.yml: set the media path first, then open http://localhost:8080

Everything else is configured in the browser: **gear icon → Settings**. Nothing needs to be edited by hand.

## Signing in and media folders

**First run:** open the server in a browser. With no account yet it shows *Welcome*: choose a username and password (8+ characters),
then a short wizard asks for your media folders and (optionally) a TMDB token. After that, everyone signs in on the login page.

- Passwords are stored as salted PBKDF2-SHA256 hashes. Sessions are random cookies (HttpOnly, SameSite=Lax; `Secure` over HTTPS),
  kept in the database only as hashes, valid 30 days with "Keep me signed in" or 12 hours of inactivity otherwise.
  Wrong passwords are slowed down per address; changing your password signs out your other devices.
- Everything under `/api` and `/stream` needs a session. Players that don't send cookies (Safari's native video/HLS) use short-lived
  signed links for that one file, and HLS sessions use their unguessable ids.
- `Auth__SetupCode=<anything>` makes the first-run form ask for that code, so nobody else can claim a freshly deployed server.
- Behind a reverse proxy, set `Auth__TrustProxy=true` so login throttling sees each visitor's `X-Forwarded-For` address instead of the proxy's.
- `Auth__Enabled=false` turns sign-in off entirely (for example if your proxy already authenticates). Settings and folders are then open
  to anyone who can reach the server, so folder editing in the web UI is refused unless you also set `Library__AllowFolderEditing=true`.
- Lost your password? Stop the server and delete the account: `sqlite3 mediaserver.db "DELETE FROM users;"`. The next visit shows setup again.

**Media folders** (Settings → Library): the folder the deployment mounts (`Library__Root`, `/media` in Docker) is always included; use
*Add a folder* to browse what the server can see and add more. Folders can be hidden, shown again or removed (your files are never
modified, and an offline drive or share is skipped rather than emptying the library). Overlapping folders are refused, as are system
locations and the server's own data. `Library__AllowedRoots=/media;/mnt` limits where folders may be added.

## Settings

Gear icon → Settings. **This device** settings live in the browser; **Server** settings live in the database and apply to everyone, immediately (no restart).

| Section | What's in it |
|---|---|
| **Appearance** *(device)* | accent colour, theme (midnight / pure black), text size, grid density, artwork style, reduced motion |
| **Home page** *(device)* | which rows to show (banner, quick buttons, Continue Watching, Live TV, recent movies/shows), banner rotation |
| **Playback** *(device)* | preferred quality, autoplay, resume or start over, auto-next, default speed, skip length, remember volume |
| **Captions** *(device)* | default (off / auto / always), language, size, background; **Keyboard shortcuts** reference |
| **General** | server name (header, tab title, sign-in page) |
| **Library** | media folders (add / hide / remove), automatic scanning and interval, files analysed at once |
| **Posters & details** | TMDB token / key (write-only) with a test button, metadata language, retry interval for unmatched titles |
| **Streaming & playback** | allow transcoding, encoder (software / NVIDIA), simultaneous streams, largest transcode, idle timeout, "watched" thresholds |
| **Live TV** | M3U playlist, XMLTV guide, stream address override, refresh schedule |
| **Network & security** | how long sign-ins last, behind-a-reverse-proxy switch |
| **Maintenance** | scan, refresh Live TV / all metadata, clear image cache, clean transcodes, compact and **back up** the database, clear watch history |
| **Logs** | the server's recent log lines (live, filterable, copyable) |
| **System & about** | library counts, running streams (with Stop), caches, version, runtime, ffmpeg, this browser's capabilities |
| **Account & devices** | change password, see and sign out signed-in devices, reset this device's preferences |

Only whitelisted settings can be changed this way: library paths, tool paths, the database location and the sign-in switch stay
deployment-only. Saved values override environment variables; *Use the default* removes the override.

## Captions and shuffle

- Captions: text subtitle tracks inside the file and `.srt` / `.vtt` / `.ass` files next to it with the same name
  (`Movie.en.srt`) are converted to WebVTT on demand and cached. Picture-based tracks (PGS, DVD) are listed but can't be shown by a browser.
  Press **C** in the player. Control defaults under Settings → Captions.
- **Random movie / Random episode** (home, Movies, TV Shows, and each show's page) pick something you haven't watched yet.

## Test

    dotnet test

## Posters and metadata (TMDB)

Without a TMDB token, artwork comes from video frames. With one you get real posters, backdrops, descriptions, genres and ratings, and
the UI switches to poster cards (Posters / Thumbnails toggle on the Movies and TV pages). Get a free API Read Access Token at
themoviedb.org (Settings, API), then set `Tmdb__ReadAccessToken` (see below). Titles that can't be matched are listed under "Needs a
match"; open any title and choose **Fix match** to search TMDB and pick the right entry (for a show this applies to every episode).
`Tmdb:BaseUrl` / `Tmdb:ImageBaseUrl` can point at a different TMDB-compatible server (used for local testing).

### Matching details

    dotnet user-secrets set "Tmdb:ReadAccessToken" "<v4 read access token>" --project src/MediaServer.Api   # or Tmdb:ApiKey
    curl -X POST localhost:5087/api/library/enrich

Responses are cached in SQLite (details 30d, searches 2d, 404s 1d, stale-if-error). Matching does one lookup per
distinct title, not per file. Bad match? Find the right id with `GET /api/tmdb/search?type=movie&q=...`, then:

    PUT    /api/media/{id}/match   {"tmdbType":"movie","tmdbId":603,"scope":"file"}   # scope "title" = whole series
    DELETE /api/media/{id}/match?scope=file                                           # back to automatic matching

Manual matches are never overwritten by automatic matching. Posters: `GET /api/media/{id}/poster` (cached on disk).
In Kubernetes, supply the token as a Secret-backed env var `Tmdb__ReadAccessToken`.

## Live TV (M3U + XMLTV)

Point the server at an IPTV source (for example Dispatcharr) and it adds a **Live TV** section: a guide timeline, a channel list, favourites, channel surfing (`[` / `]`, or type a number) and a "Go live" button.

| Setting | Meaning |
|---|---|
| `LiveTv__M3uUrl` | M3U playlist URL (http/https only) |
| `LiveTv__EpgUrl` | XMLTV guide URL (optional; defaults to the playlist's `url-tvg`) |
| `LiveTv__StreamOrigin` | Rewrites stream URLs to this origin, useful when the playlist advertises an address the server can't reach |
| `LiveTv__RefreshMinutes` | How often the playlist and guide are re-imported |

Clients never supply URLs: stream addresses stay on the server and are never returned by any API or written to logs. One live stream runs at a time; the stream is remuxed or transcoded to HLS (sliding window) by the same policy as library playback, and it reconnects automatically if the source drops.

The **System** page (gear icon, `#/system`) shows library health, running streams (with a Stop button), TMDB / Live TV state and cache sizes.

## Playback: direct play first, transcode last

The browser reports what it can decode (`?v=h264,hevc&a=aac,eac3&c=mp4,mkv`, detected with `canPlayType`), and the server
picks the cheapest path per file:

1. **Direct play** (`GET /stream/{id}`, range requests): container, video and default audio track are all supported. No server work.
2. **Direct stream** (`POST /api/hls/start`): the video is supported but the container or audio isn't (e.g. HEVC + EAC3 in MKV).
   The video is **copied untouched** into HLS (HEVC into fMP4 tagged `hvc1`, H.264 into MPEG-TS) and only the audio is
   converted to stereo AAC. Nearly free, no GPU.
3. **Transcode**: only when the browser cannot decode the video at all (e.g. HEVC in Firefox, VC-1, 4:4:4, old MPEG-4 AVIs).
   Video goes to 8-bit H.264 (`Hls:Encoder=nvenc` uses the GPU), audio to AAC.

**Quality picker.** The slider icon in the player lets you pick Original or a lower resolution (1080p / 720p / 480p / 360p, each with a
bitrate cap; only sizes the file can fill are offered). The choice is remembered and playback continues from the same moment.

**Seeking in transcodes.** A transcode gets its full-length playlist up front, so the scrubber shows the whole movie and playback can
start at any point (resume, quality change). Segment N always covers `[N×6s, (N+1)×6s)` of the source; if you seek far from where the
encoder is, the server restarts ffmpeg at that segment (one ffmpeg per session, the old one is killed). Runs started by different seeks
line up on one timeline: segmented encodes use no B-frames and a fixed 48 kHz AAC track plus a constant timestamp offset, verified by
a test that stitches segments from separate runs and checks every video frame sits on the 40 ms grid.
Direct-streamed (copied) video keeps a growing playlist because its segment boundaries depend on the source's keyframes.

If a browser claims support and then fails, the player steps down automatically: direct play, then direct stream, then transcode.
No browser decodes VC-1, MPEG-4 Part 2, DTS or TrueHD, so those always need the video or audio converted.

| Setting | Default | Meaning |
|---|---|---|
| `Hls:Encoder` | `libx264` | `nvenc` uses `h264_nvenc` (RTX 3060). The container's ffmpeg must have NVENC + GPU access |
| `Hls:MaxSessions` | `2` | Starting another evicts the least recently used |
| `Hls:IdleSeconds` | `120` | No ping/segment request for this long => ffmpeg killed |
| `Hls:SegmentSeconds` | `6` | |
| `Hls:TempDir` | `$TMP/mediaserver-hls` | Wiped at startup |
| `Tools:FfmpegPath` / `Tools:FfprobePath` | `ffmpeg` / `ffprobe` | |

ffmpeg is killed (process tree) and its directory removed when: the tab closes (`sendBeacon` to `/stop` on `pagehide`),
the player navigates away, the idle timeout elapses (the page pings every 15 s), a newer session evicts it, or the
server shuts down. Playlists are EVENT-style: playback starts immediately and the playlist gains `ENDLIST` when done.
Known limitation: seeking beyond what has been encoded so far waits for the encoder (fast for remux, usually
faster than realtime for transcodes).

`wwwroot/vendor/hls.min.js` is hls.js 1.5.17 (Apache-2.0, see `hls.LICENSE`), vendored so the server works offline.

## License

MIT. See [LICENSE](LICENSE). The bundled hls.js (`wwwroot/vendor`) is Apache-2.0; its license text is alongside it.
