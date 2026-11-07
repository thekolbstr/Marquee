using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

/// <summary>Maintenance actions, the log viewer, and the "about this server" panel. Signed-in users only (see AuthMiddleware).</summary>
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/maintenance/{task}", (string task, bool? confirm, MaintenanceService m, ScanCoordinator scans, TmdbClient tmdb, SkipDetector skips, SkipScanService skipScan) =>
        {
            switch (task)
            {
                case "refresh-metadata":
                    if (!tmdb.IsConfigured) return Results.BadRequest(new { error = "Add TMDB credentials first (Posters & details)." });
                    var n = m.ResetMetadata();
                    scans.TryStart();
                    return Results.Ok(new { message = $"Refreshing details for {n} files. This runs in the background and can take a few minutes." });
                case "scan-skips":
                    if (skips.IsRunning) return Results.Ok(new { message = "Already looking. Progress shows below." });
                    skipScan.Request();
                    return Results.Ok(new { message = "Started. It works through the library a title at a time in the background." });
                case "clear-skips":
                    return Results.Ok(new { message = $"Forgot the skip points found for {skips.Clear()} titles. Run the search again to rebuild them." });
                case "clear-image-cache":
                    return Results.Ok(new { message = $"Cleared {Bytes(m.ClearImageCache())}. Artwork and thumbnails will be rebuilt as you browse." });
                case "clean-transcodes":
                    var (folders, bytes) = m.CleanTranscodes();
                    return Results.Ok(new { message = folders == 0 ? "Nothing to clean up." : $"Removed {folders} leftover folder{(folders == 1 ? "" : "s")} ({Bytes(bytes)})." });
                case "optimize-database":
                    var (before, after) = m.OptimizeDatabase();
                    return Results.Ok(new { message = $"Database compacted: {Bytes(before)} → {Bytes(after)}." });
                case "clear-watch-history":
                    if (confirm != true) return Results.BadRequest(new { error = "Add ?confirm=true to confirm." });
                    return Results.Ok(new { message = $"Cleared watch history for {m.ClearWatchHistory()} titles." });
                default:
                    return Results.NotFound();
            }
        });

        // A consistent copy of the database. It contains settings (including any saved TMDB token) and password hashes, so keep it private.
        app.MapGet("/api/maintenance/backup", (MaintenanceService m, HttpContext ctx) =>
        {
            var path = m.CreateBackup();
            var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous });
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(stream, "application/octet-stream", $"mediaserver-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.db");
        });

        app.MapGet("/api/maintenance/skip-status", (SkipDetector skips) => Results.Ok(skips.Status()));

        app.MapGet("/api/system/logs", (MemoryLogStore logs, string? level, string? q, int? limit) =>
            Results.Ok(new { entries = logs.Query(level, q, limit ?? 200) }));

        app.MapGet("/api/system/about", async (MaintenanceService m, CancellationToken ct) => Results.Ok(await m.AboutAsync(ct)));
    }

    internal static string Bytes(long n) => n >= 1L << 30 ? $"{n / (double)(1L << 30):0.0} GB" : n >= 1L << 20 ? $"{n / (double)(1L << 20):0.#} MB" : $"{Math.Max(0, n) / 1024.0:0.#} KB";
}
