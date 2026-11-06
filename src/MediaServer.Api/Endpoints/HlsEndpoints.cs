using MediaServer.Api.Hls;
using MediaServer.Api.Library;

namespace MediaServer.Api.Endpoints;

public sealed record StartHlsRequest(long MediaId, string? Video = null, string? Audio = null, string? Containers = null, bool ForceTranscode = false,
    double StartSeconds = 0, string? Quality = null, int? AudioIndex = null, int? BurnSubtitle = null);

public static class HlsEndpoints
{
    public static void MapHlsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/hls/start", async (StartHlsRequest body, HlsSessionManager hls, CancellationToken ct) =>
        {
            try
            {
                if (!double.IsFinite(body.StartSeconds) || body.StartSeconds < 0 || body.StartSeconds > 1_000_000)
                    return Results.BadRequest(new { error = "startSeconds is out of range" });
                var s = await hls.StartAsync(body.MediaId, ct, ClientCaps.From(body.Video, body.Audio, body.Containers), body.ForceTranscode,
                    body.StartSeconds, body.Quality, body.AudioIndex, body.BurnSubtitle);
                return Results.Ok(new { sessionId = s.Id, playlistUrl = $"/api/hls/{s.Id}/index.m3u8", mode = s.Mode, reason = s.Reason, vod = s.Vod });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 422); }
            catch (HlsStartException ex) { return Results.Problem(ex.Message, statusCode: 502); }
            catch (Probing.ProbeFailedException ex) { return Results.Problem(ex.Message, statusCode: 422); }
        });

        // Playlist + segments. The file name is whitelisted by regex; the session dir is server-chosen.
        app.MapGet("/api/hls/{sessionId}/{file}", async (string sessionId, string file, HlsSessionManager hls, HttpContext ctx, CancellationToken ct) =>
        {
            if (hls.Get(sessionId) is not { } s || !HlsSessionManager.IsValidFileName(file)) return Results.NotFound();
            var path = await hls.GetFileAsync(s, file, ct);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(path, file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : file.EndsWith(".ts") ? "video/mp2t" : "video/mp4");
        });

        // Keep-alive while a player is open (even if paused with a full buffer).
        // Read-only facts about a running stream, for the player's stats panel. The session id is the capability, as for the playlist itself.
        app.MapGet("/api/hls/{sessionId}/info", (string sessionId, HlsSessionManager hls) => hls.Info(sessionId) is { } i ? Results.Ok(i) : Results.NotFound());

        app.MapPost("/api/hls/{sessionId}/ping", (string sessionId, HlsSessionManager hls) =>
        {
            if (hls.Get(sessionId) is not { } s) return Results.NotFound();
            hls.Touch(s);
            return Results.NoContent();
        });

        // Sent via navigator.sendBeacon on pagehide, and on in-app navigation. Idempotent.
        app.MapPost("/api/hls/{sessionId}/stop", async (string sessionId, HlsSessionManager hls) =>
        {
            if (HlsSessionManager.IsValidSessionId(sessionId)) await hls.StopAsync(sessionId);
            return Results.NoContent();
        });
    }
}
