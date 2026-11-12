using MediaServer.Api.Library;
using MediaServer.Api.Metadata;

using MediaServer.Api.Trakt;

namespace MediaServer.Api.Endpoints;

public static class LibraryEndpoints
{
    // The browser reports what it can decode: ?v=h264,hevc&a=aac,eac3&c=mp4,mkv (see ClientCaps).
    private static ClientCaps Caps(HttpRequest r) => ClientCaps.From(r.Query["v"].FirstOrDefault(), r.Query["a"].FirstOrDefault(), r.Query["c"].FirstOrDefault());

    public static void MapLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/items", (LibraryQueries q, string? search, string? type, string? sort, int? limit, int? offset, bool? matched, string? genre) =>
            Results.Ok(q.List(search, type, sort ?? "title", limit ?? 60, offset ?? 0, matched, genre)));

        // My List
        app.MapGet("/api/watchlist", (LibraryQueries q, WatchlistService w) => Results.Ok(new { items = q.ItemsByKeys(w.Keys()) }));
        app.MapGet("/api/watchlist/keys", (WatchlistService w) => Results.Ok(new { keys = w.Keys() }));
        app.MapGet("/api/watchlist/status/{type}/{key}", (string type, string key, WatchlistService w, LibraryQueries q) =>
            Results.Ok(new { inList = q.CanonicalItemKey(type, key) is { } k && w.Keys().Any(x => x.Type == type && x.Key == k) }));
        app.MapPut("/api/watchlist/{type}/{key}", (string type, string key, WatchlistService w, LibraryQueries q) =>
            q.CanonicalItemKey(type, key) is { } k && w.Add(type, k) ? Results.NoContent() : Results.BadRequest(new { error = "Unknown item" }));
        app.MapDelete("/api/watchlist/{type}/{key}", (string type, string key, WatchlistService w, LibraryQueries q) => { w.Remove(type, q.CanonicalItemKey(type, key) ?? key); return Results.NoContent(); });

        // Trakt.tv: connect your own account (per person), push what you finish watching, import what Trakt says you watched.
        app.MapGet("/api/trakt/status", (TraktService t, MediaServer.Api.Auth.IUserContext me) => Results.Ok(new { configured = t.IsConfigured, connected = t.IsConnected(me.UserId) }));
        app.MapPost("/api/trakt/connect", async (TraktService t, CancellationToken ct) =>
        {
            try { return Results.Ok(await t.StartAsync(ct)); }
            catch (TraktException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.BadRequest(new { error = "Could not reach Trakt." }); }
        });
        app.MapPost("/api/trakt/poll", async (TraktDeviceBody body, TraktService t, MediaServer.Api.Auth.IUserContext me, CancellationToken ct) =>
        {
            try { return Results.Ok(new { status = await t.PollAsync(me.UserId, body.DeviceCode ?? "", ct) }); }
            catch (TraktException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.BadRequest(new { error = "Could not reach Trakt." }); }
        });
        app.MapPost("/api/trakt/import", async (TraktService t, MediaServer.Api.Auth.IUserContext me, CancellationToken ct) =>
        {
            try { return Results.Ok(await t.ImportAsync(me.UserId, ct)); }
            catch (TraktException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.BadRequest(new { error = "Could not reach Trakt." }); }
        });
        app.MapDelete("/api/trakt", (TraktService t, MediaServer.Api.Auth.IUserContext me) => { t.Disconnect(me.UserId); return Results.NoContent(); });

        // Collections: named groups of titles
        app.MapGet("/api/collections", (CollectionService cs) => Results.Ok(new { collections = cs.List() }));
        app.MapPost("/api/collections", (CollectionNameBody body, CollectionService cs) =>
            cs.Create(body.Name) is { } created ? Results.Ok(created)
                : Results.BadRequest(new { error = CollectionService.CleanName(body.Name) is null ? $"Give it a name of up to {CollectionService.MaxNameLength} characters." : "You already have a collection with that name, or you have reached the limit." }));
        app.MapGet("/api/collections/{id:long}", (long id, CollectionService cs, LibraryQueries q) =>
            cs.Get(id) is { } c ? Results.Ok(new { id = c.Id, name = c.Name, items = q.ItemsByKeys(cs.Keys(id)) }) : Results.NotFound());
        app.MapPatch("/api/collections/{id:long}", (long id, CollectionNameBody body, CollectionService cs) =>
            cs.Rename(id, body.Name) ? Results.NoContent() : Results.BadRequest(new { error = "That name is empty, too long, or already used." }));
        app.MapDelete("/api/collections/{id:long}", (long id, CollectionService cs) => cs.Delete(id) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/collections/containing/{type}/{key}", (string type, string key, CollectionService cs, LibraryQueries q) =>
            Results.Ok(new { ids = q.CanonicalItemKey(type, key) is { } k ? cs.Containing(type, k) : [] }));
        app.MapPut("/api/collections/{id:long}/items/{type}/{key}", (long id, string type, string key, CollectionService cs, LibraryQueries q) =>
            q.CanonicalItemKey(type, key) is { } k && cs.Add(id, type, k) ? Results.NoContent() : Results.BadRequest(new { error = "Unknown collection or title" }));
        app.MapDelete("/api/collections/{id:long}/items/{type}/{key}", (long id, string type, string key, CollectionService cs, LibraryQueries q) =>
        { cs.Remove(id, type, q.CanonicalItemKey(type, key) ?? key); return Results.NoContent(); });

        // Mark a whole show (or one season of it) watched or unwatched.
        app.MapPost("/api/items/series/{key}/watched", (string key, int? season, bool? watched, LibraryQueries q, ProgressService p) =>
        {
            var files = q.EpisodeFiles(key, season);
            if (files.Count == 0) return Results.NotFound();
            return Results.Ok(new { changed = p.SetWatched(files, watched ?? true) });
        });

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

public sealed record CollectionNameBody(string? Name);
public sealed record TraktDeviceBody(string? DeviceCode);
