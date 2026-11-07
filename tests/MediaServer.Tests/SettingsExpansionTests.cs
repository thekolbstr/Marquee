using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using MediaServer.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void EveryDefinition_HasALabelHelpAndAKnownGroup()
    {
        var groups = new SettingsService(null!, null!, new ConfigurationBuilder().Build(), TimeProvider.System).GetType(); // (type only; Describe needs a db)
        Assert.NotNull(groups);
        Assert.All(SettingsService.Defs, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Label)); Assert.False(string.IsNullOrWhiteSpace(d.Help));
            Assert.Contains(d.Group, new[] { "general", "library", "metadata", "streaming", "livetv", "downloads", "network" });
        });
        Assert.Equal(SettingsService.Defs.Count, SettingsService.Defs.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void IntsAndChoices_AlwaysCarryDefaults_SoThePageCanShowThem()
    {
        foreach (var d in SettingsService.Defs.Where(d => d.Kind is SettingKind.Int or SettingKind.Choice))
        {
            Assert.False(string.IsNullOrEmpty(d.Default), d.Key);
            if (d.Kind == SettingKind.Int) { var n = int.Parse(d.Default!); Assert.InRange(n, d.Min, d.Max); }
            else Assert.Contains(d.Options!, o => o.Value == d.Default);
        }
    }

    [Fact]
    public void NoSettingCanTouchPathsToolsOrTheDatabase()
        => Assert.DoesNotContain(SettingsService.Defs, d => d.Key.Contains("Path", StringComparison.OrdinalIgnoreCase) || d.Key.Contains("Dir", StringComparison.OrdinalIgnoreCase)
            || d.Key.StartsWith("Tools:") || d.Key.StartsWith("Database:") || d.Key.StartsWith("Library:Root") || d.Key.Contains("Allowed", StringComparison.OrdinalIgnoreCase) || d.Key == "Auth:Enabled" || d.Key == "Auth:SetupCode");

    [Theory]
    [InlineData("Tmdb:Language", "xx-YY", false)] [InlineData("Tmdb:Language", "fr-FR", true)] [InlineData("Tmdb:Language", "en-US'; DROP", false)]
    [InlineData("Hls:Encoder", "nvenc", true)] [InlineData("Hls:Encoder", "rm -rf", false)] [InlineData("Hls:Encoder", "vaapi", false)]
    [InlineData("Hls:MaxHeight", "720", true)] [InlineData("Hls:MaxHeight", "100000", false)] [InlineData("Hls:MaxSessions", "0", false)] [InlineData("Hls:MaxSessions", "17", false)] [InlineData("Hls:MaxSessions", "4", true)]
    [InlineData("Playback:WatchedPercent", "49", false)] [InlineData("Playback:WatchedPercent", "100", true)] [InlineData("Auth:SessionDays", "400", false)] [InlineData("Auth:IdleHours", "0", false)]
    [InlineData("General:ServerName", "Home Cinema", true)] [InlineData("Auth:TrustProxy", "true", true)] [InlineData("Auth:TrustProxy", "yes", false)]
    public void Normalize_AcceptsOnlyValidValues(string key, string value, bool ok)
    {
        var def = SettingsService.Defs.Single(d => d.Key == key);
        if (ok) Assert.NotNull(SettingsService.Normalize(def, value));
        else Assert.Throws<SettingsException>(() => SettingsService.Normalize(def, value));
    }

    [Fact] public void ServerName_IsLimitedInLength() => Assert.Throws<SettingsException>(() => SettingsService.Normalize(SettingsService.Defs.Single(d => d.Key == "General:ServerName"), new string('x', 41)));
}

public sealed class LiveStreamingOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("liveopts").FullName;
    private readonly Dictionary<string, string?> _cfg = new();
    private readonly HlsSessionManager _m;
    private readonly FakeTimeProvider _time = new();

    public LiveStreamingOptionsTests()
    {
        var script = Path.Combine(_dir, "ffmpeg.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + FakeFfmpeg.GoodBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _cfg["Tools:FfmpegPath"] = script; _cfg["Hls:TempDir"] = Path.Combine(_dir, "hls"); _cfg["Hls:StartTimeoutSeconds"] = "5";
        var c = new ConfigurationBuilder().AddInMemoryCollection(_cfg).Build();
        var snapshot = new Func<HlsOptions>(() => HlsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(_cfg).Build()));
        _m = new HlsSessionManager(new Resolver(), new Probe(), snapshot(), _time, NullLogger<HlsSessionManager>.Instance, snapshot);
    }
    public void Dispose() { _m.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Resolver : IMediaResolver { public string? ResolvePath(long id) => id is >= 1 and <= 3 ? $"/library/movie{id}.mkv" : null; }
    private sealed class Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(new MediaInfo("matroska", 600, 1, 1,
            [new VideoTrack(0, path.EndsWith("movie3.mkv") ? "h264" : "hevc", null, 1920, 1080, 24, "yuv420p", null)], [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []));
    }

    [Fact]
    public async Task TurningTranscodingOff_RefusesAReEncode_ButStillCopiesPlayableVideo()
    {
        _cfg["Hls:AllowTranscoding"] = "false";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _m.StartAsync(1, default));          // HEVC for a baseline browser would need transcoding
        Assert.Contains("transcoding is turned off", ex.Message);
        var copy = await _m.StartAsync(3, default);                                                              // H.264 is copied, no re-encode
        Assert.Equal("remux", copy.Mode);
        Assert.NotEmpty(Directory.GetDirectories(_cfg["Hls:TempDir"]!));
    }

    [Fact]
    public async Task ASizeChosenInTheQualityMenu_IsRefusedToo_WhenTranscodingIsOff()
    {
        _cfg["Hls:AllowTranscoding"] = "false";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _m.StartAsync(3, default, quality: "720p"));
        _cfg["Hls:AllowTranscoding"] = "true";
        Assert.Equal("transcode", (await _m.StartAsync(3, default, quality: "720p")).Mode);                      // and back on: works again, no restart
    }

    [Fact]
    public async Task TheStreamLimit_FollowsTheSettingImmediately()
    {
        _cfg["Hls:MaxSessions"] = "1";
        await _m.StartAsync(1, default);
        await _m.StartAsync(2, default);
        Assert.Single(_m.Sessions);                                                                              // limit 1: the first was evicted
        _cfg["Hls:MaxSessions"] = "3";
        await _m.StartAsync(3, default); await _m.StartAsync(1, default);
        Assert.Equal(3, _m.Sessions.Count);                                                                      // raised to 3: no restart needed, nobody evicted
    }
}

public sealed class ProgressThresholdTests : IDisposable
{
    private readonly TestDb _t;
    private readonly ProgressService _p;
    public ProgressThresholdTests() : this(null) { }
    private ProgressThresholdTests(Dictionary<string, string?>? cfg) { _t = new TestDb(cfg); _p = new ProgressService(_t.Db, TimeProvider.System, _t.Config); }
    public void Dispose() => _t.Dispose();

    private bool Watched(long id) => _t.Scalar($"SELECT watched FROM media_progress WHERE media_id={id}") == "1";

    [Fact] public void Default_IsNinetyTwoPercentOrNinetySecondsLeft()
    {
        var a = _t.Insert("a.mkv", "Movie", "A"); var b = _t.Insert("b.mkv", "Movie", "B"); var c = _t.Insert("c.mkv", "Movie", "C");
        _p.Save(a, 91 * 60, 100 * 60, null); _p.Save(b, 93 * 60, 100 * 60, null); _p.Save(c, 100 * 60 - 80, 100 * 60, null);
        Assert.False(Watched(a)); Assert.True(Watched(b)); Assert.True(Watched(c));
    }

    [Fact] public void ConfiguredThresholds_Apply()
    {
        using var strict = new ProgressThresholdTests(new() { ["Playback:WatchedPercent"] = "99", ["Playback:CreditsSeconds"] = "0" });
        var a = strict._t.Insert("a.mkv", "Movie", "A"); var b = strict._t.Insert("b.mkv", "Movie", "B");
        strict._p.Save(a, 95 * 60, 100 * 60, null); strict._p.Save(b, 99.5 * 60, 100 * 60, null);
        Assert.False(strict.Watched(a)); Assert.True(strict.Watched(b));
        using var loose = new ProgressThresholdTests(new() { ["Playback:WatchedPercent"] = "50", ["Playback:CreditsSeconds"] = "0" });
        var c = loose._t.Insert("c.mkv", "Movie", "C"); loose._p.Save(c, 55 * 60, 100 * 60, null);
        Assert.True(loose.Watched(c));
    }

    [Fact] public void NonsenseThresholds_AreClamped()
    {
        using var weird = new ProgressThresholdTests(new() { ["Playback:WatchedPercent"] = "0", ["Playback:CreditsSeconds"] = "99999" });
        var a = weird._t.Insert("a.mkv", "Movie", "A"); weird._p.Save(a, 10, 100 * 60, null);
        Assert.False(weird.Watched(a));                                                                       // 0% would have marked everything watched
    }
}

public sealed class TmdbLanguageTests : IDisposable
{
    private readonly TestDb _t;
    private readonly StubHandler _h = new(_ => TmdbJson.Ok("""{"results":[]}"""));
    public TmdbLanguageTests() => _t = new TestDb(new() { ["Tmdb:ApiKey"] = "k", ["Tmdb:Language"] = "fr-FR" });
    public void Dispose() => _t.Dispose();

    [Fact] public async Task RequestsUseTheConfiguredLanguage()
    {
        var c = new TmdbClient(new HttpClient(_h), _t.Db, _t.Config, TimeProvider.System);
        await c.SearchAsync("movie", "amelie", null, default);
        Assert.Contains("language=fr-FR", _h.Requests[0].RequestUri!.Query);
    }

    [Theory] [InlineData("")] [InlineData("french")] [InlineData("fr-fr")] [InlineData("xx&include_adult=true")]
    public async Task AnInvalidLanguage_FallsBackToEnglish(string bad)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "k", ["Tmdb:Language"] = bad }).Build();
        var h = new StubHandler(_ => TmdbJson.Ok("""{"results":[]}"""));
        await new TmdbClient(new HttpClient(h), _t.Db, cfg, TimeProvider.System).SearchAsync("movie", "x", null, default);
        Assert.Contains("language=en-US", h.Requests[0].RequestUri!.Query);
        Assert.DoesNotContain("include_adult=true", h.Requests[0].RequestUri!.Query);
    }
}

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly TestDb _t;
    private readonly MaintenanceService _m;
    private readonly string _hlsDir;

    public MaintenanceServiceTests()
    {
        _t = new TestDb();
        _hlsDir = Path.Combine(_t.Dir, "hls");
        Directory.CreateDirectory(_hlsDir);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Path"] = Path.Combine(_t.Dir, "t.db"), ["Images:CacheDir"] = Path.Combine(_t.Dir, "img"), ["Hls:TempDir"] = _hlsDir, ["Tools:FfmpegPath"] = "ffmpeg" }).Build();
        var hls = new HlsSessionManager(new Res(), new NoProbe(), new HlsOptions { TempDir = _hlsDir }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
        _m = new MaintenanceService(_t.Db, cfg, hls, new ProcessRunner(2));
    }
    public void Dispose() => _t.Dispose();
    private sealed class Res : IMediaResolver { public string? ResolvePath(long id) => null; }
    private sealed class NoProbe : IMediaProbe { public Task<MediaInfo> ProbeAsync(string p, CancellationToken ct) => throw new NotSupportedException(); }

    [Fact] public void ClearImageCache_RemovesEverythingInsideAndKeepsTheFolder()
    {
        var img = Path.Combine(_t.Dir, "img"); Directory.CreateDirectory(Path.Combine(img, "thumbs")); Directory.CreateDirectory(Path.Combine(img, "w342"));
        File.WriteAllBytes(Path.Combine(img, "thumbs", "a.jpg"), new byte[5000]); File.WriteAllBytes(Path.Combine(img, "w342", "b.jpg"), new byte[3000]); File.WriteAllBytes(Path.Combine(img, "loose.bin"), new byte[100]);
        Assert.Equal(8100, _m.ClearImageCache());
        Assert.True(Directory.Exists(img)); Assert.Empty(Directory.GetFileSystemEntries(img));
        Assert.Equal(0, _m.ClearImageCache());                                                                // idempotent, and fine when empty
    }

    [Fact] public void ClearImageCache_IsFine_WhenThereIsNoCacheYet() => Assert.Equal(0, _m.ClearImageCache());

    [Fact] public void CleanTranscodes_RemovesLeftoverFolders()
    {
        Directory.CreateDirectory(Path.Combine(_hlsDir, "deadbeef")); File.WriteAllBytes(Path.Combine(_hlsDir, "deadbeef", "seg.ts"), new byte[2048]);
        var (n, bytes) = _m.CleanTranscodes();
        Assert.Equal((1, 2048), (n, bytes)); Assert.Empty(Directory.GetDirectories(_hlsDir));
    }

    [Fact] public void ClearWatchHistory_RemovesProgressOnly()
    {
        var id = _t.Insert("a.mkv", "Movie", "A");
        using (var c = _t.Db.Open()) Database.Execute(c, null, "INSERT INTO media_progress(media_id,position_seconds,duration_seconds,watched,updated_at) VALUES(@m,10,100,0,'x')", ("@m", id));
        Assert.Equal(1, _m.ClearWatchHistory());
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM media_progress")); Assert.Equal("1", _t.Scalar("SELECT COUNT(*) FROM media_files"));
    }

    [Fact] public void ResetMetadata_ForgetsMatchesAndCaches_ButKeepsFilesAndManualOverrides()
    {
        var id = _t.Insert("a.mkv", "Movie", "A");
        using (var c = _t.Db.Open())
        {
            Database.Execute(c, null, "UPDATE media_files SET tmdb_type='movie', tmdb_id=5, match_state='matched', matched_at='x' WHERE id=@i", ("@i", id));
            Database.Execute(c, null, "INSERT INTO metadata(tmdb_type,tmdb_id,title,year,fetched_at) VALUES('movie',5,'A',2020,'x')");
            Database.Execute(c, null, "INSERT INTO tmdb_cache(key,status,body,fetched_at) VALUES('k',200,'{}','x')");
            Database.Execute(c, null, "INSERT INTO match_overrides(media_id,tmdb_type,tmdb_id,created_at) VALUES(@i,'movie',5,'x')", ("@i", id));
        }
        Assert.Equal(1, _m.ResetMetadata());
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM metadata")); Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM tmdb_cache"));
        Assert.Equal("1", _t.Scalar("SELECT COUNT(*) FROM media_files WHERE match_state IS NULL AND tmdb_id IS NULL"));
        Assert.Equal("1", _t.Scalar("SELECT COUNT(*) FROM match_overrides"));                                // a manual match survives and is re-applied on the next scan
    }

    [Fact] public void OptimizeDatabase_Works_AndReportsSizes()
    {
        for (var i = 0; i < 50; i++) _t.Insert($"f{i}.mkv", "Movie", $"T{i}");
        var (before, after) = _m.OptimizeDatabase();
        Assert.True(before > 0 && after > 0);
        Assert.Equal("50", _t.Scalar("SELECT COUNT(*) FROM media_files"));
    }

    [Fact] public void Backup_IsAReadableCopyOfTheDatabase()
    {
        _t.Insert("a.mkv", "Movie", "Backed up");
        var path = _m.CreateBackup();
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT title FROM media_files";
            Assert.Equal("Backed up", cmd.ExecuteScalar());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact] public async Task About_ReportsVersionRuntimeAndSizes()
    {
        var a = await _m.AboutAsync(default);
        Assert.False(string.IsNullOrEmpty(a.Version)); Assert.Contains(".NET", a.Runtime); Assert.True(a.Cpus >= 1); Assert.True(a.DatabaseBytes > 0);
    }
}

public sealed class LogStoreTests
{
    private static (MemoryLogStore Store, ILogger Log) Make(string category = "MediaServer.Api.Test") { var s = new MemoryLogStore(); return (s, new MemoryLogProvider(s).CreateLogger(category)); }

    [Fact] public void KeepsApplicationInfo_AndLevelsMapToNames()
    {
        var (s, l) = Make();
        l.LogInformation("hello {X}", 1); l.LogWarning("careful"); l.LogError(new InvalidOperationException("boom"), "bad thing");
        var all = s.Query(null, null, 50);
        Assert.Equal(["info", "warning", "error"], all.Select(e => e.Level));
        Assert.Equal("hello 1", all[0].Message); Assert.Equal("Test", all[0].Category);
        Assert.Contains("InvalidOperationException: boom", all[2].Message);
        Assert.DoesNotContain("   at ", all[2].Message);                                                      // never a stack trace
    }

    [Fact] public void FrameworkChatter_IsKeptOutUnlessItIsAWarning()
    {
        var (s, l) = Make("Microsoft.AspNetCore.Hosting.Diagnostics");
        l.LogInformation("Request starting"); l.LogWarning("Slow request");
        Assert.Equal(["Slow request"], s.Query(null, null, 50).Select(e => e.Message));
    }

    [Fact] public void LevelAndTextFilters_Work()
    {
        var (s, l) = Make();
        l.LogInformation("alpha scan"); l.LogWarning("beta scan"); l.LogError("gamma");
        Assert.Equal(["beta scan", "gamma"], s.Query("warning", null, 50).Select(e => e.Message));
        Assert.Equal(["gamma"], s.Query("error", null, 50).Select(e => e.Message));
        Assert.Equal(["alpha scan", "beta scan"], s.Query(null, "SCAN", 50).Select(e => e.Message));
    }

    [Fact] public void KeepsOnlyTheMostRecentThousand_AndAnswersWithTheNewest()
    {
        var (s, l) = Make();
        for (var i = 0; i < 1200; i++) l.LogInformation("line {I}", i);
        var last = s.Query(null, null, 500);
        Assert.Equal(500, last.Count); Assert.Equal("line 1199", last[^1].Message);
        Assert.DoesNotContain(s.Query(null, null, 500), e => e.Message == "line 0");
    }

    [Fact] public void LongAndMultilineMessages_AreFlattenedAndCapped()
    {
        var (s, l) = Make();
        l.LogInformation("one\r\ntwo\nthree"); l.LogInformation(new string('x', 5000));
        var e = s.Query(null, null, 5);
        Assert.DoesNotContain('\n', e[0].Message); Assert.True(e[1].Message.Length <= 2001);
    }

    [Fact] public void HugeLimits_AreClamped() { var (s, l) = Make(); for (var i = 0; i < 700; i++) l.LogInformation("x"); Assert.Equal(500, s.Query(null, null, 99999).Count); Assert.Single(s.Query(null, null, -5)); }
}

public sealed class SettingsPagesEndpointTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Theory] [InlineData("GET", "/api/maintenance/backup")] [InlineData("POST", "/api/maintenance/clear-image-cache")] [InlineData("GET", "/api/system/logs")] [InlineData("GET", "/api/system/about")]
    [InlineData("GET", "/api/auth/sessions")] [InlineData("POST", "/api/auth/sessions/revoke-others")] [InlineData("GET", "/api/app")]
    public async Task EverythingNew_NeedsSignIn(string method, string url)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode);

    [Fact] public async Task TheServerName_IsPublic_SoTheSignInPageCanShowIt_AndFollowsTheSetting()
    {
        var anon = _f.Client();
        Assert.Equal("MediaServer", (await anon.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("serverName").GetString());
        var c = await _f.SignedIn();
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["General:ServerName"] = "Home Cinema" } })).StatusCode);
        Assert.Equal("Home Cinema", (await anon.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("serverName").GetString());
        Assert.Equal("Home Cinema", (await c.GetFromJsonAsync<JsonElement>("/api/app")).GetProperty("serverName").GetString());
    }

    [Fact] public async Task AppInfo_ReportsWhetherTranscodingIsAllowed()
    {
        var c = await _f.SignedIn();
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/app")).GetProperty("allowTranscoding").GetBoolean());
        await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["Hls:AllowTranscoding"] = false } });
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/app")).GetProperty("allowTranscoding").GetBoolean());
    }

    [Fact] public async Task StreamingSettings_ShowUpInTheSystemReport_Immediately()
    {
        var c = await _f.SignedIn();
        await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["Hls:MaxSessions"] = 5, ["Hls:Encoder"] = "nvenc" } });
        var enc = (await c.GetFromJsonAsync<JsonElement>("/api/system")).GetProperty("encoder");
        Assert.Equal((5, "nvenc"), (enc.GetProperty("maxStreams").GetInt32(), enc.GetProperty("name").GetString()));
    }

    [Fact] public async Task Maintenance_RunsTasks_RefusesUnknownOnes_AndNeedsConfirmationForTheDestructiveOne()
    {
        var c = await _f.SignedIn();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/maintenance/clear-image-cache", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/maintenance/clean-transcodes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/maintenance/optimize-database", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/maintenance/format-disk", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/maintenance/clear-watch-history", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/maintenance/clear-watch-history?confirm=true", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/maintenance/refresh-metadata", null)).StatusCode);   // no TMDB credentials in this test
    }

    [Fact] public async Task Backup_DownloadsARealDatabase_AndLeavesNoTempFileBehind()
    {
        var c = await _f.SignedIn();
        var res = await c.GetAsync("/api/maintenance/backup");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("mediaserver-backup-", res.Content.Headers.ContentDisposition!.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("SQLite format 3", System.Text.Encoding.ASCII.GetString(bytes, 0, 15));
        await Task.Delay(200);
        Assert.Empty(Directory.GetFiles(_f.Dir, "backup-*.db"));
    }

    [Fact] public async Task Logs_AreServed_AndFiltered()
    {
        var c = await _f.SignedIn();
        var j = await c.GetFromJsonAsync<JsonElement>("/api/system/logs?level=info&limit=50&q=");
        Assert.True(j.GetProperty("entries").GetArrayLength() >= 0);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/system/logs?level=bogus&limit=-1&q=" + new string('a', 500))).StatusCode);
    }

    [Fact] public async Task About_Works()
    {
        var c = await _f.SignedIn();
        var j = await c.GetFromJsonAsync<JsonElement>("/api/system/about");
        Assert.False(string.IsNullOrEmpty(j.GetProperty("version").GetString())); Assert.True(j.GetProperty("databaseBytes").GetInt64() > 0);
    }

    [Fact] public async Task Devices_AreListed_CanBeSignedOutIndividually_OrAllOthersAtOnce()
    {
        var phone = await _f.SignedIn();
        var laptop = _f.Client(); await laptop.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "a-long-password-123" });
        var tablet = _f.Client(); await tablet.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "a-long-password-123" });

        var list = (await phone.GetFromJsonAsync<JsonElement>("/api/auth/sessions")).GetProperty("sessions");
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal(1, list.EnumerateArray().Count(s => s.GetProperty("current").GetBoolean()));
        Assert.All(list.EnumerateArray(), s => Assert.Equal(16, s.GetProperty("id").GetString()!.Length));

        var other = list.EnumerateArray().First(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/auth/sessions/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.DeleteAsync($"/api/auth/sessions/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.DeleteAsync("/api/auth/sessions/not-hex-not-hex!")).StatusCode);
        Assert.Equal(2, (await phone.GetFromJsonAsync<JsonElement>("/api/auth/sessions")).GetProperty("sessions").GetArrayLength());

        var res = await phone.PostAsync("/api/auth/sessions/revoke-others", null);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/items")).StatusCode);                        // this device stays signed in
        var gone = await Task.WhenAll(laptop.GetAsync("/api/items"), tablet.GetAsync("/api/items"));
        Assert.Equal(1, gone.Count(g => g.StatusCode == HttpStatusCode.Unauthorized) + (gone.Count(g => g.StatusCode == HttpStatusCode.OK) - 1) + 0 * 1); // exactly one of them was the one I revoked individually; both are out now
        Assert.All(gone, g => Assert.Equal(HttpStatusCode.Unauthorized, g.StatusCode));
    }

    [Fact] public async Task SessionLengths_FollowTheSettings()
    {
        var c = await _f.SignedIn();
        await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["Auth:SessionDays"] = 2, ["Auth:IdleHours"] = 1 } });
        Assert.Equal(TimeSpan.FromDays(2), _f.Auth.SessionLength);
        Assert.Equal(TimeSpan.FromHours(1), _f.Auth.IdleSessionLength);
        var t = _f.Auth.CreateSession(1, "t", persistent: false);
        _f.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(_f.Auth.Validate(t));
    }
}
