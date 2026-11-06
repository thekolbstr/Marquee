using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public static class LiveFixtures
{
    // "Now" in every live test: 2026-11-06 15:00 UTC.
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

    public static string M3u(params (string Id, string Name, int No, string Group, string Url)[] ch) =>
        "#EXTM3U x-tvg-url=\"http://src.test/output/epg\"\n" + string.Concat(ch.Select(c =>
            $"#EXTINF:-1 tvg-id=\"{c.Id}\" tvg-name=\"{c.Name}\" tvg-logo=\"http://src.test/logos/{c.Id}.png\" tvg-chno=\"{c.No}\" group-title=\"{c.Group}\",{c.Name}\n{c.Url}\n"));

    public static readonly (string, string, int, string, string)[] DefaultChannels =
    [
        ("1", "CBS 12", 1, "US CBS", "http://src.test/proxy/ts/stream/aaa-111"),
        ("2", "Movie Max", 2, "US Movies", "http://src.test/proxy/ts/stream/bbb-222"),
        ("3", "Sports One", 3, "US Sports", "http://src.test/proxy/ts/stream/ccc-333"),
    ];

    public static string Epg() => """
        <?xml version="1.0"?><tv>
        <programme start="20261106140000 +0000" stop="20261106153000 +0000" channel="2"><title>Movie A</title><desc>First film</desc><category>Movie</category></programme>
        <programme start="20261106153000 +0000" stop="20261106170000 +0000" channel="2"><title>Movie B</title></programme>
        <programme start="20261106170000 +0000" stop="20261106183000 +0000" channel="2"><title>Movie C</title></programme>
        <programme start="20261106143000 +0000" stop="20261106160000 +0000" channel="3"><title>Game</title></programme>
        <programme start="20261106140000 +0000" stop="20261106160000 +0000" channel="99"><title>Unknown channel</title></programme>
        </tv>
        """;

    public static (LiveTvService Svc, StubHandler H, TestDb T, FakeTimeProvider Time) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null, Dictionary<string, string?>? cfg = null, MediaServer.Api.Security.SecretBox? box = null)
    {
        var c = new Dictionary<string, string?> { ["LiveTv:M3uUrl"] = "http://src.test/output/m3u" };
        if (cfg is not null) foreach (var (k, v) in cfg) c[k] = v;
        var t = new TestDb(c);
        var h = new StubHandler(route ?? (r => r.RequestUri!.AbsolutePath switch
        {
            "/output/m3u" => TmdbJson.Ok(M3u(DefaultChannels)),
            "/output/epg" => TmdbJson.Ok(Epg()),
            _ => TmdbJson.Status(HttpStatusCode.NotFound),
        }));
        var time = new FakeTimeProvider(Now);
        return (new LiveTvService(t.Db, new HttpClient(h), t.Config, time, NullLogger<LiveTvService>.Instance, box), h, t, time);
    }
}

public sealed class LiveTvServiceTests : IDisposable
{
    private readonly LiveTvService _s; private readonly StubHandler _h; private readonly TestDb _t; private readonly FakeTimeProvider _time;
    public LiveTvServiceTests() => (_s, _h, _t, _time) = LiveFixtures.Make();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Refresh_LoadsChannels_AndOnlyProgrammesForKnownChannels()
    {
        var r = (await _s.RefreshAsync(default))!;
        Assert.Equal((3, 4, null), (r.Channels, r.Programs, r.EpgError));            // the programme for channel 99 is ignored
        var st = _s.Status();
        Assert.Equal((true, 3, 4, false), (st.Configured, st.Channels, st.Programs, st.Refreshing));
        Assert.Null(st.LastError);
        Assert.Equal(LiveFixtures.Now, st.LastRefresh);
    }

    [Fact]
    public async Task ChannelsAreOrderedByNumber_WithNowAndNext()
    {
        await _s.RefreshAsync(default);
        var list = _s.Channels(null, null, false, 50, 0);
        Assert.Equal(3, list.Total);
        Assert.Equal(["CBS 12", "Movie Max", "Sports One"], list.Items.Select(c => c.Name));
        var movie = list.Items[1];
        Assert.Equal(("Movie A", "Movie B"), (movie.Now!.Title, movie.Next!.Title));
        Assert.Null(list.Items[0].Now);                                              // no guide data for CBS: still listed
        Assert.Equal("Game", list.Items[2].Now!.Title);
        Assert.Null(list.Items[2].Next);
    }

    [Fact]
    public async Task NowAndNextAdvanceWithTheClock()
    {
        await _s.RefreshAsync(default);
        _time.Advance(TimeSpan.FromMinutes(40));                                     // 15:40
        var movie = _s.Channels(null, "movie", false, 10, 0).Items.Single();
        Assert.Equal(("Movie B", "Movie C"), (movie.Now!.Title, movie.Next!.Title));
        _time.Advance(TimeSpan.FromHours(4));                                        // past everything
        Assert.Null(_s.Channels(null, "movie", false, 10, 0).Items.Single().Now);
    }

    [Fact]
    public async Task Guide_ReturnsProgrammesInTheWindow()
    {
        await _s.RefreshAsync(default);
        var from = LiveFixtures.Now.AddMinutes(-30).ToUnixTimeSeconds();            // 14:30
        var g = _s.Guide(null, null, false, from, 3, 50, 0);                         // 14:30 - 17:30
        Assert.Equal(from + 3 * 3600, g.To);
        Assert.Equal(["Movie A", "Movie B", "Movie C"], g.Channels.Single(c => c.Name == "Movie Max").Programs.Select(p => p.Title));
        var narrow = _s.Guide(null, null, false, from, 1, 50, 0);                    // 14:30 - 15:30: Movie A only (B starts exactly at 15:30)
        Assert.Equal(["Movie A"], narrow.Channels.Single(c => c.Name == "Movie Max").Programs.Select(p => p.Title));
        Assert.Equal(12 * 3600, _s.Guide(null, null, false, from, 999, 50, 0).To - from);   // hours are clamped
    }

    [Fact]
    public async Task GroupsSearchAndPaging()
    {
        await _s.RefreshAsync(default);
        Assert.Equal(["US CBS", "US Movies", "US Sports"], _s.Groups().Select(g => g.Name));
        Assert.Equal(["Movie Max"], _s.Channels("US Movies", null, false, 10, 0).Items.Select(c => c.Name));
        Assert.Equal(["Sports One"], _s.Channels(null, "SPORTS", false, 10, 0).Items.Select(c => c.Name));      // case-insensitive
        var page = _s.Channels(null, null, false, 1, 1);
        Assert.Equal((3, "Movie Max"), (page.Total, Assert.Single(page.Items).Name));
    }

    [Theory] [InlineData("%")] [InlineData("_")] [InlineData("' OR 1=1 --")] [InlineData("\\")] [InlineData("a%b")]
    public async Task SearchTextIsNeverAWildcardOrSql(string q)
    {
        await _s.RefreshAsync(default);
        Assert.Equal(0, _s.Channels(null, q, false, 10, 0).Total);
        Assert.Equal(3, _s.Channels(null, null, false, 10, 0).Total);               // the table is intact
    }

    [Fact]
    public async Task Favorites_SurviveARefresh_AndFilter()
    {
        await _s.RefreshAsync(default);
        var id = _s.Channels(null, "movie", false, 5, 0).Items.Single().Id;
        Assert.True(_s.SetFavorite(id, true));
        await _s.RefreshAsync(default);
        var fav = _s.Channels(null, null, true, 10, 0);
        Assert.Equal((1, id, true), (fav.Total, fav.Items[0].Id, fav.Items[0].Favorite));
        Assert.True(_s.SetFavorite(id, false));
        Assert.Equal(0, _s.Channels(null, null, true, 10, 0).Total);
        Assert.False(_s.SetFavorite(9999, true));
    }

    [Fact]
    public async Task ChannelIds_AreStable_AcrossRefreshes_AndReusedWhenAChannelReturns()
    {
        await _s.RefreshAsync(default);
        var ids = _s.Channels(null, null, false, 10, 0).Items.ToDictionary(c => c.Name, c => c.Id);
        await _s.RefreshAsync(default);
        Assert.Equal(ids, _s.Channels(null, null, false, 10, 0).Items.ToDictionary(c => c.Name, c => c.Id));
    }

    [Fact]
    public async Task ChannelsRemovedFromThePlaylist_BecomeUnavailable_ButKeepTheirId()
    {
        await _s.RefreshAsync(default);
        var sports = _s.Channels(null, "sports", false, 5, 0).Items.Single().Id;
        var (s2, _, t2, _) = LiveFixtures.Make(r => r.RequestUri!.AbsolutePath == "/output/m3u" ? TmdbJson.Ok(LiveFixtures.M3u(LiveFixtures.DefaultChannels[..2])) : TmdbJson.Ok(LiveFixtures.Epg()));
        using var _ = t2;
        await s2.RefreshAsync(default);
        await s2.RefreshAsync(default);
        Assert.Equal(2, s2.Status().Channels);
        Assert.Null(s2.GetStream(sports));
        Assert.Null(s2.Channel(sports));
    }

    [Fact]
    public async Task ChangedStreamUrl_DiscardsTheCachedProbe_ButSameUrlKeepsIt()
    {
        await _s.RefreshAsync(default);
        var id = _s.Channels(null, "cbs", false, 5, 0).Items.Single().Id;
        _s.SaveProbe(id, new MediaInfo("mpegts", null, null, null, [new VideoTrack(0, "h264", null, 1920, 1080, 30, "yuv420p", null)], [], []));
        await _s.RefreshAsync(default);
        Assert.Equal("h264", _s.GetStream(id)!.CachedInfo!.Video[0].Codec);          // unchanged URL: cache kept
        _time.Advance(TimeSpan.FromHours(13));
        Assert.Null(_s.GetStream(id)!.CachedInfo);                                    // stale probes are not trusted

        var changed = LiveFixtures.DefaultChannels.Select(c => c.Item1 == "1" ? (c.Item1, c.Item2, c.Item3, c.Item4, "http://src.test/proxy/ts/stream/NEW") : c).ToArray();
        var (s2, _, t2, _) = LiveFixtures.Make(r => TmdbJson.Ok(r.RequestUri!.AbsolutePath == "/output/m3u" ? LiveFixtures.M3u(changed) : LiveFixtures.Epg()));
        using var _ = t2;
        await s2.RefreshAsync(default);
        var id2 = s2.Channels(null, "cbs", false, 5, 0).Items.Single().Id;
        s2.SaveProbe(id2, new MediaInfo("mpegts", null, null, null, [], [], []));
        Assert.NotNull(s2.GetStream(id2)!.CachedInfo);
    }

    [Fact]
    public async Task APlaylistWithNoPlayableChannels_IsAnErrorAndLeavesExistingChannelsAlone()
    {
        await _s.RefreshAsync(default);
        var (s2, _, t2, _) = LiveFixtures.Make(r => TmdbJson.Ok("#EXTM3U\n#EXTINF:-1,Bad\nfile:///etc/passwd\n"));
        using var _ = t2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.RefreshAsync(default));
        Assert.Equal("The playlist contained no playable channels", s2.Status().LastError);
        Assert.Equal(3, _s.Status().Channels);
    }

    [Fact]
    public async Task DownloadFailure_ReportsAFriendlyErrorWithoutTheUrl()
    {
        var (s2, _, t2, _) = LiveFixtures.Make(r => TmdbJson.Status(HttpStatusCode.InternalServerError));
        using var _ = t2;
        await Assert.ThrowsAsync<HttpRequestException>(() => s2.RefreshAsync(default));
        Assert.Equal("The playlist could not be downloaded", s2.Status().LastError);
        Assert.DoesNotContain("src.test", s2.Status().LastError);
    }

    [Fact]
    public async Task AGuideFailure_DoesNotLoseTheChannels()
    {
        var (s2, _, t2, _) = LiveFixtures.Make(r => r.RequestUri!.AbsolutePath == "/output/m3u" ? TmdbJson.Ok(LiveFixtures.M3u(LiveFixtures.DefaultChannels)) : TmdbJson.Status(HttpStatusCode.BadGateway));
        using var _ = t2;
        var res = (await s2.RefreshAsync(default))!;
        Assert.Equal((3, 0), (res.Channels, res.Programs));
        Assert.Equal("The programme guide could not be loaded", res.EpgError);
        Assert.Equal(3, s2.Status().Channels);
    }

    [Fact]
    public async Task GzipCompressedGuides_WorkWithOrWithoutAContentEncodingHeader()
    {
        byte[] Gz(string s) { using var ms = new MemoryStream(); using (var z = new GZipStream(ms, CompressionLevel.Fastest)) z.Write(Encoding.UTF8.GetBytes(s)); return ms.ToArray(); }
        var (s2, _, t2, _) = LiveFixtures.Make(r => r.RequestUri!.AbsolutePath == "/output/m3u" ? TmdbJson.Ok(LiveFixtures.M3u(LiveFixtures.DefaultChannels))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Gz(LiveFixtures.Epg())) });
        using var _ = t2;
        Assert.Equal(4, (await s2.RefreshAsync(default))!.Programs);
    }

    [Fact]
    public async Task OnlyOneRefreshAtATime()
    {
        var gate = new TaskCompletionSource();
        var (s2, _, t2, _) = LiveFixtures.Make(r => { gate.Task.Wait(); return r.RequestUri!.AbsolutePath == "/output/m3u" ? TmdbJson.Ok(LiveFixtures.M3u(LiveFixtures.DefaultChannels)) : TmdbJson.Ok(LiveFixtures.Epg()); });
        using var _ = t2;
        var first = Task.Run(() => s2.RefreshAsync(default));
        await Task.Delay(300);
        Assert.True(s2.Status().Refreshing);
        Assert.Null(await s2.RefreshAsync(default));
        gate.SetResult();
        Assert.NotNull(await first);
    }

    [Fact]
    public async Task NotConfigured_Throws()
    {
        var (s2, _, t2, _) = LiveFixtures.Make(cfg: new() { ["LiveTv:M3uUrl"] = "" });
        using var _ = t2;
        Assert.False(s2.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.RefreshAsync(default));
    }

    [Theory] [InlineData("file:///etc/passwd")] [InlineData("ftp://h/x")] [InlineData("not a url")]
    public async Task OnlyHttpSourcesAreFetched(string url)
    {
        var (s2, h, t2, _) = LiveFixtures.Make(cfg: new() { ["LiveTv:M3uUrl"] = url });
        using var _ = t2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.RefreshAsync(default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task TheStreamUrlNeverAppearsInAnythingTheApiReturns()
    {
        await _s.RefreshAsync(default);
        var id = _s.Channels(null, "movie", false, 5, 0).Items.Single().Id;
        var json = string.Concat(JsonSerializer.Serialize(_s.Channels(null, null, false, 50, 0)), JsonSerializer.Serialize(_s.Guide(null, null, false, LiveFixtures.Now.ToUnixTimeSeconds(), 6, 50, 0)),
            JsonSerializer.Serialize(_s.Channel(id)), JsonSerializer.Serialize(_s.Groups()), JsonSerializer.Serialize(_s.Status()));
        Assert.DoesNotContain("src.test", json);
        Assert.DoesNotContain("proxy/ts", json);
        Assert.DoesNotContain("bbb-222", json);
        Assert.Contains($"/api/live/channels/{id}/logo", json);                       // logos go through our own proxy
    }

    [Fact]
    public async Task ChannelDetail_ListsUpcomingProgrammes()
    {
        await _s.RefreshAsync(default);
        var d = _s.Channel(_s.Channels(null, "movie", false, 5, 0).Items.Single().Id)!;
        Assert.Equal("Movie A", d.Channel.Now!.Title);
        Assert.Equal(["Movie B", "Movie C"], d.Upcoming.Select(p => p.Title));
        Assert.Null(_s.Channel(99999));
    }

    [Theory]
    [InlineData("http://192.0.2.10:9191/proxy/ts/stream/abc?x=1", "http://iptv.internal:9191", "http://iptv.internal:9191/proxy/ts/stream/abc?x=1")]
    [InlineData("http://a/b", "https://z:8443", "https://z:8443/b")]
    [InlineData("http://a/b", "", "http://a/b")]
    [InlineData("http://a/b", "not a url", "http://a/b")]
    [InlineData("http://a/b", "ftp://x", "http://a/b")]
    [InlineData(null, "http://x", null)]
    public void StreamOriginRewriting(string? url, string? origin, string? expected) => Assert.Equal(expected, LiveTvService.Rewrite(url, origin));

    [Fact]
    public async Task StreamOriginConfig_ReroutesEveryStreamAndLogoUrl()
    {
        var (s2, _, t2, _) = LiveFixtures.Make(cfg: new() { ["LiveTv:StreamOrigin"] = "http://svc.internal:9191" });
        using var _ = t2;
        await s2.RefreshAsync(default);
        var id = s2.Channels(null, "cbs", false, 5, 0).Items.Single().Id;
        Assert.Equal("http://svc.internal:9191/proxy/ts/stream/aaa-111", s2.GetStream(id)!.Url);
        Assert.StartsWith("http://svc.internal:9191/logos/", s2.LogoSource(id));
    }

    [Fact]
    public void ADownloadLargerThanAllowedIsRefused()
    {
        var s = new LimitedReadStream(new MemoryStream(new byte[100]), 50);
        Assert.Throws<InvalidDataException>(() => s.CopyTo(Stream.Null));
        Assert.Equal(100, new LimitedReadStream(new MemoryStream(new byte[100]), 100).Let(x => { var ms = new MemoryStream(); x.CopyTo(ms); return (int)ms.Length; }));
    }
}

internal static class FunctionalExt { public static R Let<T, R>(this T x, Func<T, R> f) => f(x); }
