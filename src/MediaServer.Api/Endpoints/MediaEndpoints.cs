using MediaServer.Api.Library;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

public sealed record ProgressBody(double Position, double? Duration = null, bool? Watched = null);

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        // Frame grabbed from the video (cached). Only a media ID and a whitelisted width come from the client.
        app.MapGet("/api/media/{id:long}/thumb", async (long id, int? w, ThumbnailService thumbs, HttpContext ctx, CancellationToken ct) =>
        {
            var width = w ?? 640;
            if (!ThumbnailService.IsValidWidth(width)) return Results.BadRequest(new { error = "w must be 320, 640 or 1280" });
            var file = await thumbs.GetAsync(id, width, ct);
            if (file is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(file, "image/jpeg");
        });

        // POST (not just PUT) so navigator.sendBeacon can save the position as the tab closes.
        app.MapMethods("/api/media/{id:long}/progress", ["POST", "PUT"], (long id, ProgressBody body, ProgressService progress) =>
            progress.Save(id, body.Position, body.Duration, body.Watched) ? Results.NoContent() : Results.BadRequest(new { error = "Unknown media or invalid position" }));

        app.MapDelete("/api/media/{id:long}/progress", (long id, ProgressService progress) =>
        {
            progress.Clear(id);
            return Results.NoContent();
        });

        // Captions: the list of tracks, and each one converted to WebVTT (cached). Only IDs come from the client.
        app.MapGet("/api/media/{id:long}/captions", (long id, SubtitleService subs) => Results.Ok(new { tracks = subs.List(id) }));

        app.MapGet("/api/media/{id:long}/captions/{track}.vtt", async (long id, string track, SubtitleService subs, HttpContext ctx, CancellationToken ct) =>
        {
            var file = await subs.GetVttAsync(id, track, ct);
            if (file is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(file, "text/vtt; charset=utf-8");
        });

        // Chapter marks and the skip-intro / skip-credits spans found in them.
        app.MapGet("/api/media/{id:long}/chapters", async (long id, ChapterService chapters, CancellationToken ct) =>
            await chapters.GetAsync(id, ct) is { } info ? Results.Ok(info) : Results.NotFound());

        // Seek-bar previews: 200 when the sprite sheets exist, 202 while they're being made, 404 when there won't be any.
        app.MapGet("/api/media/{id:long}/trickplay", (long id, TrickplayService trick) =>
        {
            var (state, info) = trick.Get(id);
            return state switch { TrickplayService.State.Ready => Results.Ok(info), TrickplayService.State.Generating => Results.Accepted(value: new { status = "generating" }), _ => Results.NotFound() };
        });
        app.MapGet("/api/media/{id:long}/trickplay/{n:int}.jpg", (long id, int n, TrickplayService trick, HttpContext ctx) =>
        {
            if (trick.SheetPath(id, n) is not { } file) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(file, "image/jpeg");
        });

        app.MapGet("/api/continue", (LibraryQueries q, int? limit) => Results.Ok(q.Continue(limit ?? 12)));

        // Library scans run independently of the request: a proxy timeout or closed tab doesn't abort them.
        app.MapPost("/api/library/scan", async (ScanCoordinator scans, bool? background, CancellationToken ct) =>
        {
            if (scans.TryStart() is not { } running) return Results.Conflict(new { error = "A scan is already running" });
            if (background == true) return Results.Accepted();
            var result = await running.WaitAsync(ct);
            return result is null ? Results.Problem(scans.LastError ?? "The scan did not complete", statusCode: 500) : Results.Ok(result);
        });

        app.MapGet("/api/library/status", (ScanCoordinator scans, TmdbClient tmdb, LibraryQueries q) => Results.Ok(new
        {
            scanning = scans.IsScanning, lastResult = scans.LastResult, lastFinished = scans.LastFinished, lastError = scans.LastError,
            // Lets the UI offer "add a TMDB token" or "fix matches" as appropriate.
            tmdbConfigured = tmdb.IsConfigured, unmatched = tmdb.IsConfigured ? q.UnmatchedCount() : 0,
        }));
    }
}
