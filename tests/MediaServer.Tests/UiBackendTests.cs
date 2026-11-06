using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

internal sealed class CountingRunner(IProcessRunner inner) : IProcessRunner
{
    public int Calls;
    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, CancellationToken ct) { Interlocked.Increment(ref Calls); return inner.RunAsync(e, a, ct); }
}

internal sealed class MapResolver(Dictionary<long, string> map) : IMediaResolver { public string? ResolvePath(long id) => map.GetValueOrDefault(id); }

public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly CountingRunner _runner = new(new ProcessRunner());
    public void Dispose() => _t.Dispose();

    private static bool Have(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));

    private async Task<string> Clip(string name, int seconds = 20)
    {
        var path = Path.Combine(_t.Dir, name);
        var r = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=d={seconds}:s=1280x720:r=25", "-c:v", "libx264", "-pix_fmt", "yuv420p", path], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        return path;
    }

    private void Row(long id, string path, double duration)
    {
        using var c = _t.Db.Open();
        Database.Execute(c, null, "INSERT INTO media_files(id,path,size,mtime_ticks,first_seen,last_scanned,duration_seconds) VALUES(@i,@p,1,1,'x','x',@d)", ("@i", id), ("@p", Path.GetFileName(path)), ("@d", duration));
    }

    private ThumbnailService Svc(params (long Id, string Path)[] files) =>
        new(_t.Db, new MapResolver(files.ToDictionary(f => f.Id, f => f.Path)), _runner, _t.Config, NullLogger<ThumbnailService>.Instance);

    [Fact]
    public async Task ExtractsARealJpegAtTheRequestedWidth_AndCachesIt()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        var clip = await Clip("a.mp4"); Row(1, clip, 20);
        var svc = Svc((1, clip));
        var p = await svc.GetAsync(1, 320, default);
        Assert.NotNull(p);
        var bytes = await File.ReadAllBytesAsync(p!);
        Assert.Equal((byte)0xFF, bytes[0]); Assert.Equal((byte)0xD8, bytes[1]);                 // JPEG magic
        var probe = await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", p!], default);
        Assert.Equal("320,180", probe.StdOut.Trim());
        var calls = _runner.Calls;
        Assert.Equal(p, await svc.GetAsync(1, 320, default));
        Assert.Equal(calls, _runner.Calls);                                                       // second request: no ffmpeg
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.tmp*"));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneFfmpegRun()
    {
        if (!Have("ffmpeg")) return;
        var clip = await Clip("a.mp4"); Row(1, clip, 20);
        var svc = Svc((1, clip));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => svc.GetAsync(1, 640, default)));
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.NotNull(results[0]);
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task BrokenFile_ReturnsNull_AndIsNotRetriedOnEveryView()
    {
        if (!Have("ffmpeg")) return;
        var bad = Path.Combine(_t.Dir, "bad.mkv"); await File.WriteAllTextAsync(bad, "not a video");
        var svc = Svc((1, bad));
        Assert.Null(await svc.GetAsync(1, 320, default));
        var calls = _runner.Calls;
        Assert.Null(await svc.GetAsync(1, 320, default));
        Assert.Equal(calls, _runner.Calls);
    }

    [Fact]
    public async Task ChangedFile_GetsANewThumbnail()
    {
        if (!Have("ffmpeg")) return;
        var path = await Clip("a.mp4"); Row(1, path, 20);
        var svc = Svc((1, path));
        var first = await svc.GetAsync(1, 320, default);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        var second = await svc.GetAsync(1, 320, default);
        Assert.NotEqual(first, second);
    }

    [Theory] [InlineData(0)] [InlineData(100)] [InlineData(641)] [InlineData(-320)] [InlineData(int.MaxValue)]
    public async Task OnlyWhitelistedWidths_AndNoFfmpegForRejectedOnes(int w)
    {
        var svc = Svc((1, "/x/a.mp4"));
        Assert.Null(await svc.GetAsync(1, w, default));
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task UnknownMediaOrMissingFile_IsNull()
    {
        var svc = Svc((1, "/does/not/exist.mp4"));
        Assert.Null(await svc.GetAsync(99, 320, default));
        Assert.Null(await svc.GetAsync(1, 320, default));
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task ShortClipsStillGetAFrame()
    {
        if (!Have("ffmpeg")) return;
        var clip = await Clip("short.mp4", seconds: 2); Row(1, clip, 2);
        Assert.NotNull(await Svc((1, clip)).GetAsync(1, 320, default));
    }

    [Fact]
    public async Task UnknownDuration_FallsBackToTheStartOfTheFile_InsteadOfSeekingPastTheEnd()
    {
        if (!Have("ffmpeg")) return;
        var svc = Svc((1, await Clip("nodb.mp4", seconds: 4)));   // no DB row => duration unknown => default 30s seek is beyond the end
        Assert.NotNull(await svc.GetAsync(1, 320, default));
        Assert.Equal(2, _runner.Calls);                           // first attempt produced nothing, the retry from 0s did
    }
}

public sealed class ProgressTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ProgressService _p;
    private readonly LibraryQueries _q;
    public ProgressTests() { _p = new ProgressService(_t.Db, _time, _t.Config); _q = new LibraryQueries(_t.Db); }
    public void Dispose() => _t.Dispose();

    private long Movie(string title, double duration = 7200) { var id = _t.Insert($"{title}.mkv", "Movie", title, 2020); SetDuration(id, duration); return id; }
    private long Ep(string show, int s, int e, double duration = 1500)
    {
        var id = _t.Insert($"{show}/S{s}E{e}.mkv", "Episode", show);
        using var c = _t.Db.Open(); Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, episode_title=@t, duration_seconds=@d WHERE id=@i", ("@s", s), ("@e", e), ("@t", $"Ep {e}"), ("@d", duration), ("@i", id));
        return id;
    }
    private void SetDuration(long id, double d) { using var c = _t.Db.Open(); Database.Execute(c, null, "UPDATE media_files SET duration_seconds=@d WHERE id=@i", ("@d", d), ("@i", id)); }
    private void Tick() => _time.Advance(TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData(600, 7200, false)] [InlineData(6700, 7200, true)]   // past 92%
    [InlineData(7120, 7200, true)]                                   // within the last 90s
    [InlineData(100, 200, false)] [InlineData(0, 7200, false)]
    public void WatchedThreshold(double pos, double dur, bool watched)
    {
        var id = Movie("M"); Assert.True(_p.Save(id, pos, dur, null));
        Assert.Equal(watched ? 1L : 0L, Convert.ToInt64(_t.Scalar($"SELECT watched FROM media_progress WHERE media_id={id}")));
    }

    [Fact] public void ExplicitWatchedOverrides() { var id = Movie("M"); _p.Save(id, 10, 7200, true); Assert.Equal("1", _t.Scalar($"SELECT watched FROM media_progress WHERE media_id={id}")); }

    [Fact]
    public void RejectsNonsenseAndUnknownMedia()
    {
        var id = Movie("M");
        Assert.False(_p.Save(999, 10, 100, null));
        Assert.False(_p.Save(id, -1, 100, null));
        Assert.False(_p.Save(id, double.NaN, 100, null));
        Assert.False(_p.Save(id, double.PositiveInfinity, 100, null));
        Assert.False(_p.Save(id, 10, 0, null));
        Assert.False(_p.Save(id, 10, -5, null));
        Assert.False(_p.Save(id, 2e9, 100, null));
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM media_progress"));
    }

    [Fact] public void UpsertKeepsKnownDurationWhenLaterSavesOmitIt()
    {
        var id = Movie("M"); _p.Save(id, 100, 7200, null); _p.Save(id, 200, null, null);
        Assert.Equal("7200", _t.Scalar($"SELECT CAST(duration_seconds AS INT) FROM media_progress WHERE media_id={id}"));
        Assert.Equal("200", _t.Scalar($"SELECT CAST(position_seconds AS INT) FROM media_progress WHERE media_id={id}"));
    }

    [Fact] public void ProgressIsDeletedWithItsMedia()
    {
        var id = Movie("M"); _p.Save(id, 100, 7200, null);
        using (var c = _t.Db.Open()) Database.Exec(c, $"DELETE FROM media_files WHERE id={id}");
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM media_progress"));
    }

    [Fact]
    public void Continue_ListsUnfinishedMoviesAndEpisodes_MostRecentFirst_IgnoringTinyAndFinished()
    {
        var started = Movie("Started"); var tiny = Movie("Tiny"); var done = Movie("Done");
        var a = Ep("Show A", 1, 1); var b = Ep("Show B", 2, 3);
        _p.Save(tiny, 12, 7200, null); Tick();
        _p.Save(done, 7150, 7200, null); Tick();
        _p.Save(started, 900, 7200, null); Tick();
        _p.Save(a, 300, 1500, null); Tick();
        _p.Save(b, 700, 1500, null);

        var items = _q.Continue(10);
        Assert.Equal(["Show B", "Show A", "Started"], items.Select(i => i.Title));
        Assert.Equal((b, 700.0, "episode"), (items[0].MediaId, items[0].PositionSeconds, items[0].Type));
        Assert.Contains("S2:E3", items[0].Subtitle);
        Assert.All(items, i => Assert.False(i.NextUp));
    }

    [Fact]
    public void Continue_OffersTheNextEpisode_WhenTheLatestOneIsFinished()
    {
        var e1 = Ep("Show", 1, 1); var e2 = Ep("Show", 1, 2); Ep("Show", 1, 3);
        _p.Save(e1, 1450, 1500, null);                                    // finished
        var item = Assert.Single(_q.Continue(10));
        Assert.True(item.NextUp);
        Assert.Equal((e2, 0.0), (item.MediaId, item.PositionSeconds));
    }

    [Fact]
    public void Continue_NothingToOffer_WhenTheLastEpisodeOfASeriesIsFinished()
    {
        var e1 = Ep("Show", 1, 1); _p.Save(e1, 1450, 1500, null);
        Assert.Empty(_q.Continue(10));
    }

    [Fact]
    public void Continue_OnlyTheLatestEpisodePerSeries_AndRespectsLimit()
    {
        var e1 = Ep("Show", 1, 1); var e2 = Ep("Show", 1, 2);
        _p.Save(e1, 300, 1500, null); Tick(); _p.Save(e2, 400, 1500, null);
        Assert.Equal(e2, Assert.Single(_q.Continue(10)).MediaId);
        for (var i = 0; i < 5; i++) { var m = Movie($"M{i}"); _p.Save(m, 600, 7200, null); Tick(); }
        Assert.Equal(3, _q.Continue(3).Count);
    }

    [Fact]
    public void SeriesDetail_CarriesPerEpisodeProgress_AndAResumeTarget()
    {
        var e1 = Ep("Show", 1, 1); var e2 = Ep("Show", 1, 2); var e3 = Ep("Show", 1, 3);
        var key = _q.List(null, "series", "title", 10, 0).Items.Single().Key;
        Assert.Equal(e1, _q.Series(key)!.ResumeMediaId);                       // nothing watched: start at the beginning

        _p.Save(e1, 1450, 1500, null);
        Assert.Equal(e2, _q.Series(key)!.ResumeMediaId);                       // first is done: next one

        Tick(); _p.Save(e2, 400, 1500, null);
        var d = _q.Series(key)!;
        Assert.Equal(e2, d.ResumeMediaId);                                     // in progress wins
        var eps = d.Seasons.Single().Episodes;
        Assert.Equal([true, false, false], eps.Select(e => e.Watched));
        Assert.Equal(400.0, eps[1].PositionSeconds);
        Assert.Equal($"/api/media/{e3}/thumb?w=320", eps[2].ThumbUrl);
    }

    [Fact]
    public void PlayInfo_ResumesOnlyWhenMeaningfullyStartedAndNotFinished()
    {
        var m = Movie("M");
        Assert.Equal(0, _q.Play(m, ClientCaps.Baseline)!.ResumeSeconds);
        _p.Save(m, 10, 7200, null); Assert.Equal(0, _q.Play(m, ClientCaps.Baseline)!.ResumeSeconds);       // too early to bother
        _p.Save(m, 900, 7200, null); Assert.Equal(900, _q.Play(m, ClientCaps.Baseline)!.ResumeSeconds);
        _p.Save(m, 7000, 7200, null); Assert.Equal(0, _q.Play(m, ClientCaps.Baseline)!.ResumeSeconds);     // finished
    }

    [Fact]
    public void PlayInfo_NamesTheNextEpisode()
    {
        var e1 = Ep("Show", 1, 1); var e2 = Ep("Show", 1, 2);
        var p = _q.Play(e1, ClientCaps.Baseline)!;
        Assert.Equal(e2, p.NextMediaId);
        Assert.Equal("S1:E2 · Ep 2", p.NextTitle);      // tag + title, each exactly once
        Assert.Null(_q.Play(e2, ClientCaps.Baseline)!.NextTitle);
    }
}

public sealed class BrowseArtTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly LibraryQueries _q;
    public BrowseArtTests() => _q = new LibraryQueries(_t.Db);
    public void Dispose() => _t.Dispose();

    private long Add(string title, DateTime mtime, string kind = "Movie", int? tmdb = null, string? backdrop = null)
    {
        var id = _t.Insert($"{title}-{Guid.NewGuid():N}.mkv", kind, title, 2020);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET mtime_ticks=@m, tmdb_type=@tt, tmdb_id=@ti, match_state=@ms WHERE id=@i",
            ("@m", mtime.Ticks), ("@tt", tmdb is null ? null : "movie"), ("@ti", tmdb), ("@ms", tmdb is null ? null : "matched"), ("@i", id));
        if (tmdb is not null)
            Database.Execute(c, null, "INSERT INTO metadata(tmdb_type,tmdb_id,title,year,overview,poster_path,backdrop_path,fetched_at) VALUES('movie',@i,@t,2020,'Plot','/p.jpg',@b,'x')", ("@i", tmdb), ("@t", title), ("@b", backdrop));
        return id;
    }

    [Fact]
    public void WithoutTmdb_CardsUseAFrameFromTheVideo()
    {
        var id = Add("Plain", DateTime.UtcNow);
        var item = _q.List(null, null, "title", 10, 0).Items.Single();
        Assert.Equal($"/api/media/{id}/thumb?w=640", item.ImageUrl);
        Assert.Equal($"/api/media/{id}/thumb?w=1280", item.HeroUrl);
        Assert.Null(item.PosterUrl);
    }

    [Fact]
    public void WithTmdb_CardsUseTheBackdropAndPoster()
    {
        var id = Add("Matched", DateTime.UtcNow, tmdb: 1, backdrop: "/b.jpg");
        var item = _q.List(null, null, "title", 10, 0).Items.Single();
        Assert.Equal($"/api/media/{id}/backdrop?size=w780", item.ImageUrl);
        Assert.Equal($"/api/media/{id}/backdrop?size=w1280", item.HeroUrl);
        Assert.Equal($"/api/media/{id}/poster", item.PosterUrl);
        Assert.Equal("Plot", item.Overview);
    }

    [Fact]
    public void RecentlyAdded_MeansNewestFileOnTheNas_AndNewIsFlagged()
    {
        Add("Old", DateTime.UtcNow.AddYears(-3)); Add("Newest", DateTime.UtcNow.AddDays(-2)); Add("Middle", DateTime.UtcNow.AddMonths(-4));
        var items = _q.List(null, null, "recent", 10, 0).Items;
        Assert.Equal(["Newest", "Middle", "Old"], items.Select(i => i.Title));
        Assert.Equal([true, false, false], items.Select(i => i.IsNew));
    }

    [Fact]
    public void ASeriesIsAsNewAsItsNewestEpisode()
    {
        Add("Movie", DateTime.UtcNow.AddMonths(-1));
        var e1 = Add("Show", DateTime.UtcNow.AddYears(-2), "Episode"); var e2 = Add("Show", DateTime.UtcNow.AddDays(-1), "Episode");
        var items = _q.List(null, null, "recent", 10, 0).Items;
        Assert.Equal("Show", items[0].Title);
        Assert.True(items[0].IsNew);
    }
}

public sealed class ProbeVersionTests
{
    [Fact]
    public void CodecTagIsParsed()
    {
        var m = FfprobeParser.Parse("""{"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p","codec_tag_string":"hev1"}]}""");
        Assert.Equal("hev1", m.Video.Single().CodecTag);
        Assert.Null(FfprobeParser.Parse("""{"streams":[{"index":0,"codec_type":"video","codec_name":"h264"}]}""").Video.Single().CodecTag);
    }

    private static MediaInfo Hevc(string? tag) => new("mov,mp4", 60, 1, 1, [new VideoTrack(0, "hevc", null, 1920, 1080, 24, "yuv420p", null, tag)],
        [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []);
    private static readonly ClientCaps SafariLike = ClientCaps.From("h264,hevc,hevc10", "aac,mp3,flac,ac3,eac3", "mp4");
    private static readonly ClientCaps ChromeLike = ClientCaps.From("h264,hevc,hevc10,hev1", "aac,mp3,opus,flac", "mp4,webm,mkv");

    [Fact] public void Hev1InMp4_CannotDirectPlayInSafari_ButIsCopiedAndRetagged()
    {
        var d = PlaybackPolicy.Decide(Hevc("hev1"), ".mp4", SafariLike);
        Assert.Equal(PlayMode.DirectStream, d.Mode);
        Assert.Contains("hev1", d.Reason);
        Assert.Equal("hvc1", HlsPlanner(Hevc("hev1"), SafariLike).VideoTag);
    }

    [Fact] public void Hvc1InMp4_DirectPlaysInSafari() => Assert.Equal(PlayMode.Direct, PlaybackPolicy.Decide(Hevc("hvc1"), ".mp4", SafariLike).Mode);
    [Fact] public void Hev1InMp4_DirectPlaysWhereTheBrowserAcceptsIt() => Assert.Equal(PlayMode.Direct, PlaybackPolicy.Decide(Hevc("hev1"), ".mp4", ChromeLike).Mode);
    [Fact] public void UnknownTag_FromAnOlderProbe_IsGivenTheBenefitOfTheDoubt() => Assert.Equal(PlayMode.Direct, PlaybackPolicy.Decide(Hevc(null), ".mp4", SafariLike).Mode);

    private static MediaServer.Api.Hls.HlsPlan HlsPlanner(MediaInfo i, ClientCaps c) =>
        MediaServer.Api.Hls.HlsPlanner.Decide(i, new MediaServer.Api.Hls.HlsOptions(), c, ".mp4")!;
}

public sealed class ScanCoordinatorTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("coord").FullName;
    private readonly Database _db;
    private readonly IConfiguration _cfg;
    private readonly SlowProbe _probe = new();
    private readonly FakeLife _life = new();
    private readonly ScanCoordinator _c;

    public ScanCoordinatorTests() : this(Path.Combine(Path.GetTempPath(), "x")) { }
    private ScanCoordinatorTests(string _)
    {
        var root = Path.Combine(_tmp, "lib"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "A (2020).mkv"), new byte[100]);
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Path"] = Path.Combine(_tmp, "t.db"), ["Library:Root"] = root, ["Images:CacheDir"] = Path.Combine(_tmp, "img") }).Build();
        _db = new Database(_cfg); _db.Migrate();
        var tmdb = new TmdbClient(new HttpClient(new StubHandler(_ => TmdbJson.Status(HttpStatusCode.NotFound))), _db, _cfg, TimeProvider.System); // unconfigured
        var services = new ServiceCollection().AddLogging()
            .AddScoped(_ => new MetadataService(_db, tmdb, TimeProvider.System, _cfg, NullLogger<MetadataService>.Instance)).BuildServiceProvider();
        var scanner = new LibraryScanner(_db, _probe, _cfg, NullLogger<LibraryScanner>.Instance);
        _c = new ScanCoordinator(scanner, services.GetRequiredService<IServiceScopeFactory>(), _life, TimeProvider.System, NullLogger<ScanCoordinator>.Instance);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private sealed class SlowProbe : IMediaProbe
    {
        public TimeSpan Delay;
        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) { if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct); return new MediaInfo("matroska", 60, 1, 1, [], [], []); }
    }
    private sealed class FakeLife : IHostApplicationLifetime
    {
        public readonly CancellationTokenSource Stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stop.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stop.Cancel();
    }

    [Fact]
    public async Task ScanCompletes_RecordsTheOutcome_AndEnrichmentWithoutATokenIsSilentlySkipped()
    {
        var run = _c.TryStart();
        Assert.NotNull(run);
        var result = await run!;
        Assert.Equal(1, result!.Added);
        Assert.False(_c.IsScanning);
        Assert.Equal(1, _c.LastResult!.Added);
        Assert.NotNull(_c.LastFinished);
        Assert.Null(_c.LastError);
    }

    [Fact]
    public async Task OnlyOneScanAtATime()
    {
        _probe.Delay = TimeSpan.FromMilliseconds(400);
        var first = _c.TryStart();
        Assert.True(_c.IsScanning);
        Assert.Null(_c.TryStart());
        await first!;
        Assert.NotNull(_c.TryStart());
    }

    [Fact]
    public async Task MissingLibraryFolder_SetsAnError_WithoutLeakingThePath_AndAllowsALaterRetry()
    {
        Directory.Delete(Path.Combine(_tmp, "lib"), true);
        Assert.Null(await _c.TryStart()!);
        Assert.Equal("The library folder is missing or not mounted", _c.LastError);
        Assert.DoesNotContain(_tmp, _c.LastError);
        Directory.CreateDirectory(Path.Combine(_tmp, "lib"));
        Assert.NotNull(await _c.TryStart()!);
        Assert.Null(_c.LastError);
    }

    [Fact]
    public async Task ScanIsNotTiedToTheCallerWaitingForIt()
    {
        _probe.Delay = TimeSpan.FromMilliseconds(300);
        var run = _c.TryStart()!;
        using var impatient = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(impatient.Token));   // the HTTP caller gives up...
        var result = await run;                                                                            // ...the scan still finishes
        Assert.Equal(1, result!.Added);
    }
}

public sealed class UiEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uiep").FullName;
    private readonly WebApplicationFactory<Program> _f;
    private readonly HttpClient _c;

    public UiEndpointTests(): this(true) { }
    private UiEndpointTests(bool _)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", Path.Combine(_dir, "lib"))
            .UseSetting("Library:AutoScan", "false").UseSetting("Images:CacheDir", Path.Combine(_dir, "img")));
        _c = _f.CreateClient();
    }
    public void Dispose() { _c.Dispose(); _f.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private long AddRow(string title = "Movie")
    {
        var db = _f.Services.GetRequiredService<Database>();
        using var c = db.Open();
        Database.Execute(c, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,year,duration_seconds) VALUES(@p,1,1,'x','x',1,'Movie',@t,2020,7200)", ("@p", $"{title}.mkv"), ("@t", title));
        return Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()"));
    }

    [Theory] [InlineData("/api/media/1/thumb?w=999")] [InlineData("/api/media/1/thumb?w=0")] [InlineData("/api/media/1/thumb?w=-1")]
    public async Task ThumbRejectsBadWidths(string url) => Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync(url)).StatusCode);

    [Theory] [InlineData("/api/media/999/thumb")] [InlineData("/api/media/999/thumb?w=320")]
    public async Task ThumbForUnknownMedia404s(string url) => Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync(url)).StatusCode);

    [Fact]
    public async Task ProgressRoundTrip_FeedsContinueWatching_AndCanBeCleared()
    {
        var id = AddRow();
        Assert.Equal(HttpStatusCode.NoContent, (await _c.PostAsJsonAsync($"/api/media/{id}/progress", new { position = 900, duration = 7200 })).StatusCode);
        var cont = await _c.GetFromJsonAsync<JsonElement>("/api/continue");
        Assert.Equal(id, cont[0].GetProperty("mediaId").GetInt64());
        Assert.Equal(900, cont[0].GetProperty("positionSeconds").GetDouble());

        var play = await _c.GetFromJsonAsync<JsonElement>($"/api/media/{id}/play");
        Assert.Equal(900, play.GetProperty("resumeSeconds").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await _c.PutAsJsonAsync($"/api/media/{id}/progress", new { position = 1000, duration = 7200 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/media/{id}/progress")).StatusCode);
        Assert.Equal(0, (await _c.GetFromJsonAsync<JsonElement>("/api/continue")).GetArrayLength());
    }

    [Fact]
    public async Task SendBeaconStyleBlobIsAccepted()
    {
        var id = AddRow();
        var content = new StringContent("{\"position\":120,\"duration\":3000}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.NoContent, (await _c.PostAsync($"/api/media/{id}/progress", content)).StatusCode);
    }

    [Theory]
    [InlineData("{\"position\":-5}")] [InlineData("{\"position\":1e12}")] [InlineData("{\"position\":10,\"duration\":0}")] [InlineData("{}x")] [InlineData("")]
    public async Task ProgressRejectsNonsense(string body)
    {
        var id = AddRow();
        var res = await _c.PostAsync($"/api/media/{id}/progress", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, res.StatusCode.ToString());
    }

    [Fact] public async Task ProgressForUnknownMedia400s()
        => Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsJsonAsync("/api/media/999/progress", new { position = 100, duration = 1000 })).StatusCode);

    [Fact]
    public async Task ScanEndpoint_ReturnsTheResult_AndStatusReflectsIt()
    {
        var res = await _c.PostAsync("/api/library/scan", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());
        var st = await _c.GetFromJsonAsync<JsonElement>("/api/library/status");
        Assert.False(st.GetProperty("scanning").GetBoolean());
        Assert.Equal(JsonValueKind.Object, st.GetProperty("lastResult").ValueKind);
        Assert.Equal(JsonValueKind.Null, st.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task BackgroundScanReturns202_AndFinishes()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _c.PostAsync("/api/library/scan?background=true", null)).StatusCode);
        for (var i = 0; i < 50; i++)
        {
            var st = await _c.GetFromJsonAsync<JsonElement>("/api/library/status");
            if (!st.GetProperty("scanning").GetBoolean() && st.GetProperty("lastFinished").ValueKind != JsonValueKind.Null) return;
            await Task.Delay(50);
        }
        Assert.Fail("background scan never finished");
    }

    [Fact]
    public async Task ScanWithMissingFolder_Is500WithAFriendlyMessage_NotAPath()
    {
        Directory.Delete(Path.Combine(_dir, "lib"));
        var res = await _c.PostAsync("/api/library/scan", null);
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("missing or not mounted", text);
        Assert.DoesNotContain(_dir, text);
    }
}

/// <summary>Regression tests for failures seen on the real library: OOM from buffering full-size 4K frames, and a file whose seek hangs.</summary>
public sealed class ThumbnailResilienceTests : IDisposable
{
    private readonly TestDb _t;
    public ThumbnailResilienceTests() => _t = new TestDb(new() { ["Thumbnails:AttemptTimeoutSeconds"] = "1", ["Thumbnails:MaxConcurrency"] = "2" });
    public void Dispose() => _t.Dispose();

    private sealed class FakeRunner(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, CancellationToken ct) { lock (Calls) Calls.Add(a); return handler(a, ct); }
    }

    private string File1(string name = "m.mp4") { var p = Path.Combine(_t.Dir, name); File.WriteAllBytes(p, [1, 2, 3]); return p; }
    private ThumbnailService Svc(FakeRunner r, params (long, string)[] files) =>
        new(_t.Db, new MapResolver(files.ToDictionary(f => f.Item1, f => f.Item2)), r, _t.Config, NullLogger<ThumbnailService>.Instance);
    private static Task<ProcessResult> WriteOutput(IReadOnlyList<string> args, int bytes) { File.WriteAllBytes(args[^1], new byte[bytes]); return Task.FromResult(new ProcessResult(0, "", "")); }
    private string Marker(long id, int width, string path) => Directory.GetFiles(Path.Combine(_t.Dir, "img", "thumbs"), $"{id}-{width}-*.fail").Single();

    [Fact]
    public async Task FramesAreScaledBeforeTheThumbnailFilterBuffersThem_AndOnlyKeyframesAreDecoded()
    {
        var runner = new FakeRunner((a, _) => WriteOutput(a, 4000));
        Assert.NotNull(await Svc(runner, (1, File1())).GetAsync(1, 640, default));
        var args = runner.Calls[0];
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.True(vf.IndexOf("scale=640", StringComparison.Ordinal) is var s && s >= 0 && vf.IndexOf("thumbnail", StringComparison.Ordinal) > s,
            $"scale must come before thumbnail so buffered frames are small, got: {vf}");
        Assert.Equal("nokey", args[args.ToList().IndexOf("-skip_frame") + 1]);
        Assert.Equal("2", args[args.ToList().IndexOf("-threads") + 1]);
        Assert.True(args.ToList().IndexOf("-skip_frame") < args.ToList().IndexOf("-i"), "decoder options must precede the input");
    }

    [Fact]
    public async Task ABlankFrameIsNotUsedAsArtwork()
    {
        var runner = new FakeRunner((a, _) => WriteOutput(a, 300));                 // a solid-black JPEG is a few hundred bytes
        var svc = Svc(runner, (1, File1()));
        Assert.Null(await svc.GetAsync(1, 640, default));
        Assert.Equal(2, runner.Calls.Count);                                         // seek attempt, then start-of-file attempt
        Assert.All(Directory.GetFiles(Path.Combine(_t.Dir, "img", "thumbs")), f => Assert.EndsWith(".fail", f));            // no temp files left behind
    }

    [Fact]
    public async Task ASeekThatHangsTimesOutPerAttempt_NotForTheWholeRequest()
    {
        var calls = 0;
        var runner = new FakeRunner(async (a, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { await Task.Delay(Timeout.Infinite, ct); }      // seek stalls (the 17 GB MKV)
            return await WriteOutput(a, 4000);                                                          // start of file works
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var path = await Svc(runner, (1, File1())).GetAsync(1, 640, default);
        Assert.NotNull(path);                                                                           // the second attempt still got its turn
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 5);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task BothAttemptsTimingOut_GivesUp_AndRemembersItBriefly()
    {
        var runner = new FakeRunner(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new ProcessResult(0, "", ""); });
        var svc = Svc(runner, (1, File1()));
        Assert.Null(await svc.GetAsync(1, 640, default));
        Assert.Equal("timeout", File.ReadAllText(Marker(1, 640, "")));
        var calls = runner.Calls.Count;
        Assert.Null(await svc.GetAsync(1, 640, default));
        Assert.Equal(calls, runner.Calls.Count);                                     // a fresh marker means no retry storm
    }

    [Theory]
    [InlineData("failed", 3, false)]            // a real failure is remembered for a day
    [InlineData("failed", 25 * 60, true)]
    [InlineData("timeout", 3, false)]           // a stall is probably transient: retry after ~10 minutes
    [InlineData("timeout", 11, true)]
    public async Task FailureMarkersExpire(string kind, int ageMinutes, bool retried)
    {
        var good = new FakeRunner((a, _) => WriteOutput(a, 4000));
        var svc = Svc(good, (1, File1()));
        var cacheDir = Path.Combine(_t.Dir, "img", "thumbs"); Directory.CreateDirectory(cacheDir);
        var ticks = new FileInfo(Path.Combine(_t.Dir, "m.mp4")).LastWriteTimeUtc.Ticks;
        var marker = Path.Combine(cacheDir, $"1-640-{ticks:x}.jpg.fail");
        File.WriteAllText(marker, kind); File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-ageMinutes));
        var result = await svc.GetAsync(1, 640, default);
        Assert.Equal(retried, result is not null);
        Assert.Equal(retried ? 1 : 0, good.Calls.Count);
    }

    [Fact]
    public async Task OnlyAFewThumbnailsAreExtractedAtOnce()
    {
        int running = 0, peak = 0;
        var runner = new FakeRunner(async (a, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) peak = Math.Max(peak, now);
            await Task.Delay(120);
            Interlocked.Decrement(ref running);
            return await WriteOutput(a, 4000);
        });
        var files = Enumerable.Range(1, 8).Select(i => ((long)i, File1($"m{i}.mp4"))).ToArray();
        var svc = Svc(runner, files);
        var results = await Task.WhenAll(files.Select(f => svc.GetAsync(f.Item1, 640, default)));
        Assert.All(results, Assert.NotNull);
        Assert.True(peak <= 2, $"peak concurrency was {peak}");
    }
}
