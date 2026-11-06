using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;

namespace MediaServer.Tests;

public sealed class AudioTrackPlanTests
{
    private static MediaInfo Info(params AudioTrack[] audio) => new("matroska", 600, 1, 1, [new VideoTrack(0, "h264", "High", 1920, 1080, 24, "yuv420p", null)], audio,
        [new SubtitleTrack(5, "hdmv_pgs_subtitle", "eng", null, false, false), new SubtitleTrack(6, "subrip", "eng", null, false, false)]);

    private static readonly AudioTrack English = new(1, "eac3", 6, "5.1", 48000, "eng", null, true, null);
    private static readonly AudioTrack French = new(2, "aac", 2, "stereo", 48000, "fre", null, false, null);
    private static readonly HlsOptions Opts = new();

    [Fact] public void TheDefaultTrackIsUsed_WhenNoneIsChosen() => Assert.Equal(1, HlsPlanner.Decide(Info(English, French), Opts, ClientCaps.Baseline, ".mkv")!.AudioStream);

    [Fact] public void AChosenTrack_BeatsTheDefault()
    {
        var plan = HlsPlanner.Decide(Info(English, French), Opts, ClientCaps.Baseline, ".mkv", audioIndex: 2)!;
        Assert.Equal(2, plan.AudioStream);
        Assert.Equal("remux", plan.Mode);                                                                  // only the audio changes: the video is still copied
    }

    [Fact] public void AnUnknownTrack_FallsBackToTheDefault() => Assert.Equal(1, HlsPlanner.Decide(Info(English, French), Opts, ClientCaps.Baseline, ".mkv", audioIndex: 99)!.AudioStream);

    [Fact] public void ACopiedStereoAacTrack_IsPassedThrough_OtherTracksAreConverted()
    {
        Assert.Equal(AudioMode.Copy, HlsPlanner.Decide(Info(English, French), Opts, ClientCaps.Baseline, ".mkv", audioIndex: 2)!.Audio);
        Assert.Equal(AudioMode.Aac, HlsPlanner.Decide(Info(English, French), Opts, ClientCaps.Baseline, ".mkv", audioIndex: 1)!.Audio);
    }

    [Fact] public void BurningInPictureSubtitles_ForcesAReEncode_EvenForPlayableVideo()
    {
        var copy = HlsPlanner.Decide(Info(English), Opts, ClientCaps.Baseline, ".mkv")!;
        var burn = HlsPlanner.Decide(Info(English), Opts, ClientCaps.Baseline, ".mkv", burnSubtitle: 5)!;
        Assert.Equal(VideoMode.Copy, copy.Video);
        Assert.Equal((VideoMode.Encode, 5), (burn.Video, burn.BurnSubtitle));
        Assert.Contains("subtitles", burn.Reason);
        Assert.True(burn.Vod);                                                                              // known runtime: seekable like any transcode
    }

    [Fact] public void AnUnknownSubtitleTrack_IsIgnored() => Assert.Equal(VideoMode.Copy, HlsPlanner.Decide(Info(English), Opts, ClientCaps.Baseline, ".mkv", burnSubtitle: 42)!.Video);
}

public sealed class BurnInCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("burn").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static HlsPlan Plan(bool burn) => new(0, null, VideoMode.Encode, AudioMode.None, Vod: true, DurationSeconds: 2, BurnSubtitle: burn ? 1 : null);

    [Fact] public void WithoutBurnIn_TheCommandIsUnchanged()
    {
        var a = HlsCommandBuilder.Build("/in.mkv", "/out", Plan(false), new HlsOptions());
        Assert.Contains("-sn", a); Assert.Contains("-vf", a); Assert.DoesNotContain("-filter_complex", a); Assert.Equal("0:0", a[a.IndexOf("-map") + 1]);
    }

    [Fact] public void WithBurnIn_TheVideoAndTheSubtitleShareOneFilterGraph()
    {
        var a = HlsCommandBuilder.Build("/in.mkv", "/out", Plan(true), new HlsOptions());
        var graph = a[a.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:0][0:1]overlay=", graph); Assert.EndsWith("[v]", graph); Assert.Contains("scale=", graph);
        Assert.Equal("[v]", a[a.IndexOf("-map") + 1]);
        Assert.DoesNotContain("-sn", a); Assert.DoesNotContain("-vf", a);
        Assert.Equal("-i", a[a.IndexOf("/in.mkv") - 1]);                                                    // the input path still follows -i
    }

    [Fact] public void BurnIn_NeverAppliesToCopiedVideo()
    {
        var a = HlsCommandBuilder.Build("/in.mkv", "/out", Plan(true) with { Video = VideoMode.Copy }, new HlsOptions());
        Assert.DoesNotContain("-filter_complex", a); Assert.Contains("copy", a);
    }

    [Fact] public async Task TheGraphIsAcceptedByRealFfmpeg_AndProducesSegments()
    {
        // Stream 1 is a second picture standing in for the subtitle bitmaps (ffmpeg can't author PGS here); it exercises the same overlay graph and mapping.
        var src = Path.Combine(_dir, "in.mkv");
        var gen = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=d=2:s=320x240:r=25", "-f", "lavfi", "-i", "color=c=red@0.5:s=320x240:d=2:r=25,format=rgba",
            "-map", "0", "-map", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", src], default);
        Assert.True(gen.ExitCode == 0, gen.StdErr);
        var outDir = Path.Combine(_dir, "out"); Directory.CreateDirectory(outDir);
        var args = HlsCommandBuilder.Build(src, outDir, Plan(true), new HlsOptions { SegmentSeconds = 2 });
        var run = await new ProcessRunner().RunAsync("ffmpeg", args, default);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.NotEmpty(Directory.GetFiles(outDir, "seg*.ts"));
        var probe = await new ProcessRunner().RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", Directory.GetFiles(outDir, "seg*.ts")[0]], default);
        Assert.StartsWith("320,240", probe.StdOut.Trim());
    }
}
