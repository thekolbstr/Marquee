using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Security;

namespace MediaServer.Api.Trakt;

public sealed record TraktDevice(string DeviceCode, string UserCode, string VerificationUrl, int IntervalSeconds, int ExpiresInSeconds);
public sealed record TraktImportResult(int Movies, int Episodes);
public sealed class TraktException(string message) : Exception(message);

/// <summary>
/// Trakt.tv watch history. You connect your own Trakt account (a code you enter on trakt.tv, so Marquee never sees your Trakt password).
/// What you finish watching is added to your Trakt history, and "Import" marks what Trakt says you have watched as watched here.
/// Only TMDB ids, season and episode numbers and the time you watched go to Trakt. The client secret and your tokens are stored encrypted.
/// </summary>
public sealed class TraktService(Database db, HttpClient http, IConfiguration config, SecretBox box, TimeProvider time, ILogger<TraktService> log)
{
    private const string Api = "https://api.trakt.tv";
    private string? ClientId => config["Trakt:ClientId"] is { Length: > 0 } c ? c : null;
    private string? ClientSecret => config["Trakt:ClientSecret"] is { Length: > 0 } c ? c : null;
    public bool IsConfigured => ClientId is not null && ClientSecret is not null;

    // ------------------------------------------------------------------ connecting

    public bool IsConnected(long userId)
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT 1 FROM trakt_accounts WHERE user_id=@u", r => 1, ("@u", userId)).Count > 0;
    }

    public async Task<TraktDevice> StartAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new TraktException("Trakt is not set up. An administrator needs to add the Trakt client id and secret in Settings → Trakt.");
        using var res = await http.SendAsync(Post("/oauth/device/code", new { client_id = ClientId }, null), ct);
        if (!res.IsSuccessStatusCode) throw new TraktException("Trakt did not accept the client id. Check it in Settings → Trakt.");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        return new TraktDevice(Str(r, "device_code") ?? "", Str(r, "user_code") ?? "", Str(r, "verification_url") ?? "https://trakt.tv/activate", Int(r, "interval", 5), Int(r, "expires_in", 600));
    }

    /// <returns>"pending", "connected", "expired" or "denied".</returns>
    public async Task<string> PollAsync(long userId, string deviceCode, CancellationToken ct)
    {
        if (!IsConfigured || deviceCode.Length is 0 or > 200) throw new TraktException("Start again.");
        using var res = await http.SendAsync(Post("/oauth/device/token", new { code = deviceCode, client_id = ClientId, client_secret = ClientSecret }, null), ct);
        switch ((int)res.StatusCode)
        {
            case 200:
                using (var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct))) Save(userId, doc.RootElement);
                return "connected";
            case 400 or 429: return "pending";    // not entered yet, or polling too fast
            case 404 or 410 or 409: return "expired";
            case 418: return "denied";
            default: throw new TraktException($"Trakt answered with an error ({(int)res.StatusCode}).");
        }
    }

    public void Disconnect(long userId)
    {
        using var c = db.Open();
        Database.Execute(c, null, "DELETE FROM trakt_accounts WHERE user_id=@u", ("@u", userId));
    }

    private void Save(long userId, JsonElement t)
    {
        var access = Str(t, "access_token") ?? throw new TraktException("Trakt did not return a token.");
        var refresh = Str(t, "refresh_token") ?? "";
        var expires = time.GetUtcNow().AddSeconds(Int(t, "expires_in", 7_776_000));
        using var c = db.Open();
        Database.Execute(c, null, """
            INSERT INTO trakt_accounts(user_id, access_token, refresh_token, expires_at, connected_at) VALUES(@u,@a,@r,@e,@n)
            ON CONFLICT(user_id) DO UPDATE SET access_token=excluded.access_token, refresh_token=excluded.refresh_token, expires_at=excluded.expires_at
            """, ("@u", userId), ("@a", box.Protect(access, "trakt.access")), ("@r", box.Protect(refresh, "trakt.refresh")),
            ("@e", expires.ToString("O", CultureInfo.InvariantCulture)), ("@n", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
    }

    /// <summary>A valid access token for this person, refreshed when it is about to run out. Null if they are not connected (or Trakt no longer accepts the link).</summary>
    private async Task<string?> TokenAsync(long userId, CancellationToken ct)
    {
        (string Access, string Refresh, DateTimeOffset Expires)? row;
        using (var c = db.Open())
            row = Database.Query(c, "SELECT access_token, refresh_token, expires_at FROM trakt_accounts WHERE user_id=@u",
                r => (r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)), ("@u", userId)).Select(x => ((string, string, DateTimeOffset)?)x).FirstOrDefault();
        if (row is null) return null;
        var access = box.TryUnprotect(row.Value.Access, "trakt.access");
        var refresh = box.TryUnprotect(row.Value.Refresh, "trakt.refresh");
        if (access is null) { Disconnect(userId); return null; }                      // the key changed: ask them to connect again
        if (row.Value.Expires - time.GetUtcNow() > TimeSpan.FromDays(2)) return access;
        if (string.IsNullOrEmpty(refresh)) return access;
        using var res = await http.SendAsync(Post("/oauth/token", new { refresh_token = refresh, client_id = ClientId, client_secret = ClientSecret, redirect_uri = "urn:ietf:wg:oauth:2.0:oob", grant_type = "refresh_token" }, null), ct);
        if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) { Disconnect(userId); return null; }   // revoked on trakt.tv
        if (!res.IsSuccessStatusCode) return access;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        Save(userId, doc.RootElement);
        return Str(doc.RootElement, "access_token");
    }

    // ------------------------------------------------------------------ pushing what you watched

    /// <summary>Adds titles to the person's Trakt history in the background. Never blocks or fails what called it.</summary>
    public void QueueWatched(long userId, IReadOnlyCollection<long> mediaIds)
    {
        if (!IsConfigured || mediaIds.Count == 0) return;
        var ids = mediaIds.Take(500).ToList();
        _ = Task.Run(async () =>
        {
            try { await PushWatchedAsync(userId, ids, CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation("Could not add to Trakt: {Reason}", ex.GetType().Name); }
        });
    }

    internal async Task PushWatchedAsync(long userId, IReadOnlyList<long> mediaIds, CancellationToken ct)
    {
        if (!IsConnected(userId)) return;
        var payload = BuildHistory(Load(mediaIds), time.GetUtcNow());
        if (payload is null) return;
        var token = await TokenAsync(userId, ct);
        if (token is null) return;
        using var res = await http.SendAsync(Post("/sync/history", payload, token), ct);
        if (!res.IsSuccessStatusCode) log.LogInformation("Trakt answered {Status} to a history update", (int)res.StatusCode);
    }

    private sealed record Local(long Id, string? Kind, string? TmdbType, long? TmdbId, int? Season, int? Episode);

    private List<Local> Load(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return [];
        using var c = db.Open();
        return Database.Query(c, $"SELECT id, kind, tmdb_type, tmdb_id, season, episode FROM media_files WHERE deleted_at IS NULL AND tmdb_id IS NOT NULL AND id IN ({string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))})",
            r => new Local(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5)));
    }

    /// <summary>Trakt's /sync/history body: films by TMDB id; episodes under their show's TMDB id with season and episode numbers. Null if nothing is matched to TMDB.</summary>
    internal static object? BuildHistory(IReadOnlyList<(long TmdbId, string Kind, int? Season, int? Episode)> items, DateTimeOffset watchedAt)
    {
        var at = watchedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var movies = items.Where(i => i.Kind == "Movie").Select(i => i.TmdbId).Distinct().Select(id => new { watched_at = at, ids = new { tmdb = id } }).ToList();
        var shows = items.Where(i => i.Kind == "Episode" && i.Season is not null && i.Episode is not null).GroupBy(i => i.TmdbId)
            .Select(g => new
            {
                ids = new { tmdb = g.Key },
                seasons = g.GroupBy(e => e.Season!.Value).Select(s => new { number = s.Key, episodes = s.Select(e => e.Episode!.Value).Distinct().Select(n => new { number = n, watched_at = at }).ToList() }).ToList(),
            }).ToList();
        return movies.Count == 0 && shows.Count == 0 ? null : new { movies, shows };
    }

    private static object? BuildHistory(List<Local> rows, DateTimeOffset at)
        => BuildHistory(rows.Where(r => r.Kind is "Movie" or "Episode").Select(r => (r.TmdbId!.Value, r.Kind!, r.Season, r.Episode)).ToList(), at);

    // ------------------------------------------------------------------ importing what Trakt says you watched

    /// <summary>Marks everything Trakt lists as watched as watched here. It never un-watches anything.</summary>
    public async Task<TraktImportResult> ImportAsync(long userId, CancellationToken ct)
    {
        var token = await TokenAsync(userId, ct) ?? throw new TraktException("Connect your Trakt account first.");
        var movies = await GetAsync("/sync/watched/movies", token, ct);
        var shows = await GetAsync("/sync/watched/shows", token, ct);
        var watchedMovies = ParseWatchedMovies(movies);
        var watchedEpisodes = ParseWatchedEpisodes(shows);
        return Apply(userId, watchedMovies, watchedEpisodes);
    }

    internal static HashSet<long> ParseWatchedMovies(JsonElement root)
    {
        var set = new HashSet<long>();
        if (root.ValueKind != JsonValueKind.Array) return set;
        foreach (var m in root.EnumerateArray())
            if (m.TryGetProperty("movie", out var mv) && mv.TryGetProperty("ids", out var ids) && ids.TryGetProperty("tmdb", out var t) && t.TryGetInt64(out var id)) set.Add(id);
        return set;
    }

    internal static HashSet<(long Show, int Season, int Episode)> ParseWatchedEpisodes(JsonElement root)
    {
        var set = new HashSet<(long, int, int)>();
        if (root.ValueKind != JsonValueKind.Array) return set;
        foreach (var s in root.EnumerateArray())
        {
            if (!s.TryGetProperty("show", out var sh) || !sh.TryGetProperty("ids", out var ids) || !ids.TryGetProperty("tmdb", out var t) || !t.TryGetInt64(out var show)) continue;
            if (!s.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array) continue;
            foreach (var se in seasons.EnumerateArray())
            {
                if (!se.TryGetProperty("number", out var sn) || !sn.TryGetInt32(out var season) || !se.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in eps.EnumerateArray()) if (e.TryGetProperty("number", out var en) && en.TryGetInt32(out var ep)) set.Add((show, season, ep));
            }
        }
        return set;
    }

    private TraktImportResult Apply(long userId, HashSet<long> movies, HashSet<(long Show, int Season, int Episode)> episodes)
    {
        int m = 0, e = 0;
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var local = Database.Query(c, "SELECT id, kind, tmdb_id, season, episode, COALESCE(duration_seconds, 1) FROM media_files WHERE deleted_at IS NULL AND tmdb_id IS NOT NULL AND kind IN ('Movie','Episode')",
            r => (Id: r.GetInt64(0), Kind: r.GetString(1), Tmdb: r.GetInt64(2), Season: r.IsDBNull(3) ? (int?)null : r.GetInt32(3), Episode: r.IsDBNull(4) ? (int?)null : r.GetInt32(4), Duration: r.GetDouble(5)));
        foreach (var f in local)
        {
            var hit = f.Kind == "Movie" ? movies.Contains(f.Tmdb) : f.Season is { } s && f.Episode is { } n && episodes.Contains((f.Tmdb, s, n));
            if (!hit) continue;
            var changed = Database.Execute(c, tx, """
                INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(@u,@m,@d,@d,1,@t)
                ON CONFLICT(user_id, media_id) DO UPDATE SET watched=1, position_seconds=excluded.position_seconds, updated_at=excluded.updated_at WHERE watched=0
                """, ("@u", userId), ("@m", f.Id), ("@d", f.Duration), ("@t", now));
            if (changed > 0) { if (f.Kind == "Movie") m++; else e++; }
        }
        tx.Commit();
        return new TraktImportResult(m, e);
    }

    // ------------------------------------------------------------------ plumbing

    private HttpRequestMessage Post(string path, object body, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = JsonContent.Create(body) };
        Headers(req, bearer);
        return req;
    }

    private void Headers(HttpRequestMessage req, string? bearer)
    {
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", ClientId);
        req.Headers.UserAgent.ParseAdd("Marquee");
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
    }

    private async Task<JsonElement> GetAsync(string path, string bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        Headers(req, bearer);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new TraktException("Trakt no longer accepts this link. Disconnect and connect again.");
        if (!res.IsSuccessStatusCode) throw new TraktException($"Trakt answered with an error ({(int)res.StatusCode}).");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;
}
