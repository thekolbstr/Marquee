using System.Net;
using MediaServer.Api.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public class TmdbMatcherTests
{
    private static TmdbHit H(int id, string title, int? year, double pop = 1) => new(id, title, title, year, pop, null, null);

    [Fact]
    public void ExactTitleAndYear_BeatsMorePopularWrongYear()
        => Assert.Equal(2, TmdbMatcher.Pick([H(1, "Dune", 1984, 99), H(2, "Dune", 2021, 5)], "Dune", 2021)!.Id);

    [Fact]
    public void ExactTitleBeatsLongerSimilarTitle()
        => Assert.Equal(2, TmdbMatcher.Pick([H(1, "The Matrix Resurrections", 2021), H(2, "The Matrix", 1999)], "The Matrix", 1999)!.Id);

    [Fact]
    public void YearToleranceOfOne()
        => Assert.Equal(1, TmdbMatcher.Pick([H(1, "Some Film", 2011)], "Some Film", 2010)!.Id);

    [Fact]
    public void NoYear_TakesFirstExactTitleInRelevanceOrder()
        => Assert.Equal(1, TmdbMatcher.Pick([H(1, "Dune", 2021), H(2, "Dune", 1984)], "Dune", null)!.Id);

    [Theory]
    [InlineData("Ocean's Eleven", "Oceans Eleven")]
    [InlineData("Amélie", "Amelie")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    public void KeyIgnoresCasePunctuationDiacritics(string a, string b) => Assert.Equal(TmdbMatcher.Key(a), TmdbMatcher.Key(b));

    [Fact]
    public void UnrelatedResults_AreNotMatched()
        => Assert.Null(TmdbMatcher.Pick([H(1, "Completely Different", 2010)], "Inception", 2010));

    [Fact]
    public void WrongYearFarAway_IsNotMatched()
        => Assert.Null(TmdbMatcher.Pick([H(1, "Inception", 1950)], "Inception", 2010));
}

public sealed class TmdbClientTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public void Dispose() => _t.Dispose();

    private TmdbClient Client(StubHandler h, string? key = "k3y", string? token = null)
    {
        var cfg = new Dictionary<string, string?> { ["Tmdb:ApiKey"] = key, ["Tmdb:ReadAccessToken"] = token };
        var c = new TestDb(cfg); // separate config, same DB path semantics
        _t.Config["Tmdb:ApiKey"] = key; _t.Config["Tmdb:ReadAccessToken"] = token;
        c.Dispose();
        return new TmdbClient(new HttpClient(h), _t.Db, _t.Config, _time);
    }

    [Fact]
    public async Task SecondCallIsServedFromCache()
    {
        var h = new StubHandler(_ => TmdbJson.Search((603, "The Matrix", "1999-03-30", 80)));
        var c = Client(h);
        Assert.Single(await c.SearchAsync("movie", "The Matrix", 1999, default));
        Assert.Single(await c.SearchAsync("movie", "The Matrix", 1999, default));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task ApiKeySentAsQuery_ButNeverStoredInCacheKey()
    {
        var h = new StubHandler(_ => TmdbJson.Search());
        await Client(h).SearchAsync("movie", "x", null, default);
        Assert.Contains("api_key=k3y", h.Requests[0].RequestUri!.Query);
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM tmdb_cache WHERE key LIKE '%k3y%' OR key LIKE '%api_key%'"));
    }

    [Fact]
    public async Task ReadAccessToken_UsesBearerHeader_NotQueryString()
    {
        var h = new StubHandler(_ => TmdbJson.Search());
        await Client(h, key: null, token: "tok").SearchAsync("movie", "x", null, default);
        Assert.Equal("Bearer", h.Requests[0].Headers.Authorization!.Scheme);
        Assert.DoesNotContain("api_key", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task NotFound_IsNegativeCached_ThenRetriedAfterTtl()
    {
        var h = new StubHandler(_ => TmdbJson.Status(HttpStatusCode.NotFound));
        var c = Client(h);
        Assert.Null(await c.GetDetailsAsync("movie", 1, default));
        Assert.Null(await c.GetDetailsAsync("movie", 1, default));
        Assert.Single(h.Requests);
        _time.Advance(TimeSpan.FromDays(2));
        Assert.Null(await c.GetDetailsAsync("movie", 1, default));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task StaleCacheIsServedWhenTmdbErrors()
    {
        var fail = false;
        var h = new StubHandler(_ => fail ? TmdbJson.Status(HttpStatusCode.BadGateway) : TmdbJson.Details(603, "The Matrix", "1999-03-30"));
        var c = Client(h);
        Assert.Equal("The Matrix", (await c.GetDetailsAsync("movie", 603, default))!.Title);
        _time.Advance(TimeSpan.FromDays(40)); fail = true;
        Assert.Equal("The Matrix", (await c.GetDetailsAsync("movie", 603, default))!.Title);
    }

    [Fact]
    public async Task ServerErrorWithEmptyCache_Throws()
    {
        var c = Client(new StubHandler(_ => TmdbJson.Status(HttpStatusCode.InternalServerError)));
        var ex = await Assert.ThrowsAsync<TmdbException>(() => c.GetDetailsAsync("movie", 1, default));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.Status);
    }

    [Fact]
    public async Task Unauthorized_ThrowsClearError()
    {
        var c = Client(new StubHandler(_ => TmdbJson.Status(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<TmdbException>(() => c.SearchAsync("movie", "x", null, default));
    }

    [Fact]
    public async Task RateLimited_RetriesOnceHonouringRetryAfter()
    {
        var n = 0;
        var h = new StubHandler(_ =>
        {
            if (n++ > 0) return TmdbJson.Search((1, "A", "2000-01-01", 1));
            var r = TmdbJson.Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });
        var cfg = new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "k" };
        _t.Config["Tmdb:ApiKey"] = "k";
        var c = new TmdbClient(new HttpClient(h), _t.Db, _t.Config, TimeProvider.System);
        Assert.Single(await c.SearchAsync("movie", "A", null, default));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task NotConfigured_ThrowsWithoutNetwork()
    {
        var h = new StubHandler(_ => TmdbJson.Search());
        var c = new TmdbClient(new HttpClient(h), _t.Db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _time);
        await Assert.ThrowsAsync<TmdbNotConfiguredException>(() => c.SearchAsync("movie", "x", null, default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task InvalidType_Rejected()
        => await Assert.ThrowsAsync<ArgumentException>(() => Client(new StubHandler(_ => TmdbJson.Search())).SearchAsync("person", "x", null, default));

    [Fact]
    public async Task DetailsParsing()
    {
        var d = (await Client(new StubHandler(_ => TmdbJson.Details(603, "The Matrix", "1999-03-30"))).GetDetailsAsync("movie", 603, default))!;
        Assert.Equal((603, "The Matrix", 1999, 8.2), (d.Id, d.Title, d.Year, d.Rating));
        Assert.Equal(["Action", "Sci-Fi"], d.Genres);
        Assert.Equal("/poster603.jpg", d.PosterPath);
    }
}

public sealed class MetadataServiceTests : IDisposable
{
    private readonly TestDb _t = new(new() { ["Tmdb:ApiKey"] = "k" });
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly StubHandler _http;
    private readonly MetadataService _svc;
    public void Dispose() => _t.Dispose();

    public MetadataServiceTests()
    {
        _http = new StubHandler(Route);
        var client = new TmdbClient(new HttpClient(_http), _t.Db, _t.Config, _time);
        _svc = new MetadataService(_t.Db, client, _time, _t.Config, NullLogger<MetadataService>.Instance);
    }

    private static HttpResponseMessage Route(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        var q = Uri.UnescapeDataString(r.RequestUri.Query);
        if (path.EndsWith("/search/movie"))
        {
            if (q.Contains("query=The Matrix&") && q.Contains("year=1999"))
                return TmdbJson.Search((748, "The Matrix Resurrections", "2021-12-16", 99), (603, "The Matrix", "1999-03-30", 50));
            if (q.Contains("query=Wonder Woman&") && q.Contains("year=1984")) return TmdbJson.Search();
            if (q.Contains("query=Wonder Woman 1984&")) return TmdbJson.Search((464052, "Wonder Woman 1984", "2020-12-16", 70));
            if (q.Contains("query=Obscure Thing")) return TmdbJson.Search((9, "Totally Unrelated", "1950-01-01", 1));
            return TmdbJson.Search();
        }
        if (path.EndsWith("/search/tv"))
            return q.Contains("query=Breaking Bad") ? TmdbJson.Search((1396, "Breaking Bad", "2008-01-20", 90)) : TmdbJson.Search();
        return path switch
        {
            var p when p.EndsWith("/movie/603") => TmdbJson.Details(603, "The Matrix", "1999-03-30"),
            var p when p.EndsWith("/movie/464052") => TmdbJson.Details(464052, "Wonder Woman 1984", "2020-12-16"),
            var p when p.EndsWith("/tv/1396") => TmdbJson.Details(1396, "Breaking Bad", "2008-01-20"),
            var p when p.EndsWith("/tv/2222") => TmdbJson.Details(2222, "Better Call Saul", "2015-02-08"),
            _ => TmdbJson.Status(HttpStatusCode.NotFound),
        };
    }

    [Fact]
    public async Task MatchesMovie_PicksExactTitleOverMorePopularSequel()
    {
        var id = _t.Insert("The.Matrix.1999.mkv", "Movie", "The Matrix", 1999);
        var r = await _svc.EnrichAsync(default);
        Assert.Equal(1, r!.Matched);
        var m = _svc.GetMatch(id)!;
        Assert.Equal(("matched", "movie", 603, "The Matrix"), (m.State, m.TmdbType, m.TmdbId, m.Title));
        Assert.Equal("/poster603.jpg", m.PosterPath);
        Assert.Equal("Action, Sci-Fi", m.Genres);
    }

    [Fact]
    public async Task SeriesWithManyEpisodes_CostsOneSearch()
    {
        for (var i = 1; i <= 5; i++) _t.Insert($"Breaking Bad/S01E0{i}.mkv", "Episode", "Breaking Bad");
        _t.Insert("Breaking.Bad.S01E06.mkv", "Episode", "breaking bad"); // same series, different spelling
        var r = await _svc.EnrichAsync(default);
        Assert.Equal(1, r!.Groups);
        Assert.Equal(6, r.Matched);
        Assert.Equal(1, _http.Count("/search/tv"));
        Assert.Equal("6", _t.Scalar("SELECT COUNT(*) FROM media_files WHERE tmdb_type='tv' AND tmdb_id=1396"));
    }

    [Fact]
    public async Task YearThatIsPartOfTheTitle_FallsBackToTitlePlusYear()
    {
        var id = _t.Insert("Wonder.Woman.1984.mkv", "Movie", "Wonder Woman", 1984);
        await _svc.EnrichAsync(default);
        Assert.Equal(464052, _svc.GetMatch(id)!.TmdbId);
    }

    [Fact]
    public async Task Unmatched_IsNotRetriedUntilRetryWindowPasses()
    {
        var id = _t.Insert("Obscure.Thing.mkv", "Movie", "Obscure Thing");
        Assert.Equal(1, (await _svc.EnrichAsync(default))!.Unmatched);
        Assert.Equal("unmatched", _svc.GetMatch(id)!.State);

        _http.Requests.Clear();
        Assert.Equal(0, (await _svc.EnrichAsync(default))!.Groups);
        Assert.Empty(_http.Requests);

        _time.Advance(TimeSpan.FromDays(8));
        _http.Requests.Clear(); // the response cache (1 day negative TTL) has expired too
        Assert.Equal(1, (await _svc.EnrichAsync(default))!.Groups);
        Assert.NotEmpty(_http.Requests);
    }

    [Fact]
    public async Task FileOverride_WinsAndSurvivesReEnrichment()
    {
        var id = _t.Insert("The.Matrix.1999.mkv", "Movie", "The Matrix", 1999);
        await _svc.EnrichAsync(default);
        Assert.Equal(1, await _svc.SetOverrideAsync(id, "movie", 464052, "file", default));
        Assert.Equal(("manual", 464052), (_svc.GetMatch(id)!.State, _svc.GetMatch(id)!.TmdbId));

        await _svc.EnrichAsync(default);
        Assert.Equal(464052, _svc.GetMatch(id)!.TmdbId);
    }

    [Fact]
    public async Task TitleOverride_AppliesToWholeSeries_AndToFilesScannedLater()
    {
        var a = _t.Insert("a/S01E01.mkv", "Episode", "Breaking Bad");
        var b = _t.Insert("a/S01E02.mkv", "Episode", "Breaking Bad");
        await _svc.EnrichAsync(default);
        Assert.Equal(2, await _svc.SetOverrideAsync(a, "tv", 2222, "title", default));
        Assert.Equal(2222, _svc.GetMatch(b)!.TmdbId);

        var later = _t.Insert("a/S01E03.mkv", "Episode", "Breaking Bad");
        _http.Requests.Clear();
        var r = await _svc.EnrichAsync(default);
        Assert.Equal(1, r!.OverridesApplied);
        Assert.Equal(2222, _svc.GetMatch(later)!.TmdbId);
        Assert.Equal(0, _http.Count("/search/tv")); // override means no search at all
    }

    [Fact]
    public async Task ClearOverride_ReturnsRowsToAutomaticMatching()
    {
        var id = _t.Insert("The.Matrix.1999.mkv", "Movie", "The Matrix", 1999);
        await _svc.SetOverrideAsync(id, "movie", 464052, "file", default);
        Assert.Equal(1, _svc.ClearOverride(id, "file"));
        Assert.Null(_svc.GetMatch(id)!.State);
        await _svc.EnrichAsync(default);
        Assert.Equal(603, _svc.GetMatch(id)!.TmdbId);
    }

    [Fact]
    public async Task Override_WithUnknownTmdbId_Is404Style_AndChangesNothing()
    {
        var id = _t.Insert("x.mkv", "Movie", "X");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _svc.SetOverrideAsync(id, "movie", 424242, "file", default));
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM match_overrides"));
        Assert.Null(await _svc.SetOverrideAsync(9999, "movie", 603, "file", default));
    }

    [Fact]
    public async Task Override_ValidatesInput()
    {
        var id = _t.Insert("x.mkv", "Extra", "");
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.SetOverrideAsync(id, "person", 1, "file", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.SetOverrideAsync(id, "movie", 603, "everything", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.SetOverrideAsync(id, "movie", 603, "title", default));
    }

    [Fact]
    public async Task TmdbOutage_LeavesRowsPendingForNextRun()
    {
        var down = new StubHandler(_ => TmdbJson.Status(HttpStatusCode.ServiceUnavailable));
        var svc = new MetadataService(_t.Db, new TmdbClient(new HttpClient(down), _t.Db, _t.Config, _time), _time, _t.Config, NullLogger<MetadataService>.Instance);
        var id = _t.Insert("The.Matrix.1999.mkv", "Movie", "The Matrix", 1999);
        var r = await svc.EnrichAsync(default);
        Assert.Equal(1, r!.Errors);
        Assert.Null(svc.GetMatch(id)!.State);
    }

    [Fact]
    public async Task SoftDeletedAndNonMediaRowsAreSkipped()
    {
        _t.Insert("e.mkv", "Extra", "Making Of");
        var gone = _t.Insert("g.mkv", "Movie", "The Matrix", 1999);
        using (var c = _t.Db.Open()) MediaServer.Api.Data.Database.Exec(c, $"UPDATE media_files SET deleted_at='x' WHERE id={gone}");
        Assert.Equal(0, (await _svc.EnrichAsync(default))!.Groups);
    }
}

public sealed class ImageCacheTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task DownloadsOnce_ThenServesFromDisk()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var cache = new ImageCache(new HttpClient(h), _t.Config);
        var p1 = await cache.GetAsync("w342", "/abcde12345.jpg", default);
        var p2 = await cache.GetAsync("w342", "/abcde12345.jpg", default);
        Assert.Equal(p1, p2);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(p1!));
        Assert.Single(h.Requests);
        Assert.Equal("https://image.tmdb.org/t/p/w342/abcde12345.jpg", h.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("w342", "/../../etc/passwd")]
    [InlineData("w342", "/..%2f..%2fx.jpg")]
    [InlineData("w342", "/a/b.jpg")]
    [InlineData("w342", "abcde12345.jpg")]
    [InlineData("w342", "/abcde12345.exe")]
    [InlineData("../w342", "/abcde12345.jpg")]
    [InlineData("w1", "/abcde12345.jpg")]
    public async Task RejectsHostileInput_WithoutAnyRequest(string size, string path)
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Null(await new ImageCache(new HttpClient(h), _t.Config).GetAsync(size, path, default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Missing_ReturnsNull_AndLeavesNoTempFiles()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await new ImageCache(new HttpClient(h), _t.Config).GetAsync("w342", "/abcde12345.jpg", default));
        var imgDir = Path.Combine(_t.Dir, "img");
        Assert.Empty(Directory.Exists(imgDir) ? Directory.GetFiles(imgDir, "*", SearchOption.AllDirectories) : []);
    }
}
