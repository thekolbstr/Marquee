using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class SecurityHeaderTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Theory] [InlineData("/")] [InlineData("/app.js")] [InlineData("/api/auth/state")] [InlineData("/health")] [InlineData("/manifest.webmanifest")]
    public async Task EveryResponse_CarriesTheSecurityHeaders(string url)
    {
        var res = await _f.Client().GetAsync(url);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("camera=()", res.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact] public async Task TheContentSecurityPolicy_IsStrict_ButLetsThePlayerAndCastWork()
    {
        var csp = (await _f.Client().GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp); Assert.Contains("frame-ancestors 'none'", csp); Assert.Contains("object-src 'none'", csp); Assert.Contains("base-uri 'none'", csp);
        var scripts = csp.Split(';').Select(x => x.Trim()).Single(x => x.StartsWith("script-src"));
        Assert.DoesNotContain("'unsafe-inline'", scripts); Assert.DoesNotContain("'unsafe-eval'", scripts);        // no inline or eval'd script, ever
        Assert.Contains("https://www.gstatic.com", scripts);                                                         // Google Cast's library
        Assert.Contains("media-src 'self' blob:", csp); Assert.Contains("worker-src 'self' blob:", csp);              // hls.js
    }

    [Fact] public async Task ThePage_UsesNoInlineScripts_SoTheStrictPolicyIsActuallyWorkable()
    {
        var html = await _f.Client().GetStringAsync("/");
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)[^>]*>\s*\S", html);                                             // every <script> has a src
        Assert.DoesNotMatch(@"\son(click|load|error)\s*=", html);                                                    // and no inline event handlers
    }
}

public sealed class ErrorHandlingTests : IDisposable
{
    private sealed class ExplodingProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => throw new InvalidOperationException("secret internal detail at /srv/private/file.mkv");
    }

    private readonly AuthFixture _f = new();
    private readonly WebApplicationFactory<Program> _boom;
    public ErrorHandlingTests()
    {
        _boom = _f.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => { s.RemoveAll<IMediaProbe>(); s.AddSingleton<IMediaProbe, ExplodingProbe>(); }));
    }
    public void Dispose() { _boom.Dispose(); _f.Dispose(); }

    private async Task<HttpClient> SignedInBoom()
    {
        var c = _boom.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/setup", new { username = "alex", password = "a-long-password-123" })).StatusCode);
        using var db = _boom.Services.GetRequiredService<Database>().Open();
        Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned) VALUES('x.mkv',1,1,'x','x')");
        Directory.CreateDirectory(Path.Combine(_f.Dir, "lib")); File.WriteAllBytes(Path.Combine(_f.Dir, "lib", "x.mkv"), [0]);
        return c;
    }

    [Fact] public async Task AnUnexpectedFailure_IsAShortJsonAnswerWithAReference_AndNeverLeaksDetails()
    {
        var c = await SignedInBoom();
        var res = await c.GetAsync("/api/probe/1");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret internal detail", text); Assert.DoesNotContain("/srv/private", text); Assert.DoesNotContain("InvalidOperationException", text);
        var j = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Something went wrong on the server.", j.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(j.GetProperty("reference").GetString()));
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
    }

    [Fact] public async Task TheFailure_IsLoggedWithItsReference_ForWhoeverRunsTheServer()
    {
        var c = await SignedInBoom();
        var reference = (await (await c.GetAsync("/api/probe/1")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reference").GetString()!;
        var logs = _boom.Services.GetRequiredService<MediaServer.Api.Maintenance.MemoryLogStore>().Query("error", reference, 10);
        Assert.Single(logs);
        Assert.Contains("InvalidOperationException", logs[0].Message);                                              // the operator sees the cause; the client never does
    }

    [Theory] [InlineData("{\"position\": ")] [InlineData("not json")] [InlineData("")]
    public async Task MalformedInput_IsABadRequest_NotAServerError(string body)
    {
        var c = await _f.SignedIn("alex");
        var res = await c.PostAsync("/api/media/1/progress", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("That request couldn't be understood.", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}

public sealed class RoutingFallbackTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task AnUnknownApiAddress_IsAJson404()
    {
        var c = await _f.SignedIn("alex");
        var res = await c.GetAsync("/api/definitely/not/here");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("Not found", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact] public async Task AMissingStaticFile_IsAnOrdinary404() => Assert.Equal(HttpStatusCode.NotFound, (await _f.Client().GetAsync("/no-such-file.js")).StatusCode);

    [Fact] public async Task AnEndpointsOwn404_KeepsItsOwnBody()
    {
        var c = await _f.SignedIn("alex");
        var res = await c.GetAsync("/api/items/movie/99999");                                                          // handled by the endpoint itself
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact] public async Task Readiness_ReportsTheDatabaseIsReachable_WithoutSigningIn()
    {
        var res = await _f.Client().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ready", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}

public sealed class StreamInfoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("streaminfo").FullName;
    private readonly HlsSessionManager _m;
    private readonly Dictionary<string, string?> _cfg = new();

    public StreamInfoTests()
    {
        var script = Path.Combine(_dir, "ffmpeg.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + FakeFfmpeg.GoodBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _cfg["Tools:FfmpegPath"] = script; _cfg["Hls:TempDir"] = Path.Combine(_dir, "hls"); _cfg["Hls:StartTimeoutSeconds"] = "5";
        Func<HlsOptions> snap = () => HlsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(_cfg).Build());
        _m = new HlsSessionManager(new Resolver(), new Probe(), snap(), new FakeTimeProvider(), NullLogger<HlsSessionManager>.Instance, snap);
    }
    public void Dispose() { _m.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Resolver : IMediaResolver { public string? ResolvePath(long id) => id is 1 or 2 ? $"/library/movie{id}.mkv" : null; }
    private sealed class Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(new MediaInfo("matroska", 600, 1, 1,
            [new VideoTrack(0, path.EndsWith("movie2.mkv") ? "hevc" : "h264", null, 1920, 1080, 24, "yuv420p", null)], [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []));
    }

    [Fact] public async Task ACopiedStream_ReportsThatNothingWasEncoded()
    {
        var s = await _m.StartAsync(1, default);
        var i = _m.Info(s.Id)!;
        Assert.Equal(("remux", "none (video copied)", false, false), (i.Mode, i.Encoder, i.Vod, i.Live));
        Assert.Equal(0, i.Relaunches); Assert.True(i.AgeSeconds >= 0); Assert.False(i.BurnedInSubtitles);
    }

    [Fact] public async Task ATranscode_NamesTheEncoderInUse_AndKnowsItsSegmentCount()
    {
        var s = await _m.StartAsync(2, default);                                                                       // HEVC for a baseline browser: re-encoded
        var i = _m.Info(s.Id)!;
        Assert.Equal(("transcode", "libx264", true), (i.Mode, i.Encoder, i.Vod));
        Assert.True(i.SegmentsTotal > 0); Assert.InRange(i.SegmentsReady, 0, i.SegmentsTotal);
        _cfg["Hls:Encoder"] = "nvenc";
        Assert.Equal("nvenc", _m.Info(s.Id)!.Encoder);                                                                  // follows the setting
    }

    [Fact] public async Task UnknownOrMalformedIds_HaveNoInfo()
    {
        await _m.StartAsync(1, default);
        Assert.Null(_m.Info(new string('a', 32))); Assert.Null(_m.Info("../../etc/passwd")); Assert.Null(_m.Info(""));
    }

    [Fact] public async Task TheInfo_NeverContainsPathsOrUrls()
    {
        var s = await _m.StartAsync(1, default);
        var json = JsonSerializer.Serialize(_m.Info(s.Id));
        Assert.DoesNotContain("/library", json); Assert.DoesNotContain(_dir, json); Assert.DoesNotContain("http", json);
    }
}

public sealed class StreamInfoEndpointTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();
    [Fact] public async Task UnknownSession_Is404_AndNeedsNoCookie_LikeTheRestOfTheStream()
        => Assert.Equal(HttpStatusCode.NotFound, (await _f.Client().GetAsync($"/api/hls/{new string('b', 32)}/info")).StatusCode);
}

public sealed class ProgressOnItemsTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FixedUser _who = new();
    private readonly LibraryQueries _q;
    private readonly ProgressService _p;
    public ProgressOnItemsTests() { _q = new LibraryQueries(_t.Db, _who); _p = new ProgressService(_t.Db, TimeProvider.System, _t.Config, _who); }
    public void Dispose() => _t.Dispose();

    private long Add(string title, string kind, double duration, int? season = null, int? ep = null, string? file = null)
    {
        var id = _t.Insert(file ?? $"{title}-{Guid.NewGuid():N}.mkv", kind, title, 2020);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET duration_seconds=@d, season=@s, episode=@e WHERE id=@i", ("@d", duration), ("@s", season), ("@e", ep), ("@i", id));
        return id;
    }
    private ItemSummary Item(string type, string title) => _q.List(null, type, "title", 50, 0).Items.Single(i => i.Title == title);

    [Fact] public void AMovieYouAreWatching_ReportsProgressAndTimeLeft()
    {
        var m = Add("Heat", "Movie", 6000); _p.Save(m, 1500, 6000, null);
        var i = Item("movie", "Heat");
        Assert.Equal(0.25, i.Progress!.Value, 3); Assert.Equal(4500, i.LeftSeconds); Assert.False(i.Watched);
    }

    [Fact] public void AnUntouchedMovie_HasNoProgress() { Add("Heat", "Movie", 6000); var i = Item("movie", "Heat"); Assert.Null(i.Progress); Assert.Null(i.LeftSeconds); Assert.False(i.Watched); }
    [Fact] public void AFinishedMovie_IsMarkedWatched_NotInProgress()
    {
        var m = Add("Heat", "Movie", 6000); _p.Save(m, 5900, 6000, null);
        var i = Item("movie", "Heat"); Assert.True(i.Watched); Assert.Null(i.Progress); Assert.Null(i.LeftSeconds);
    }
    [Fact] public void AJustStartedMovie_IsNotShownAsInProgress() { var m = Add("Heat", "Movie", 6000); _p.Save(m, 10, 6000, null); Assert.Null(Item("movie", "Heat").Progress); }

    [Fact] public void ProgressIsPerPerson()
    {
        var m = Add("Heat", "Movie", 6000);
        _who.UserId = 1; _p.Save(m, 1500, 6000, null);
        _who.UserId = 2; Assert.Null(Item("movie", "Heat").Progress);
        _who.UserId = 1; Assert.NotNull(Item("movie", "Heat").Progress);
    }

    [Fact] public void AShow_ReportsTheEpisodeYouAreInTheMiddleOf_AndHowManyAreUnseen()
    {
        var e1 = Add("Show", "Episode", 1500, 1, 1); var e2 = Add("Show", "Episode", 1500, 1, 2); Add("Show", "Episode", 1500, 1, 3);
        _p.Save(e1, 1490, 1500, null); _p.Save(e2, 500, 1500, null);
        var i = Item("series", "Show");
        Assert.Equal((1000.0, 2, false), (i.LeftSeconds, i.Unwatched, i.Watched)); Assert.Equal(1.0 / 3, i.Progress!.Value, 3);
    }

    [Fact] public void AShowWhereEverythingIsSeen_IsMarkedWatched()
    {
        var e1 = Add("Show", "Episode", 1500, 1, 1); _p.Save(e1, 1490, 1500, null);
        var i = Item("series", "Show"); Assert.True(i.Watched); Assert.Equal(0, i.Unwatched); Assert.Null(i.LeftSeconds);
    }

    [Fact] public void TheMostRecentlyPlayedVersion_Wins_ForAMovieWithSeveralFiles()
    {
        var a = Add("Heat", "Movie", 6000, file: "Heat (1995) 1080p.mkv"); var b = Add("Heat", "Movie", 6000, file: "Heat (1995) 720p.mkv");
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET tmdb_type='movie', tmdb_id=1, match_state='matched' WHERE id IN (@a,@b)", ("@a", a), ("@b", b));
        using (var c = _t.Db.Open()) Database.Execute(c, null, "INSERT INTO metadata(tmdb_type,tmdb_id,title,year,fetched_at) VALUES('movie',1,'Heat',1995,'x')");
        _p.Save(a, 3000, 6000, null); System.Threading.Thread.Sleep(5); _p.Save(b, 600, 6000, null);
        Assert.Equal(5400, Item("movie", "Heat").LeftSeconds);
    }
}

public sealed class FileInfoDetailTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact] public void ThePlayInfo_CarriesTheTechnicalDetailsTheStatsPanelShows()
    {
        var id = _t.Insert("m.mkv", "Movie", "M");
        var info = new MediaInfo("matroska,webm", 600, 8_200_000, 615_000_000, [new VideoTrack(0, "h264", "High", 1920, 1080, 23.976, "yuv420p", null)], [new AudioTrack(1, "eac3", 6, "5.1", 48000, "eng", null, true, null)], []);
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET info_json=@j, duration_seconds=600 WHERE id=@i", ("@j", JsonSerializer.Serialize(info)), ("@i", id));
        var p = new LibraryQueries(_t.Db).Play(id, ClientCaps.Baseline)!;
        Assert.Equal(("High", 23.976, 8_200_000L, 615_000_000L, "yuv420p"), (p.VideoProfile, p.FrameRate, p.BitRate, p.SizeBytes, p.PixelFormat));
        Assert.Equal("matroska,webm", p.Container);
    }

    [Fact] public void AFileWithoutProbeData_HasNoDetails_AndDoesNotBreak()
    {
        var id = _t.Insert("m.mkv", "Movie", "M");
        var p = new LibraryQueries(_t.Db).Play(id, ClientCaps.Baseline)!;
        Assert.Null(p.VideoProfile); Assert.Null(p.BitRate);
    }
}

public sealed class AppInfoTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task TheAppInfo_ReportsCastingAddresses_Once_ConfiguredOrNot()
    {
        var c = await _f.SignedIn("alex");
        var a = await c.GetFromJsonAsync<JsonElement>("/api/app");
        Assert.Equal(JsonValueKind.Null, a.GetProperty("castBaseUrl").ValueKind); Assert.Equal(JsonValueKind.Null, a.GetProperty("httpsPort").ValueKind);
        await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["Cast:BaseUrl"] = "http://192.0.2.10:8080" } });
        Assert.Equal("http://192.0.2.10:8080", (await c.GetFromJsonAsync<JsonElement>("/api/app")).GetProperty("castBaseUrl").GetString());
    }

    [Theory] [InlineData("ftp://x")] [InlineData("javascript:alert(1)")] [InlineData("not a url")]
    public async Task TheCastAddress_MustBeAnHttpUrl(string value)
    {
        var c = await _f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["Cast:BaseUrl"] = value } })).StatusCode);
    }
}
