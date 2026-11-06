using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Tests;

public sealed class ChapterParsingTests
{
    private static string Json(params (double S, double E, string T)[] ch) => JsonSerializer.Serialize(new
    { chapters = ch.Select(c => new { start_time = c.S.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), end_time = c.E.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), tags = new { title = c.T } }) });

    [Fact] public void ParsesChapters() => Assert.Equal([new Chapter(0, 90.5, "Opening"), new Chapter(90.5, 1200, "Episode")], ChapterService.ParseProbe(Json((0, 90.5, "Opening"), (90.5, 1200, "Episode"))));

    [Theory] [InlineData("")] [InlineData("not json")] [InlineData("{}")] [InlineData("{\"chapters\":5}")] [InlineData("{\"chapters\":[{\"start_time\":\"x\",\"end_time\":\"2\"}]}")]
    public void GarbageGivesNoChapters(string json) => Assert.Empty(ChapterService.ParseProbe(json));

    [Fact] public void BackwardsOrNegativeRangesAreDropped() => Assert.Empty(ChapterService.ParseProbe(Json((100, 50, "Bad"), (-5, 10, "Neg"))));

    [Theory] [InlineData("Intro")] [InlineData("Opening")] [InlineData("OP")] [InlineData("Recap")] [InlineData("Title Sequence")] [InlineData("Previously on")]
    public void IntroTitlesAreRecognised(string title) => Assert.Equal("intro", Assert.Single(ChapterService.Build([new Chapter(10, 100, title)]).Markers).Kind);

    [Theory] [InlineData("Credits")] [InlineData("End Credits")] [InlineData("Ending")] [InlineData("Outro")] [InlineData("ED")]
    public void CreditsTitlesAreRecognised(string title) => Assert.Equal("credits", Assert.Single(ChapterService.Build([new Chapter(1300, 1400, title)]).Markers).Kind);

    [Theory] [InlineData("Chapter 1")] [InlineData("The Operation")] [InlineData("Scene 4")] [InlineData("")] [InlineData("Opening Night Party Scene Extended")]
    public void OrdinaryChaptersAreNotMarkers(string title)
    {
        // "Opening Night..." does contain the word Opening, but a 15-minute chapter isn't a title sequence
        var longChapter = new Chapter(0, 900, title);
        Assert.DoesNotContain(ChapterService.Build([longChapter]).Markers, m => m.Kind == "intro");
    }

    [Fact] public void AnIntroLongerThanTenMinutes_IsNotOffered() => Assert.Empty(ChapterService.Build([new Chapter(0, 700, "Intro")]).Markers);
    [Fact] public void VeryShortCreditsAreIgnored() => Assert.Empty(ChapterService.Build([new Chapter(0, 2, "Credits")]).Markers);
}

public sealed class RealFileMediaTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly string _media;
    private sealed class Resolver(Func<long, string?> f) : IMediaResolver { public string? ResolvePath(long id) => f(id); }

    public RealFileMediaTests() { _media = Path.Combine(_t.Dir, "lib"); Directory.CreateDirectory(_media); }
    public void Dispose() => _t.Dispose();

    private static async Task Run(params string[] args) { var r = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", .. args], default); Assert.True(r.ExitCode == 0, r.StdErr); }

    private async Task<(long Id, string Path)> VideoWithChapters()
    {
        var plain = Path.Combine(_media, "plain.mkv"); var meta = Path.Combine(_media, "meta.txt"); var file = Path.Combine(_media, "show.mkv");
        await Run("-f", "lavfi", "-i", "testsrc=d=25:s=160x90:r=10", "-c:v", "libx264", plain);
        File.WriteAllText(meta, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=8000\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=8000\nEND=16000\ntitle=Episode\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=16000\nEND=25000\ntitle=End Credits\n");
        await Run("-i", plain, "-i", meta, "-map", "0", "-map_metadata", "1", "-c", "copy", file);
        return (_t.Insert("show.mkv", "Episode", "Show"), file);
    }

    private ChapterService Chapters(Func<long, string?> resolve) => new(_t.Db, new Resolver(resolve), new ProcessRunner(2), _t.Config, TimeProvider.System);

    [Fact] public async Task ReadsRealChapters_FindsTheSkipSpans_AndCachesThem()
    {
        var (id, file) = await VideoWithChapters();
        var svc = Chapters(_ => file);
        var info = (await svc.GetAsync(id, default))!;
        Assert.Equal(3, info.Chapters.Count);
        Assert.Equal([("intro", 0.0, 8.0), ("credits", 16.0, 25.0)], info.Markers.Select(m => (m.Kind, m.Start, m.End)));
        Assert.Equal("1", _t.Scalar("SELECT COUNT(*) FROM media_chapters"));
        var again = Chapters(_ => "/nonexistent/should-not-be-probed.mkv");                                   // a cache hit needs no ffprobe...
        Assert.Null(await again.GetAsync(id, default));                                                        // ...but an unreadable file is simply unknown
    }

    [Fact] public async Task AFileWithoutChapters_HasNoSkipButtons()
    {
        var plain = Path.Combine(_media, "p.mkv"); await Run("-f", "lavfi", "-i", "testsrc=d=3:s=160x90:r=10", "-c:v", "libx264", plain);
        var id = _t.Insert("p.mkv", "Movie", "P");
        var info = (await Chapters(_ => plain).GetAsync(id, default))!;
        Assert.Empty(info.Chapters); Assert.Empty(info.Markers);
    }

    [Fact] public async Task UnknownMedia_IsNull() => Assert.Null(await Chapters(_ => null).GetAsync(99, default));

    // ---- seek-bar previews
    private TrickplayService Trick(Func<long, string?> resolve, Dictionary<string, string?>? extra = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(extra ?? []) { ["Images:CacheDir"] = Path.Combine(_t.Dir, "img") }).Build();
        return new TrickplayService(_t.Db, new Resolver(resolve), new ProcessRunner(2), cfg, NullLogger<TrickplayService>.Instance);
    }

    [Theory] [InlineData(0, 10)] [InlineData(120, 5)] [InlineData(3600, 5)] [InlineData(7200, 10)] [InlineData(14400, 20)] [InlineData(86400, 110)]
    public void TheInterval_ScalesWithTheRuntime_ToAboutEightHundredFrames(double duration, double expected) => Assert.Equal(expected, TrickplayService.IntervalFor(duration));

    [Fact] public async Task GeneratesSpriteSheets_InTheBackground_ThenServesThem()
    {
        var file = Path.Combine(_media, "long.mkv");
        await Run("-f", "lavfi", "-i", "testsrc=d=125:s=320x180:r=5", "-c:v", "libx264", "-g", "10", "-preset", "ultrafast", file);
        var id = _t.Insert("long.mkv", "Movie", "Long");
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET duration_seconds=125, info_json=@j WHERE id=@i",
            ("@j", JsonSerializer.Serialize(new MediaServer.Api.Probing.MediaInfo("matroska", 125, null, null, [new MediaServer.Api.Probing.VideoTrack(0, "h264", null, 320, 180, 5, "yuv420p", null)], [], []))), ("@i", id));
        var svc = Trick(_ => file);
        Assert.Equal(TrickplayService.State.Generating, svc.Get(id).State);
        TrickplayInfo? info = null;
        for (var i = 0; i < 100 && info is null; i++) { await Task.Delay(200); if (svc.Get(id) is { State: TrickplayService.State.Ready } r) info = r.Info; }
        Assert.NotNull(info);
        Assert.Equal((5.0, 160, 90, 10, 10, 26, 1), (info!.Interval, info.Width, info.Height, info.Columns, info.Rows, info.Count, info.Sheets));
        var sheet = svc.SheetPath(id, 0)!;
        Assert.NotNull(sheet); Assert.True(new FileInfo(sheet).Length > 1000);
        Assert.Null(svc.SheetPath(id, 1)); Assert.Null(svc.SheetPath(id, -1)); Assert.Null(svc.SheetPath(id, 99999));
        Assert.Equal(TrickplayService.State.Ready, svc.Get(id).State);                                        // second request: straight from the cache
        var probe = await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", sheet], default);
        Assert.StartsWith("1600,900", probe.StdOut.Trim());                                                    // 10×10 tiles of 160×90
    }

    [Fact] public void ShortClips_AndDisabledPreviews_AreNotGenerated()
    {
        var file = Path.Combine(_media, "s.mkv"); File.WriteAllBytes(file, [0]);
        var id = _t.Insert("s.mkv", "Movie", "S");
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET duration_seconds=30 WHERE id=@i", ("@i", id));
        Assert.Equal(TrickplayService.State.Unavailable, Trick(_ => file).Get(id).State);
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET duration_seconds=3000 WHERE id=@i", ("@i", id));
        Assert.Equal(TrickplayService.State.Unavailable, Trick(_ => file, new() { ["Playback:Trickplay"] = "false" }).Get(id).State);
        Assert.Equal(TrickplayService.State.Unavailable, Trick(_ => null).Get(id).State);
    }

    [Fact] public async Task AFailedGeneration_IsRememberedForADay_InsteadOfRetriedEveryRequest()
    {
        var file = Path.Combine(_media, "broken.mkv"); File.WriteAllText(file, "this is not a video");
        var id = _t.Insert("broken.mkv", "Movie", "B");
        using (var c = _t.Db.Open()) Database.Execute(c, null, "UPDATE media_files SET duration_seconds=3000 WHERE id=@i", ("@i", id));
        var svc = Trick(_ => file);
        svc.Get(id);
        for (var i = 0; i < 100 && svc.Get(id).State == TrickplayService.State.Generating; i++) await Task.Delay(100);
        Assert.Equal(TrickplayService.State.Unavailable, svc.Get(id).State);
        Assert.Equal(TrickplayService.State.Unavailable, svc.Get(id).State);
    }
}

public sealed class ChapterAndPreviewEndpointTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task TheyNeedSignIn_AndUnknownMediaIsNotFound()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().GetAsync("/api/media/1/chapters")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().GetAsync("/api/media/1/trickplay")).StatusCode);
        var c = await _f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/media/99/chapters")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/media/99/trickplay")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/media/99/trickplay/0.jpg")).StatusCode);
    }
}
