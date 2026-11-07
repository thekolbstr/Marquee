using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaServer.Api.Arr;

public enum ArrKind { Radarr, Sonarr }

public sealed class ArrException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed record ArrQueueItem(string Source, string Title, string? Detail, string State, double Percent, string? TimeLeft, string? Problem, string? Poster = null);
public sealed record ArrUpcoming(string Source, string Title, string? Detail, string Date, bool HasFile, bool Monitored, string? Poster = null);
public sealed record ArrLookupItem(string Source, string Title, int? Year, string? Overview, int? TmdbId, int? TvdbId, bool InLibrary, string? Poster = null, double? Rating = null, string? Status = null);
public sealed record ArrOption(int Id, string Name);
public sealed record ArrOptions(IReadOnlyList<ArrOption> Profiles, IReadOnlyList<ArrOption> Folders);

/// <summary>
/// Talks to Radarr (movies) and Sonarr (TV, v4 API) with the address and key saved in Settings. The key goes in a header, never in a URL.
/// Everything is read on each call, so changing a setting takes effect straight away.
/// </summary>
public sealed class ArrService(HttpClient http, IConfiguration config)
{
    private static string Name(ArrKind k) => k == ArrKind.Radarr ? "Radarr" : "Sonarr";
    private string? BaseUrl(ArrKind k) => config[$"{Name(k)}:Url"] is { Length: > 0 } u ? u.TrimEnd('/') : null;
    private string? Key(ArrKind k) => config[$"{Name(k)}:ApiKey"] is { Length: > 0 } key ? key : null;

    public bool IsConfigured(ArrKind k) => BaseUrl(k) is not null && Key(k) is not null;
    public bool AnyConfigured => IsConfigured(ArrKind.Radarr) || IsConfigured(ArrKind.Sonarr);

    private async Task<JsonNode?> SendAsync(ArrKind k, HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct)
    {
        if (!IsConfigured(k)) throw new ArrException($"{Name(k)} is not set up.");
        using var req = new HttpRequestMessage(method, $"{BaseUrl(k)}/api/v3/{pathAndQuery}");
        req.Headers.Add("X-Api-Key", Key(k));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            throw new ArrException(resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"{Name(k)} rejected the API key.",
                HttpStatusCode.NotFound => $"{Name(k)} doesn't have that address. Check the URL (and that it's a recent version).",
                _ => ProblemText(text) ?? $"{Name(k)} answered {(int)resp.StatusCode}.",
            }, resp.StatusCode);
        }
        catch (HttpRequestException ex) { throw new ArrException($"Could not reach {Name(k)}: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ArrException($"{Name(k)} didn't answer in time."); }
        catch (JsonException) { throw new ArrException($"{Name(k)} answered with something unexpected. Is the URL right?"); }
    }

    /// <summary>The *arr apps explain a refused request as [{ "errorMessage": "..." }] or { "message": "..." }.</summary>
    internal static string? ProblemText(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            if (n is JsonArray a) return string.Join(" ", a.Select(x => (string?)x?["errorMessage"]).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) is { Length: > 0 } t ? t : null;
            return (string?)n?["message"] is { Length: > 0 } m ? m : (string?)n?["errorMessage"];
        }
        catch (JsonException) { return null; }
    }

    public async Task<(bool Ok, string Message)> PingAsync(ArrKind k, CancellationToken ct)
    {
        if (!IsConfigured(k)) return (false, $"Save the {Name(k)} address and API key first.");
        try
        {
            var n = await SendAsync(k, HttpMethod.Get, "system/status", null, ct);
            var app = (string?)n?["appName"];
            if (app is not null && !app.Contains(Name(k), StringComparison.OrdinalIgnoreCase)) return (false, $"That address is {app}, not {Name(k)}.");
            return (true, $"Connected to {Name(k)} {(string?)n?["version"]}".TrimEnd());
        }
        catch (ArrException ex) { return (false, ex.Message); }
    }

    // ---------------------------------------------------------------- what is downloading
    public async Task<IReadOnlyList<ArrQueueItem>> QueueAsync(CancellationToken ct)
    {
        var items = new List<ArrQueueItem>();
        foreach (var k in new[] { ArrKind.Radarr, ArrKind.Sonarr })
        {
            if (!IsConfigured(k)) continue;
            var include = k == ArrKind.Radarr ? "includeMovie=true" : "includeSeries=true&includeEpisode=true";
            var page = await SendAsync(k, HttpMethod.Get, $"queue?page=1&pageSize=50&{include}", null, ct);
            foreach (var r in page?["records"]?.AsArray() ?? [])
            {
                if (r is null) continue;
                var size = (double?)r["size"] ?? 0; var left = (double?)r["sizeleft"] ?? 0;
                var title = k == ArrKind.Radarr ? Title((string?)r["movie"]?["title"], (int?)r["movie"]?["year"]) : (string?)r["series"]?["title"];
                var ep = r["episode"];
                var detail = k == ArrKind.Sonarr && ep is not null ? $"S{(int?)ep["seasonNumber"]}E{(int?)ep["episodeNumber"]} · {(string?)ep["title"]}" : null;
                var warn = (string?)r["errorMessage"] ?? (r["statusMessages"] is JsonArray { Count: > 0 } sm ? (string?)sm[0]?["messages"]?[0] : null);
                var bad = string.Equals((string?)r["trackedDownloadStatus"], "error", StringComparison.OrdinalIgnoreCase) || string.Equals((string?)r["status"], "failed", StringComparison.OrdinalIgnoreCase);
                items.Add(new ArrQueueItem(Name(k), title ?? (string?)r["title"] ?? "Download", detail, State((string?)r["trackedDownloadState"], (string?)r["status"]),
                    size > 0 ? Math.Round(Math.Clamp(1 - left / size, 0, 1) * 100, 1) : 0, (string?)r["timeleft"], bad || warn is not null ? warn ?? "Something went wrong" : null,
                    ArrArt.PosterPath(k == ArrKind.Radarr ? r["movie"]?["images"] : r["series"]?["images"])));
            }
        }
        return items;
    }

    private static string State(string? tracked, string? status) => tracked switch
    {
        "downloading" => "Downloading", "importPending" or "importBlocked" => "Waiting to import", "importing" => "Importing", "imported" => "Imported",
        "failedPending" or "failed" => "Failed", "ignored" => "Ignored", _ => status switch { "queued" => "Queued", "paused" => "Paused", "completed" => "Completed", "failed" => "Failed", _ => "Queued" },
    };

    // ---------------------------------------------------------------- what is coming
    public async Task<IReadOnlyList<ArrUpcoming>> UpcomingAsync(int days, CancellationToken ct)
    {
        var from = DateTime.UtcNow.Date; var to = from.AddDays(Math.Clamp(days, 1, 90));
        var range = $"start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}";
        var items = new List<ArrUpcoming>();
        if (IsConfigured(ArrKind.Sonarr))
            foreach (var e in (await SendAsync(ArrKind.Sonarr, HttpMethod.Get, $"calendar?{range}&includeSeries=true&unmonitored=false", null, ct))?.AsArray() ?? [])
            {
                if (e is null || (string?)e["airDateUtc"] is not { } date) continue;
                items.Add(new ArrUpcoming("Sonarr", (string?)e["series"]?["title"] ?? "Episode", $"S{(int?)e["seasonNumber"]}E{(int?)e["episodeNumber"]} · {(string?)e["title"]}",
                    date, (bool?)e["hasFile"] ?? false, (bool?)e["monitored"] ?? true, ArrArt.PosterPath(e["series"]?["images"])));
            }
        if (IsConfigured(ArrKind.Radarr))
            foreach (var m in (await SendAsync(ArrKind.Radarr, HttpMethod.Get, $"calendar?{range}&unmonitored=false", null, ct))?.AsArray() ?? [])
            {
                if (m is null) continue;
                // The date people can actually watch it: home release first, then cinemas.
                var which = new[] { ("digitalRelease", "Digital release"), ("physicalRelease", "Disc release"), ("inCinemas", "In cinemas") }
                    .Select(x => (Date: (string?)m[x.Item1], Label: x.Item2)).Where(x => x.Date is not null && string.CompareOrdinal(x.Date![..10], from.ToString("yyyy-MM-dd")) >= 0 && string.CompareOrdinal(x.Date[..10], to.ToString("yyyy-MM-dd")) <= 0).FirstOrDefault();
                if (which.Date is null) continue;
                items.Add(new ArrUpcoming("Radarr", Title((string?)m["title"], (int?)m["year"]) ?? "Movie", which.Label, which.Date, (bool?)m["hasFile"] ?? false, (bool?)m["monitored"] ?? true, ArrArt.PosterPath(m["images"])));
            }
        return items.OrderBy(i => i.Date, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- finding and adding things
    public async Task<IReadOnlyList<ArrLookupItem>> LookupAsync(string term, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(term);
        var items = new List<ArrLookupItem>();
        foreach (var (k, path) in new[] { (ArrKind.Radarr, "movie/lookup"), (ArrKind.Sonarr, "series/lookup") })
        {
            if (!IsConfigured(k)) continue;
            foreach (var r in (await SendAsync(k, HttpMethod.Get, $"{path}?term={q}", null, ct))?.AsArray().Take(12) ?? [])
            {
                if (r is null || (string?)r["title"] is not { } title) continue;
                var overview = (string?)r["overview"];
                items.Add(new ArrLookupItem(Name(k), title, (int?)r["year"] is > 0 and var y ? y : null, overview is { Length: > 280 } ? overview[..280] + "…" : overview,
                    (int?)r["tmdbId"] is > 0 and var t ? t : null, (int?)r["tvdbId"] is > 0 and var v ? v : null, ((int?)r["id"] ?? 0) > 0,
                    ArrArt.PosterPath(r["images"]), Rating(r["ratings"]), (string?)r["status"]));
            }
        }
        return items;
    }

    public async Task<ArrOptions> OptionsAsync(ArrKind k, CancellationToken ct)
    {
        var profiles = (await SendAsync(k, HttpMethod.Get, "qualityprofile", null, ct) as JsonArray)?.Select(p => new ArrOption((int)p!["id"]!, (string?)p["name"] ?? "Profile")).ToList() ?? [];
        var folders = (await SendAsync(k, HttpMethod.Get, "rootfolder", null, ct) as JsonArray)?.Select(f => new ArrOption((int)f!["id"]!, (string?)f["path"] ?? "")).Where(f => f.Name.Length > 0).ToList() ?? [];
        return new ArrOptions(profiles, folders);
    }

    /// <summary>Adds a film (by TMDB id) or a show (by TVDB id) and has the app search for it right away.</summary>
    public async Task AddAsync(ArrKind k, int externalId, int qualityProfileId, string rootFolder, CancellationToken ct)
    {
        // Ask the app to describe the item first: that's the object it wants back, with its own titles and images filled in.
        var found = await SendAsync(k, HttpMethod.Get, k == ArrKind.Radarr ? $"movie/lookup/tmdb?tmdbId={externalId}" : $"series/lookup?term=tvdb:{externalId}", null, ct);
        var item = (found is JsonArray a ? a.FirstOrDefault() : found) as JsonObject ?? throw new ArrException($"{Name(k)} couldn't find that title.");
        if (((int?)item["id"] ?? 0) > 0) throw new ArrException($"It's already in {Name(k)}.", HttpStatusCode.Conflict);
        var folders = (await SendAsync(k, HttpMethod.Get, "rootfolder", null, ct)) as JsonArray ?? [];
        if (!folders.Any(f => string.Equals(((string?)f?["path"])?.TrimEnd('/', '\\'), rootFolder.TrimEnd('/', '\\'), StringComparison.Ordinal)))
            throw new ArrException("Pick one of the folders from the list.");
        item["qualityProfileId"] = qualityProfileId;
        item["rootFolderPath"] = rootFolder;
        item["monitored"] = true;
        if (k == ArrKind.Radarr)
        {
            item["minimumAvailability"] = "released";
            item["addOptions"] = new JsonObject { ["searchForMovie"] = true };
        }
        else
        {
            item["seasonFolder"] = true;
            item["addOptions"] = new JsonObject { ["monitor"] = "all", ["searchForMissingEpisodes"] = true, ["searchForCutoffUnmetEpisodes"] = false };
        }
        await SendAsync(k, HttpMethod.Post, k == ArrKind.Radarr ? "movie" : "series", item, ct);
    }

    /// <summary>Radarr nests the score under the site it came from, Sonarr has a single value. Out of 10; 0 means "no votes".</summary>
    private static double? Rating(JsonNode? ratings)
    {
        var v = (double?)ratings?["value"] ?? (double?)ratings?["tmdb"]?["value"] ?? (double?)ratings?["imdb"]?["value"];
        return v is > 0 ? Math.Round(v.Value, 1) : null;
    }

    private static string? Title(string? title, int? year) => title is null ? null : year is > 0 ? $"{title} ({year})" : title;
}
