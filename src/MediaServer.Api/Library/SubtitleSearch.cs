using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Api.Maintenance;

namespace MediaServer.Api.Library;

/// <summary>What to look for. Only the title, year and season/episode leave the server.</summary>
public sealed record SubtitleQuery(string Title, int? Year, int? Season, int? Episode);

public sealed record SubtitleResult(string FileId, string Name, string Language, int Downloads, bool HearingImpaired, string? Release, bool Trusted);

public sealed class SubtitleProviderException(string message) : Exception(message);

public interface ISubtitleProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<SubtitleResult>> SearchAsync(SubtitleQuery query, string language, CancellationToken ct);
    /// <returns>The subtitle file as SRT bytes.</returns>
    Task<byte[]> DownloadAsync(string fileId, CancellationToken ct);
}

/// <summary>
/// OpenSubtitles.com (REST API v1). Needs an API key from opensubtitles.com/consumers and an account (downloads are signed-in and limited per day by them).
/// The key and password are only ever sent to OpenSubtitles, in headers and the login call, and are never logged.
/// </summary>
public sealed class OpenSubtitlesClient(HttpClient http, IConfiguration config, TimeProvider time) : ISubtitleProvider
{
    private const string Api = "https://api.opensubtitles.com/api/v1";
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly Regex LangRx = new("^[a-z]{2,3}(-[A-Za-z]{2,4})?$", RegexOptions.Compiled);
    private readonly SemaphoreSlim _login = new(1, 1);
    private string? _token; private DateTimeOffset _tokenUntil;

    private string? Key => config["Subtitles:OpenSubtitlesApiKey"] is { Length: > 0 } k ? k : null;
    private string? User => config["Subtitles:OpenSubtitlesUsername"] is { Length: > 0 } u ? u : null;
    private string? Password => config["Subtitles:OpenSubtitlesPassword"] is { Length: > 0 } p ? p : null;
    public bool IsConfigured => Key is not null && User is not null && Password is not null;

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? bearer = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Api-Key", Key);
        req.Headers.UserAgent.ParseAdd($"Marquee v{AppVersion.Current.Split('-')[0]}");   // the API requires an app name and version
        req.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    public async Task<IReadOnlyList<SubtitleResult>> SearchAsync(SubtitleQuery q, string language, CancellationToken ct)
    {
        if (!IsConfigured) throw new SubtitleProviderException("Subtitle search is not set up. Add your OpenSubtitles details in Settings → Subtitles.");
        if (!LangRx.IsMatch(language)) throw new SubtitleProviderException("Unknown language.");
        // The API wants lower-case parameters in alphabetical order.
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["languages"] = language.ToLowerInvariant(), ["query"] = q.Title, ["type"] = q.Season is not null ? "episode" : "movie" };
        if (q.Year is { } y && q.Season is null) parts["year"] = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (q.Season is { } s) parts["season_number"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (q.Episode is { } e) parts["episode_number"] = e.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var url = $"{Api}/subtitles?" + string.Join('&', parts.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var res = await http.SendAsync(Request(HttpMethod.Get, url), ct);
        await EnsureOk(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return ParseSearch(doc.RootElement);
    }

    /// <summary>Reads the search response, best first. Results without a downloadable file are skipped.</summary>
    internal static IReadOnlyList<SubtitleResult> ParseSearch(JsonElement root)
    {
        var list = new List<SubtitleResult>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in data.EnumerateArray().Take(100))
        {
            if (!item.TryGetProperty("attributes", out var a)) continue;
            if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
            var f = files[0];
            if (!f.TryGetProperty("file_id", out var idEl) || !idEl.TryGetInt64(out var fileId)) continue;
            list.Add(new SubtitleResult(fileId.ToString(System.Globalization.CultureInfo.InvariantCulture), Clip(Str(f, "file_name") ?? Str(a, "release") ?? "subtitle", 120) ?? "subtitle",
                Clip(Str(a, "language"), 12) ?? "", a.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out var n) ? n : 0,
                a.TryGetProperty("hearing_impaired", out var hi) && hi.ValueKind == JsonValueKind.True, Clip(Str(a, "release"), 120),
                a.TryGetProperty("from_trusted", out var tr) && tr.ValueKind == JsonValueKind.True));
        }
        return list.OrderByDescending(r => r.Trusted).ThenByDescending(r => r.Downloads).ToList();
    }

    public async Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
    {
        if (!IsConfigured) throw new SubtitleProviderException("Subtitle search is not set up. Add your OpenSubtitles details in Settings → Subtitles.");
        if (!long.TryParse(fileId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) throw new SubtitleProviderException("Unknown subtitle.");
        var token = await TokenAsync(ct);
        using var res = await http.SendAsync(Request(HttpMethod.Post, $"{Api}/download", new { file_id = id, sub_format = "srt" }, token), ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) { _token = null; throw new SubtitleProviderException("OpenSubtitles did not accept the sign-in. Check the username and password in Settings → Subtitles."); }
        await EnsureOk(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var link = Str(doc.RootElement, "link");
        if (!AllowedLink(link, out var uri)) throw new SubtitleProviderException("OpenSubtitles returned an unexpected download address.");
        using var file = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(file);
        if (file.Content.Headers.ContentLength > MaxBytes) throw new SubtitleProviderException("That subtitle file is too large.");
        await using var stream = await file.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { ms.Write(buffer, 0, read); if (ms.Length > MaxBytes) throw new SubtitleProviderException("That subtitle file is too large."); }
        return ms.ToArray();
    }

    /// <summary>Only https links on OpenSubtitles' own domains are followed.</summary>
    internal static bool AllowedLink(string? link, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host.ToLowerInvariant();
        if (!(h == "opensubtitles.com" || h.EndsWith(".opensubtitles.com") || h == "opensubtitles.org" || h.EndsWith(".opensubtitles.org"))) return false;
        uri = u; return true;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && time.GetUtcNow() < _tokenUntil) return _token;
        await _login.WaitAsync(ct);
        try
        {
            if (_token is not null && time.GetUtcNow() < _tokenUntil) return _token;
            using var res = await http.SendAsync(Request(HttpMethod.Post, $"{Api}/login", new { username = User, password = Password }), ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) throw new SubtitleProviderException("OpenSubtitles did not accept the sign-in. Check the username and password in Settings → Subtitles.");
            await EnsureOk(res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            _token = Str(doc.RootElement, "token") ?? throw new SubtitleProviderException("OpenSubtitles did not return a sign-in token.");
            _tokenUntil = time.GetUtcNow().AddHours(12);   // their tokens last a day
            return _token;
        }
        finally { _login.Release(); }
    }

    private static Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return Task.CompletedTask;
        throw new SubtitleProviderException(res.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "OpenSubtitles says slow down. Try again in a minute.",
            HttpStatusCode.NotAcceptable => "You have used today's download allowance on OpenSubtitles. Try again tomorrow.",
            HttpStatusCode.Forbidden => "OpenSubtitles refused the API key. Check it in Settings → Subtitles.",
            HttpStatusCode.Unauthorized => "OpenSubtitles did not accept the sign-in. Check the username and password in Settings → Subtitles.",
            _ => $"OpenSubtitles answered with an error ({(int)res.StatusCode}).",
        });
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;

    /// <summary>Subtitle bytes to clean UTF-8 text: a byte-order mark is dropped, and a file that is not valid UTF-8 is read as Latin-1 (the usual case for older files).</summary>
    public static string ToText(byte[] bytes)
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        string text;
        try { text = strict.GetString(bytes); } catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return text.TrimStart('﻿');
    }

    /// <summary>A real subtitle file is text with timing lines; anything else (an HTML error page, a zip) is refused.</summary>
    public static bool LooksLikeSubtitles(string text) => text.Length > 20 && !text.Contains('\0') && text.Contains("-->");
}
