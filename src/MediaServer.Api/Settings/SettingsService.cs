using System.Globalization;
using Microsoft.Extensions.Primitives;
using MediaServer.Api.Data;
using MediaServer.Api.LiveTv;

namespace MediaServer.Api.Settings;

public enum SettingKind { Text, Secret, Url, SecretUrl, Bool, Int, Choice }

public sealed record SettingOption(string Value, string Label);

/// <summary>One setting the web UI is allowed to change. Anything not listed here cannot be written through the API.</summary>
public sealed record SettingDef(string Key, string Group, string Label, string Help, SettingKind Kind, int Min = 0, int Max = 0, string? Placeholder = null,
    string? Default = null, IReadOnlyList<SettingOption>? Options = null, string? Unit = null);

public sealed record SettingState(string Key, string Label, string Help, string Kind, string? Value, bool IsSet, string? Preview, string Source, int? Min, int? Max, string? Placeholder,
    IReadOnlyList<SettingOption>? Options = null, string? Unit = null, string? Default = null);
public sealed record SettingGroup(string Id, string Title, string Description, IReadOnlyList<SettingState> Items);

public sealed class SettingsException(string key, string message) : Exception(message)
{
    public string Key { get; } = key;
}

/// <summary>A configuration source backed by the settings table, layered over appsettings and environment variables.</summary>
public sealed class DbSettingsProvider(Database db) : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override void Load()
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1)));
        Data = rows.ToDictionary(r => r.Item1, r => (string?)r.Item2, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Re-reads the table and tells everything holding IConfiguration that values changed.</summary>
    public void Reload() { Load(); OnReload(); }
}

public sealed class SettingsService(Database db, DbSettingsProvider provider, IConfiguration config, TimeProvider time)
{
    private static SettingOption[] Opt(params (string V, string L)[] o) => o.Select(x => new SettingOption(x.V, x.L)).ToArray();

    private static readonly SettingOption[] Languages = Opt(("en-US", "English"), ("es-ES", "Español"), ("fr-FR", "Français"), ("de-DE", "Deutsch"), ("it-IT", "Italiano"), ("pt-BR", "Português (Brasil)"),
        ("nl-NL", "Nederlands"), ("sv-SE", "Svenska"), ("pl-PL", "Polski"), ("ru-RU", "Русский"), ("tr-TR", "Türkçe"), ("ja-JP", "日本語"), ("ko-KR", "한국어"), ("zh-CN", "中文"));

    public static readonly IReadOnlyList<SettingDef> Defs =
    [
        // general
        new("General:ServerName", "general", "Server name", "Shown in the header, the browser tab and on the sign-in page.", SettingKind.Text, Placeholder: "MediaServer"),

        // library
        new("Library:AutoScan", "library", "Scan automatically", "Look for new and removed files on a schedule, so nobody has to press the scan button.", SettingKind.Bool, Default: "true"),
        new("Library:ScanIntervalMinutes", "library", "Scan every", "0 means scan once when the server starts.", SettingKind.Int, 0, 10080, Default: "360", Unit: "minutes"),
        new("Library:ProbeParallelism", "library", "Files analysed at once", "More is faster on a big first scan but works the disk harder. Lower it for a NAS or a spinning disk.", SettingKind.Int, 1, 16, Default: "4"),

        // metadata
        new("Tmdb:ReadAccessToken", "metadata", "TMDB read access token", "The long “API Read Access Token” from themoviedb.org → Settings → API. Preferred over the API key.", SettingKind.Secret),
        new("Tmdb:ApiKey", "metadata", "TMDB API key", "The short v3 key. Only needed if you don't use the token above.", SettingKind.Secret),
        new("Tmdb:Language", "metadata", "Language", "Titles, descriptions and episode names come in this language. Existing titles change after “Refresh all metadata” under Maintenance.", SettingKind.Choice, Default: "en-US", Options: Languages),
        new("Metadata:RetryUnmatchedDays", "metadata", "Look again for unmatched titles every", "Titles with no TMDB match are tried again after this long, in case TMDB has added them.", SettingKind.Int, 1, 90, Default: "7", Unit: "days"),

        // streaming
        new("Hls:AllowTranscoding", "streaming", "Allow transcoding", "Re-encoding video is what makes a server work hard. Turn it off and only files a device can play directly (or with the video copied) will play.", SettingKind.Bool, Default: "true"),
        new("Hls:Encoder", "streaming", "Video encoder", "Software works everywhere. NVIDIA needs a GPU and the NVIDIA container runtime; if it isn't available, transcodes fail.", SettingKind.Choice, Default: "libx264", Options: Opt(("libx264", "Software (CPU)"), ("nvenc", "NVIDIA GPU (NVENC)"))),
        new("Hls:MaxSessions", "streaming", "Simultaneous streams", "Starting one more stops the one nobody has touched the longest. Live TV counts as a stream.", SettingKind.Int, 1, 16, Default: "2"),
        new("Hls:MaxHeight", "streaming", "Largest transcode", "Transcodes are scaled down to this height. Originals that don't need transcoding are never changed.", SettingKind.Choice, Default: "1080", Options: Opt(("480", "480p"), ("720", "720p"), ("1080", "1080p"), ("1440", "1440p"), ("2160", "4K"))),
        new("Hls:IdleSeconds", "streaming", "Stop idle streams after", "A stream nobody is watching (a closed tab) is ended after this long.", SettingKind.Int, 30, 900, Default: "120", Unit: "seconds"),
        new("Playback:WatchedPercent", "streaming", "Mark as watched after", "Share of the runtime you must pass for a title to count as watched.", SettingKind.Int, 50, 100, Default: "92", Unit: "%"),
        new("Playback:CreditsSeconds", "streaming", "…or when this little is left", "Also counts as watched when this many seconds remain (credits).", SettingKind.Int, 0, 900, Default: "90", Unit: "seconds"),

        // live tv
        new("LiveTv:M3uUrl", "livetv", "Playlist URL (M3U)", "Your IPTV provider or Dispatcharr playlist. It may contain a password, so it is never shown again once saved.", SettingKind.SecretUrl, Placeholder: "http://host:9191/output/m3u"),
        new("LiveTv:EpgUrl", "livetv", "Guide URL (XMLTV)", "Optional. Leave empty to use the guide address named inside the playlist.", SettingKind.SecretUrl, Placeholder: "http://host:9191/output/epg"),
        new("LiveTv:StreamOrigin", "livetv", "Stream address override", "Optional. Replaces the address in every stream URL, for when the playlist advertises one this server can't reach.", SettingKind.Url, Placeholder: "http://internal-host:9191"),
        new("LiveTv:AutoRefresh", "livetv", "Refresh automatically", "Re-import the channel list and guide on a schedule.", SettingKind.Bool, Default: "true"),
        new("LiveTv:RefreshMinutes", "livetv", "Refresh every", "At least 5.", SettingKind.Int, 5, 10080, Default: "360", Unit: "minutes"),

        // network & security
        new("Auth:SessionDays", "network", "“Keep me signed in” lasts", "How long a remembered sign-in stays valid. Applies to new sign-ins.", SettingKind.Int, 1, 365, Default: "30", Unit: "days"),
        new("Auth:IdleHours", "network", "Other sign-ins end after", "Sign-ins that weren't remembered end after this long without use.", SettingKind.Int, 1, 168, Default: "12", Unit: "hours"),
        new("Auth:TrustProxy", "network", "Behind a reverse proxy", "Turn on if this server sits behind Traefik, nginx or Caddy, so failed sign-ins are tracked per visitor instead of per proxy. Leave it off if visitors connect directly.", SettingKind.Bool, Default: "false"),
    ];

    private static readonly (string Id, string Title, string Description)[] Groups =
    [
        ("general", "General", "The basics."),
        ("library", "Library", "Where your media lives, and how the server keeps up with it."),
        ("metadata", "Posters & details", "Artwork, descriptions, ratings and episode info come from TMDB. It's free: create an account and copy a key."),
        ("streaming", "Streaming & playback", "How the server delivers video to your devices."),
        ("livetv", "Live TV", "Watch channels from any M3U playlist with an XMLTV guide, such as an IPTV service or Dispatcharr."),
        ("network", "Network & security", "Sign-in sessions and how the server sees its visitors."),
    ];

    private static SettingDef? Find(string key) => Defs.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SettingGroup> Describe()
    {
        var saved = Saved();
        return Groups.Select(g => new SettingGroup(g.Id, g.Title, g.Description,
            Defs.Where(d => d.Group == g.Id).Select(d => State(d, saved)).ToList())).ToList();
    }

    private SettingState State(SettingDef d, Dictionary<string, string> saved)
    {
        var fromUi = saved.TryGetValue(d.Key, out var v);
        var effective = fromUi ? v : config[d.Key];
        var isSet = !string.IsNullOrWhiteSpace(effective);
        var source = fromUi ? "ui" : isSet ? "deployment" : "default";
        string? value = null, preview = null;
        switch (d.Kind)
        {
            case SettingKind.Secret: break;                                             // write-only
            case SettingKind.SecretUrl: preview = isSet ? Redact(effective!) : null; break; // may embed credentials
            case SettingKind.Bool: value = ParseBool(effective, d.Default != "false") ? "true" : "false"; isSet = true; break;
            case SettingKind.Int: case SettingKind.Choice: value = string.IsNullOrWhiteSpace(effective) ? d.Default : effective; isSet = true; break;
            default: value = effective; break;
        }
        return new SettingState(d.Key, d.Label, d.Help, d.Kind.ToString().ToLowerInvariant(), value, isSet, preview, source,
            d.Kind == SettingKind.Int ? d.Min : null, d.Kind == SettingKind.Int ? d.Max : null, d.Placeholder, d.Options, d.Unit, d.Default);
    }

    private static bool ParseBool(string? s, bool fallback) => bool.TryParse(s, out var b) ? b : fallback;

    /// <summary>scheme://host[:port]/… — enough to recognise the source without exposing a path or a password.</summary>
    public static string Redact(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority.Split('@')[^1]}/…" : "(saved)";

    private Dictionary<string, string> Saved()
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(r => r.Item1, r => r.Item2, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Validates every change first and applies them together. A null or empty value removes the override.</summary>
    /// <returns>The keys that changed.</returns>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string?> changes)
    {
        var normalized = new List<(SettingDef Def, string? Value)>();
        foreach (var (key, raw) in changes)
        {
            var d = Find(key) ?? throw new SettingsException(key, "Unknown setting");
            normalized.Add((d, Normalize(d, raw)));
        }
        var saved = Saved();
        var changed = new List<string>();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var (d, value) in normalized)
        {
            saved.TryGetValue(d.Key, out var old);
            if (value is null)
            {
                if (old is null) continue;
                Database.Execute(c, tx, "DELETE FROM settings WHERE key=@k", ("@k", d.Key));
            }
            else
            {
                if (old == value) continue;
                Database.Execute(c, tx, """
                    INSERT INTO settings(key,value,updated_at) VALUES(@k,@v,@t)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
                    """, ("@k", d.Key), ("@v", value), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
            }
            changed.Add(d.Key);
        }
        tx.Commit();
        if (changed.Count > 0) provider.Reload();
        return changed;
    }

    internal static string? Normalize(SettingDef d, string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Any(char.IsControl)) throw new SettingsException(d.Key, "Contains characters that aren't allowed");
        switch (d.Kind)
        {
            case SettingKind.Bool:
                if (!bool.TryParse(v, out var b)) throw new SettingsException(d.Key, "Must be on or off");
                return b ? "true" : "false";
            case SettingKind.Int:
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < d.Min || n > d.Max)
                    throw new SettingsException(d.Key, $"Must be a whole number from {d.Min} to {d.Max}");
                return n.ToString(CultureInfo.InvariantCulture);
            case SettingKind.Url:
            case SettingKind.SecretUrl:
                if (v.Length > 2000 || !M3uParser.IsAllowedUrl(v)) throw new SettingsException(d.Key, "Must be a full http:// or https:// address");
                return v;
            case SettingKind.Choice:
                if (d.Options is null || !d.Options.Any(o => o.Value == v)) throw new SettingsException(d.Key, "Pick one of the listed options");
                return v;
            case SettingKind.Secret:
                if (v.Length > 600 || v.Any(char.IsWhiteSpace)) throw new SettingsException(d.Key, "That doesn't look like a valid key");
                return v;
            default:
                if (v.Length > (d.Key == "General:ServerName" ? 40 : 500)) throw new SettingsException(d.Key, "Too long");
                return v;
        }
    }
}
