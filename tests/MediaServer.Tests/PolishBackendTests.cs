using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Endpoints;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class TmdbSeasonTests : IDisposable
{
    private readonly TestDb _t = new(new() { ["Tmdb:ApiKey"] = "k" });
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public void Dispose() => _t.Dispose();
    private TmdbClient Client(StubHandler h) => new(new HttpClient(h), _t.Db, _t.Config, _time);

    private static HttpResponseMessage Season(params (int N, string Name, string? Overview, string? Still)[] eps) => TmdbJson.Ok(JsonSerializer.Serialize(new
    {
        episodes = eps.Select(e => new { episode_number = e.N, name = e.Name, overview = e.Overview, air_date = "2022-02-18", runtime = 57, still_path = e.Still }),
    }));

    [Fact]
    public async Task ParsesEpisodes_AndCachesTheSeason()
    {
        var h = new StubHandler(_ => Season((1, "Good News About Hell", "Mark returns.", "/still1.jpg"), (2, "Half Loop", null, null)));
        var c = Client(h);
        var eps = await c.GetSeasonAsync(95396, 1, default);
        Assert.Equal(["Good News About Hell", "Half Loop"], eps.Select(e => e.Name));
        Assert.Equal((1, "Mark returns.", "/still1.jpg", 57, "2022-02-18"), (eps[0].Number, eps[0].Overview, eps[0].StillPath, eps[0].Runtime, eps[0].AirDate));
        Assert.Null(eps[1].StillPath);
        await c.GetSeasonAsync(95396, 1, default);
        Assert.Single(h.Requests);                                                      // second call: cache
        Assert.Contains("/tv/95396/season/1", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact] public async Task UnknownSeason_IsEmpty() => Assert.Empty(await Client(new StubHandler(_ => TmdbJson.Status(HttpStatusCode.NotFound))).GetSeasonAsync(1, 99, default));

    [Theory] [InlineData(0, 1)] [InlineData(-5, 1)] [InlineData(5, -1)] [InlineData(5, 5000)]
    public async Task NonsenseIds_AreRejectedWithoutANetworkCall(int tv, int season)
    {
        var h = new StubHandler(_ => Season());
        Assert.Empty(await Client(h).GetSeasonAsync(tv, season, default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task EntriesWithoutAnEpisodeNumberAreSkipped_AndTheListIsBounded()
    {
        var weird = JsonSerializer.Serialize(new { episodes = new object[] { new { name = "no number" }, new { episode_number = "x", name = "string number" }, new { episode_number = 3, name = "ok" } } });
        Assert.Equal(["ok"], (await Client(new StubHandler(_ => TmdbJson.Ok(weird))).GetSeasonAsync(1, 1, default)).Select(e => e.Name));
        var many = JsonSerializer.Serialize(new { episodes = Enumerable.Range(1, 900).Select(i => new { episode_number = i, name = $"E{i}" }) });
        Assert.Equal(500, (await Client(new StubHandler(_ => TmdbJson.Ok(many))).GetSeasonAsync(2, 1, default)).Count);
    }

    [Fact] public async Task ServerError_Throws_SoTheEndpointCanDegrade()
        => await Assert.ThrowsAsync<TmdbException>(() => Client(new StubHandler(_ => TmdbJson.Status(HttpStatusCode.BadGateway))).GetSeasonAsync(1, 1, default));
}

public sealed class GenreTests : IDisposable
{
    private readonly TestDb _t;
    private readonly LibraryQueries _q;
    public void Dispose() => _t.Dispose();

    private void Add(string title, string kind, int tmdb, string? genres, double? rating, int? season = null, int? ep = null)
    {
        var id = _t.Insert($"{title}-{Guid.NewGuid():N}.mkv", kind, title, 2020);
        using var c = _t.Db.Open();
        var type = kind == "Episode" ? "tv" : "movie";
        Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, tmdb_type=@t, tmdb_id=@i, match_state='matched' WHERE id=@id", ("@s", season), ("@e", ep), ("@t", type), ("@i", tmdb), ("@id", id));
        Database.Execute(c, null, "INSERT OR IGNORE INTO metadata(tmdb_type,tmdb_id,title,year,rating,genres,fetched_at) VALUES(@t,@i,@title,2020,@r,@g,'x')", ("@t", type), ("@i", tmdb), ("@title", title), ("@r", rating), ("@g", genres));
    }

    public GenreTests() { _t = new TestDb(); _q = new LibraryQueries(_t.Db); Seed(); }
    private void Seed()
    {
        Add("Space Epic", "Movie", 1, "Science Fiction, Adventure", 8.1);
        Add("Dark Comedy", "Movie", 2, "Comedy, Drama", 7.2);
        Add("Plain Comedy", "Movie", 3, "Comedy", 6.0);
        Add("Sci Show", "Episode", 4, "Sci-Fi & Fantasy, Drama", 8.8, 1, 1);
        Add("No Genres", "Movie", 5, null, null);
    }

    [Fact]
    public void SummariesCarryRatingAndGenres()
    {
        var item = _q.List(null, "movie", "title", 20, 0).Items.Single(i => i.Title == "Space Epic");
        Assert.Equal((8.1, "Science Fiction, Adventure"), (item.Rating, item.Genres));
    }

    [Theory] [InlineData("Comedy", 2)] [InlineData("comedy", 2)] [InlineData(" Drama ", 2)] [InlineData("Adventure", 1)] [InlineData("Documentary", 0)]
    public void GenreFilter_IsCaseInsensitive_AndMatchesWholeGenresOnly(string genre, int expected)
        => Assert.Equal(expected, _q.List(null, null, "title", 50, 0, genre: genre).Total);

    [Theory] [InlineData("Com")] [InlineData("Sci")] [InlineData("%")] [InlineData("Comedy, Drama")] [InlineData("' OR '1'='1")]
    public void PartialOrHostileGenreText_MatchesNothing(string genre) => Assert.Equal(0, _q.List(null, null, "title", 50, 0, genre: genre).Total);

    [Fact] public void GenreFilter_CombinesWithTypeAndSearch()
    {
        Assert.Equal(["Dark Comedy"], _q.List("dark", "movie", "title", 50, 0, genre: "Drama").Items.Select(i => i.Title));
        Assert.Equal(["Sci Show"], _q.List(null, "series", "title", 50, 0, genre: "Drama").Items.Select(i => i.Title));
    }

    [Fact]
    public void GenreList_CountsEachTitleOnce_MostCommonFirst_PerType()
    {
        var all = _q.Genres(null);
        Assert.Equal(("Comedy", 2), all[0]);
        Assert.Equal(2, all.First(g => g.Name == "Drama").Count);
        Assert.DoesNotContain(all, g => g.Name.Length == 0);
        Assert.Equal(["Sci-Fi & Fantasy", "Drama"], _q.Genres("series").Select(g => g.Name).OrderByDescending(n => n == "Sci-Fi & Fantasy"));
        Assert.DoesNotContain(_q.Genres("movie"), g => g.Name == "Sci-Fi & Fantasy");
    }

    [Fact] public void Stats_CountTitlesFilesAndUnmatched()
    {
        var s = _q.Stats();
        Assert.Equal((4, 1, 1, 5), (s.Movies, s.Shows, s.Episodes, s.Files));
    }
}

public sealed class PolishEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("polishep").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;
    private readonly StubHandler _tmdb = new(r => r.RequestUri!.AbsolutePath.Contains("/season/1")
        ? TmdbJson.Ok(JsonSerializer.Serialize(new { episodes = new[] { new { episode_number = 1, name = "Pilot", overview = "It begins.", air_date = "2022-02-18", runtime = 57, still_path = "/abc123 xyz.jpg" } } }))
        : TmdbJson.Status(HttpStatusCode.BadGateway));

    public PolishEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", Path.Combine(_dir, "lib")).UseSetting("Library:AutoScan", "false")
             .UseSetting("Tmdb:ApiKey", "k").UseSetting("Images:CacheDir", Path.Combine(_dir, "img")).UseSetting("Hls:TempDir", Path.Combine(_dir, "hls"));
            b.ConfigureTestServices(s => s.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => _tmdb));
        });
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task SeasonEndpoint_ReturnsEpisodeInfo_WithImageUrlsThroughOurProxy()
    {
        var j = await _c.GetFromJsonAsync<JsonElement>("/api/items/series/tmdb:95396/season/1");
        var e = j.GetProperty("episodes")[0];
        Assert.Equal(("Pilot", "It begins.", 1), (e.GetProperty("name").GetString(), e.GetProperty("overview").GetString(), e.GetProperty("number").GetInt32()));
        var still = e.GetProperty("stillUrl").GetString()!;
        Assert.StartsWith("/api/tmdb/image?path=", still);
        Assert.Contains("%20", still);                                                  // the path is escaped, not trusted
        Assert.EndsWith("&size=w342", still);
    }

    [Theory] [InlineData("/api/items/series/title:some show/season/1")] [InlineData("/api/items/series/tmdb:abc/season/1")] [InlineData("/api/items/series/tmdb:-5/season/1")]
    public async Task SeasonEndpoint_IsEmptyForUnmatchedOrMalformedKeys(string url)
        => Assert.Equal(0, (await _c.GetFromJsonAsync<JsonElement>(url)).GetProperty("episodes").GetArrayLength());

    [Fact]
    public async Task SeasonEndpoint_DegradesToEmpty_WhenTmdbFails()
    {
        var res = await _c.GetAsync("/api/items/series/tmdb:95396/season/2");            // the stub answers 502 for season 2
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("episodes").GetArrayLength());
    }

    [Fact]
    public async Task GenresEndpoint_Works_AndIgnoresUnknownTypes()
    {
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/genres")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/genres?type=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/items?genre=Comedy&limit=5")).StatusCode);
    }

    [Fact]
    public async Task SystemEndpoint_ReportsLibraryTmdbLiveAndStreams()
    {
        var j = await _c.GetFromJsonAsync<JsonElement>("/api/system");
        Assert.True(j.GetProperty("tmdb").GetProperty("configured").GetBoolean());
        Assert.False(j.GetProperty("live").GetProperty("configured").GetBoolean());
        Assert.Equal(0, j.GetProperty("library").GetProperty("files").GetInt32());
        Assert.Equal(0, j.GetProperty("streams").GetArrayLength());
        Assert.True(j.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(j.GetProperty("encoder").GetProperty("maxStreams").GetInt32() >= 1);
        var text = await _c.GetStringAsync("/api/system");
        Assert.DoesNotContain(_dir, text);                                                 // no server paths
    }

    [Fact]
    public void DirSize_SumsFiles_AndToleratesMissingDirectories()
    {
        var d = Path.Combine(_dir, "sz"); Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllBytes(Path.Combine(d, "a"), new byte[100]); File.WriteAllBytes(Path.Combine(d, "sub", "b"), new byte[250]);
        Assert.Equal(350, SystemEndpoints.DirSize(d));
        Assert.Equal(0, SystemEndpoints.DirSize(Path.Combine(_dir, "nope")));
    }
}
