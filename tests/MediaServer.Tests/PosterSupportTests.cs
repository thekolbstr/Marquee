using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class MatchedFilterTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly LibraryQueries _q;
    public MatchedFilterTests() => _q = new LibraryQueries(_t.Db);
    public void Dispose() => _t.Dispose();

    private long Add(string title, string kind = "Movie", int? tmdb = null, int? season = null, int? ep = null)
    {
        var id = _t.Insert($"{title}-{Guid.NewGuid():N}.mkv", kind, title, 2020);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, tmdb_type=@tt, tmdb_id=@ti, match_state=@ms WHERE id=@i",
            ("@s", season), ("@e", ep), ("@tt", tmdb is null ? null : kind == "Episode" ? "tv" : "movie"), ("@ti", tmdb), ("@ms", tmdb is null ? "unmatched" : "matched"), ("@i", id));
        if (tmdb is not null)
            Database.Execute(c, null, "INSERT OR IGNORE INTO metadata(tmdb_type,tmdb_id,title,year,poster_path,fetched_at) VALUES(@tt,@ti,@t,2020,'/poster12345.jpg','x')",
                ("@tt", kind == "Episode" ? "tv" : "movie"), ("@ti", tmdb), ("@t", title));
        return id;
    }

    [Fact]
    public void MatchedFilter_SeparatesMatchedFromUnmatched_AcrossMoviesAndSeries()
    {
        Add("Good Movie", tmdb: 1); Add("Lost Movie");
        Add("Good Show", "Episode", tmdb: 2, season: 1, ep: 1); Add("Good Show", "Episode", tmdb: 2, season: 1, ep: 2);
        Add("Lost Show", "Episode", season: 1, ep: 1);

        Assert.Equal(["Lost Movie", "Lost Show"], _q.List(null, null, "title", 50, 0, matched: false).Items.Select(i => i.Title));
        Assert.Equal(["Good Movie", "Good Show"], _q.List(null, null, "title", 50, 0, matched: true).Items.Select(i => i.Title));
        Assert.Equal(4, _q.List(null, null, "title", 50, 0).Total);                           // no filter: everything
        Assert.Equal(2, _q.UnmatchedCount());
        Assert.Equal(1, _q.List(null, "series", "title", 50, 0, matched: false).Total);       // combines with the type filter
    }

    [Fact]
    public void ASeriesWithSomeMatchedEpisodesCountsAsMatched()
    {
        Add("Show", "Episode", tmdb: 5, season: 1, ep: 1); Add("Show", "Episode", season: 1, ep: 2);
        Assert.Equal(0, _q.UnmatchedCount());
    }

    [Fact]
    public void SeriesDetail_NamesTheEpisodeToUseForMatching()
    {
        var e2 = Add("Show", "Episode", season: 1, ep: 2); var e1 = Add("Show", "Episode", season: 1, ep: 1);
        var key = _q.List(null, "series", "title", 10, 0).Items.Single().Key;
        Assert.Equal(e1, _q.Series(key)!.MatchMediaId);
    }

    [Fact]
    public void PostersAreExposedOnlyForMatchedTitles()
    {
        var m = Add("With Poster", tmdb: 9); Add("Without Poster");
        var items = _q.List(null, null, "title", 10, 0).Items;
        Assert.Equal($"/api/media/{m}/poster", items.Single(i => i.Title == "With Poster").PosterUrl);
        Assert.Null(items.Single(i => i.Title == "Without Poster").PosterUrl);
    }
}

public sealed class TmdbEndpointConfigTests : IDisposable
{
    private readonly TestDb _t = new(new() { ["Tmdb:BaseUrl"] = "http://127.0.0.1:1/3/", ["Tmdb:ImageBaseUrl"] = "http://127.0.0.1:1/t/p/", ["Tmdb:ApiKey"] = "k" });
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task TmdbClient_UsesTheConfiguredBaseUrl()
    {
        var h = new StubHandler(_ => TmdbJson.Search());
        var c = new TmdbClient(new HttpClient(h), _t.Db, _t.Config, TimeProvider.System);
        await c.SearchAsync("movie", "x", null, default);
        Assert.StartsWith("http://127.0.0.1:1/3/search/movie", h.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ImageCache_UsesTheConfiguredBaseUrl()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        Assert.NotNull(await new ImageCache(new HttpClient(h), _t.Config).GetAsync("w154", "/abcde12345.jpg", default));
        Assert.Equal("http://127.0.0.1:1/t/p/w154/abcde12345.jpg", h.Requests[0].RequestUri!.ToString());
    }
}

public sealed class PosterEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("posterep").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;

    public PosterEndpointTests() : this(false) { }
    private PosterEndpointTests(bool configured)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", Path.Combine(_dir, "lib")).UseSetting("Library:AutoScan", "false")
            .UseSetting("Images:CacheDir", Path.Combine(_dir, "img")));
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("/api/tmdb/image?path=/../../etc/passwd")]
    [InlineData("/api/tmdb/image?path=/a/b.jpg")]
    [InlineData("/api/tmdb/image?path=abcde12345.jpg")]
    [InlineData("/api/tmdb/image?path=/abcde12345.exe")]
    [InlineData("/api/tmdb/image?path=%2F..%2F..%2Fx.jpg")]
    public async Task CandidateImageProxy_RejectsHostilePathsWithoutAnyNetworkRequest(string url)
        => Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync(url)).StatusCode);

    [Theory] [InlineData("w1")] [InlineData("../w154")] [InlineData("original/../x")]
    public async Task CandidateImageProxy_OnlyKnownSizes(string size)
        => Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/tmdb/image?path=/abcde12345.jpg&size={Uri.EscapeDataString(size)}")).StatusCode);

    [Fact]
    public async Task Status_SaysWhetherTmdbIsConfigured_SoTheUiCanOfferTheRightThing()
    {
        var st = await _c.GetFromJsonAsync<JsonElement>("/api/library/status");
        Assert.False(st.GetProperty("tmdbConfigured").GetBoolean());
        Assert.Equal(0, st.GetProperty("unmatched").GetInt32());                  // nothing to "fix" without TMDB
    }

    [Fact]
    public async Task Status_CountsUnmatchedTitles_OnceTmdbIsConfigured()
    {
        using var configured = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Database:Path", Path.Combine(_dir, "t2.db")).UseSetting("Library:Root", Path.Combine(_dir, "lib")).UseSetting("Library:AutoScan", "false")
            .UseSetting("Tmdb:ApiKey", "key"));
        var c = configured.CreateClient();
        using (var conn = configured.Services.GetRequiredService<Database>().Open())
            Database.Execute(conn, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,year,match_state) VALUES('a.mkv',1,1,'x','x',1,'Movie','A',2020,'unmatched')");
        var st = await c.GetFromJsonAsync<JsonElement>("/api/library/status");
        Assert.True(st.GetProperty("tmdbConfigured").GetBoolean());
        Assert.Equal(1, st.GetProperty("unmatched").GetInt32());
        var list = await c.GetFromJsonAsync<JsonElement>("/api/items?matched=false");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/items?matched=true")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SearchWithoutAToken_IsAFriendly503()
        => Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _c.GetAsync("/api/tmdb/search?type=movie&q=dune")).StatusCode);
}

public sealed class ClientEventTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clientev").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;
    public ClientEventTests()
    {
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false"));
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task AcceptsAReport()
        => Assert.Equal(HttpStatusCode.NoContent, (await _c.PostAsJsonAsync("/api/client-events", new { kind = "native-error", mediaId = 1, mode = "remux", detail = "x", mediaError = "4:foo", nativeHls = true, mse = true, caps = new { v = "h264", a = "aac", c = "mp4" } })).StatusCode);

    [Fact]
    public async Task RefusesOversizedBodies()
    {
        var big = new StringContent("{\"kind\":\"" + new string('a', 20_000) + "\"}", System.Text.Encoding.UTF8, "application/json");
        var res = await _c.PostAsync("/api/client-events", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Theory]
    [InlineData("a\nb\r\nFAKE LOG LINE", "a b  FAKE LOG LINE")]
    [InlineData("tab\there", "tab here")]
    public void ControlCharactersCannotForgeLogLines(string input, string expected)
        => Assert.Equal(expected, MediaServer.Api.Endpoints.ClientEventEndpoints.Clean(input, 100));

    [Fact] public void TruncatesAndHandlesNull()
    {
        Assert.Equal(5, MediaServer.Api.Endpoints.ClientEventEndpoints.Clean("abcdefghij", 5).Length);
        Assert.Equal("", MediaServer.Api.Endpoints.ClientEventEndpoints.Clean(null, 5));
    }

    [Fact]
    public void RateLimitsAFlood()
    {
        var t = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var allowed = Enumerable.Range(0, 200).Count(_ => MediaServer.Api.Endpoints.ClientEventEndpoints.Allow(t));
        Assert.Equal(MediaServer.Api.Endpoints.ClientEventEndpoints.MaxPerMinute, allowed);
        Assert.True(MediaServer.Api.Endpoints.ClientEventEndpoints.Allow(t.AddMinutes(2)));   // a new window opens
    }

    [Fact]
    public async Task RejectsMalformedJson() => Assert.Equal(HttpStatusCode.BadRequest,
        (await _c.PostAsync("/api/client-events", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
}
