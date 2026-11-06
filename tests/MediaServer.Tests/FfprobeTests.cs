using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Tests;

public class FfprobeTests
{
    private const string Json = """
    {"streams":[
      {"index":0,"codec_name":"h264","codec_type":"video","profile":"High","width":1920,"height":1080,"pix_fmt":"yuv420p","avg_frame_rate":"24000/1001","bit_rate":"4000000","disposition":{"default":1,"attached_pic":0}},
      {"index":1,"codec_name":"eac3","codec_type":"audio","channels":6,"channel_layout":"5.1(side)","sample_rate":"48000","tags":{"language":"eng","title":"Surround"},"disposition":{"default":1}},
      {"index":2,"codec_name":"aac","codec_type":"audio","channels":2,"sample_rate":"48000","tags":{"LANGUAGE":"jpn"},"disposition":{"default":0}},
      {"index":3,"codec_name":"subrip","codec_type":"subtitle","tags":{"language":"eng"},"disposition":{"default":0,"forced":1}},
      {"index":4,"codec_name":"mjpeg","codec_type":"video","width":600,"height":900,"disposition":{"attached_pic":1}}
    ],"format":{"format_name":"matroska,webm","duration":"7265.120000","size":"3500000000","bit_rate":"3853000"}}
    """;

    [Fact]
    public void ParsesTypedTracks()
    {
        var m = FfprobeParser.Parse(Json);
        Assert.Equal("matroska,webm", m.Container);
        Assert.Equal(7265.12, m.DurationSeconds);
        Assert.Equal(3_500_000_000L, m.SizeBytes);

        var v = Assert.Single(m.Video); // cover art excluded
        Assert.Equal(("h264", 1920, 1080), (v.Codec, v.Width, v.Height));
        Assert.Equal(23.976, v.FrameRate);

        Assert.Equal(2, m.Audio.Count);
        Assert.Equal(("eac3", 6, "eng", true), (m.Audio[0].Codec, m.Audio[0].Channels, m.Audio[0].Language, m.Audio[0].IsDefault));
        Assert.Equal("jpn", m.Audio[1].Language); // tag keys are case-insensitive

        var s = Assert.Single(m.Subtitles);
        Assert.True(s.IsForced);
        Assert.Equal("subrip", s.Codec);
    }

    [Fact]
    public void MissingFormatAndStreams_YieldsEmpty()
    {
        var m = FfprobeParser.Parse("{}");
        Assert.Null(m.DurationSeconds);
        Assert.Empty(m.Video);
    }

    [Fact]
    public async Task NonZeroExit_Throws()
    {
        var probe = new FfprobeMediaProbe(new FakeRunner(new ProcessResult(1, "", "No such file")), new ConfigurationBuilder().Build());
        var ex = await Assert.ThrowsAsync<ProbeFailedException>(() => probe.ProbeAsync("/x", default));
        Assert.Contains("No such file", ex.Message);
    }

    [Fact]
    public async Task PathIsPassedAsSingleArgument_NotInterpolated()
    {
        var fake = new FakeRunner(new ProcessResult(0, "{}", ""));
        var probe = new FfprobeMediaProbe(fake, new ConfigurationBuilder().Build());
        await probe.ProbeAsync("/media/a b; rm -rf ~ -x.mkv", default);
        Assert.Equal("/media/a b; rm -rf ~ -x.mkv", fake.LastArgs![^1]);
        Assert.Equal("-i", fake.LastArgs[^2]);
    }

    [Fact]
    public async Task RealFfprobe_OnGeneratedFile()
    {
        if (!ToolExists("ffmpeg") || !ToolExists("ffprobe")) return; // environment without ffmpeg
        var dir = Directory.CreateTempSubdirectory("probe").FullName;
        try
        {
            var file = Path.Combine(dir, "t.mkv");
            var gen = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc=d=2:s=320x240:r=25",
                "-f", "lavfi", "-i", "sine=d=2", "-c:v", "libx264", "-c:a", "aac", file], default);
            Assert.Equal(0, gen.ExitCode);
            var m = await new FfprobeMediaProbe(new ProcessRunner(), new ConfigurationBuilder().Build()).ProbeAsync(file, default);
            Assert.InRange(m.DurationSeconds!.Value, 1.9, 2.2);
            Assert.Equal("h264", m.Video.Single().Codec);
            Assert.Equal(320, m.Video.Single().Width);
            Assert.Equal("aac", m.Audio.Single().Codec);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Cancellation_KillsChildProcess()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("sleep", ["30"], cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrencyIsBounded()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var runner = new ProcessRunner(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(runner.RunAsync("sleep", ["0.4"], default), runner.RunAsync("sleep", ["0.4"], default));
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.75), "two 0.4s jobs must serialize with limit 1");
    }

    private static bool ToolExists(string n) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, n)));

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public IReadOnlyList<string>? LastArgs;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, CancellationToken ct) { LastArgs = a; return Task.FromResult(result); }
    }
}

public class ErrorScrubbingTests
{
    [Fact]
    public void ServerPathsNeverReachClients()
    {
        var msg = ErrorText.Scrub("/data/media/Movies/Broken.2001.mkv: Invalid data\n[mkv @ 0x1] bad /data/media/Movies/Broken.2001.mkv", "/data/media/Movies/Broken.2001.mkv");
        Assert.DoesNotContain("/data", msg);
        Assert.Contains("Broken.2001.mkv: Invalid data", msg);
    }

    [Fact]
    public async Task ProbeFailureMessageContainsFileNameOnly()
    {
        var probe = new FfprobeMediaProbe(new ScrubRunner("/secret/dir/x.mkv: Invalid data found"), new ConfigurationBuilder().Build());
        var ex = await Assert.ThrowsAsync<ProbeFailedException>(() => probe.ProbeAsync("/secret/dir/x.mkv", default));
        Assert.DoesNotContain("/secret", ex.Message);
        Assert.Contains("x.mkv", ex.Message);
    }

    private sealed class ScrubRunner(string stderr) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, CancellationToken ct) => Task.FromResult(new ProcessResult(1, "", stderr));
    }
}
