using System.Text;
using System.Text.Json;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Processes;

namespace MediaServer.Tests;

public sealed class SubtitleDownloadTests : IDisposable
{
    private readonly TestDb _t = new();
    private sealed class Resolver(Func<long, string?> f) : IMediaResolver { public string? ResolvePath(long id) => f(id); }
    private sealed class NoRunner : IProcessRunner { public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, CancellationToken ct) => Task.FromResult(new ProcessResult(1, "", "")); }
    private sealed class Fake(byte[]? file = null) : ISubtitleProvider
    {
        public int Downloads; public SubtitleQuery? Last;
        public bool IsConfigured => true;
        public Task<IReadOnlyList<SubtitleResult>> SearchAsync(SubtitleQuery q, string language, CancellationToken ct) { Last = q; return Task.FromResult<IReadOnlyList<SubtitleResult>>([new("42", "Film.srt", language, 10, false, null, true)]); }
        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct) { Downloads++; return Task.FromResult(file ?? Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello there\n")); }
    }

    private (SubtitleService Subs, long Movie, long Episode) Build()
    {
        var movie = _t.Insert(Path.Combine(_t.Dir, "lib", "Film (2020).mkv"), "Movie", "Film", 2020);
        var ep = _t.Insert(Path.Combine(_t.Dir, "lib", "Show S02E05.mkv"), "Episode", "Show", 2019);
        using (var c = _t.Db.Open()) MediaServer.Api.Data.Database.Execute(c, null, "UPDATE media_files SET season=2, episode=5 WHERE id=@i", ("@i", ep));
        var subs = new SubtitleService(_t.Db, new Resolver(id => id == movie || id == ep ? Path.Combine(_t.Dir, "lib", $"{id}.mkv") : null), new NoRunner(), _t.Config);
        return (subs, movie, ep);
    }
    public void Dispose() => _t.Dispose();

    [Fact] public void QueryFor_UsesTitleYear_AndSeasonEpisodeForShows()
    {
        var (subs, movie, ep) = Build();
        Assert.Equal(new SubtitleQuery("Film", 2020, null, null), subs.QueryFor(movie));
        Assert.Equal(new SubtitleQuery("Show", 2019, 2, 5), subs.QueryFor(ep));
        Assert.Null(subs.QueryFor(999));
    }

    [Fact] public async Task Download_IsStoredInTheDataFolder_NotNextToTheVideo_AndListedAsATrack()
    {
        var (subs, movie, _) = Build();
        var fake = new Fake();
        var d = new SubtitleDownloads(subs, fake, TimeProvider.System);
        var id = await d.DownloadAsync(movie, "42", "en", CancellationToken.None);
        Assert.Equal("d0", id);
        var track = Assert.Single(subs.List(movie), t => t.Source == "download");
        Assert.Equal("d0", track.Id); Assert.Equal("en", track.Language); Assert.Contains("downloaded", track.Label);
        var saved = Directory.GetFiles(Path.Combine(_t.Dir, "subtitles"), "*.srt", SearchOption.AllDirectories);
        Assert.Single(saved); Assert.Contains("en-42.srt", saved[0]);
        Assert.False(Directory.Exists(Path.Combine(_t.Dir, "lib")));                       // nothing was written beside the media
    }

    [Fact] public async Task Download_NormalisesEncoding_AndRefusesNonSubtitles()
    {
        var (subs, movie, _) = Build();
        var latin1 = Encoding.Latin1.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nCafé olé\n");   // not valid UTF-8
        await new SubtitleDownloads(subs, new Fake(latin1), TimeProvider.System).DownloadAsync(movie, "7", "fr", CancellationToken.None);
        Assert.Contains("Café olé", File.ReadAllText(Directory.GetFiles(Path.Combine(_t.Dir, "subtitles"), "fr-7.srt", SearchOption.AllDirectories)[0]));
        var html = Encoding.UTF8.GetBytes("<html><body>Rate limit exceeded, please try again later</body></html>");
        await Assert.ThrowsAsync<SubtitleProviderException>(() => new SubtitleDownloads(subs, new Fake(html), TimeProvider.System).DownloadAsync(movie, "8", "en", CancellationToken.None));
    }

    [Theory] [InlineData("../x", "1")] [InlineData("en", "../1")] [InlineData("en", "")] [InlineData("e n", "1")]
    public void SaveDownloaded_RejectsUnsafeNames(string language, string fileId)
    {
        var (subs, movie, _) = Build();
        Assert.Null(subs.SaveDownloaded(movie, language, fileId, Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHi there friend\n")));
        Assert.Null(subs.SaveDownloaded(999, "en", "1", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHi there friend\n")));   // unknown title
    }

    [Fact] public async Task RemoveDownloaded_DeletesOnlyThatFile()
    {
        var (subs, movie, _) = Build();
        var d = new SubtitleDownloads(subs, new Fake(), TimeProvider.System);
        await d.DownloadAsync(movie, "1", "en", CancellationToken.None); await d.DownloadAsync(movie, "2", "es", CancellationToken.None);
        Assert.Equal(2, subs.List(movie).Count(t => t.Source == "download"));
        Assert.False(subs.RemoveDownloaded(movie, "e0")); Assert.False(subs.RemoveDownloaded(movie, "d9")); Assert.False(subs.RemoveDownloaded(movie, "../d0"));
        Assert.True(subs.RemoveDownloaded(movie, "d0"));
        Assert.Equal("es", Assert.Single(subs.List(movie), t => t.Source == "download").Language);
    }

    [Fact] public async Task Downloads_AreCappedPerHour()
    {
        var (subs, movie, _) = Build();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var d = new SubtitleDownloads(subs, new Fake(), time);
        for (var i = 0; i < SubtitleDownloads.MaxPerHour; i++) { try { await d.DownloadAsync(movie, i.ToString(), "en", CancellationToken.None); } catch (SubtitleProviderException) { /* the per-title limit of 10 is fine here */ } }
        var ex = await Assert.ThrowsAsync<SubtitleProviderException>(() => d.DownloadAsync(movie, "99", "en", CancellationToken.None));
        Assert.Contains("lot of downloads", ex.Message);
        time.Advance(TimeSpan.FromHours(2));
        var after = await Record.ExceptionAsync(() => d.DownloadAsync(movie, "99", "en", CancellationToken.None));
        Assert.DoesNotContain("lot of downloads", after?.Message ?? "");
    }

    private const string SearchJson = """
        { "total_count": 3, "data": [
          { "id": "1", "type": "subtitle", "attributes": { "language": "en", "download_count": 50,  "hearing_impaired": false, "from_trusted": false, "release": "Film.2020.WEB", "files": [ { "file_id": 111, "file_name": "film.web.srt" } ] } },
          { "id": "2", "type": "subtitle", "attributes": { "language": "en", "download_count": 10,  "hearing_impaired": true,  "from_trusted": true,  "release": "Film.2020.BluRay", "files": [ { "file_id": 222, "file_name": "film.bluray.srt" } ] } },
          { "id": "3", "type": "subtitle", "attributes": { "language": "en", "download_count": 999, "files": [] } }
        ] }
        """;

    [Fact] public void ParseSearch_RanksTrustedThenPopular_AndSkipsEntriesWithoutAFile()
    {
        using var doc = JsonDocument.Parse(SearchJson);
        var r = OpenSubtitlesClient.ParseSearch(doc.RootElement);
        Assert.Equal(["222", "111"], r.Select(x => x.FileId));
        Assert.True(r[0].Trusted); Assert.True(r[0].HearingImpaired); Assert.Equal("film.bluray.srt", r[0].Name);
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(OpenSubtitlesClient.ParseSearch(empty.RootElement));
    }

    [Theory]
    [InlineData("https://dl.opensubtitles.com/en/download/x.srt", true)] [InlineData("https://www.opensubtitles.org/a", true)]
    [InlineData("http://dl.opensubtitles.com/x", false)] [InlineData("https://opensubtitles.com.evil.example/x", false)] [InlineData("https://evilopensubtitles.com/x", false)] [InlineData("file:///etc/passwd", false)] [InlineData(null, false)]
    public void OnlyOpenSubtitlesLinksAreFollowed(string? link, bool ok) => Assert.Equal(ok, OpenSubtitlesClient.AllowedLink(link, out _));
}
