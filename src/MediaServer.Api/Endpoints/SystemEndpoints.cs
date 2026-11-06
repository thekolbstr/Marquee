using System.Diagnostics;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

/// <summary>A single-glance view for the person running the server: library health, what's streaming (and what it costs), disk use.</summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system", (LibraryQueries q, ScanCoordinator scans, TmdbClient tmdb, LiveTvService live, HlsSessionManager hls, HlsOptions hlsOptions, IConfiguration config) =>
        {
            var stats = q.Stats();
            var liveStatus = live.Status();
            var streams = hls.Sessions.OrderBy(s => s.StartedAt).Select(s => new
            {
                id = s.Id,
                kind = s.MediaId < 0 ? "live" : "library",
                title = s.MediaId < 0 ? live.Channel(-s.MediaId)?.Channel.Name : q.Play(s.MediaId, ClientCaps.Baseline)?.Title,
                mode = s.Mode, reason = s.Reason, startedAt = s.StartedAt, running = s.IsRunning,
            }).ToList();
            var imagesDir = Path.GetFullPath(config["Images:CacheDir"] ?? "data/images");
            return Results.Ok(new
            {
                uptimeSeconds = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
                library = new { stats.Movies, stats.Shows, stats.Episodes, stats.Files, stats.Unmatched, scanning = scans.IsScanning, lastScan = scans.LastFinished, scanError = scans.LastError },
                tmdb = new { configured = tmdb.IsConfigured },
                live = new { configured = liveStatus.Configured, channels = liveStatus.Channels, programmes = liveStatus.Programs, lastRefresh = liveStatus.LastRefresh, error = liveStatus.LastError },
                encoder = new { name = HlsOptions.From(config).Encoder, maxStreams = HlsOptions.From(config).MaxSessions, transcoding = HlsOptions.From(config).AllowTranscoding },
                streams,
                storageBytes = new { artwork = DirSize(imagesDir), transcode = DirSize(hlsOptions.TempDir) },
            });
        });
    }

    /// <summary>Total size of a directory tree (bounded, so a huge cache can't make this slow).</summary>
    internal static long DirSize(string dir)
    {
        long total = 0; var n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                total += f.Length;
                if (++n >= 200_000) break;
            }
        }
        catch (IOException) { }
        return total;
    }
}
