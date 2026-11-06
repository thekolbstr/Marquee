using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

public sealed class TmdbException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class TmdbNotConfiguredException() : Exception("TMDB is not configured. Set Tmdb:ApiKey (or Tmdb:ReadAccessToken) via user-secrets / a Kubernetes Secret.");

public sealed record TmdbHit(int Id, string Title, string? OriginalTitle, int? Year, double Popularity, string? PosterPath, string? Overview);

public sealed record TmdbEpisode(int Number, string Name, string? Overview, string? AirDate, int? Runtime, string? StillPath);

public sealed record TmdbDetails(string Type, int Id, string Title, string? OriginalTitle, int? Year, string? ReleaseDate,
    string? Overview, string? PosterPath, string? BackdropPath, double? Rating, IReadOnlyList<string> Genres);

/// <summary>
/// Minimal TMDB v3 client with a SQLite response cache (positive TTL, short negative TTL, stale-if-error).
/// Credentials go in the Authorization header (or api_key query) and are never part of the cache key.
/// </summary>
public sealed class TmdbClient
{
    // Details rarely change. Search results must expire well inside Metadata:RetryUnmatchedDays (7), otherwise a retry
    // of an unmatched file would just re-read the same stale result list and never see newly added TMDB entries.
    private static readonly TimeSpan DetailsTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromDays(2);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly Database _db;
    private readonly TimeProvider _time;
    private readonly string? _bearer;
    private readonly string? _apiKey;
    private readonly string _lang;

    public TmdbClient(HttpClient http, Database db, IConfiguration config, TimeProvider time)
    {
        _http = http; _db = db; _time = time;
        var token = config["Tmdb:ReadAccessToken"];
        var key = config["Tmdb:ApiKey"];
        // Be forgiving: a v4 read token (JWT) pasted into ApiKey still works.
        if (string.IsNullOrWhiteSpace(token) && key is { Length: > 40 } && key.StartsWith("eyJ", StringComparison.Ordinal)) { token = key; key = null; }
        _bearer = string.IsNullOrWhiteSpace(token) ? null : token;
        _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
        _http.BaseAddress ??= new Uri(config["Tmdb:BaseUrl"] ?? "https://api.themoviedb.org/3/");
        _lang = System.Text.RegularExpressions.Regex.IsMatch(config["Tmdb:Language"] ?? "", "^[a-z]{2}-[A-Z]{2}$") ? config["Tmdb:Language"]! : "en-US";
    }

    public bool IsConfigured => _bearer is not null || _apiKey is not null;

    /// <summary>Asks TMDB something trivial to find out whether the saved credentials work. Never cached.</summary>
    public async Task<(bool Ok, string Message)> PingAsync(CancellationToken ct)
    {
        if (!IsConfigured) return (false, "No TMDB credentials are saved.");
        try
        {
            using var resp = await SendAsync("configuration", ct);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => (true, "Connected to TMDB."),
                HttpStatusCode.Unauthorized => (false, "TMDB rejected these credentials."),
                _ => (false, $"TMDB answered {(int)resp.StatusCode}."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, "Could not reach TMDB from the server."); }
    }

    public async Task<IReadOnlyList<TmdbHit>> SearchAsync(string type, string query, int? year, CancellationToken ct)
    {
        RequireType(type);
        var q = $"search/{type}?query={Uri.EscapeDataString(query)}&include_adult=false&language={_lang}";
        if (year is { } y) q += type == "movie" ? $"&year={y}" : $"&first_air_date_year={y}";

        var body = await GetCachedAsync(q, SearchTtl, ct);
        if (body is null) return [];
        using var doc = JsonDocument.Parse(body);
        var hits = new List<TmdbHit>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return hits;
        foreach (var r in results.EnumerateArray().Take(10))
        {
            var title = Str(r, type == "movie" ? "title" : "name");
            if (title is null || !r.TryGetProperty("id", out var idEl)) continue;
            hits.Add(new TmdbHit(idEl.GetInt32(), title, Str(r, type == "movie" ? "original_title" : "original_name"),
                YearOf(Str(r, type == "movie" ? "release_date" : "first_air_date")),
                r.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0,
                Str(r, "poster_path"), Str(r, "overview")));
        }
        return hits;
    }

    public async Task<TmdbDetails?> GetDetailsAsync(string type, int id, CancellationToken ct)
    {
        RequireType(type);
        var body = await GetCachedAsync($"{type}/{id}?language={_lang}", DetailsTtl, ct);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var title = Str(r, type == "movie" ? "title" : "name");
        if (title is null) return null;
        var date = Str(r, type == "movie" ? "release_date" : "first_air_date");
        var genres = r.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => Str(x, "name")).OfType<string>().ToList() : [];
        return new TmdbDetails(type, id, title, Str(r, type == "movie" ? "original_title" : "original_name"), YearOf(date), date,
            Str(r, "overview"), Str(r, "poster_path"), Str(r, "backdrop_path"),
            r.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null, genres);
    }

    /// <summary>Episode names, descriptions and stills for one season of a show. Empty if TMDB has none.</summary>
    public async Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tvId, int season, CancellationToken ct)
    {
        if (tvId <= 0 || season < 0 || season > 1000) return [];
        var body = await GetCachedAsync($"tv/{tvId}/season/{season}?language={_lang}", DetailsTtl, ct);
        if (body is null) return [];
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array) return [];
        var list = new List<TmdbEpisode>();
        foreach (var e in eps.EnumerateArray())
        {
            if (!e.TryGetProperty("episode_number", out var n) || n.ValueKind != JsonValueKind.Number) continue;
            list.Add(new TmdbEpisode(n.GetInt32(), Str(e, "name") ?? $"Episode {n.GetInt32()}", Str(e, "overview"), Str(e, "air_date"), e.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null, Str(e, "still_path")));
            if (list.Count >= 500) break;
        }
        return list;
    }

    /// <returns>Response body, or null for a (cached) 404.</returns>
    private async Task<string?> GetCachedAsync(string key, TimeSpan positiveTtl, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var cached = ReadCache(key);
        if (cached is { } c && now - c.FetchedAt < (c.Status == 200 ? positiveTtl : NegativeTtl))
            return c.Status == 200 ? c.Body : null;

        if (!IsConfigured)
        {
            if (cached is { } old) return old.Status == 200 ? old.Body : null; // serve stale rather than fail
            throw new TmdbNotConfiguredException();
        }

        HttpResponseMessage resp;
        try { resp = await SendAsync(key, ct); }
        catch (HttpRequestException) when (cached is not null) { return cached.Value.Status == 200 ? cached.Value.Body : null; }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    WriteCache(key, 200, body, now);
                    return body;
                case HttpStatusCode.NotFound:
                    WriteCache(key, 404, null, now);
                    return null;
                case HttpStatusCode.Unauthorized:
                    throw new TmdbException("TMDB rejected the credentials (401). Check Tmdb:ApiKey / Tmdb:ReadAccessToken.", resp.StatusCode);
                default:
                    if (cached is { } stale) return stale.Status == 200 ? stale.Body : null; // stale-if-error
                    throw new TmdbException($"TMDB returned {(int)resp.StatusCode}", resp.StatusCode);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string key, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var url = _bearer is null ? key + (key.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(_apiKey!) : key;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 1) return resp;

            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            resp.Dispose();
            await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, _time, ct);
        }
    }

    private (int Status, string? Body, DateTimeOffset FetchedAt)? ReadCache(string key)
    {
        using var c = _db.Open();
        var rows = Database.Query(c, "SELECT status, body, fetched_at FROM tmdb_cache WHERE key=@k",
            r => (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture)), ("@k", key));
        return rows.Count == 0 ? null : rows[0];
    }

    private void WriteCache(string key, int status, string? body, DateTimeOffset at)
    {
        using var c = _db.Open();
        Database.Execute(c, null, """
            INSERT INTO tmdb_cache(key,status,body,fetched_at) VALUES(@k,@s,@b,@t)
            ON CONFLICT(key) DO UPDATE SET status=excluded.status, body=excluded.body, fetched_at=excluded.fetched_at
            """, ("@k", key), ("@s", status), ("@b", body), ("@t", at.ToString("O", CultureInfo.InvariantCulture)));
    }

    private static void RequireType(string type)
    {
        if (type is not ("movie" or "tv")) throw new ArgumentException("type must be 'movie' or 'tv'", nameof(type));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
}
