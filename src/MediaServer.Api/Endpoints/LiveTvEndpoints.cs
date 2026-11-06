using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Probing;

namespace MediaServer.Api.Endpoints;

public sealed record StartLiveRequest(string? Video = null, string? Audio = null, string? Containers = null, bool ForceTranscode = false);

public static class LiveTvEndpoints
{
    public static void MapLiveTvEndpoints(this WebApplication app)
    {
        app.MapGet("/api/live/status", (LiveTvService live) => Results.Ok(live.Status()));

        app.MapPost("/api/live/refresh", async (LiveTvService live, bool? background, CancellationToken ct) =>
        {
            if (!live.IsConfigured) return Results.Problem("Live TV is not configured", statusCode: 503);
            try
            {
                if (background == true)
                {
                    _ = Task.Run(async () => { try { await live.RefreshAsync(CancellationToken.None); } catch { /* recorded in status */ } });
                    return Results.Accepted();
                }
                return await live.RefreshAsync(ct) is { } r ? Results.Ok(r) : Results.Conflict(new { error = "A refresh is already running" });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { return Results.Problem(live.Status().LastError ?? "Refresh failed", statusCode: 502); }
        });

        app.MapGet("/api/live/groups", (LiveTvService live) => Results.Ok(live.Groups()));

        app.MapGet("/api/live/channels", (LiveTvService live, string? group, string? search, bool? favorites, int? limit, int? offset) =>
            Results.Ok(live.Channels(group, search, favorites == true, limit ?? 100, offset ?? 0)));

        // The timeline guide: channels with their programmes for a window (default: from the last half hour, 6 hours).
        app.MapGet("/api/live/guide", (LiveTvService live, TimeProvider time, string? group, string? search, bool? favorites, long? from, int? hours, int? limit, int? offset) =>
        {
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var start = from is { } f && f > 0 ? f : now - now % 1800;
            return Results.Ok(live.Guide(group, search, favorites == true, start, hours ?? 6, limit ?? 40, offset ?? 0));
        });

        app.MapGet("/api/live/channels/{id:long}", (long id, LiveTvService live) => live.Channel(id) is { } c ? Results.Ok(c) : Results.NotFound());

        app.MapPost("/api/live/channels/{id:long}/favorite", (long id, LiveTvService live) => live.SetFavorite(id, true) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/live/channels/{id:long}/favorite", (long id, LiveTvService live) => live.SetFavorite(id, false) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/live/channels/{id:long}/logo", async (long id, LiveTvService live, LiveLogoCache logos, HttpContext ctx, CancellationToken ct) =>
        {
            if (live.LogoSource(id) is not { } url) return Results.NotFound();
            if (await logos.GetAsync(id, url, ct) is not { } logo) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
            return Results.File(logo.Path, logo.ContentType);
        });

        // Start watching a channel. The browser sends what it can decode; the server returns an HLS session (files, ping and stop
        // reuse /api/hls/{session}/...). The channel's stream URL never leaves the server.
        app.MapPost("/api/live/channels/{id:long}/start", async (long id, StartLiveRequest? body, LiveTvService live, IMediaProbe probe, HlsSessionManager hls, CancellationToken ct) =>
        {
            body ??= new StartLiveRequest();
            if (live.GetStream(id) is not { } stream) return Results.NotFound();
            try
            {
                var info = stream.CachedInfo;
                if (info is null) { info = await probe.ProbeLiveAsync(stream.Url, ct); live.SaveProbe(id, info); }
                var s = await hls.StartLiveAsync(id, stream.Url, info, ct, ClientCaps.From(body.Video, body.Audio, body.Containers), body.ForceTranscode);
                return Results.Ok(new { sessionId = s.Id, playlistUrl = $"/api/hls/{s.Id}/index.m3u8", mode = s.Mode, reason = s.Reason, live = true });
            }
            catch (ProbeFailedException ex) { return Results.Problem(ex.Message, statusCode: 502); }
            catch (HlsStartException ex) { return Results.Problem(ex.Message, statusCode: 502); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 422); }
        });
    }
}
