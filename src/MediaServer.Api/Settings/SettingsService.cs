using System.Globalization;
using Microsoft.Extensions.Primitives;
using MediaServer.Api.Data;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Security;

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
public sealed class DbSettingsProvider(Database db, SecretBox? box = null) : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override void Load()
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1)));
        // A value that can't be decrypted (the key was lost or replaced) is treated as not set, so the page asks for it again instead of failing.
        Data = rows.Where(r => !r.Item1.StartsWith("auth.", StringComparison.Ordinal))
            .Select(r => (r.Item1, Value: box is null ? r.Item2 : box.TryUnprotect(r.Item2, r.Item1))).Where(r => r.Value is not null)
            .ToDictionary(r => r.Item1, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Re-reads the table and tells everything holding IConfiguration that values changed.</summary>
    public void Reload() { Load(); OnReload(); }
}

public sealed class SettingsService(Database db, DbSettingsProvider provider, IConfiguration config, TimeProvider time, SecretBox? box = null)
{
    private static SettingOption[] Opt(params (string V, string L)[] o) => o.Select(x => new SettingOption(x.V, x.L)).ToArray();

    private static readonly SettingOption[] Languages = Opt(("en-US", "English"), ("es-ES", "Español"), ("fr-FR", "Français"), ("de-DE", "Deutsch"), ("it-IT", "Italiano"), ("pt-BR", "Português (Brasil)"),
        ("nl-NL", "Nederlands"), ("sv-SE", "Svenska"), ("pl-PL", "Polski"), ("ru-RU", "Русский"), ("tr-TR", "Türkçe"), ("ja-JP", "日本語"), ("ko-KR", "한국어"), ("zh-CN", "中文"));

    public static readonly IReadOnlyList<SettingDef> Defs =
    [
        // general
        new("General:ServerName", "general", "Server name", "Shown in the header, the browser tab and on the sign-in page.", SettingKind.Text, Placeholder: "MediaServer"),

        new("General:LoginMessage", "general", "Sign-in message", "Optional. A short note shown under the sign-in form, such as who to ask for an account.", SettingKind.Text, Placeholder: "Ask Alex if you need an account"),

        // library
        new("Library:AutoScan", "library", "Scan automatically", "Look for new and removed files on a schedule, so nobody has to press the scan button.", SettingKind.Bool, Default: "true"),
        new("Library:ScanIntervalMinutes", "library", "Scan every", "0 means scan once when the server starts.", SettingKind.Int, 0, 10080, Default: "360", Unit: "minutes"),
        new("Library:ProbeParallelism", "library", "Files analysed at once", "More is faster on a big first scan but works the disk harder. Lower it for a NAS or a spinning disk.", SettingKind.Int, 1, 16, Default: "4"),

        new("Library:MinFileSizeMb", "library", "Ignore files smaller than", "Skips samples and trailers. 0 includes everything. Files that no longer qualify leave the library at the next scan; nothing on disk is touched.", SettingKind.Int, 0, 10240, Default: "0", Unit: "MB"),
        new("Library:IgnoreWords", "library", "Ignore folders and files containing", "Words separated by commas, for example sample, extras, featurettes. Anything whose folder or file name contains one is left out of the library.", SettingKind.Text, Placeholder: "sample, extras, featurettes"),

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
        new("Hls:Preset", "streaming", "Encoding speed", "Software transcodes only. Slower gives a smaller or nicer picture at the same quality but uses a lot more CPU. “Very fast” suits most servers.", SettingKind.Choice, Default: "veryfast",
            Options: Opt(("ultrafast", "Ultra fast"), ("superfast", "Super fast"), ("veryfast", "Very fast"), ("faster", "Faster"), ("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow"))),
        new("Hls:Crf", "streaming", "Transcode quality", "Software transcodes only. Lower numbers look better and make bigger streams. 18 is near-lossless, 21 is a good default, 26 is noticeably softer.", SettingKind.Int, 14, 30, Default: "21"),
        new("Hls:SegmentSeconds", "streaming", "Movie and show segment length", "How long each piece of a transcoded video is. Shorter starts and seeks faster, longer is gentler on the CPU. Applies to streams started afterwards.", SettingKind.Int, 2, 20, Default: "6", Unit: "seconds"),
        new("Hls:LiveSegmentSeconds", "streaming", "Live TV segment length", "Shorter means a faster channel change but more load. Applies to streams started afterwards.", SettingKind.Int, 1, 10, Default: "2", Unit: "seconds"),
        new("Hls:IdleSeconds", "streaming", "Stop idle streams after", "A stream nobody is watching (a closed tab) is ended after this long.", SettingKind.Int, 30, 900, Default: "120", Unit: "seconds"),
        new("Playback:Trickplay", "streaming", "Seek-bar previews", "Show a small picture when you hover or drag on the progress bar. The server reads each title once in the background to make them, which takes a while on large files.", SettingKind.Bool, Default: "true"),
        new("Playback:WatchedPercent", "streaming", "Mark as watched after", "Share of the runtime you must pass for a title to count as watched.", SettingKind.Int, 50, 100, Default: "92", Unit: "%"),
        new("Playback:CreditsSeconds", "streaming", "…or when this little is left", "Also counts as watched when this many seconds remain (credits).", SettingKind.Int, 0, 900, Default: "90", Unit: "seconds"),

        // live tv
        new("LiveTv:M3uUrl", "livetv", "Playlist URL (M3U)", "Your IPTV provider or Dispatcharr playlist. It may contain a password, so it is never shown again once saved.", SettingKind.SecretUrl, Placeholder: "http://host:9191/output/m3u"),
        new("LiveTv:EpgUrl", "livetv", "Guide URL (XMLTV)", "Optional. Leave empty to use the guide address named inside the playlist.", SettingKind.SecretUrl, Placeholder: "http://host:9191/output/epg"),
        new("LiveTv:StreamOrigin", "livetv", "Stream address override", "Optional. Replaces the address in every stream URL, for when the playlist advertises one this server can't reach.", SettingKind.Url, Placeholder: "http://internal-host:9191"),
        new("LiveTv:AutoRefresh", "livetv", "Refresh automatically", "Re-import the channel list and guide on a schedule.", SettingKind.Bool, Default: "true"),
        new("LiveTv:RefreshMinutes", "livetv", "Refresh every", "At least 5.", SettingKind.Int, 5, 10080, Default: "360", Unit: "minutes"),

        new("Recordings:MaxConcurrent", "livetv", "Recordings at once", "How many channels can be recorded at the same time.", SettingKind.Int, 1, 8, Default: "2"),
        new("Recordings:BeforeSeconds", "livetv", "Start recording early by", "A little before a scheduled programme, in case it starts early.", SettingKind.Int, 0, 1800, Default: "60", Unit: "seconds"),
        new("Recordings:AfterSeconds", "livetv", "Keep recording afterwards for", "Live TV often runs over.", SettingKind.Int, 0, 3600, Default: "120", Unit: "seconds"),
        new("Recordings:MinFreeGb", "livetv", "Stop recording when free space is below", "Protects the disk: a recording that would leave less than this free is not started.", SettingKind.Int, 0, 10000, Default: "2", Unit: "GB"),
        new("Recordings:KeepDays", "livetv", "Delete recordings after", "0 keeps them until you delete them yourself.", SettingKind.Int, 0, 3650, Default: "0", Unit: "days"),

        // downloads (Radarr / Sonarr)
        new("Radarr:Url", "downloads", "Radarr address", "Where Radarr (movies) runs, as this server sees it.", SettingKind.Url, Placeholder: "http://radarr:7878"),
        new("Radarr:ApiKey", "downloads", "Radarr API key", "Radarr → Settings → General → API Key. It's never shown again once saved.", SettingKind.Secret),
        new("Sonarr:Url", "downloads", "Sonarr address", "Where Sonarr (TV, version 4) runs, as this server sees it.", SettingKind.Url, Placeholder: "http://sonarr:8989"),
        new("Sonarr:ApiKey", "downloads", "Sonarr API key", "Sonarr → Settings → General → API Key. It's never shown again once saved.", SettingKind.Secret),

        // network & security
        new("Cast:BaseUrl", "network", "Address for casting", "Optional. Cast devices (Chromecast) fetch the video themselves, so they need an address for this server that works from their side: plain http, not https. Leave empty to use the one you're browsing with.", SettingKind.Url, Placeholder: "http://192.0.2.10:8080"),
        new("Auth:SessionDays", "network", "“Keep me signed in” lasts", "How long a remembered sign-in stays valid. Applies to new sign-ins.", SettingKind.Int, 1, 365, Default: "30", Unit: "days"),
        new("Auth:MinPasswordLength", "network", "Shortest password allowed", "Applies when someone sets or changes a password. Existing passwords keep working.", SettingKind.Int, 8, 64, Default: "8", Unit: "characters"),
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
        ("downloads", "Downloads (Radarr & Sonarr)", "Connect your download managers to see what's coming, add titles from here, and have new files appear in the library as soon as they're imported."),
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
        return Database.Query(c, "SELECT key, value FROM settings WHERE key NOT LIKE 'auth.%'", r => (r.GetString(0), r.GetString(1)))
            .Select(r => (r.Item1, Value: box is null ? r.Item2 : box.TryUnprotect(r.Item2, r.Item1))).Where(r => r.Value is not null)
            .ToDictionary(r => r.Item1, r => r.Value!, StringComparer.OrdinalIgnoreCase);
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
                var stored = box is not null && d.Kind is SettingKind.Secret or SettingKind.SecretUrl ? box.Protect(value, d.Key) : value;
                Database.Execute(c, tx, """
                    INSERT INTO settings(key,value,updated_at) VALUES(@k,@v,@t)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
                    """, ("@k", d.Key), ("@v", stored), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
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
                if (v.Length > (d.Key == "General:ServerName" ? 40 : d.Key == "General:LoginMessage" ? 300 : 500)) throw new SettingsException(d.Key, "Too long");
                return v;
        }
    }
}
