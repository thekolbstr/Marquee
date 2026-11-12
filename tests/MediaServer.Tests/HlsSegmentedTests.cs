using System.Diagnostics;
using System.Globalization;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Tests;

public class HlsQualityAndPlaylistTests
{
    private static readonly HlsOptions O = new();
    private static MediaInfo Src(int width, double? duration = 600, string codec = "hevc", string audio = "aac") =>
        new("matroska", duration, 1, 1, [new VideoTrack(2, codec, null, width, width * 9 / 16, 24, "yuv420p", null)],
            [new AudioTrack(5, audio, 2, null, 48000, null, null, true, null)], []);

    [Theory]
    [InlineData("720p", true)] [InlineData("720P", true)] [InlineData("1080p", true)] [InlineData("360p", true)]
    [InlineData("4k", false)] [InlineData("720", false)] [InlineData("720p; rm -rf /", false)] [InlineData("", false)] [InlineData(null, false)]
    public void OnlyKnownQualitiesAreAccepted(string? name, bool known) => Assert.Equal(known, HlsQuality.Find(name) is not null);

    [Theory]
    [InlineData(1920, 1920, true)] [InlineData(1916, 1920, true)] [InlineData(3840, 1920, true)]   // 1916 = cropped 1080p
    [InlineData(1280, 1920, false)] [InlineData(640, 1280, false)] [InlineData(null, 1280, false)]
    public void AQualityNeverUpscales(int? sourceWidth, int qualityWidth, bool applies)
        => Assert.Equal(applies, HlsQuality.All.First(q => q.MaxWidth == qualityWidth).AppliesTo(sourceWidth));

    [Fact]
    public void ChoosingAQuality_ForcesASegmentedTranscode_EvenWhenTheVideoCouldBeCopied()
    {
        var chrome = PlaybackPolicyTests.Chrome;
        var copy = HlsPlanner.Decide(Src(1920, codec: "h264"), O, chrome, ".mkv")!;
        Assert.Equal((VideoMode.Copy, false), (copy.Video, copy.Vod));                                   // no choice: untouched

        var p = HlsPlanner.Decide(Src(1920, codec: "h264"), O, chrome, ".mkv", quality: "720p")!;
        Assert.Equal((VideoMode.Encode, true, 1280, 4000), (p.Video, p.Vod, p.MaxWidth, p.VideoKbps));
        Assert.Equal(600, p.DurationSeconds);
        Assert.Equal("Transcoding to 720p (your choice)", p.Reason);
        Assert.Equal(AudioMode.Aac, p.Audio);                                                            // segmented => audio re-encoded too
        Assert.Equal(160, p.AudioKbps);
    }

    [Fact] public void AQualityLargerThanTheSourceIsIgnored()
    {
        var p = HlsPlanner.Decide(Src(1280, codec: "h264"), O, PlaybackPolicyTests.Chrome, ".mkv", quality: "1080p")!;
        Assert.Equal((VideoMode.Copy, (int?)null), (p.Video, p.MaxWidth));
    }

    [Fact] public void SourcesWithoutADuration_FallBackToTheGrowingPlaylist()
        => Assert.False(HlsPlanner.Decide(Src(1920, duration: null), O)!.Vod);

    [Fact] public void CopiedVideo_NeverUsesTheSegmentedPath()
        => Assert.False(HlsPlanner.Decide(Src(1920, codec: "h264"), O, PlaybackPolicyTests.Chrome, ".mkv")!.Vod);

    [Fact] public void UnknownQualityStringsDoNothing()
        => Assert.Equal(VideoMode.Copy, HlsPlanner.Decide(Src(1920, codec: "h264"), O, PlaybackPolicyTests.Chrome, ".mkv", quality: "999p; evil")!.Video);

    [Fact]
    public void SegmentedBuilder_StartsAtTheRightPlace_AndStampsTheSourceTimeline()
    {
        var plan = HlsPlanner.Decide(Src(1920, codec: "hevc"), O, ClientCaps.Baseline, ".mkv", quality: "720p")!;
        var args = HlsCommandBuilder.Build("/media/-odd name.mkv", "/tmp/s", plan, new HlsOptions { SegmentSeconds = 6 }, startIndex: 16);
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal("96", After("-ss"));                                                     // 16 × 6
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"), "input seek must precede -i");
        Assert.Equal("/media/-odd name.mkv", After("-i"));
        Assert.Equal("96.021333", After("-output_ts_offset"));                                // segment 16 = 96 s, plus the constant AAC-priming shift every run uses
        Assert.Equal("16", After("-start_number"));
        Assert.Equal("0", After("-muxdelay"));
        Assert.Equal("expr:gte(t,n_forced*6)", After("-force_key_frames"));
        Assert.Equal("scale=min(1280\\,iw):-2", After("-vf"));
        Assert.Equal(["-maxrate", "4000k", "-bufsize", "8000k"], args.Skip(args.IndexOf("-maxrate")).Take(4));
        Assert.Equal("160k", After("-b:a"));
        Assert.Equal("48000", After("-ar"));                                                  // fixed rate so the priming constant is known
        Assert.Equal("0", After("-bf"));                                                      // no B-frames: run-dependent reorder delay would misalign seams
        Assert.Equal("/tmp/s/ffmpeg.m3u8", args[^1]);                                         // ffmpeg's own playlist, not the served one
        Assert.DoesNotContain("-hls_playlist_type", args);
        Assert.Equal("/tmp/s/seg%05d.ts", After("-hls_segment_filename"));
    }

    [Fact]
    public void TheEncodingSpeedAndQualitySettings_ReachTheEncoder()
    {
        var o = new HlsOptions { Preset = "slow", Crf = 18 };
        var full = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", HlsPlanner.Decide(Src(1920, codec: "hevc"), o, ClientCaps.Baseline, ".mkv")!, o);
        Assert.Equal(("slow", "18"), (full[full.IndexOf("-preset") + 1], full[full.IndexOf("-crf") + 1]));
        var capped = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", HlsPlanner.Decide(Src(1920, codec: "hevc"), o, ClientCaps.Baseline, ".mkv", quality: "480p")!, o);
        Assert.Equal("20", capped[capped.IndexOf("-crf") + 1]);                                 // bitrate-capped streams stay a little softer
    }

    [Theory] [InlineData("slow", "slow")] [InlineData("SLOW", "slow")] [InlineData("placebo", "veryfast")] [InlineData("; rm -rf /", "veryfast")] [InlineData(null, "veryfast")]
    public void OnlyKnownPresetsAreAccepted(string? preset, string expected)
        => Assert.Equal(expected, HlsOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hls:Preset"] = preset }).Build()).Preset);

    [Theory] [InlineData("3", 14)] [InlineData("99", 30)] [InlineData("23", 23)] [InlineData("x", 21)]
    public void TheCrfIsKeptInASaneRange(string crf, int expected)
        => Assert.Equal(expected, HlsOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hls:Crf"] = crf }).Build()).Crf);

    [Theory]
    [InlineData("videotoolbox", "h264_videotoolbox")] [InlineData("qsv", "h264_qsv")] [InlineData("vaapi", "h264_vaapi")] [InlineData("nvenc", "h264_nvenc")]
    public void HardwareEncoders_UseTheirOwnCodec_AndNeverX264(string encoder, string codec)
    {
        var o = new HlsOptions { Encoder = encoder };
        var plan = HlsPlanner.Decide(Src(1920), o, ClientCaps.Baseline, ".mkv", quality: "480p")!;
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", plan, o);
        Assert.Contains(codec, args); Assert.DoesNotContain("libx264", args);
        Assert.Equal(encoder == "vaapi", args.Contains("-vaapi_device"));
        Assert.Equal(encoder == "vaapi", args[args.IndexOf("-vf") + 1].EndsWith("format=nv12,hwupload"));
        if (encoder == "vaapi") Assert.True(args.IndexOf("-vaapi_device") < args.IndexOf("-i"));
    }

    [Theory] [InlineData("videotoolbox", "videotoolbox")] [InlineData("QSV", "qsv")] [InlineData("nonsense", "libx264")] [InlineData(null, "libx264")]
    public void Encoder_FallsBackToSoftware_WhenUnknown(string? value, string expected)
        => Assert.Equal(expected, HlsOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hls:Encoder"] = value }).Build()).Encoder);

    [Fact] public void EncoderTest_UsesTheChosenEncoder()
    {
        Assert.Contains("h264_vaapi", HlsCommandBuilder.BuildEncoderTest(new HlsOptions { Encoder = "vaapi", VaapiDevice = "/dev/dri/renderD129" }));
        Assert.Contains("/dev/dri/renderD129", HlsCommandBuilder.BuildEncoderTest(new HlsOptions { Encoder = "vaapi", VaapiDevice = "/dev/dri/renderD129" }));
        Assert.Contains("libx264", HlsCommandBuilder.BuildEncoderTest(new HlsOptions()));
    }

    [Fact]
    public void SegmentedBuilder_FromTheStart_HasNoSeek_AndNvencUsesConstrainedVbr()
    {
        var plan = HlsPlanner.Decide(Src(1920), new HlsOptions { Encoder = "nvenc" }, ClientCaps.Baseline, ".mkv", quality: "480p")!;
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", plan, new HlsOptions { Encoder = "nvenc" });
        Assert.DoesNotContain("-ss", args);
        Assert.Contains("h264_nvenc", args);
        Assert.Equal("28", args[args.IndexOf("-cq") + 1]);
        Assert.Equal("1800k", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("scale=min(854\\,iw):-2", args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void UnconstrainedTranscodes_KeepTheirOriginalQualitySettings()
    {
        var plan = HlsPlanner.Decide(Src(1920), O, ClientCaps.Baseline, ".mkv")!;           // HEVC for a baseline client, no quality picked
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", plan, O);
        Assert.DoesNotContain("-maxrate", args);
        Assert.Equal("21", args[args.IndexOf("-crf") + 1]);
        Assert.Equal("scale=-2:min(1080\\,ih)", args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void VodPlaylist_ListsEverySegmentUpFront_WithAShortFinalOne()
    {
        var text = HlsCommandBuilder.BuildVodPlaylist(20, 6);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-PLAYLIST-TYPE:VOD"], lines.Take(3));
        Assert.Contains("#EXT-X-TARGETDURATION:6", lines);
        Assert.Equal("#EXT-X-ENDLIST", lines[^1]);
        Assert.Equal(["seg00000.ts", "seg00001.ts", "seg00002.ts", "seg00003.ts"], lines.Where(l => l.StartsWith("seg")));
        Assert.Equal(["6.000", "6.000", "6.000", "2.000"], lines.Where(l => l.StartsWith("#EXTINF")).Select(l => l[8..^1]));
    }

    [Theory]
    [InlineData(6.016, 2, 3)]      // audio 16 ms longer than video: no phantom 4th segment (the bug found by the real-ffmpeg test)
    [InlineData(6.2, 2, 3)]
    [InlineData(6.3, 2, 4)]        // a 0.3 s tail is a real segment
    [InlineData(12, 6, 2)] [InlineData(12.01, 6, 2)] [InlineData(12.5, 6, 3)]
    [InlineData(0.1, 6, 1)] [InlineData(0, 6, 1)] [InlineData(5671.708, 6, 946)]
    public void SegmentCount_FoldsSubFrameTailsIntoTheLastSegment(double duration, int seg, int expected)
        => Assert.Equal(expected, HlsCommandBuilder.SegmentCount(duration, seg));

    [Fact]
    public void FoldedTail_ExtendsTheLastEntry_SoTheTimelineStillAddsUp()
    {
        var text = HlsCommandBuilder.BuildVodPlaylist(6.016, 2);
        var durations = text.Split('\n').Where(l => l.StartsWith("#EXTINF")).Select(l => double.Parse(l[8..^1], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3, durations.Count);
        Assert.Equal(6.016, durations.Sum(), 3);
    }
}

/// <summary>Restart logic, using a fake ffmpeg that makes 3 numbered segments from -start_number and logs every run.</summary>
public sealed class HlsSegmentedManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hlsseg").FullName;
    private readonly string _tmp;
    private readonly HlsSessionManager _m;
    public HlsSegmentedManagerTests()
    {
        _tmp = Path.Combine(_dir, "hls");
        var script = Path.Combine(_dir, "ffmpeg.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + FakeFfmpeg.GoodBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _m = new HlsSessionManager(new Resolver(), new Probe(), new HlsOptions { FfmpegPath = script, TempDir = _tmp, MaxSessions = 3, SegmentWaitSeconds = 1, StartTimeoutSeconds = 5 },
            TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
    }
    public void Dispose() { _m.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Resolver : IMediaResolver { public string? ResolvePath(long id) => id is >= 1 and <= 3 ? $"/library/movie{id}.mkv" : null; }
    private sealed class Probe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var h264 = path.EndsWith("movie2.mkv"); double? dur = path.EndsWith("movie3.mkv") ? null : 600;
            return Task.FromResult(new MediaInfo("matroska", dur, 1, 1, [new VideoTrack(0, h264 ? "h264" : "hevc", null, 1920, 1080, 24, "yuv420p", null)],
                [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []));
        }
    }

    private (int Pid, int Start, string Ss)[] Runs() => File.Exists(Path.Combine(_tmp, "runs.log"))
        ? File.ReadAllLines(Path.Combine(_tmp, "runs.log")).Select(l => l.Split(' ')).Select(p => (int.Parse(p[0][4..]), int.Parse(p[1][13..]), p[2][3..])).ToArray() : [];
    private static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }

    [Fact]
    public async Task StartsAtTheRequestedPosition_AndPublishesTheFullPlaylistImmediately()
    {
        var s = await _m.StartAsync(1, default, startSeconds: 100);
        var run = Assert.Single(Runs());
        Assert.Equal((16, "96"), (run.Start, run.Ss));                                  // 100 s => segment 16 => ffmpeg -ss 96
        Assert.True(File.Exists(Path.Combine(s.Dir, "seg00016.ts")));
        var playlist = File.ReadAllText(Path.Combine(s.Dir, "index.m3u8"));
        Assert.Equal(100, playlist.Split('\n').Count(l => l.StartsWith("#EXTINF")));    // all 100 segments are listed up front
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        Assert.True(s.Vod);
    }

    [Fact]
    public async Task StartingAtZero_HasNoSeek()
    {
        await _m.StartAsync(1, default);
        Assert.Equal((0, "none"), (Runs()[0].Start, Runs()[0].Ss));
    }

    [Theory] [InlineData(-50.0, 0, "none")] [InlineData(10_000.0, 99, "594")]
    public async Task OutOfRangeStartsAreClamped(double start, int index, string ss)
    {
        await _m.StartAsync(1, default, startSeconds: start);
        Assert.Equal((index, ss), (Runs()[0].Start, Runs()[0].Ss));
    }

    [Fact]
    public async Task SegmentsTheEncoderAlreadyMade_AreServedWithoutAnyRestart()
    {
        var s = await _m.StartAsync(1, default, startSeconds: 100);
        Assert.NotNull(await _m.GetFileAsync(s, "seg00017.ts", default));
        Assert.NotNull(await _m.GetFileAsync(s, "seg00018.ts", default));
        Assert.Single(Runs());
    }

    [Fact]
    public async Task ASegmentJustAheadOfTheEncoder_IsWaitedFor_NotRestarted()
    {
        var s = await _m.StartAsync(1, default, startSeconds: 100);          // fake made 16,17,18; next expected is 19
        var sw = Stopwatch.StartNew();
        Assert.Null(await _m.GetFileAsync(s, "seg00021.ts", default));        // within look-ahead: waits (fake never makes it), then gives up
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 4);
        Assert.Single(Runs());                                                // ...and did NOT kill/restart ffmpeg for a 2-segment gap
    }

    [Fact]
    public async Task AFarForwardSeek_RestartsFfmpegThere_AndKillsTheOldRun()
    {
        var s = await _m.StartAsync(1, default, startSeconds: 0);
        var firstPid = Runs()[0].Pid;
        Assert.NotNull(await _m.GetFileAsync(s, "seg00060.ts", default));
        var runs = Runs();
        Assert.Equal([0, 60], runs.Select(r => r.Start));
        Assert.Equal("360", runs[1].Ss);
        Assert.False(Alive(firstPid));                                         // only one ffmpeg per session
        Assert.True(Alive(runs[1].Pid));
        Assert.Equal(runs[1].Pid, s.ProcessId);
    }

    [Fact]
    public async Task SeekingBackBeforeTheRunsStart_Restarts_ButEarlierSegmentsAreStillServed()
    {
        var s = await _m.StartAsync(1, default, startSeconds: 100);            // run A: 16.. 
        Assert.NotNull(await _m.GetFileAsync(s, "seg00005.ts", default));       // before A's start => run B at 5
        Assert.Equal([16, 5], Runs().Select(r => r.Start));
        Assert.NotNull(await _m.GetFileAsync(s, "seg00017.ts", default));       // made by run A earlier, still on disk
        Assert.Equal(2, Runs().Length);                                         // no third run
    }

    [Fact]
    public async Task ManyConcurrentSeeks_NeverLeaveMoreThanOneFfmpegAlive()
    {
        var s = await _m.StartAsync(1, default);
        var targets = new[] { 30, 55, 80, 12, 95, 42, 70, 25 };
        await Task.WhenAll(targets.Select(t => _m.GetFileAsync(s, $"seg{t:00000}.ts", default)));
        await Task.Delay(300);
        var alive = Runs().Select(r => r.Pid).Count(Alive);
        Assert.Equal(1, alive);
    }

    [Fact]
    public async Task StopKillsTheCurrentRun_AndRemovesEverything()
    {
        var s = await _m.StartAsync(1, default);
        await _m.GetFileAsync(s, "seg00070.ts", default);                       // restart once so an older run also existed
        await _m.StopAsync(s.Id);
        Assert.All(Runs(), r => Assert.False(Alive(r.Pid)));
        Assert.False(Directory.Exists(s.Dir));
        Assert.Null(await _m.GetFileAsync(s, "seg00001.ts", default));          // a stopped session can't be restarted by a late request
        Assert.Equal(2, Runs().Length);
    }

    [Fact]
    public async Task SegmentsBeyondTheEnd_AreNotWaitedFor()
    {
        var s = await _m.StartAsync(1, default);
        var sw = Stopwatch.StartNew();
        Assert.Null(await _m.GetFileAsync(s, "seg00100.ts", default));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Single(Runs());
    }

    [Fact]
    public async Task ChoosingAQuality_ForcesTheSegmentedPath_ForAFileThatWouldOtherwiseBeCopied()
    {
        var plain = await _m.StartAsync(2, default, PlaybackPolicyTests.Chrome);                   // h264, Chrome => direct stream
        Assert.Equal(("remux", false), (plain.Mode, plain.Vod));
        var q = await _m.StartAsync(2, default, PlaybackPolicyTests.Chrome, startSeconds: 60, quality: "720p");
        Assert.Equal(("transcode", true), (q.Mode, q.Vod));
        Assert.Equal("Transcoding to 720p (your choice)", q.Reason);
        Assert.Equal(1, _m.ActiveCount);                                                           // the quality change replaced the old session
        Assert.Equal(10, Runs().Last().Start);
    }

    [Fact]
    public async Task UnknownDuration_UsesTheGrowingPlaylist()
    {
        var s = await _m.StartAsync(3, default, startSeconds: 500);
        Assert.False(s.Vod);
        Assert.Equal("none", Runs()[0].Ss);                                                        // can't start mid-way without a known length
    }
}

/// <summary>Real ffmpeg: the pieces from separate runs must line up on one timeline, or seeking would glitch or stall.</summary>
public sealed class HlsSegmentedRealFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hlssegreal").FullName;
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Have(string t) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, t)));
    private sealed class One(string p) : IMediaResolver { public string? ResolvePath(long id) => id == 1 ? p : null; }

    private async Task<string> Clip()
    {
        var path = Path.Combine(_dir, "src.mp4");
        var r = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=d=20:s=1280x720:r=25", "-f", "lavfi", "-i", "sine=d=20",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", path], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        return path;
    }

    private HlsSessionManager Mgr(string clip) => new(new One(clip), new FfprobeMediaProbe(new ProcessRunner(), new ConfigurationBuilder().Build()),
        new HlsOptions { TempDir = Path.Combine(_dir, "hls"), SegmentSeconds = 2, SegmentWaitSeconds = 20 }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);

    private static async Task<double> FirstPts(string file)
    {
        var r = await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file], default);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => double.Parse(l.Trim(','), CultureInfo.InvariantCulture)).Min();
    }

    [Fact]
    public async Task StartingMidFile_ProducesTheRightTimestamps_AndTheRequestedSize()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        using var m = Mgr(await Clip());
        var s = await m.StartAsync(1, default, startSeconds: 8, quality: "480p");
        Assert.Equal(10, s.SegmentCount);                                                          // 20 s / 2 s

        var seg4 = Path.Combine(s.Dir, "seg00004.ts");
        Assert.InRange(await FirstPts(seg4), 7.9, 8.3);                                            // segment 4 = source time 8.0 s, not 0
        var probe = await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,codec_name", "-of", "csv=p=0", seg4], default);
        Assert.Equal(["h264,854,480"], probe.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct());   // scaled to 480p, re-encoded (ffprobe lists TS streams twice)
    }

    [Fact]
    public async Task SeekingAround_ThenLettingItFinish_ProducesOneContinuousTimeline()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        using var m = Mgr(await Clip());
        var s = await m.StartAsync(1, default, startSeconds: 8, quality: "720p");
        Assert.NotNull(await m.GetFileAsync(s, "seg00008.ts", default));                           // forward, within look-ahead of the run
        Assert.NotNull(await m.GetFileAsync(s, "seg00000.ts", default));                           // back before the run's start => a second ffmpeg at 0
        Assert.InRange(await FirstPts(Path.Combine(s.Dir, "seg00000.ts")), -0.05, 0.3);

        await HlsWait.CompleteAsync(Path.Combine(s.Dir, "index.m3u8"));                            // every segment, from both runs, now exists

        // Decode the whole thing exactly as a player would.
        var decode = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-allowed_extensions", "ALL", "-i", Path.Combine(s.Dir, "index.m3u8"), "-f", "null", "-"], default);
        Assert.Equal(0, decode.ExitCode);
        // Video must be flawless across the seam. Audio may repeat one AAC frame boundary (21 ms) where two independent runs meet: inaudible.
        var msgs = decode.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(msgs, m => m.Contains("stream 0"));
        Assert.True(msgs.Length <= 2, string.Join("\n", msgs));

        // Timestamps: no gap, no overlap, across the seam between the two runs.
        var pts = (await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-allowed_extensions", "ALL", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0",
            Path.Combine(s.Dir, "index.m3u8")], default)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => double.Parse(l.Trim(','), CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
        Assert.InRange(pts[0], -0.05, 0.1);
        Assert.InRange(pts[^1], 19.8, 20.1);
        var maxGap = pts.Zip(pts.Skip(1), (a, b) => b - a).Max();
        Assert.True(maxGap < 0.05, $"largest timestamp gap was {maxGap:F3}s (one frame is 0.04s)");
        Assert.True(pts.Count is >= 495 and <= 505, $"expected ~500 frames, got {pts.Count}");     // no duplicated or dropped frames at the seams
    }
}
