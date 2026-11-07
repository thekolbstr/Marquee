using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaServer.Api.Arr;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class WatchActivityTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FixedUser _who = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero));
    private readonly ProgressService _p;
    private readonly ActivityService _a;
    public WatchActivityTests() { _p = new ProgressService(_t.Db, _clock, _t.Config, _who); _a = new ActivityService(_t.Db, _clock); _clock.SetLocalTimeZone(TimeZoneInfo.Utc); }
    public void Dispose() => _t.Dispose();

    private long Movie(string title) => _t.Insert($"{title}.mkv", "Movie", title, 2020);
    private long Ep(string show, int s, int e)
    {
        var id = _t.Insert($"{show} S{s}E{e}.mkv", "Episode", show, 2020);
        using var c = _t.Db.Open(); Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, episode_title='Pilot' WHERE id=@i", ("@s", s), ("@e", e), ("@i", id));
        return id;
    }
    /// <summary>Plays from `from` for `beats` heartbeats, ten seconds apart.</summary>
    private void Watch(long media, double from, int beats) { for (var i = 0; i < beats; i++) { _p.Save(media, from + i * 10, 6000, null); _clock.Advance(TimeSpan.FromSeconds(10)); } }

    [Fact] public void HeartbeatsAddUpToWatchTime_AndTheFirstOneStartsAPlay()
    {
        var m = Movie("Film");
        Watch(m, 100, 7);                                           // positions 100..160 over 60 s
        var r = _a.Report(7);
        Assert.Equal(60, r.TotalSeconds, 1);
        Assert.Equal(1, r.TotalPlays);
        Assert.Equal(1, r.ActivePeople);
    }

    [Fact] public void SeekingForward_DoesNotCountAsWatching()
    {
        var m = Movie("Film");
        _p.Save(m, 100, 6000, null); _clock.Advance(TimeSpan.FromSeconds(10));
        _p.Save(m, 3000, 6000, null);                                // jumped 48 minutes in 10 s
        Assert.InRange(_a.Report(7).TotalSeconds, 0, 21);
    }

    [Fact] public void SeekingBack_AndPausing_AddNothing()
    {
        var m = Movie("Film");
        _p.Save(m, 500, 6000, null); _clock.Advance(TimeSpan.FromSeconds(10));
        _p.Save(m, 200, 6000, null); _clock.Advance(TimeSpan.FromSeconds(10));
        _p.Save(m, 200, 6000, null);                                 // paused: same position
        Assert.Equal(0, _a.Report(7).TotalSeconds);
    }

    [Fact] public void ALongGap_StartsASecondPlay()
    {
        var m = Movie("Film");
        Watch(m, 0, 3);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Watch(m, 30, 3);
        var r = _a.Report(7);
        Assert.Equal(2, r.TotalPlays);
        Assert.Equal(20 + 20, r.TotalSeconds, 1);                    // two beats of credit in each sitting
    }

    [Fact] public void EachPersonIsCountedSeparately_AndNamesComeFromTheirAccount()
    {
        using (var c = _t.Db.Open())
        {
            Database.Execute(c, null, "INSERT INTO users(id,username,password_hash,created_at,display_name) VALUES(1,'alex','x','x',NULL),(2,'sam','x','x','Sam')");
        }
        var m = Movie("Film");
        _who.UserId = 1; Watch(m, 0, 4);
        _who.UserId = 2; Watch(m, 0, 11);
        var r = _a.Report(7);
        Assert.Equal(2, r.ActivePeople);
        Assert.Equal(new[] { "Sam", "alex" }, r.People.Select(x => x.Name));                  // most time first
        Assert.Equal(new[] { 100.0, 30.0 }, r.People.Select(x => x.Seconds));
        Assert.Equal(2, r.Top.Single().Viewers);
    }

    [Fact] public void EpisodesRollUpIntoTheirShow_AndMoviesStaySeparate()
    {
        var a = Ep("Some Show", 1, 1); var b = Ep("Some Show", 1, 2); var f = Movie("Film");
        Watch(a, 0, 6); Watch(b, 0, 6); Watch(f, 0, 3);
        var r = _a.Report(7);
        Assert.Equal(2, r.Top.Count);
        Assert.Equal(("Some Show", "series", 100.0), (r.Top[0].Title, r.Top[0].Kind, r.Top[0].Seconds));
        Assert.Equal("S1E2 · Pilot", r.Recent.First(x => x.Title == "Some Show").Detail);
    }

    [Fact] public void TheDailyChart_CoversEveryDay_IncludingQuietOnes()
    {
        var m = Movie("Film");
        Watch(m, 0, 4);
        _clock.Advance(TimeSpan.FromDays(3));
        Watch(m, 60, 4);
        var r = _a.Report(7);
        Assert.Equal(7, r.PerDay.Count);
        Assert.Equal("2026-11-09", r.PerDay[^1].Day);
        Assert.Equal(new[] { 30.0, 0, 0, 30.0 }, r.PerDay.TakeLast(4).Select(x => x.Seconds));
        Assert.Single(_a.Report(1).PerDay);
        Assert.Equal(30, _a.Report(1).TotalSeconds);                     // only today
    }

    [Fact] public void NowWatching_ListsRecentHeartbeatsOnly()
    {
        var m = Movie("Film");
        Watch(m, 0, 3);
        Assert.Single(_a.Report(7).Now);
        _clock.Advance(TimeSpan.FromSeconds(ActivityService.NowWindowSeconds + 5));
        Assert.Empty(_a.Report(7).Now);
    }

    [Fact] public void BadInput_DoesNotChangeTheCounts()
    {
        var m = Movie("Film");
        Assert.False(_p.Save(m, double.NaN, 100, null));
        Assert.False(_p.Save(999, 10, 100, null));
        Assert.Equal(0, _a.Report(7).TotalPlays);
        Assert.Equal(ActivityService.MaxDays, _a.Report(100000).Days);
    }
}

public sealed class ArrServiceTests
{
    private static ArrService Make(Func<HttpRequestMessage, HttpResponseMessage> route, out StubHandler stub, bool both = true)
    {
        stub = new StubHandler(route);
        var cfg = new Dictionary<string, string?> { ["Radarr:Url"] = "http://radarr.test:7878/", ["Radarr:ApiKey"] = "rk" };
        if (both) { cfg["Sonarr:Url"] = "http://sonarr.test:8989"; cfg["Sonarr:ApiKey"] = "sk"; }
        return new ArrService(new HttpClient(stub), new ConfigurationBuilder().AddInMemoryCollection(cfg).Build());
    }

    [Fact] public async Task Ping_ReportsVersion_AndSendsTheKeyInAHeader()
    {
        var arr = Make(_ => TmdbJson.Ok("""{"appName":"Radarr","version":"5.2.1"}"""), out var stub);
        var (ok, msg) = await arr.PingAsync(ArrKind.Radarr, default);
        Assert.True(ok); Assert.Contains("5.2.1", msg);
        var req = stub.Requests.Single();
        Assert.Equal("http://radarr.test:7878/api/v3/system/status", req.RequestUri!.ToString());
        Assert.Equal("rk", req.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain("rk", req.RequestUri.Query);
    }

    [Fact] public async Task Ping_ExplainsWrongKeysWrongAppsAndMissingSetup()
    {
        Assert.Equal((false, "Radarr rejected the API key."), await Make(_ => TmdbJson.Status(HttpStatusCode.Unauthorized), out _).PingAsync(ArrKind.Radarr, default));
        var (ok, msg) = await Make(_ => TmdbJson.Ok("""{"appName":"Sonarr","version":"4"}"""), out _).PingAsync(ArrKind.Radarr, default);
        Assert.False(ok); Assert.Contains("not Radarr", msg);
        var (ok2, msg2) = await Make(_ => TmdbJson.Ok("{}"), out _, both: false).PingAsync(ArrKind.Sonarr, default);
        Assert.False(ok2); Assert.Contains("Save the Sonarr", msg2);
        var down = Make(_ => throw new HttpRequestException("connection refused"), out _);
        Assert.Contains("Could not reach Radarr", (await down.PingAsync(ArrKind.Radarr, default)).Message);
    }

    [Fact] public async Task Queue_MergesBothAppsWithProgress()
    {
        var arr = Make(r => r.RequestUri!.Host.StartsWith("radarr")
            ? TmdbJson.Ok("""{"records":[{"title":"x.2020.1080p","status":"downloading","trackedDownloadState":"downloading","size":1000,"sizeleft":250,"timeleft":"00:12:00","movie":{"title":"Big Film","year":2020}}]}""")
            : TmdbJson.Ok("""{"records":[{"title":"s","status":"warning","trackedDownloadStatus":"error","errorMessage":"No files found","size":0,"sizeleft":0,"series":{"title":"A Show"},"episode":{"seasonNumber":2,"episodeNumber":5,"title":"Five"}}]}"""), out var stub);
        var q = await arr.QueueAsync(default);
        Assert.Equal(2, q.Count);
        Assert.Equal(("Radarr", "Big Film (2020)", "Downloading", 75.0, "00:12:00", (string?)null), (q[0].Source, q[0].Title, q[0].State, q[0].Percent, q[0].TimeLeft, q[0].Problem));
        Assert.Equal(("A Show", "S2E5 · Five", "No files found"), (q[1].Title, q[1].Detail, q[1].Problem));
        Assert.Contains("includeMovie=true", stub.Requests[0].RequestUri!.Query);
        Assert.Null(q[0].Poster);
    }

    [Fact] public async Task Upcoming_UsesTheHomeReleaseDate_AndSortsByDate()
    {
        var soon = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd");
        var sooner = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd");
        var arr = Make(r => r.RequestUri!.Host.StartsWith("radarr")
            ? TmdbJson.Ok("[{\"title\":\"Later Film\",\"year\":2026,\"digitalRelease\":\"" + soon + "T00:00:00Z\",\"inCinemas\":\"2020-01-01T00:00:00Z\",\"hasFile\":false,\"monitored\":true}]")
            : TmdbJson.Ok("[{\"title\":\"Pilot\",\"seasonNumber\":1,\"episodeNumber\":1,\"airDateUtc\":\"" + sooner + "T02:00:00Z\",\"hasFile\":true,\"series\":{\"title\":\"New Show\"}}]"), out _);
        var u = await arr.UpcomingAsync(14, default);
        Assert.Equal(new[] { "New Show", "Later Film (2026)" }, u.Select(x => x.Title));
        Assert.Equal("Digital release", u[1].Detail);
        Assert.True(u[0].HasFile);
    }

    [Fact] public async Task Lookup_MarksTitlesAlreadyInTheLibrary()
    {
        var arr = Make(r => r.RequestUri!.AbsolutePath.EndsWith("movie/lookup")
            ? TmdbJson.Ok("""[{"title":"Have It","year":2019,"tmdbId":5,"id":12},{"title":"New One","year":2024,"tmdbId":6,"id":0}]""") : TmdbJson.Ok("[]"), out var stub);
        var items = await arr.LookupAsync("film name", default);
        Assert.Equal(new[] { true, false }, items.Select(i => i.InLibrary));
        Assert.Equal(6, items[1].TmdbId);
        Assert.Contains("term=film%20name", stub.Requests[0].RequestUri!.Query);
    }

    [Fact] public async Task Add_PostsTheLookedUpItemWithYourChoices_AndSearchesRightAway()
    {
        JsonNode? posted = null;
        var arr = Make(r =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("movie/lookup/tmdb")) return TmdbJson.Ok("""{"title":"Big Film","tmdbId":77,"year":2024,"titleSlug":"big-film-77","id":0}""");
            if (p.EndsWith("rootfolder")) return TmdbJson.Ok("""[{"id":1,"path":"/movies/"}]""");
            if (p.EndsWith("/movie") && r.Method == HttpMethod.Post) { posted = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return TmdbJson.Ok("{}"); }
            return TmdbJson.Status(HttpStatusCode.NotFound);
        }, out _);
        await arr.AddAsync(ArrKind.Radarr, 77, 4, "/movies", default);
        Assert.Equal(("big-film-77", 4, "/movies", true), ((string?)posted!["titleSlug"], (int)posted["qualityProfileId"]!, (string?)posted["rootFolderPath"], (bool)posted["addOptions"]!["searchForMovie"]!));
    }

    [Fact] public async Task Add_RefusesAFolderThatIsNotOneOfTheApps_AndTitlesAlreadyThere()
    {
        var arr = Make(r => r.RequestUri!.AbsolutePath.EndsWith("rootfolder") ? TmdbJson.Ok("""[{"id":1,"path":"/movies"}]""") : TmdbJson.Ok("""{"title":"T","id":0}"""), out var stub);
        var ex = await Assert.ThrowsAsync<ArrException>(() => arr.AddAsync(ArrKind.Radarr, 1, 1, "/etc", default));
        Assert.Contains("folders", ex.Message);
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Post);
        var dup = Make(_ => TmdbJson.Ok("""{"title":"T","id":9}"""), out _);
        Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<ArrException>(() => dup.AddAsync(ArrKind.Radarr, 1, 1, "/m", default))).Status);
    }

    [Fact] public async Task Sonarr_Add_UsesTheTvdbLookup_AndMonitorsEverything()
    {
        JsonNode? posted = null;
        var arr = Make(r =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("series/lookup")) return TmdbJson.Ok("""[{"title":"A Show","tvdbId":42,"id":0}]""");
            if (p.EndsWith("rootfolder")) return TmdbJson.Ok("""[{"id":1,"path":"/tv"}]""");
            if (p.EndsWith("/series") && r.Method == HttpMethod.Post) { posted = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return TmdbJson.Ok("{}"); }
            return TmdbJson.Status(HttpStatusCode.NotFound);
        }, out var stub);
        await arr.AddAsync(ArrKind.Sonarr, 42, 2, "/tv", default);
        Assert.Contains("term=tvdb:42", stub.Requests[0].RequestUri!.Query);
        Assert.Equal(("all", true), (((string?)posted!["addOptions"]!["monitor"])!, (bool)posted["addOptions"]!["searchForMissingEpisodes"]!));
    }

    [Theory]
    [InlineData("""[{"errorMessage":"Path is already configured"},{"errorMessage":"Path is already configured"}]""", "Path is already configured")]
    [InlineData("""{"message":"Bad things"}""", "Bad things")]
    [InlineData("not json", null)]
    public void ProblemText_PullsTheReasonOutOfTheAppsReply(string body, string? expected) => Assert.Equal(expected, ArrService.ProblemText(body));
}

public sealed class ArrEndpointTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task TheWebhook_NeedsNoCookie_ButDoesNeedTheRightToken()
    {
        var admin = await _f.SignedIn("alex");
        var path = (await admin.GetFromJsonAsync<JsonElement>("/api/arr/status")).GetProperty("webhookPath").GetString()!;
        Assert.Matches("^/api/arr/webhook/[0-9a-f]{48}$", path);
        var arr = _f.Client();                                                                        // Radarr: no session at all
        var test = await arr.PostAsJsonAsync(path, new { eventType = "Test" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.False((await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scan").GetBoolean());
        var import = await arr.PostAsJsonAsync(path, new { eventType = "Download" });
        Assert.True((await import.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scan").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await arr.PostAsJsonAsync("/api/arr/webhook/" + new string('0', 48), new { eventType = "Download" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await arr.GetAsync("/api/arr/status")).StatusCode);   // everything else still needs a session
    }

    [Fact] public async Task MakingANewAddress_RetiresTheOldOne()
    {
        var admin = await _f.SignedIn("alex");
        var old = (await admin.GetFromJsonAsync<JsonElement>("/api/arr/status")).GetProperty("webhookPath").GetString()!;
        var fresh = (await (await admin.PostAsync("/api/arr/webhook/reset", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("webhookPath").GetString()!;
        Assert.NotEqual(old, fresh);
        var anon = _f.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync(old, new { eventType = "Download" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync(fresh, new { eventType = "Download" })).StatusCode);
    }

    [Fact] public async Task WithNothingConfigured_TheEndpointsSayWhatIsMissing()
    {
        var admin = await _f.SignedIn("alex");
        var s = await admin.GetFromJsonAsync<JsonElement>("/api/arr/status");
        Assert.False(s.GetProperty("radarr").GetBoolean()); Assert.False(s.GetProperty("sonarr").GetBoolean());
        var q = await admin.GetAsync("/api/arr/queue");
        Assert.Equal(HttpStatusCode.OK, q.StatusCode);                                               // nothing to ask, nothing to show
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/arr/lookup?term=")).StatusCode);
        var add = await admin.PostAsJsonAsync("/api/arr/add", new { source = "Radarr", id = 5, qualityProfileId = 1, rootFolder = "/m" });
        Assert.Equal(HttpStatusCode.BadGateway, add.StatusCode);
        Assert.Contains("not set up", (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var test = await admin.PostAsync("/api/settings/test/radarr", null);
        Assert.False((await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
    }

    [Fact] public async Task TheActivityReport_IsAvailableToAdministrators()
    {
        var admin = await _f.SignedIn("alex");
        var r = await admin.GetFromJsonAsync<JsonElement>("/api/system/activity?days=7");
        Assert.Equal(7, r.GetProperty("days").GetInt32()); Assert.Equal(7, r.GetProperty("perDay").GetArrayLength());
    }
}

public sealed class ArrArtTests
{
    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w342/abcDEF123.jpg", true)]
    [InlineData("https://artworks.thetvdb.com/banners/posters/12345-1.jpg", true)]
    [InlineData("http://image.tmdb.org/t/p/w342/abc.jpg", false)]                 // not https
    [InlineData("https://evil.example/t/p/w342/abc.jpg", false)]                  // not an artwork site
    [InlineData("https://image.tmdb.org.evil.example/abc.jpg", false)]
    [InlineData("https://user@image.tmdb.org/abc.jpg", false)]
    [InlineData("https://image.tmdb.org:8443/abc.jpg", false)]
    [InlineData("https://image.tmdb.org/abc.jpg?x=1", false)]
    [InlineData("https://image.tmdb.org/abc.html", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void OnlyHttpsPicturesOnTheArtworkSites_AreFetched(string url, bool allowed) => Assert.Equal(allowed, ArrArt.Allowed(url) is not null);

    [Fact] public void DotDotSegments_AreResolvedBeforeAnyRequest_SoTheyCannotLeaveTheArtworkSite()
        => Assert.Equal("https://image.tmdb.org/etc/passwd.jpg", ArrArt.Allowed("https://image.tmdb.org/t/p/../../etc/passwd.jpg")!.AbsoluteUri);

    [Fact] public void PosterPath_PicksThePosterAndAsksForASmallerSize()
    {
        var images = JsonNode.Parse("""[{"coverType":"fanart","remoteUrl":"https://image.tmdb.org/t/p/original/fan.jpg"},{"coverType":"poster","remoteUrl":"https://image.tmdb.org/t/p/original/poster1.jpg"}]""");
        var path = ArrArt.PosterPath(images)!;
        Assert.StartsWith("/api/arr/art?u=", path);
        Assert.Equal("https://image.tmdb.org/t/p/w342/poster1.jpg", Uri.UnescapeDataString(path["/api/arr/art?u=".Length..]));
        Assert.Null(ArrArt.PosterPath(JsonNode.Parse("""[{"coverType":"poster","remoteUrl":"https://evil.example/p.jpg"}]""")));
        Assert.Null(ArrArt.PosterPath(null));
    }

    [Fact] public async Task TheArtwork_IsFetchedOnceAndThenServedFromDisk()
    {
        var dir = Directory.CreateTempSubdirectory("art").FullName;
        try
        {
            var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } } });
            var art = new ArrArt(new HttpClient(stub), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Images:CacheDir"] = dir }).Build());
            var url = "https://image.tmdb.org/t/p/w342/abcDEF123.jpg";
            var first = await art.GetAsync(url, default); var second = await art.GetAsync(url, default);
            Assert.Equal("image/jpeg", first!.Value.ContentType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(second!.Value.File));
            Assert.Single(stub.Requests);
            Assert.Null(await art.GetAsync("https://evil.example/a.jpg", default));
            Assert.Single(stub.Requests);                                                         // never even asked
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact] public async Task ThePageCannotUseTheArtworkRouteToReachOtherSites()
    {
        using var f = new AuthFixture();
        var admin = await f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/arr/art?u=" + Uri.EscapeDataString("https://evil.example/a.jpg"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/arr/art")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client().GetAsync("/api/arr/art?u=x")).StatusCode);
    }
}
