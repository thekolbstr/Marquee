using System.Diagnostics;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public class HlsPlannerAndBuilderTests
{
    private static readonly HlsOptions O = new();
    private static MediaInfo Info(string vcodec, string pix, int h, params (string Codec, int Ch)[] audio) =>
        new("matroska", 60, 1, 1, [new VideoTrack(3, vcodec, null, 1920, h, 24, pix, null)],
            audio.Select((a, i) => new AudioTrack(7 + i, a.Codec, a.Ch, null, 48000, null, null, i == 0, null)).ToList(), []);

    [Fact] public void CompatibleVideo_IsCopied_NotReencoded()
    {
        var p = HlsPlanner.Decide(Info("h264", "yuv420p", 1080, ("aac", 2)), O)!;
        Assert.Equal((VideoMode.Copy, AudioMode.Copy, "remux"), (p.Video, p.Audio, p.Mode));
        Assert.Equal((3, 7), (p.VideoStream, p.AudioStream!.Value)); // absolute stream indexes
    }

    [Theory]
    [InlineData("h264", "yuv444p", 1080)]
    [InlineData("h264", "yuv420p10le", 1080)]
    [InlineData("hevc", "yuv420p", 1080)]
    [InlineData("mpeg4", "yuv420p", 480)]
    public void IncompatibleVideo_IsEncoded(string codec, string pix, int h)
        => Assert.Equal(VideoMode.Encode, HlsPlanner.Decide(Info(codec, pix, h, ("aac", 2)), O)!.Video);

    [Theory]
    [InlineData("aac", 2, AudioMode.Copy)]
    [InlineData("aac", 6, AudioMode.Aac)]   // 5.1 is downmixed
    [InlineData("eac3", 6, AudioMode.Aac)]
    [InlineData("dts", 6, AudioMode.Aac)]
    public void AudioDecision(string codec, int ch, AudioMode expected)
        => Assert.Equal(expected, HlsPlanner.Decide(Info("h264", "yuv420p", 1080, (codec, ch)), O)!.Audio);

    [Fact] public void TallH264IsStillCopied_MaxHeightOnlyAppliesWhenEncoding()
        => Assert.Equal(VideoMode.Copy, HlsPlanner.Decide(Info("h264", "yuv420p", 2160, ("aac", 2)), O)!.Video);

    [Fact] public void Hevc_IsCopiedIntoFragmentedMp4_WhenTheClientCanDecodeIt()
    {
        var p = HlsPlanner.Decide(Info("hevc", "yuv420p", 2160, ("eac3", 6)), O, PlaybackPolicyTests.Chrome, ".mkv")!;
        Assert.Equal((VideoMode.Copy, AudioMode.Aac, HlsSegments.Fmp4, "hvc1", "remux"), (p.Video, p.Audio, p.Segments, p.VideoTag, p.Mode));
        Assert.Contains("untouched", p.Reason);
        var ten = HlsPlanner.Decide(Info("hevc", "yuv420p10le", 1080, ("aac", 2)), O, PlaybackPolicyTests.Chrome, ".mkv")!;
        Assert.Equal((VideoMode.Copy, AudioMode.Copy), (ten.Video, ten.Audio));
    }

    [Fact] public void Hevc_IsEncodedToH264_WhenTheClientCannotDecodeIt()
    {
        foreach (var caps in new[] { ClientCaps.Baseline, PlaybackPolicyTests.Firefox })
        {
            var p = HlsPlanner.Decide(Info("hevc", "yuv420p", 1080, ("aac", 2)), O, caps, ".mkv")!;
            Assert.Equal((VideoMode.Encode, HlsSegments.MpegTs, (string?)null), (p.Video, p.Segments, p.VideoTag));
        }
    }

    [Fact] public void ForceTranscode_OverridesCopy_ForBrowsersThatClaimedSupportButFailed()
    {
        var p = HlsPlanner.Decide(Info("hevc", "yuv420p", 1080, ("aac", 2)), O, PlaybackPolicyTests.Chrome, ".mkv", forceTranscode: true)!;
        Assert.Equal(VideoMode.Encode, p.Video);
        Assert.Contains("Transcoding", p.Reason);
        Assert.Contains("direct stream failed", p.Reason);
    }

    [Fact] public void H264Copy_KeepsTheProvenMpegTsPath()
        => Assert.Equal((HlsSegments.MpegTs, (string?)null), (HlsPlanner.Decide(Info("h264", "yuv420p", 1080, ("aac", 2)), O)!.Segments, HlsPlanner.Decide(Info("h264", "yuv420p", 1080, ("aac", 2)), O)!.VideoTag));

    [Fact] public void Fmp4HevcCopy_Args()
    {
        var plan = new HlsPlan(0, 1, VideoMode.Copy, AudioMode.Aac, HlsSegments.Fmp4, "hvc1");
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", plan, O);
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
        Assert.Equal("fmp4", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("init.mp4", args[args.IndexOf("-hls_fmp4_init_filename") + 1]);
        Assert.Equal("/tmp/s/seg%05d.m4s", args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.DoesNotContain("libx264", args); Assert.DoesNotContain("h264_nvenc", args);
    }

    [Fact] public void MpegTsPlan_HasNoFmp4Args()
    {
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", new HlsPlan(0, 1, VideoMode.Copy, AudioMode.Copy), O);
        Assert.DoesNotContain("-hls_fmp4_init_filename", args); Assert.DoesNotContain("-tag:v", args);
        Assert.Equal("/tmp/s/seg%05d.ts", args[args.IndexOf("-hls_segment_filename") + 1]);
    }

    [Fact] public void NoAudio_IsMapped_AsNone() => Assert.Equal((AudioMode.None, null), (HlsPlanner.Decide(Info("h264", "yuv420p", 720), O)!.Audio, HlsPlanner.Decide(Info("h264", "yuv420p", 720), O)!.AudioStream));
    [Fact] public void NoVideo_ReturnsNull() => Assert.Null(HlsPlanner.Decide(new MediaInfo("mp3", 1, 1, 1, [], [], []), O));

    [Fact]
    public void Copy_Args_AreDiscrete_AndInputFollowsDashI()
    {
        var args = HlsCommandBuilder.Build("/media/-evil name; rm -rf ~ $(x).mkv", "/tmp/s", new HlsPlan(0, 1, VideoMode.Copy, AudioMode.Copy), O);
        var i = args.IndexOf("-i");
        Assert.Equal("/media/-evil name; rm -rf ~ $(x).mkv", args[i + 1]);       // one verbatim element
        Assert.Equal(1, args.Count(a => a.Contains("rm -rf")));
        Assert.Contains("copy", args); Assert.DoesNotContain("libx264", args);
        Assert.Equal("/tmp/s/index.m3u8", args[^1]);
        Assert.Contains("-nostdin", args);
        Assert.Equal(["-map", "0:0"], args.Skip(args.IndexOf("-map")).Take(2));
    }

    [Fact]
    public void Encode_Libx264_ForcesSegmentAlignedKeyframes_AndNeverUpscales()
    {
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", new HlsPlan(0, 1, VideoMode.Encode, AudioMode.Aac), new HlsOptions { SegmentSeconds = 4 });
        Assert.Contains("libx264", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("expr:gte(t,n_forced*4)", args);
        Assert.Contains("scale=-2:min(1080\\,ih)", args);
        Assert.Equal("4", args[args.IndexOf("-hls_time") + 1]);
        Assert.Contains("independent_segments+temp_file", args);
    }

    [Fact]
    public void Encode_Nvenc()
    {
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", new HlsPlan(0, null, VideoMode.Encode, AudioMode.None), new HlsOptions { Encoder = "nvenc" });
        Assert.Contains("h264_nvenc", args); Assert.DoesNotContain("libx264", args); Assert.Contains("-an", args);
    }

    [Fact] public void OptionsFromConfig_ClampsAndNormalizes()
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Hls:Encoder"] = "NVENC", ["Hls:MaxSessions"] = "0", ["Hls:SegmentSeconds"] = "9999", ["Hls:Encoder2"] = "x" }).Build();
        var o = HlsOptions.From(c);
        Assert.Equal(("nvenc", 1, 20), (o.Encoder, o.MaxSessions, o.SegmentSeconds));
        Assert.Equal("libx264", HlsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hls:Encoder"] = "rm -rf" }).Build()).Encoder);
    }
}

/// <summary>Lifecycle tests use a fake "ffmpeg" script so process behavior is deterministic.</summary>
internal static class HlsWait
{
    /// <summary>Waits until a playlist is complete AND every segment it names exists (a synthesized VOD playlist is complete from the start).</summary>
    public static async Task CompleteAsync(string playlist, int seconds = 30)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (File.Exists(playlist))
            {
                var text = File.ReadAllText(playlist);
                var dir = Path.GetDirectoryName(playlist)!;
                var segs = text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                if (text.Contains("#EXT-X-ENDLIST") && segs.Count > 0 && segs.All(f => File.Exists(Path.Combine(dir, f)))) return;
            }
            await Task.Delay(100);
        }
        var dir2 = Path.GetDirectoryName(playlist)!;
        Assert.Fail($"HLS output never completed. Dir has: [{string.Join(", ", Directory.Exists(dir2) ? Directory.GetFiles(dir2).Select(Path.GetFileName) : [])}]\n{(File.Exists(playlist) ? File.ReadAllText(playlist) : "(no playlist)")}");
    }
}

internal static class FakeFfmpeg
{
    public const string GoodBody = """
        start=0; prev=""; ss=""
        for a in "$@"; do
          [ "$prev" = "-start_number" ] && start=$a
          [ "$prev" = "-ss" ] && ss=$a
          prev=$a; last=$a
        done
        dir=$(dirname "$last")
        echo "pid=$$ start_number=$start ss=${ss:-none}" >> "$(dirname "$dir")/runs.log"
        printf '#EXTM3U\n#EXTINF:6.0,\nseg00000.ts\n' > "$last"
        i=$start; n=0
        while [ $n -lt 3 ]; do printf 'SEG' > "$(printf "$dir/seg%05d.ts" $i)"; i=$((i+1)); n=$((n+1)); done
        exec sleep 120
        """ + "\n";
}

public sealed class HlsLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hls").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly HlsSessionManager _m;
    private readonly string _tmp;

    public HlsLifecycleTests() : this("good") { }

    private HlsLifecycleTests(string mode)
    {
        _tmp = Path.Combine(_dir, "hls");
        _m = Make(mode);
    }

    private HlsSessionManager Make(string mode, int max = 2, int idle = 120, int startTimeout = 5)
    {
        var script = Path.Combine(_dir, $"fake-ffmpeg-{mode}.sh");
        var body = mode switch
        {
            // Behaves like ffmpeg's HLS output: writes its playlist (last arg) and 3 segments starting at -start_number, logs the run, then stays alive.
            "good" => FakeFfmpeg.GoodBody,
            "fail" => "echo 'Unknown encoder xyz' 1>&2\nexit 1\n",
            "silent" => "exec sleep 120\n",
            _ => throw new ArgumentException(mode),
        };
        File.WriteAllText(script, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var opts = new HlsOptions { FfmpegPath = script, TempDir = _tmp, MaxSessions = max, IdleSeconds = idle, StartTimeoutSeconds = startTimeout };
        return new HlsSessionManager(new Resolver(), new FakeProbe(), opts, _time, NullLogger<HlsSessionManager>.Instance);
    }

    public void Dispose() { _m.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Resolver : IMediaResolver
    {
        public string? ResolvePath(long id) => id is >= 1 and <= 7 ? $"/library/movie{id}.mkv" : null;
    }
    private sealed class FakeProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var h264 = path.EndsWith("movie6.mkv");
            double? duration = path.EndsWith("movie7.mkv") ? null : 600;
            return Task.FromResult(new MediaInfo("matroska", duration, 1, 1,
                [new VideoTrack(0, h264 ? "h264" : "hevc", null, 1920, 1080, 24, "yuv420p", null)],
                [new AudioTrack(1, h264 ? "aac" : "eac3", h264 ? 2 : 6, null, 48000, null, null, true, null)], []));
        }
    }

    private static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }

    [Fact]
    public async Task Start_ReturnsOnlyOncePlayable_AndCreatesDir()
    {
        var s = await _m.StartAsync(1, default);
        Assert.True(s.IsRunning);
        Assert.Matches("^[0-9a-f]{32}$", s.Id);
        Assert.Contains("#EXTINF", File.ReadAllText(Path.Combine(s.Dir, "index.m3u8")));
        Assert.Equal("transcode", s.Mode);
        Assert.Same(s, _m.Get(s.Id));
    }

    [Fact]
    public async Task Stop_KillsProcess_RemovesDir_IsIdempotent()
    {
        var s = await _m.StartAsync(1, default);
        var pid = s.ProcessId;
        Assert.True(Alive(pid));
        await _m.StopAsync(s.Id);
        await _m.StopAsync(s.Id);
        Assert.False(Alive(pid));
        Assert.False(Directory.Exists(s.Dir));
        Assert.Null(_m.Get(s.Id));
    }

    [Fact]
    public async Task IdleSessions_AreReaped_ButTouchedOnesSurvive()
    {
        var a = await _m.StartAsync(1, default);
        var b = await _m.StartAsync(2, default);
        _time.Advance(TimeSpan.FromSeconds(100));
        _m.Touch(b);                                   // b is "playing"
        _time.Advance(TimeSpan.FromSeconds(50));       // a idle 150s > 120s, b idle 50s
        await _m.ReapIdleAsync();
        Assert.False(Alive(a.ProcessId));
        Assert.Null(_m.Get(a.Id));
        Assert.True(Alive(b.ProcessId));
        Assert.NotNull(_m.Get(b.Id));
    }

    [Fact]
    public async Task SegmentRequests_CountAsActivity()
    {
        var s = await _m.StartAsync(1, default);
        File.WriteAllText(Path.Combine(s.Dir, "seg00000.ts"), "x");
        _time.Advance(TimeSpan.FromSeconds(110));
        Assert.NotNull(await _m.GetFileAsync(s, "seg00000.ts", default));   // touches
        _time.Advance(TimeSpan.FromSeconds(110));
        await _m.ReapIdleAsync();
        Assert.True(s.IsRunning);
    }

    [Fact]
    public async Task StartingMoreThanMaxSessions_EvictsLeastRecentlyUsed()
    {
        using var m = Make("good", max: 2);
        var a = await m.StartAsync(1, default);
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = await m.StartAsync(2, default);
        _time.Advance(TimeSpan.FromSeconds(1));
        m.Touch(a);                                    // b is now the LRU
        var c = await m.StartAsync(3, default);
        Assert.Equal(2, m.ActiveCount);
        Assert.False(Alive(b.ProcessId));
        Assert.True(Alive(a.ProcessId)); Assert.True(Alive(c.ProcessId));
    }

    [Fact]
    public async Task RestartingSameMedia_ReplacesThePreviousSession()
    {
        var a = await _m.StartAsync(1, default);
        var b = await _m.StartAsync(1, default);
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(Alive(a.ProcessId));
        Assert.Equal(1, _m.ActiveCount);
    }

    [Fact]
    public async Task FfmpegFailure_SurfacesStderr_AndLeavesNothingBehind()
    {
        using var m = Make("fail");
        var ex = await Assert.ThrowsAsync<HlsStartException>(() => m.StartAsync(1, default));
        Assert.Contains("Unknown encoder xyz", ex.Message);
        Assert.DoesNotContain("/library", ex.Message);
        Assert.Equal(0, m.ActiveCount);
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Fact]
    public async Task NoOutputWithinTimeout_KillsProcess()
    {
        using var m = Make("silent", startTimeout: 1);
        var ex = await Assert.ThrowsAsync<HlsStartException>(() => m.StartAsync(1, default));
        Assert.Contains("no output", ex.Message);
        Assert.Equal(0, m.ActiveCount);
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Fact]
    public async Task ClientAbortingTheStartRequest_KillsProcess()
    {
        using var m = Make("silent", startTimeout: 30);
        using var cts = new CancellationTokenSource(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.StartAsync(1, cts.Token));
        Assert.Equal(0, m.ActiveCount);
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Fact]
    public async Task StopAll_KillsEverything()
    {
        var a = await _m.StartAsync(1, default);
        var b = await _m.StartAsync(2, default);
        await _m.StopAllAsync();
        Assert.False(Alive(a.ProcessId)); Assert.False(Alive(b.ProcessId));
        Assert.Equal(0, _m.ActiveCount);
    }

    [Fact]
    public async Task UnknownMedia_And_MissingFfmpeg()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _m.StartAsync(99, default));
        var bad = new HlsSessionManager(new Resolver(), new FakeProbe(), new HlsOptions { FfmpegPath = "/nonexistent/ffmpeg", TempDir = _tmp }, _time, NullLogger<HlsSessionManager>.Instance);
        var ex = await Assert.ThrowsAsync<HlsStartException>(() => bad.StartAsync(1, default));
        Assert.Contains("Could not start ffmpeg", ex.Message);
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Fact]
    public void LeftoverDirsFromACrashAreCleanedAtStartup()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "deadbeef"));
        File.WriteAllText(Path.Combine(_tmp, "deadbeef", "seg00000.ts"), "x");
        using var fresh = Make("good");
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Theory]
    [InlineData("index.m3u8", true)] [InlineData("seg00001.ts", true)] [InlineData("seg1.ts", false)]
    [InlineData("../index.m3u8", false)] [InlineData("index.m3u8.bak", false)] [InlineData("seg00001.ts/../../x", false)]
    [InlineData("INDEX.M3U8", false)] [InlineData("", false)]
    [InlineData("init.mp4", true)] [InlineData("seg00001.m4s", true)] [InlineData("init.mp4.x", false)] [InlineData("seg1.m4s", false)] [InlineData("../init.mp4", false)] [InlineData("seg00001.mp4", false)]
    public void FileNameWhitelist(string name, bool ok) => Assert.Equal(ok, HlsSessionManager.IsValidFileName(name));

    [Fact]
    public async Task GetFile_RejectsNonWhitelistedNames_AndSegmentsBeyondTheEndOfTheFile()
    {
        var s = await _m.StartAsync(1, default);
        Assert.Null(await _m.GetFileAsync(s, "../../etc/passwd", default));
        Assert.Null(await _m.GetFileAsync(s, "seg1.ts", default));
        Assert.Equal(100, s.SegmentCount);                                         // 600 s / 6 s
        Assert.Null(await _m.GetFileAsync(s, "seg00100.ts", default));              // one past the end: nothing to wait for, no restart
        Assert.Null(await _m.GetFileAsync(s, "seg09999.ts", default));
        Assert.Single(File.ReadAllLines(Path.Combine(_tmp, "runs.log")));           // none of that started another ffmpeg
    }

}

/// <summary>End-to-end with real ffmpeg: the output must actually be browser-compatible, not merely produced.</summary>
public sealed class HlsRealFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hlsreal").FullName;
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Have(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));

    private async Task<string> Gen(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var r = await new MediaServer.Api.Processes.ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", .. args, path], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        return path;
    }

    private string LastDir = "";

    private sealed class OneFile(string path) : IMediaResolver { public string? ResolvePath(long id) => id == 1 ? path : null; }

    private async Task<(MediaInfo Out, string Mode)> RunThrough(string input, ClientCaps? caps = null)
    {
        var cfg = new ConfigurationBuilder().Build();
        var runner = new MediaServer.Api.Processes.ProcessRunner();
        var probe = new FfprobeMediaProbe(runner, cfg);
        using var m = new HlsSessionManager(new OneFile(input), probe, new HlsOptions { TempDir = Path.Combine(_dir, "hls"), SegmentSeconds = 2 }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
        var s = await m.StartAsync(1, default, caps);
        LastDir = s.Dir;

        var playlist = Path.Combine(s.Dir, "index.m3u8");
        await HlsWait.CompleteAsync(playlist);

        // Decode the produced playlist with ffprobe, as a player would.
        var r = await runner.RunAsync("ffprobe", ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-allowed_extensions", "ALL", "-i", playlist], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        return (FfprobeParser.Parse(r.StdOut), s.Mode);
    }

    [Fact]
    public async Task Yuv444Source_IsTranscodedToBrowserSafeH264_AndPlays()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        var src = await Gen("t444.mp4", "-f", "lavfi", "-i", "testsrc=d=5:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=5",
            "-c:v", "libx264", "-pix_fmt", "yuv444p", "-c:a", "aac", "-shortest");
        var (o, mode) = await RunThrough(src);
        Assert.Equal("transcode", mode);
        var v = Assert.Single(o.Video);
        Assert.Equal(("h264", "yuv420p"), (v.Codec, v.PixelFormat));
        Assert.Equal("aac", Assert.Single(o.Audio).Codec);
        Assert.InRange(o.DurationSeconds!.Value, 4.5, 5.6);
    }

    [Fact]
    public async Task CompatibleVideoInMkvWithAc3_IsRemuxed_AudioBecomesAac()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        var src = await Gen("ac3.mkv", "-f", "lavfi", "-i", "testsrc=d=5:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=5",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "ac3", "-shortest");
        var (o, mode) = await RunThrough(src);
        Assert.Equal("remux", mode);
        Assert.Equal(("h264", "yuv420p"), (o.Video[0].Codec, o.Video[0].PixelFormat));
        Assert.Equal("aac", o.Audio[0].Codec);
        Assert.InRange(o.DurationSeconds!.Value, 4.5, 5.6);
    }

    [Fact]
    public async Task VideoOnlyFile_Works()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        var src = await Gen("silent.mp4", "-f", "lavfi", "-i", "testsrc=d=3:s=320x240:r=25", "-c:v", "mpeg4");
        var (o, _) = await RunThrough(src);
        Assert.Equal("h264", o.Video[0].Codec);
        Assert.Empty(o.Audio);
    }
}

public sealed class HlsEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hlsep").FullName;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _c;

    public HlsEndpointTests()
    {
        var script = Path.Combine(_dir, "ffmpeg.sh");
        File.WriteAllText(script, "#!/bin/sh\nfor last; do :; done\nprintf '#EXTM3U\\n#EXTINF:6.0,\\nseg00000.ts\\n' > \"$last\"\nprintf 'SEGMENT' > \"$(dirname \"$last\")/seg00000.ts\"\nexec sleep 120\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", Path.Combine(_dir, "t.db")).UseSetting("Library:Root", _dir).UseSetting("Library:AutoScan", "false")
             .UseSetting("Tools:FfmpegPath", script).UseSetting("Hls:TempDir", Path.Combine(_dir, "hls"));
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IMediaResolver>(new Fixed());
                s.AddSingleton<IMediaProbe>(new Probe());
            });
        });
        _c = _factory.CreateClient();
    }

    public void Dispose() { _c.Dispose(); _factory.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Fixed : IMediaResolver { public string? ResolvePath(long id) => id == 1 ? "/x/movie.mkv" : null; }
    private sealed class Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) =>
            Task.FromResult(new MediaInfo("matroska", 60, 1, 1, [new VideoTrack(0, "h264", null, 640, 360, 25, "yuv444p", null)], [], []));
    }

    private static StringContent Json(string s) => new(s, System.Text.Encoding.UTF8, "application/json");

    private async Task<(string Id, string Url)> Start()
    {
        var res = await _c.PostAsync("/api/hls/start", Json("{\"mediaId\":1}"));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("sessionId").GetString()!, doc.RootElement.GetProperty("playlistUrl").GetString()!);
    }

    [Fact]
    public async Task StartServePingStop_FullHttpLifecycle()
    {
        var (id, url) = await Start();
        var pl = await _c.GetAsync(url);
        Assert.Equal("application/vnd.apple.mpegurl", pl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", pl.Headers.CacheControl!.ToString());
        Assert.Contains("#EXTINF", await pl.Content.ReadAsStringAsync());

        var seg = await _c.GetAsync($"/api/hls/{id}/seg00000.ts");
        Assert.Equal("video/mp2t", seg.Content.Headers.ContentType!.MediaType);
        Assert.Equal("SEGMENT", await seg.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await _c.PostAsync($"/api/hls/{id}/ping", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await _c.PostAsync($"/api/hls/{id}/stop", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await _c.PostAsync($"/api/hls/{id}/stop", null)).StatusCode); // idempotent
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.GetAsync(url)).StatusCode);                          // gone
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.PostAsync($"/api/hls/{id}/ping", null)).StatusCode);
    }

    [Theory]
    [InlineData("seg1.ts")]
    [InlineData("index.m3u8.bak")]
    [InlineData("..%2Findex.m3u8")]
    [InlineData("%2e%2e%2f%2e%2e%2fetc%2fpasswd")]
    [InlineData("INDEX.M3U8")]
    public async Task NonWhitelistedFiles404_EvenForALiveSession(string file)
    {
        var (id, _) = await Start();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.GetAsync($"/api/hls/{id}/{file}")).StatusCode);
    }

    [Theory]
    [InlineData("0123")] [InlineData("00000000000000000000000000000000")] [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task UnknownOrMalformedSessionIds404(string id)
    {
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.GetAsync($"/api/hls/{id}/index.m3u8")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.PostAsync($"/api/hls/{id}/ping", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await _c.PostAsync($"/api/hls/{id}/stop", null)).StatusCode); // harmless
    }

    [Theory]
    [InlineData("..", false)] [InlineData("../x", false)] [InlineData("", false)] [InlineData("0123456789abcdef0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    public void SessionIdFormat(string id, bool ok) => Assert.Equal(ok, HlsSessionManager.IsValidSessionId(id));

    [Fact]
    public async Task StartUnknownMedia_404()
        => Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _c.PostAsync("/api/hls/start", Json("{\"mediaId\":99}"))).StatusCode);

    [Fact]
    public async Task SessionIdsAreUnique_AndNotSequential()
    {
        var (a, _) = await Start();
        var (b, _) = await Start();
        Assert.NotEqual(a, b);
    }
}

/// <summary>HEVC pass-through: the point of the capability-driven policy is that browsers which can decode HEVC never trigger a transcode.</summary>
public sealed class HlsHevcRealFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hlshevc").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Have(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));
    private static bool HasEncoder(string name)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-hide_banner -encoders") { RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o.Contains(name);
    }

    private sealed class OneFile(string path) : IMediaResolver { public string? ResolvePath(long id) => id == 1 ? path : null; }

    private async Task<string> HevcMkvWithEac3()
    {
        var path = Path.Combine(_dir, "hevc.mkv");
        var r = await new MediaServer.Api.Processes.ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=d=6:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=6",
            "-c:v", "libx265", "-pix_fmt", "yuv420p", "-x265-params", "keyint=25:log-level=error", "-c:a", "eac3", path], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        return path;
    }

    private async Task<(string Dir, string Mode, string Reason, string Playlist)> Run(string input, ClientCaps caps, bool force = false)
    {
        var runner = new MediaServer.Api.Processes.ProcessRunner();
        using var m = new HlsSessionManager(new OneFile(input), new FfprobeMediaProbe(runner, new ConfigurationBuilder().Build()),
            new HlsOptions { TempDir = Path.Combine(_dir, "hls"), SegmentSeconds = 2 }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
        var s = await m.StartAsync(1, default, caps, force);
        var playlist = Path.Combine(s.Dir, "index.m3u8");
        await HlsWait.CompleteAsync(playlist);
        // copy everything out before the manager (and the session dir) is disposed
        var keep = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(keep);
        foreach (var f in Directory.GetFiles(s.Dir)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)));
        return (keep, s.Mode, s.Reason, Path.Combine(keep, "index.m3u8"));
    }

    private static async Task<(string Json, string DecodeErrors)> Inspect(string playlist)
    {
        var runner = new MediaServer.Api.Processes.ProcessRunner();
        var probe = await runner.RunAsync("ffprobe", ["-v", "error", "-print_format", "json", "-show_streams", "-allowed_extensions", "ALL", "-i", playlist], default);
        Assert.Equal(0, probe.ExitCode);
        var decode = await runner.RunAsync("ffmpeg", ["-v", "error", "-allowed_extensions", "ALL", "-i", playlist, "-f", "null", "-"], default);
        return (probe.StdOut, decode.StdErr.Trim() + (decode.ExitCode != 0 ? $" [exit {decode.ExitCode}]" : ""));
    }

    [Fact]
    public async Task HevcMkvWithEac3_ForAHevcCapableBrowser_IsCopiedNotReencoded_AndDecodesCleanly()
    {
        if (!Have("ffmpeg") || !Have("ffprobe") || !HasEncoder("libx265")) return;
        var src = await HevcMkvWithEac3();
        var (dir, mode, reason, playlist) = await Run(src, PlaybackPolicyTests.Chrome);

        Assert.Equal("remux", mode);
        Assert.Contains("untouched", reason);
        Assert.True(File.Exists(Path.Combine(dir, "init.mp4")), "fMP4 init segment missing");
        Assert.NotEmpty(Directory.GetFiles(dir, "seg*.m4s"));
        Assert.Empty(Directory.GetFiles(dir, "*.ts"));
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"", File.ReadAllText(playlist));

        var (json, decodeErrors) = await Inspect(playlist);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var v = streams.Single(x => x.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("hevc", v.GetProperty("codec_name").GetString());
        Assert.Equal("hvc1", v.GetProperty("codec_tag_string").GetString());   // hev1 is rejected by Safari and Chrome in HLS
        Assert.Equal("aac", streams.Single(x => x.GetProperty("codec_type").GetString() == "audio").GetProperty("codec_name").GetString());
        Assert.Equal("", decodeErrors);                                        // every packet decodes without error
    }

    [Fact]
    public async Task SameFile_ForABrowserWithoutHevc_IsTranscodedToH264()
    {
        if (!Have("ffmpeg") || !Have("ffprobe") || !HasEncoder("libx265")) return;
        var (dir, mode, _, playlist) = await Run(await HevcMkvWithEac3(), PlaybackPolicyTests.Firefox);
        Assert.Equal("transcode", mode);
        Assert.NotEmpty(Directory.GetFiles(dir, "*.ts"));
        var (json, decodeErrors) = await Inspect(playlist);
        Assert.Contains("\"codec_name\": \"h264\"", json);
        Assert.Equal("", decodeErrors);
    }

    [Fact]
    public async Task ForceTranscode_ReencodesEvenForAHevcCapableBrowser()
    {
        if (!Have("ffmpeg") || !Have("ffprobe") || !HasEncoder("libx265")) return;
        var (_, mode, reason, playlist) = await Run(await HevcMkvWithEac3(), PlaybackPolicyTests.Chrome, force: true);
        Assert.Equal("transcode", mode);
        Assert.Contains("direct stream failed", reason);
        Assert.Contains("\"codec_name\": \"h264\"", (await Inspect(playlist)).Json);
    }
}
