using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

/// <summary>Maintenance actions, the log viewer, and the "about this server" panel. Signed-in users only (see AuthMiddleware).</summary>
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/maintenance/{task}", async (string task, bool? confirm, MaintenanceService m, ScanCoordinator scans, TmdbClient tmdb, SkipDetector skips, SkipScanService skipScan, CancellationToken ct, MediaServer.Api.Notifications.NotificationService notify) =>
        {
            switch (task)
            {
                case "test-notification":
                    var (sent, why) = await notify.SendAsync(MediaServer.Api.Notifications.NotifyEvent.Test, "Marquee", "This is a test message. If you can read it, notifications work.", ct);
                    return sent ? Results.Ok(new { message = "Sent. Check your device." }) : Results.BadRequest(new { error = why });
                case "test-encoder":
                    var (works, text) = await m.TestEncoderAsync(ct);
                    return works ? Results.Ok(new { message = text }) : Results.BadRequest(new { error = text });
                case "refresh-metadata":
                    if (!tmdb.IsConfigured) return Results.BadRequest(new { error = "Add TMDB credentials first (Posters & details)." });
                    var n = m.ResetMetadata();
                    _ = scans.TryStart();   // runs in the background
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

        // Is a newer release out? (Administrators only, like everything under /api/system.) POST forces a fresh look.
        app.MapGet("/api/system/update", async (UpdateService u, CancellationToken ct) => Results.Ok(await u.CheckAsync(false, ct)));
        app.MapPost("/api/system/update/check", async (UpdateService u, CancellationToken ct) => Results.Ok(await u.CheckAsync(true, ct)));

        // A read-only report: duplicates, unreadable files, unmatched titles, files that need converting. (Administrators only.)
        app.MapGet("/api/system/library-health", (LibraryHealthService h) => Results.Ok(h.Report()));

        app.MapGet("/api/system/about", async (MaintenanceService m, CancellationToken ct) => Results.Ok(await m.AboutAsync(ct)));
    }

    internal static string Bytes(long n) => n >= 1L << 30 ? $"{n / (double)(1L << 30):0.0} GB" : n >= 1L << 20 ? $"{n / (double)(1L << 20):0.#} MB" : $"{Math.Max(0, n) / 1024.0:0.#} KB";
}
