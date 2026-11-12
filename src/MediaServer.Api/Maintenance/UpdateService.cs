using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Maintenance;

public sealed record UpdateInfo(bool Enabled, string Current, string? Latest, bool Newer, string? Url, string? DownloadUrl, string? DownloadName, string? Notes, DateTimeOffset? CheckedAt, string? Error);

/// <summary>
/// Asks GitHub Releases whether a newer version of Marquee exists. It only tells you: it never downloads or replaces anything.
/// The answer is kept for hours, so opening Settings does not ask GitHub every time. Turn it off with Settings → General → Check for updates.
/// </summary>
public sealed partial class UpdateService(HttpClient http, IConfiguration config, TimeProvider time, ILogger<UpdateService> log)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(12), RetryAfterFailure = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _last;
    private DateTimeOffset _validUntil;

    public bool Enabled => config.GetValue("Updates:Check", true);
    private string Repository => config["Updates:Repository"] is { Length: > 0 } r && RepoRx().IsMatch(r) ? r : "thekolbstr/Marquee";

    [GeneratedRegex(@"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")] private static partial Regex RepoRx();
    [GeneratedRegex(@"\d+(\.\d+){1,3}")] private static partial Regex NumbersRx();

    /// <summary>The numbers in a tag or version ("v0.0.3-alpha", "alpha-0.0.2", "0.0.3-dev"), or null if there are none (a git hash, "dev").</summary>
    public static Version? ParseCore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || NumbersRx().Match(text) is not { Success: true } m) return null;
        var parts = m.Value.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).ToList();
        if (parts.Any(p => p < 0)) return null;
        while (parts.Count < 3) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts.Count > 3 ? parts[3] : 0);
    }

    /// <summary>True when the latest release has a higher number than what is running. A build with no number (a git hash) never counts as out of date.</summary>
    public static bool IsNewer(string? latest, string? current) => ParseCore(latest) is { } l && ParseCore(current) is { } c && l > c;

    /// <summary>Which download in a release is for this machine, by the names the package workflow gives them.</summary>
    public static string? PlatformToken(Architecture arch, bool windows, bool linux, bool mac) => (windows, linux, mac, arch) switch
    {
        (true, _, _, Architecture.X64) => "windows-x64",
        (_, true, _, Architecture.X64) => "linux-x64",
        (_, true, _, Architecture.Arm64) => "linux-arm64",
        (_, _, true, Architecture.Arm64) => "macos-arm64",
        (_, _, true, Architecture.X64) => "macos-x64",
        _ => null,
    };

    private static string? ThisPlatform() => PlatformToken(RuntimeInformation.OSArchitecture, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS());

    public async Task<UpdateInfo> CheckAsync(bool force, CancellationToken ct)
    {
        var current = AppVersion.Current;
        if (!Enabled) return new UpdateInfo(false, current, null, false, null, null, null, null, null, null);
        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _last is not null && time.GetUtcNow() < _validUntil) return _last with { Current = current };
            try
            {
                _last = await FetchAsync(current, ct);
                _validUntil = time.GetUtcNow() + Fresh;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Could not check for updates: {Error}", ex.Message);
                _last = new UpdateInfo(true, current, _last?.Latest, _last?.Newer ?? false, _last?.Url, _last?.DownloadUrl, _last?.DownloadName, _last?.Notes, _last?.CheckedAt, "Could not reach GitHub.");
                _validUntil = time.GetUtcNow() + RetryAfterFailure;
            }
            return _last;
        }
        finally { _gate.Release(); }
    }

    private async Task<UpdateInfo> FetchAsync(string current, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=15");
        req.Headers.UserAgent.ParseAdd("Marquee-update-check");   // GitHub requires a User-Agent; nothing about this server or its owner is sent
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement, current, time.GetUtcNow(), ThisPlatform());
    }

    /// <summary>Picks the highest-numbered published release from GitHub's list. Links are only passed on if they point at github.com.</summary>
    internal static UpdateInfo Parse(JsonElement releases, string current, DateTimeOffset now, string? platform)
    {
        JsonElement? best = null; Version? bestV = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var v = ParseCore(Str(r, "tag_name"));
            if (v is null || (bestV is not null && v <= bestV)) continue;
            best = r; bestV = v;
        }
        if (best is not { } b) return new UpdateInfo(true, current, null, false, null, null, null, null, now, null);
        var tag = Str(b, "tag_name")!;
        string? dl = null, dlName = null;
        if (platform is not null && b.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (Str(a, "name") is { } n && n.Contains(platform, StringComparison.OrdinalIgnoreCase) && GitHubLink(Str(a, "browser_download_url")) is { } link) { dl = link; dlName = n; break; }
        var notes = Str(b, "body");
        if (notes is { Length: > 1500 }) notes = notes[..1500] + "…";
        return new UpdateInfo(true, current, tag.TrimStart('v', 'V'), IsNewer(tag, current), GitHubLink(Str(b, "html_url")), dl, dlName, notes, now, null);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string? GitHubLink(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host is "github.com" or "www.github.com" ? u.AbsoluteUri : null;
}
