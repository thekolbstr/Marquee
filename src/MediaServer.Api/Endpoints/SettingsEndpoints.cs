using System.Text.Json;
using MediaServer.Api.Arr;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Metadata;
using MediaServer.Api.Settings;

namespace MediaServer.Api.Endpoints;

public sealed record SettingsBody(Dictionary<string, JsonElement>? Values);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", (SettingsService s) => Results.Ok(new { groups = s.Describe() }));

        app.MapPut("/api/settings", (SettingsBody body, SettingsService s, LiveTvService live, ScanCoordinator scans, IHostApplicationLifetime life, HttpContext ctx) =>
        {
            if (ctx.Request.ContentLength > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (body.Values is not { Count: > 0 and <= 40 }) return Results.BadRequest(new { error = "No settings were provided" });
            var map = new Dictionary<string, string?>();
            IReadOnlyList<string> changed;
            try
            {
                foreach (var (k, v) in body.Values)
                    map[k] = v.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => v.GetRawText(),
                        _ => throw new SettingsException(k, "Unsupported value"),
                    };
                changed = s.Apply(map);
            }
            catch (SettingsException ex) { return Results.BadRequest(new { error = ex.Message, field = ex.Key }); }

            // Apply what changed straight away: fetch the Live TV channels, or match titles once TMDB credentials arrive.
            if (changed.Any(k => k.StartsWith("LiveTv:", StringComparison.OrdinalIgnoreCase)) && live.IsConfigured)
                _ = Task.Run(async () => { try { await live.RefreshAsync(life.ApplicationStopping); } catch { /* recorded in status */ } });
            if (changed.Any(k => k.StartsWith("Tmdb:", StringComparison.OrdinalIgnoreCase))) scans.TryStart();
            return Results.Ok(new { changed, groups = s.Describe() });
        });

        // "Does it work?" buttons next to the fields.
        app.MapPost("/api/settings/test/{app:regex(^(radarr|sonarr)$)}", async (string app, ArrService arr, CancellationToken ct) =>
        {
            var (ok, message) = await arr.PingAsync(Enum.Parse<ArrKind>(app, true), ct);
            return Results.Ok(new { ok, message });
        });

        app.MapPost("/api/settings/test/tmdb", async (TmdbClient tmdb, CancellationToken ct) =>
        {
            var (ok, message) = await tmdb.PingAsync(ct);
            return Results.Ok(new { ok, message });
        });
    }
}
