using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using MediaServer.Api.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Tests;

public sealed class SettingsEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("settingsep").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;

    public SettingsEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", Path.Combine(_dir, "lib")).UseSetting("Library:AutoScan", "false")
            .UseSetting("Images:CacheDir", Path.Combine(_dir, "img")).UseSetting("Hls:TempDir", Path.Combine(_dir, "hls"))
            .UseSetting("LiveTv:AutoRefresh", "false").UseSetting("Library:ScanIntervalMinutes", "123"));
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Task<HttpResponseMessage> Put(object values) => _c.PutAsJsonAsync("/api/settings", new { values });
    private async Task<JsonElement> Item(string key)
    {
        var j = await _c.GetFromJsonAsync<JsonElement>("/api/settings");
        return j.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray()).First(i => i.GetProperty("key").GetString() == key);
    }
    private string? Effective(string key) => _f.Services.GetRequiredService<IConfiguration>()[key];

    [Fact]
    public async Task ListsGroupedSettings_WithTheDeploymentValueAndItsSource()
    {
        var j = await _c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(["general", "library", "metadata", "streaming", "livetv", "network"], j.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetString()));
        var i = await Item("Library:ScanIntervalMinutes");
        Assert.Equal(("123", "deployment"), (i.GetProperty("value").GetString(), i.GetProperty("source").GetString()));
        Assert.Equal("default", (await Item("LiveTv:RefreshMinutes")).GetProperty("source").GetString());
    }

    [Fact]
    public async Task SavedValuesOverrideTheDeployment_TakeEffectImmediately_AndCanBeRemoved()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put(new Dictionary<string, object?> { ["Library:ScanIntervalMinutes"] = 45, ["Library:AutoScan"] = false })).StatusCode);
        Assert.Equal(("45", "false"), (Effective("Library:ScanIntervalMinutes"), Effective("Library:AutoScan")));
        Assert.Equal("ui", (await Item("Library:ScanIntervalMinutes")).GetProperty("source").GetString());

        await Put(new Dictionary<string, object?> { ["Library:ScanIntervalMinutes"] = null });
        Assert.Equal("123", Effective("Library:ScanIntervalMinutes"));                        // back to what the deployment says
        Assert.Equal("deployment", (await Item("Library:ScanIntervalMinutes")).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Secrets_AreWriteOnly_NeverEchoedBack()
    {
        await Put(new Dictionary<string, object?> { ["Tmdb:ReadAccessToken"] = "super-secret-token-xyz" });
        var i = await Item("Tmdb:ReadAccessToken");
        Assert.True(i.GetProperty("isSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, i.GetProperty("value").ValueKind);
        Assert.DoesNotContain("super-secret-token-xyz", await _c.GetStringAsync("/api/settings"));
        Assert.Equal("super-secret-token-xyz", Effective("Tmdb:ReadAccessToken"));
        var again = await (await Put(new Dictionary<string, object?> { ["Library:AutoScan"] = true })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token-xyz", again);                              // the PUT response is redacted too
    }

    [Fact]
    public async Task PlaylistUrls_AreShownOnlyAsHostPreview_BecauseTheyCarryPasswords()
    {
        await Put(new Dictionary<string, object?> { ["LiveTv:M3uUrl"] = "http://user:hunter2@iptv.example:9191/get.php?username=me&password=hunter2" });
        var text = await _c.GetStringAsync("/api/settings");
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("username=me", text);
        var i = await Item("LiveTv:M3uUrl");
        Assert.Equal("http://iptv.example:9191/…", i.GetProperty("preview").GetString());
        Assert.Contains("hunter2", Effective("LiveTv:M3uUrl"));                               // but the server itself has the real value
    }

    [Theory]
    [InlineData("LiveTv:StreamOrigin", "ftp://host/x")]
    [InlineData("LiveTv:StreamOrigin", "javascript:alert(1)")]
    [InlineData("LiveTv:M3uUrl", "not a url")]
    [InlineData("Library:ScanIntervalMinutes", "-5")]
    [InlineData("Library:ScanIntervalMinutes", "999999")]
    [InlineData("Library:ScanIntervalMinutes", "abc")]
    [InlineData("LiveTv:RefreshMinutes", "1")]
    [InlineData("Library:AutoScan", "maybe")]
    [InlineData("Tmdb:ApiKey", "has spaces in it")]
    public async Task InvalidValues_AreRejected_NamingTheField(string key, string value)
    {
        var res = await Put(new Dictionary<string, object?> { [key] = value });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(key, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("Library:Root")]
    [InlineData("Database:Path")]
    [InlineData("Tools:FfmpegPath")]
    [InlineData("Hls:TempDir")]
    [InlineData("ConnectionStrings:x")]
    public async Task OnlyWhitelistedSettingsCanBeChanged_NeverPathsOrExecutables(string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new Dictionary<string, object?> { [key] = "/tmp/evil" })).StatusCode);
        Assert.NotEqual("/tmp/evil", Effective(key));
    }

    [Fact]
    public async Task ABadValueAppliesNothing()
    {
        var res = await Put(new Dictionary<string, object?> { ["Library:ScanIntervalMinutes"] = 10, ["LiveTv:StreamOrigin"] = "ftp://nope" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("123", Effective("Library:ScanIntervalMinutes"));
    }

    [Fact] public async Task EmptyOrMissingBodies_AreRejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new Dictionary<string, object?>())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PutAsJsonAsync("/api/settings", new { })).StatusCode);
    }

    [Fact] public async Task NestedValuesAreRejected()
        => Assert.Equal(HttpStatusCode.BadRequest, (await _c.PutAsync("/api/settings", new StringContent("""{"values":{"Library:AutoScan":{"a":1}}}""", System.Text.Encoding.UTF8, "application/json"))).StatusCode);

    [Fact]
    public async Task TmdbTest_ReportsMissingCredentials()
    {
        var j = await (await _c.PostAsync("/api/settings/test/tmdb", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(j.GetProperty("ok").GetBoolean());
        Assert.Contains("No TMDB credentials", j.GetProperty("message").GetString());
    }
}

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void SavedSettingsSurviveARestart()
    {
        var p1 = new DbSettingsProvider(_t.Db);
        var cfg = new ConfigurationManager(); cfg["LiveTv:RefreshMinutes"] = "360"; ((IConfigurationBuilder)cfg).Add(p1);
        new SettingsService(_t.Db, p1, cfg, TimeProvider.System).Apply(new Dictionary<string, string?> { ["LiveTv:RefreshMinutes"] = "30" });
        Assert.Equal("30", cfg["LiveTv:RefreshMinutes"]);

        var p2 = new DbSettingsProvider(_t.Db);                                              // "restart": a fresh provider reads the table
        var cfg2 = new ConfigurationManager(); cfg2["LiveTv:RefreshMinutes"] = "360"; ((IConfigurationBuilder)cfg2).Add(p2);
        Assert.Equal("30", cfg2["LiveTv:RefreshMinutes"]);
    }

    [Fact]
    public void ReloadNotifiesListeners()
    {
        var p = new DbSettingsProvider(_t.Db);
        var cfg = new ConfigurationManager(); ((IConfigurationBuilder)cfg).Add(p);
        var fired = 0; using var _ = ((IConfiguration)cfg).GetReloadToken().RegisterChangeCallback(_ => fired++, null);
        new SettingsService(_t.Db, p, cfg, TimeProvider.System).Apply(new Dictionary<string, string?> { ["Library:AutoScan"] = "false" });
        Assert.True(fired >= 1);
    }

    [Theory] [InlineData("http://a:b@host:1/x?y=z", "http://host:1/…")] [InlineData("https://iptv.example/get.php", "https://iptv.example/…")] [InlineData("garbage", "(saved)")]
    public void Redact_KeepsOnlySchemeAndHost(string url, string expected) => Assert.Equal(expected, SettingsService.Redact(url));
}

public sealed class CaptionTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly string _media;
    public CaptionTests() { _media = Path.Combine(_t.Dir, "lib"); Directory.CreateDirectory(_media); }
    public void Dispose() => _t.Dispose();

    private sealed class Resolver(Func<long, string?> f) : IMediaResolver { public string? ResolvePath(long id) => f(id); }

    private SubtitleService Service(Func<long, string?> resolve) => new(_t.Db, new Resolver(resolve), new ProcessRunner(2), _t.Config);

    private static string MakeVideo(string path, string srt)
    {
        var sub = path + ".src.srt"; File.WriteAllText(sub, srt);
        var r = Process.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=2", "-i", sub, "-map", "0", "-map", "1", "-c:v", "libx264", "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", path);
        Assert.True(r == 0, "ffmpeg could not build the test video");
        File.Delete(sub);
        return path;
    }

    private long AddFile(string rel, IReadOnlyList<SubtitleTrack> subs)
    {
        var id = _t.Insert(rel, "Movie", "Test");
        var info = new MediaInfo("mp4", 2, null, null, [], [], subs);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET info_json=@j WHERE id=@id", ("@j", JsonSerializer.Serialize(info)), ("@id", id));
        return id;
    }

    private const string Srt = "1\n00:00:00,500 --> 00:00:01,500\nHello <i>there</i>\n";

    [Fact]
    public async Task ConvertsAnEmbeddedTextTrack_ToWebVtt_AndCachesIt()
    {
        var path = MakeVideo(Path.Combine(_media, "movie.mp4"), Srt);
        var id = AddFile("movie.mp4", [new SubtitleTrack(1, "mov_text", "eng", null, false, false)]);
        var svc = Service(_ => path);
        var list = svc.List(id);
        Assert.Equal(("e1", "English", true, "embedded"), (list[0].Id, list[0].Label, list[0].Supported, list[0].Source));
        var vtt = await svc.GetVttAsync(id, "e1", default);
        Assert.NotNull(vtt);
        var text = File.ReadAllText(vtt!);
        Assert.StartsWith("WEBVTT", text); Assert.Contains("Hello", text);
        Assert.Equal(vtt, await svc.GetVttAsync(id, "e1", default));                          // second call: the same cached file
    }

    [Fact]
    public async Task SidecarFiles_AreListedWithTheirLanguage_AndConverted()
    {
        var path = Path.Combine(_media, "Show S01E01.mkv"); File.WriteAllText(path, "x");
        File.WriteAllText(Path.Combine(_media, "Show S01E01.es.srt"), Srt);
        File.WriteAllText(Path.Combine(_media, "Show S01E01.en.forced.srt"), Srt);
        File.WriteAllText(Path.Combine(_media, "Other.en.srt"), Srt);                      // belongs to a different video
        var id = AddFile("Show S01E01.mkv", []);
        var svc = Service(_ => path);
        var list = svc.List(id);
        Assert.Equal(2, list.Count);
        Assert.All(list, t => Assert.Equal("file", t.Source));
        Assert.Contains(list, t => t.Label == "Spanish");
        Assert.StartsWith("WEBVTT", File.ReadAllText((await svc.GetVttAsync(id, "f1", default))!));
    }

    [Fact]
    public async Task PictureBasedTracks_AreListedButNotConvertible()
    {
        var path = Path.Combine(_media, "m.mkv"); File.WriteAllText(path, "x");
        var id = AddFile("m.mkv", [new SubtitleTrack(3, "hdmv_pgs_subtitle", "fre", null, false, false)]);
        var svc = Service(_ => path);
        Assert.False(svc.List(id).Single().Supported);
        Assert.Null(await svc.GetVttAsync(id, "e3", default));
    }

    [Theory] [InlineData("e99")] [InlineData("f7")] [InlineData("x1")] [InlineData("e-1")] [InlineData("e1;rm")] [InlineData("../../etc/passwd")] [InlineData("")] [InlineData("e999999999999")]
    public async Task UnknownOrHostileTrackIds_AreRefused(string track)
    {
        var path = Path.Combine(_media, "n.mp4"); File.WriteAllText(path, "x");
        var id = AddFile("n.mp4", [new SubtitleTrack(2, "subrip", "eng", null, false, false)]);
        Assert.Null(await Service(_ => path).GetVttAsync(id, track, default));
    }

    [Fact] public async Task UnknownMedia_HasNoCaptions()
    {
        var svc = Service(_ => null);
        Assert.Empty(svc.List(12345));
        Assert.Null(await svc.GetVttAsync(12345, "e0", default));
    }

    [Theory]
    [InlineData("SDH", "eng", "English – SDH")]
    [InlineData("English", "eng", "English")]
    [InlineData(null, "spa", "Spanish")]
    [InlineData(null, null, "Track 3")]
    [InlineData(null, "xx", "xx")]
    public void Labels_UseTheLanguageName_AndKeepAnInformativeTitle(string? title, string? lang, string expected)
        => Assert.Equal(expected, SubtitleService.Label(title, lang, null, 2));

    private static class Process
    {
        public static int Run(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(args[0]) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return p.ExitCode;
        }
    }
}

public sealed class RandomPickTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly LibraryQueries _q;
    public RandomPickTests() { _q = new LibraryQueries(_t.Db); }
    public void Dispose() => _t.Dispose();

    private long Add(string title, string kind, string? genres = null, bool watched = false, int? tmdb = null)
    {
        var id = _t.Insert($"{title}-{Guid.NewGuid():N}.mkv", kind, title, 2020);
        using var c = _t.Db.Open();
        if (genres is not null)
        {
            Database.Execute(c, null, "UPDATE media_files SET tmdb_type='movie', tmdb_id=@i, match_state='matched' WHERE id=@id", ("@i", tmdb ?? (int)id), ("@id", id));
            Database.Execute(c, null, "INSERT INTO metadata(tmdb_type,tmdb_id,title,year,genres,fetched_at) VALUES('movie',@i,@t,2020,@g,'x')", ("@i", tmdb ?? (int)id), ("@t", title), ("@g", genres));
        }
        if (watched) Database.Execute(c, null, "INSERT INTO media_progress(media_id,position_seconds,duration_seconds,watched,updated_at) VALUES(@m,100,100,1,'2026-01-01T00:00:00Z')", ("@m", id));
        return id;
    }

    [Fact] public void EmptyLibrary_HasNothingToPick() => Assert.Null(_q.Random("movie", null));

    [Fact]
    public void PrefersUnwatched_ButFallsBackToWatched()
    {
        var seen = Add("Seen", "Movie", watched: true);
        var fresh = Add("Fresh", "Movie");
        for (var i = 0; i < 25; i++) Assert.Equal(fresh, _q.Random("movie", null)!.MediaId);
        using (var c = _t.Db.Open()) Database.Execute(c, null, "INSERT INTO media_progress(media_id,position_seconds,duration_seconds,watched,updated_at) VALUES(@m,100,100,1,'x')", ("@m", fresh));
        Assert.Contains(_q.Random("movie", null)!.MediaId, new[] { seen, fresh });
    }

    [Fact]
    public void RespectsKindAndGenre()
    {
        Add("A Comedy", "Movie", "Comedy"); var drama = Add("A Drama", "Movie", "Drama"); Add("Ep One", "Episode");
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(drama, _q.Random("movie", "drama")!.MediaId);
            Assert.Equal("episode", _q.Random("episode", null)!.Kind);
        }
        Assert.Null(_q.Random("movie", "Horror"));
    }

    [Fact]
    public void EveryTitleCanBePicked()
    {
        for (var i = 0; i < 4; i++) Add($"M{i}", "Movie");
        var seen = Enumerable.Range(0, 200).Select(_ => _q.Random("movie", null)!.MediaId).Distinct().Count();
        Assert.Equal(4, seen);
    }
}
