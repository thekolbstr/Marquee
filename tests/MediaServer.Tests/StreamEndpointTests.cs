using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using MediaServer.Api.Media;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Tests;

public sealed class StreamEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ms-tests").FullName;
    private readonly byte[] _data = new byte[300_000]; // spans several 64 KiB buffers
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public StreamEndpointTests()
    {
        new Random(42).NextBytes(_data);
        File.WriteAllBytes(Path.Combine(_dir, "a.mp4"), _data);
        File.WriteAllBytes(Path.Combine(_dir, "empty.mp4"), []);
        var a = Path.Combine(_dir, "a.mp4");
        var empty = Path.Combine(_dir, "empty.mp4");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false");
            b.ConfigureTestServices(s => s.AddSingleton<IMediaResolver>(new FixedResolver(new() { [1] = a, [2] = empty })));
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Get(string range, int id = 1, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, $"/stream/{id}");
        if (range.Length > 0) req.Headers.TryAddWithoutValidation("Range", range);
        return _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    [Fact]
    public async Task Full_200_AdvertisesRanges()
    {
        var res = await Get("");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("bytes", res.Headers.AcceptRanges.Single());
        Assert.Equal("video/mp4", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_data.Length, res.Content.Headers.ContentLength);
        Assert.Equal(_data, await res.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=70000-140000", 70000, 140000)]   // crosses buffer boundaries
    [InlineData("bytes=250000-", 250000, 299999)]
    [InlineData("bytes=-1234", 298766, 299999)]
    [InlineData("bytes=299000-999999", 299000, 299999)] // clamped
    public async Task Partial_206_ExactBytesAndHeaders(string range, int start, int end)
    {
        var res = await Get(range);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(start, end, _data.Length), res.Content.Headers.ContentRange);
        Assert.Equal(end - start + 1, res.Content.Headers.ContentLength);
        Assert.Equal(_data[start..(end + 1)], await res.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=300000-")]
    [InlineData("bytes=-0")]
    public async Task Unsatisfiable_416_WithStarContentRange(string range)
    {
        var res = await Get(range);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, res.StatusCode);
        Assert.Equal("bytes */300000", res.Content.Headers.ContentRange!.ToString());
    }

    [Theory]
    [InlineData("bytes=0-5,10-15")]
    [InlineData("bytes=garbage")]
    public async Task IgnoredRange_FallsBackTo200(string range)
    {
        var res = await Get(range);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(_data.Length, (await res.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Head_ReturnsHeadersNoBody()
    {
        var res = await Get("bytes=10-19", method: HttpMethod.Head);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(10, res.Content.Headers.ContentLength);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task IfRange_Mismatch_IgnoresRange()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/stream/1");
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        req.Headers.TryAddWithoutValidation("If-Range", "\"stale\"");
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(_data.Length, res.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task IfRange_Match_HonoursRange()
    {
        var etag = (await Get("")).Headers.ETag!.ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, "/stream/1");
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        req.Headers.TryAddWithoutValidation("If-Range", etag);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
    }

    [Fact]
    public async Task EmptyFile_200EmptyBody_AndRangeIs416()
    {
        Assert.Equal(HttpStatusCode.OK, (await Get("", 2)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await Get("bytes=0-", 2)).StatusCode);
    }

    [Theory]
    [InlineData("/stream/99")]
    [InlineData("/stream/0")]
    [InlineData("/stream/-1")]
    [InlineData("/stream/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/stream/%2Fetc%2Fpasswd")]
    public async Task UnknownOrNonIntegerIds_404(string url)
        => Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);

    private sealed class FixedResolver(Dictionary<long, string> map) : IMediaResolver
    {
        public string? ResolvePath(long id) => map.GetValueOrDefault(id);
    }
}

public sealed class WebClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("web").FullName;
    private readonly WebApplicationFactory<Program> _factory;
    public WebClientTests() => _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false"));
    public void Dispose() { _factory.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/app.js", "javascript")]
    [InlineData("/live.js", "javascript")]
    [InlineData("/settings.js", "javascript")]
    [InlineData("/auth.js", "javascript")]
    [InlineData("/extras.js", "javascript")]
    [InlineData("/sw.js", "javascript")]
    [InlineData("/live.css", "css")]
    [InlineData("/style.css", "text/css")]
    public async Task ServesClientAssets(string url, string type)
    {
        var res = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(type, res.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task EmptyLibrary_ReturnsEmptyList()
    {
        var res = await _factory.CreateClient().GetAsync("/api/items?search=x&type=movie&sort=bogus&limit=-3");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"total\":0", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/items/movie/1")]
    [InlineData("/api/items/series/nope")]
    [InlineData("/api/media/1/play")]
    public async Task UnknownItems404(string url) => Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(url)).StatusCode);

    [Fact]
    public async Task ClientUsesNoHtmlInjectionSinks()
        => Assert.DoesNotMatch(@"\.(innerHTML|outerHTML)\s*=|insertAdjacentHTML|document\.write",
            await _factory.CreateClient().GetStringAsync("/app.js"));
}
