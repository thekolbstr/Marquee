using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Probing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class LiveTvEndpointTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly string _dir = Directory.CreateTempSubdirectory("liveep").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;
    private readonly StubHandler _source;
    private readonly CountingProbe _probe = new();

    public LiveTvEndpointTests()
    {
        var script = Path.Combine(_dir, "ffmpeg.sh");
        File.WriteAllText(script, "#!/bin/sh\nfor last; do :; done\nprintf '#EXTM3U\\n#EXTINF:2.0,\\nseg00000.ts\\n' > \"$last\"\nprintf SEGMENT > \"$(dirname \"$last\")/seg00000.ts\"\nexec sleep 120\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _source = new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/output/m3u" => TmdbJson.Ok(LiveFixtures.M3u(LiveFixtures.DefaultChannels)),
            "/output/epg" => TmdbJson.Ok(LiveFixtures.Epg()),
            "/logos/1.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/png") } } },
            "/logos/2.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<svg onload=alert(1)/>", System.Text.Encoding.UTF8, "image/svg+xml") },
            "/logos/3.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[600_000]) { Headers = { ContentType = new("image/png") } } },
            _ => TmdbJson.Status(HttpStatusCode.NotFound),
        });

        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false")
             .UseSetting("LiveTv:M3uUrl", "http://src.test/output/m3u").UseSetting("LiveTv:AutoRefresh", "false")
             .UseSetting("Images:CacheDir", Path.Combine(_dir, "img")).UseSetting("Tools:FfmpegPath", script).UseSetting("Hls:TempDir", Path.Combine(_dir, "hls"));
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(sp => new LiveTvService(sp.GetRequiredService<Database>(), new HttpClient(_source), sp.GetRequiredService<IConfiguration>(),
                    new FakeTimeProvider(LiveFixtures.Now), NullLogger<LiveTvService>.Instance));
                s.AddSingleton(sp => new LiveLogoCache(new HttpClient(_source), sp.GetRequiredService<IConfiguration>(), TimeProvider.System));
                s.AddSingleton<IMediaProbe>(_probe);
            });
        });
        _c = _f.CreateClient();
    }

    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class CountingProbe : IMediaProbe
    {
        public int LiveCalls;
        public Task<MediaInfo> ProbeAsync(string p, CancellationToken ct) => throw new NotSupportedException();
        public Task<MediaInfo> ProbeLiveAsync(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref LiveCalls);
            if (url.Contains("ccc-333")) throw new ProbeFailedException("The stream could not be opened: <stream>: Connection refused");
            return Task.FromResult(new MediaInfo("mpegts", null, null, null, [new VideoTrack(0, "h264", null, 1920, 1080, 30, "yuv420p", null, null, "progressive")],
                [new AudioTrack(1, "ac3", 2, null, 48000, null, null, true, null)], []));
        }
    }

    private async Task<long> IdOf(string name)
    {
        var l = await _c.GetFromJsonAsync<JsonElement>($"/api/live/channels?search={Uri.EscapeDataString(name)}");
        return l.GetProperty("items")[0].GetProperty("id").GetInt64();
    }
    private async Task Refresh() => Assert.Equal(HttpStatusCode.OK, (await _c.PostAsync("/api/live/refresh", null)).StatusCode);

    [Fact]
    public async Task Status_AndRefresh()
    {
        Assert.True((await _c.GetFromJsonAsync<JsonElement>("/api/live/status")).GetProperty("configured").GetBoolean());
        var res = await _c.PostAsync("/api/live/refresh", null);
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((3, 4), (j.GetProperty("channels").GetInt32(), j.GetProperty("programs").GetInt32()));
        Assert.Equal(3, (await _c.GetFromJsonAsync<JsonElement>("/api/live/status")).GetProperty("channels").GetInt32());
    }

    [Fact]
    public async Task BackgroundRefresh_Returns202()
        => Assert.Equal(HttpStatusCode.Accepted, (await _c.PostAsync("/api/live/refresh?background=true", null)).StatusCode);

    [Fact]
    public async Task ChannelsGroupsGuideAndDetail()
    {
        await Refresh();
        var list = await _c.GetFromJsonAsync<JsonElement>("/api/live/channels");
        Assert.Equal(3, list.GetProperty("total").GetInt32());
        var movie = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Movie Max");
        Assert.Equal("Movie A", movie.GetProperty("now").GetProperty("title").GetString());
        Assert.Equal("Movie B", movie.GetProperty("next").GetProperty("title").GetString());

        Assert.Equal(["US CBS", "US Movies", "US Sports"], (await _c.GetFromJsonAsync<JsonElement>("/api/live/groups")).EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        Assert.Equal(1, (await _c.GetFromJsonAsync<JsonElement>("/api/live/channels?group=US%20Sports")).GetProperty("total").GetInt32());

        var guide = await _c.GetFromJsonAsync<JsonElement>($"/api/live/guide?from={LiveFixtures.Now.AddMinutes(-30).ToUnixTimeSeconds()}&hours=3");
        var mm = guide.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Movie Max").GetProperty("programs");
        Assert.Equal(3, mm.GetArrayLength());

        var id = movie.GetProperty("id").GetInt64();
        var d = await _c.GetFromJsonAsync<JsonElement>($"/api/live/channels/{id}");
        Assert.Equal(2, d.GetProperty("upcoming").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/live/channels/99999")).StatusCode);
    }

    [Fact]
    public async Task Favorites_RoundTrip()
    {
        await Refresh();
        var id = await IdOf("Movie");
        Assert.Equal(HttpStatusCode.NoContent, (await _c.PostAsync($"/api/live/channels/{id}/favorite", null)).StatusCode);
        var fav = await _c.GetFromJsonAsync<JsonElement>("/api/live/channels?favorites=true");
        Assert.Equal((1, true), (fav.GetProperty("total").GetInt32(), fav.GetProperty("items")[0].GetProperty("favorite").GetBoolean()));
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/live/channels/{id}/favorite")).StatusCode);
        Assert.Equal(0, (await _c.GetFromJsonAsync<JsonElement>("/api/live/channels?favorites=true")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsync("/api/live/channels/99999/favorite", null)).StatusCode);
    }

    [Theory] [InlineData("/api/live/channels")] [InlineData("/api/live/guide")] [InlineData("/api/live/groups")] [InlineData("/api/live/status")]
    public async Task RawResponsesNeverContainTheSourceAddressOrStreamUrls(string url)
    {
        await Refresh();
        var text = await _c.GetStringAsync(url);
        Assert.DoesNotContain("src.test", text);
        Assert.DoesNotContain("proxy/ts", text);
        Assert.DoesNotContain("aaa-111", text);
    }

    [Fact]
    public async Task SearchWildcardsAndHostileLimitsAreHarmless()
    {
        await Refresh();
        Assert.Equal(0, (await _c.GetFromJsonAsync<JsonElement>("/api/live/channels?search=%25")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/live/channels?limit=-5&offset=-9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/live/channels?limit=99999999")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/api/live/guide?hours=-1&from=-5&limit=0")).StatusCode);
    }

    [Fact]
    public async Task LogoProxy_ServesPngs_WithAStrictCsp_AndCachesThem()
    {
        await Refresh();
        var id = await IdOf("CBS");
        var res = await _c.GetAsync($"/api/live/channels/{id}/logo");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await res.Content.ReadAsByteArrayAsync());
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        var calls = _source.Requests.Count(r => r.RequestUri!.AbsolutePath == "/logos/1.png");
        await _c.GetAsync($"/api/live/channels/{id}/logo");
        Assert.Equal(calls, _source.Requests.Count(r => r.RequestUri!.AbsolutePath == "/logos/1.png"));      // second view: from disk
    }

    [Fact]
    public async Task LogoProxy_RefusesSvgAndOversizedImages_AndUnknownChannels()
    {
        await Refresh();
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/live/channels/{await IdOf("Movie")}/logo")).StatusCode);    // svg can carry script
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/live/channels/{await IdOf("Sports")}/logo")).StatusCode);   // > 512 KB
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/live/channels/99999/logo")).StatusCode);
    }

    [Fact]
    public async Task Start_ReturnsAnHlsSession_WithoutRevealingTheStream_AndReusesTheCachedProbe()
    {
        await Refresh();
        var id = await IdOf("CBS");
        var res = await _c.PostAsJsonAsync($"/api/live/channels/{id}/start", new { video = "h264", audio = "aac,mp3", containers = "mp4" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("src.test", body); Assert.DoesNotContain("aaa-111", body);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("remux", true), (j.GetProperty("mode").GetString(), j.GetProperty("live").GetBoolean()));
        Assert.StartsWith("Live", j.GetProperty("reason").GetString());

        var playlist = await _c.GetAsync(j.GetProperty("playlistUrl").GetString());          // served by the shared HLS endpoints
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Contains("#EXTINF", await playlist.Content.ReadAsStringAsync());

        Assert.Equal(1, _probe.LiveCalls);
        await _c.PostAsJsonAsync($"/api/live/channels/{id}/start", new { });                 // second start: probe result is cached
        Assert.Equal(1, _probe.LiveCalls);
    }

    [Fact]
    public async Task ChangingChannel_EndsTheFirstSession()
    {
        await Refresh();
        var a = JsonDocument.Parse(await (await _c.PostAsJsonAsync($"/api/live/channels/{await IdOf("CBS")}/start", new { })).Content.ReadAsStringAsync()).RootElement;
        await _c.PostAsJsonAsync($"/api/live/channels/{await IdOf("Movie")}/start", new { });
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync(a.GetProperty("playlistUrl").GetString())).StatusCode);
    }

    [Fact]
    public async Task AChannelThatCannotBeOpened_Is502_WithAFriendlyMessageAndNoUrl()
    {
        await Refresh();
        var res = await _c.PostAsJsonAsync($"/api/live/channels/{await IdOf("Sports")}/start", new { });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("src.test", text); Assert.DoesNotContain("ccc-333", text);
    }

    [Fact]
    public async Task StartingAnUnknownChannel_Is404()
        => Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsJsonAsync("/api/live/channels/424242/start", new { })).StatusCode);

    [Fact]
    public async Task Start_ToleratesAnEmptyBody()
    {
        await Refresh();
        Assert.Equal(HttpStatusCode.OK, (await _c.PostAsync($"/api/live/channels/{await IdOf("CBS")}/start", null)).StatusCode);
    }
}

public sealed class LiveTvUnconfiguredTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("liveoff").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;
    public LiveTvUnconfiguredTests()
    {
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false"));
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task WithoutASource_TheApiSaysSo_AndRefreshIsA503()
    {
        Assert.False((await _c.GetFromJsonAsync<JsonElement>("/api/live/status")).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _c.PostAsync("/api/live/refresh", null)).StatusCode);
        Assert.Equal(0, (await _c.GetFromJsonAsync<JsonElement>("/api/live/channels")).GetProperty("total").GetInt32());
    }
}
