using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Security;
using MediaServer.Api.Trakt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class TraktTests : IDisposable
{
    private sealed class Handler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            var (code, json) = respond(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private readonly TestDb _t = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero));
    public void Dispose() => _t.Dispose();

    private TraktService Svc(Handler h, bool configured = true)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Trakt:ClientId"] = configured ? "cid" : null, ["Trakt:ClientSecret"] = configured ? "csecret" : null }).Build();
        return new TraktService(_t.Db, new HttpClient(h), cfg, new SecretBox(new byte[32]), _time, NullLogger<TraktService>.Instance);
    }

    private const string Token = """{ "access_token": "ACCESS-TOKEN", "refresh_token": "REFRESH-TOKEN", "expires_in": 7776000 }""";

    private long Add(string kind, long? tmdb, int? season = null, int? episode = null, double duration = 6000)
    {
        var id = _t.Insert($"/m/{Guid.NewGuid():N}.mkv", kind, "T", 2020);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET tmdb_id=@t, tmdb_type=@y, season=@s, episode=@e, duration_seconds=@d WHERE id=@i", ("@t", tmdb), ("@y", kind == "Movie" ? "movie" : "tv"), ("@s", season), ("@e", episode), ("@d", duration), ("@i", id));
        return id;
    }

    private async Task Connect(TraktService svc, long user = 1)
    {
        Assert.Equal("connected", await svc.PollAsync(user, "dc", default));
    }

    [Fact] public async Task Unconfigured_RefusesToStart_AndIsNeverConnected()
    {
        var svc = Svc(new Handler((_, _) => (HttpStatusCode.OK, "{}")), configured: false);
        Assert.False(svc.IsConfigured);
        await Assert.ThrowsAsync<TraktException>(() => svc.StartAsync(default));
        svc.QueueWatched(1, [1]);   // does nothing, and does not throw
    }

    [Fact] public async Task DeviceFlow_StartsWithTheClientId_AndStoresTheTokensEncrypted()
    {
        var polls = 0;
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/oauth/device/code" => (HttpStatusCode.OK, """{ "device_code": "dc", "user_code": "ABCD1234", "verification_url": "https://trakt.tv/activate", "expires_in": 600, "interval": 5 }"""),
            "/oauth/device/token" => ++polls < 2 ? (HttpStatusCode.BadRequest, "") : (HttpStatusCode.OK, Token),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var svc = Svc(h);
        var d = await svc.StartAsync(default);
        Assert.Equal(("ABCD1234", "dc", 5), (d.UserCode, d.DeviceCode, d.IntervalSeconds));
        Assert.Contains("\"client_id\":\"cid\"", h.Seen[0].Body); Assert.DoesNotContain("csecret", h.Seen[0].Body);   // the secret is only for the token exchange
        Assert.Equal("pending", await svc.PollAsync(1, "dc", default)); Assert.False(svc.IsConnected(1));
        Assert.Equal("connected", await svc.PollAsync(1, "dc", default)); Assert.True(svc.IsConnected(1));
        using var c = _t.Db.Open();
        var stored = Convert.ToString(Database.Scalar(c, "SELECT access_token || refresh_token FROM trakt_accounts"))!;
        Assert.DoesNotContain("ACCESS-TOKEN", stored); Assert.DoesNotContain("REFRESH-TOKEN", stored); Assert.Contains("enc:v1:", stored);
        Assert.False(svc.IsConnected(2));                                          // per person
        svc.Disconnect(1); Assert.False(svc.IsConnected(1));
    }

    [Theory] [InlineData(HttpStatusCode.NotFound, "expired")] [InlineData(HttpStatusCode.Gone, "expired")] [InlineData((HttpStatusCode)418, "denied")] [InlineData((HttpStatusCode)429, "pending")]
    public async Task Poll_MapsTraktAnswers(HttpStatusCode code, string expected)
        => Assert.Equal(expected, await Svc(new Handler((_, _) => (code, ""))).PollAsync(1, "dc", default));

    [Fact] public async Task FinishingAMovie_AddsItToTraktHistory_WithTheRightHeadersAndIds()
    {
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath == "/oauth/device/token" ? (HttpStatusCode.OK, Token) : (HttpStatusCode.Created, "{}"));
        var svc = Svc(h); await Connect(svc);
        var movie = Add("Movie", 603); var ep1 = Add("Episode", 1399, 1, 1); var ep2 = Add("Episode", 1399, 1, 2); var unmatched = Add("Movie", null);
        await svc.PushWatchedAsync(1, [movie, ep1, ep2, unmatched], default);
        var (req, body) = h.Seen.Last();
        Assert.Equal("/sync/history", req.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer ACCESS-TOKEN", req.Headers.Authorization!.ToString()); Assert.Equal("cid", req.Headers.GetValues("trakt-api-key").Single()); Assert.Equal("2", req.Headers.GetValues("trakt-api-version").Single());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(603, doc.RootElement.GetProperty("movies")[0].GetProperty("ids").GetProperty("tmdb").GetInt64());
        var show = doc.RootElement.GetProperty("shows")[0];
        Assert.Equal(1399, show.GetProperty("ids").GetProperty("tmdb").GetInt64());
        Assert.Equal(2, show.GetProperty("seasons")[0].GetProperty("episodes").GetArrayLength());
        Assert.Single(doc.RootElement.GetProperty("movies").EnumerateArray());                   // the file with no TMDB match is not sent
        Assert.Contains("2026-11-12T20:00:00.000Z", body);
        Assert.DoesNotContain("/m/", body);                                                      // no file names or paths leave the server
    }

    [Fact] public async Task NothingIsSent_ForSomeoneNotConnected_OrWhenNothingIsMatched()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK, "{}"));
        var svc = Svc(h);
        await svc.PushWatchedAsync(1, [Add("Movie", 5)], default);
        Assert.Empty(h.Seen);
        var h2 = new Handler((req, _) => req.RequestUri!.AbsolutePath == "/oauth/device/token" ? (HttpStatusCode.OK, Token) : (HttpStatusCode.OK, "{}"));
        var s2 = Svc(h2); await Connect(s2);
        await s2.PushWatchedAsync(1, [Add("Movie", null)], default);
        Assert.DoesNotContain(h2.Seen, s => s.Req.RequestUri!.AbsolutePath == "/sync/history");
    }

    [Fact] public async Task AnExpiredLinkOnTrakt_DisconnectsInsteadOfFailingForever()
    {
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath switch { "/oauth/device/token" => (HttpStatusCode.OK, """{ "access_token": "A", "refresh_token": "R", "expires_in": 3600 }"""), "/oauth/token" => (HttpStatusCode.BadRequest, ""), _ => (HttpStatusCode.OK, "{}") });
        var svc = Svc(h); await Connect(svc);                                       // expires in an hour, so it must be refreshed first
        await svc.PushWatchedAsync(1, [Add("Movie", 7)], default);
        Assert.False(svc.IsConnected(1));
    }

    [Fact] public async Task Import_MarksWatchedTitlesWatched_AndLeavesTheRestAlone()
    {
        const string movies = """[ { "plays": 1, "movie": { "title": "A", "ids": { "tmdb": 603 } } } ]""";
        const string shows = """[ { "show": { "ids": { "tmdb": 1399 } }, "seasons": [ { "number": 1, "episodes": [ { "number": 1 }, { "number": 3 } ] } ] } ]""";
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath switch { "/oauth/device/token" => (HttpStatusCode.OK, Token), "/sync/watched/movies" => (HttpStatusCode.OK, movies), "/sync/watched/shows" => (HttpStatusCode.OK, shows), _ => (HttpStatusCode.OK, "{}") });
        var svc = Svc(h); await Connect(svc);
        var m = Add("Movie", 603); var other = Add("Movie", 999); var e1 = Add("Episode", 1399, 1, 1); var e2 = Add("Episode", 1399, 1, 2); var e3 = Add("Episode", 1399, 1, 3);
        using (var c = _t.Db.Open()) Database.Execute(c, null, "INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(1,@m,10,6000,0,'x')", ("@m", other));
        var r = await svc.ImportAsync(1, default);
        Assert.Equal((1, 2), (r.Movies, r.Episodes));
        using var c2 = _t.Db.Open();
        Assert.Equal(3L, Database.Scalar(c2, "SELECT COUNT(*) FROM media_progress WHERE watched=1"));
        Assert.Equal(0L, Database.Scalar(c2, $"SELECT COUNT(*) FROM media_progress WHERE watched=1 AND media_id IN ({other},{e2})"));
        Assert.Equal(0L, Database.Scalar(c2, "SELECT COUNT(*) FROM media_progress WHERE user_id<>1"));
        var again = await svc.ImportAsync(1, default);                              // importing twice changes nothing
        Assert.Equal((0, 0), (again.Movies, again.Episodes));
    }

    [Fact] public async Task Import_NeverUnwatches()
    {
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath switch { "/oauth/device/token" => (HttpStatusCode.OK, Token), _ => (HttpStatusCode.OK, "[]") });
        var svc = Svc(h); await Connect(svc);
        var m = Add("Movie", 1);
        using (var c = _t.Db.Open()) Database.Execute(c, null, "INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(1,@m,6000,6000,1,'x')", ("@m", m));
        await svc.ImportAsync(1, default);
        using var c2 = _t.Db.Open();
        Assert.Equal(1L, Database.Scalar(c2, "SELECT COUNT(*) FROM media_progress WHERE watched=1"));
    }

    [Fact] public async Task ImportWithoutAConnection_SaysSo()
        => await Assert.ThrowsAsync<TraktException>(() => Svc(new Handler((_, _) => (HttpStatusCode.OK, "[]"))).ImportAsync(1, default));

    [Fact] public async Task ProgressService_AddsToTrakt_OnlyWhenATitleBecomesWatched()
    {
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath == "/oauth/device/token" ? (HttpStatusCode.OK, Token) : (HttpStatusCode.Created, "{}"));
        var svc = Svc(h); await Connect(svc);
        var progress = new ProgressService(_t.Db, _time, _t.Config, null, svc);
        var m = Add("Movie", 603);
        progress.Save(m, 100, 6000, null);                                          // not finished
        progress.Save(m, 5990, 6000, null);                                         // finished: this one counts
        progress.Save(m, 5995, 6000, null);                                         // still watched: must not add it again
        for (var i = 0; i < 50 && h.Seen.Count(s => s.Req.RequestUri!.AbsolutePath == "/sync/history") < 1; i++) await Task.Delay(50);
        await Task.Delay(150);
        Assert.Equal(1, h.Seen.Count(s => s.Req.RequestUri!.AbsolutePath == "/sync/history"));
    }
}
