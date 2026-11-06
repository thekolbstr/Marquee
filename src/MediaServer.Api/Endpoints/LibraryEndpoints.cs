using MediaServer.Api.Library;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

public static class LibraryEndpoints
{
    // The browser reports what it can decode: ?v=h264,hevc&a=aac,eac3&c=mp4,mkv (see ClientCaps).
    private static ClientCaps Caps(HttpRequest r) => ClientCaps.From(r.Query["v"].FirstOrDefault(), r.Query["a"].FirstOrDefault(), r.Query["c"].FirstOrDefault());

    public static void MapLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/items", (LibraryQueries q, string? search, string? type, string? sort, int? limit, int? offset, bool? matched, string? genre) =>
            Results.Ok(q.List(search, type, sort ?? "title", limit ?? 60, offset ?? 0, matched, genre)));

        // "Surprise me": a random movie or episode (unwatched first), optionally within a genre.
        app.MapGet("/api/random", (LibraryQueries q, string? kind, string? genre) =>
            q.Random(kind == "episode" ? "episode" : "movie", genre) is { } pick ? Results.Ok(pick) : Results.NotFound(new { error = "Nothing to pick from" }));

        app.MapGet("/api/genres", (LibraryQueries q, string? type) =>
            Results.Ok(q.Genres(type is "movie" or "series" ? type : null).Select(g => new { name = g.Name, count = g.Count })));

        // Episode names, descriptions and stills from TMDB for one season of a matched show (empty when unavailable).
        app.MapGet("/api/items/series/{key}/season/{season:int}", async (string key, int season, TmdbClient tmdb, CancellationToken ct) =>
        {
            if (!tmdb.IsConfigured || !key.StartsWith("tmdb:", StringComparison.Ordinal) || !int.TryParse(key.AsSpan(5), out var tvId)) return Results.Ok(new { episodes = Array.Empty<object>() });
            try
            {
                var eps = await tmdb.GetSeasonAsync(tvId, season, ct);
                return Results.Ok(new
                {
                    episodes = eps.Select(e => new
                    {
                        number = e.Number, name = e.Name, overview = e.Overview, airDate = e.AirDate, runtime = e.Runtime,
                        stillUrl = e.StillPath is null ? null : $"/api/tmdb/image?path={Uri.EscapeDataString(e.StillPath)}&size=w342",
                    }),
                });
            }
            catch (Exception ex) when (ex is TmdbException or HttpRequestException) { return Results.Ok(new { episodes = Array.Empty<object>() }); } // decoration only: never fail the page
        });

        app.MapGet("/api/items/movie/{mediaId:long}", (long mediaId, LibraryQueries q, HttpRequest r) =>
            q.Movie(mediaId, Caps(r)) is { } m ? Results.Ok(m) : Results.NotFound());

        app.MapGet("/api/items/series/{key}", (string key, LibraryQueries q) =>
            q.Series(key) is { } s ? Results.Ok(s) : Results.NotFound());

        app.MapGet("/api/media/{id:long}/play", (long id, LibraryQueries q, HttpRequest r, ILoggerFactory logs, MediaServer.Api.Auth.AuthService auth) =>
        {
            if (q.Play(id, Caps(r)) is not { } p) return Results.NotFound();
            logs.CreateLogger("Playback").LogInformation("Play media {Id}: {Mode} ({Reason}) [client v={V} a={A} c={C}; {Agent}]", id, p.Mode, p.Reason,
                r.Query["v"].ToString(), r.Query["a"].ToString(), r.Query["c"].ToString(), r.Headers.UserAgent.ToString());
            return Results.Ok(p with { StreamUrl = $"{p.StreamUrl}?k={auth.StreamToken(id)}" });   // players that skip cookies can still direct-play
        });
    }
}
