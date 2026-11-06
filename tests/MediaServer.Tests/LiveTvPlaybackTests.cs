using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Tests;

public class LivePlanAndCommandTests
{
    private static readonly HlsOptions O = new();
    private static MediaInfo Chan(string vcodec = "h264", string pix = "yuv420p", string? field = "progressive", string acodec = "ac3", int ch = 2) =>
        new("mpegts", null, null, null, [new VideoTrack(0, vcodec, null, 1920, 1080, 30, pix, null, null, field)],
            [new AudioTrack(1, acodec, ch, null, 48000, null, null, true, null)], []);

    [Fact]
    public void CompatibleProgressiveH264_IsCopied_AndAc3BecomesAac()
    {
        var p = HlsPlanner.DecideLive(Chan(), O)!;
        Assert.Equal((VideoMode.Copy, AudioMode.Aac, true, false, "remux"), (p.Video, p.Audio, p.Live, p.Vod, p.Mode));
        Assert.Equal("Live · direct stream, video untouched", p.Reason);
    }

    [Fact] public void StereoAacIsCopied() => Assert.Equal(AudioMode.Copy, HlsPlanner.DecideLive(Chan(acodec: "aac"), O)!.Audio);
    [Fact] public void SurroundAacIsDownmixed() => Assert.Equal(AudioMode.Aac, HlsPlanner.DecideLive(Chan(acodec: "aac", ch: 6), O)!.Audio);

    [Theory] [InlineData("tt")] [InlineData("bb")] [InlineData("tb")] [InlineData("bt")]
    public void InterlacedVideoIsEncodedAndDeinterlaced_BecauseBrowsersDoNotDeinterlace(string field)
    {
        var p = HlsPlanner.DecideLive(Chan(field: field), O)!;
        Assert.Equal((VideoMode.Encode, true), (p.Video, p.Deinterlace));
        Assert.Contains("interlaced", p.Reason);
    }

    [Theory] [InlineData("progressive")] [InlineData("unknown")] [InlineData(null)]
    public void ProgressiveOrUnknownFieldOrder_IsNotDeinterlaced(string? field)
        => Assert.False(HlsPlanner.DecideLive(Chan(field: field), O)!.Deinterlace);

    [Fact] public void Mpeg2IsEncoded() => Assert.Equal(VideoMode.Encode, HlsPlanner.DecideLive(Chan(vcodec: "mpeg2video"), O)!.Video);

    [Fact]
    public void HevcIsCopiedIntoFmp4_ForABrowserThatDecodesIt_AndEncodedForOneThatDoesNot()
    {
        var copy = HlsPlanner.DecideLive(Chan(vcodec: "hevc"), O, PlaybackPolicyTests.Chrome)!;
        Assert.Equal((VideoMode.Copy, HlsSegments.Fmp4, "hvc1"), (copy.Video, copy.Segments, copy.VideoTag));
        Assert.Equal(VideoMode.Encode, HlsPlanner.DecideLive(Chan(vcodec: "hevc"), O, PlaybackPolicyTests.Firefox)!.Video);
    }

    [Fact]
    public void ForceTranscode_ReencodesAChannelTheBrowserFailedToPlay()
    {
        var p = HlsPlanner.DecideLive(Chan(), O, forceTranscode: true)!;
        Assert.Equal(VideoMode.Encode, p.Video);
        Assert.Contains("failed in this browser", p.Reason);
    }

    [Fact] public void NoVideo_IsNull() => Assert.Null(HlsPlanner.DecideLive(new MediaInfo("mpegts", null, null, null, [], [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []), O));
    [Fact] public void NoAudioIsFine() => Assert.Equal(AudioMode.None, HlsPlanner.DecideLive(new MediaInfo("mpegts", null, null, null, Chan().Video, [], []), O)!.Audio);

    [Fact]
    public void LiveCommand_RestrictsProtocols_Reconnects_AndKeepsASlidingWindow()
    {
        var plan = HlsPlanner.DecideLive(Chan(), O)!;
        var args = HlsCommandBuilder.Build("http://src/live/abc.ts", "/tmp/s", plan, O);
        string After(string f) => args[args.IndexOf(f) + 1];
        Assert.Equal("http,https,tcp,tls,crypto", After("-protocol_whitelist"));
        Assert.True(args.IndexOf("-protocol_whitelist") < args.IndexOf("-i"), "input options must precede -i");
        Assert.Equal("http://src/live/abc.ts", After("-i"));
        Assert.Equal("15000000", After("-rw_timeout"));
        Assert.Equal("1", After("-reconnect")); Assert.Equal("1", After("-reconnect_streamed")); Assert.Equal("5", After("-reconnect_delay_max"));
        Assert.DoesNotContain("-ss", args);                                                  // live has no seek
        Assert.Equal("2", After("-hls_time"));                                               // short segments: quick channel changes
        Assert.Equal("30", After("-hls_list_size"));
        Assert.Equal("delete_segments+independent_segments+temp_file", After("-hls_flags")); // old segments are deleted
        Assert.DoesNotContain("-hls_playlist_type", args);                                   // never an ENDLIST/VOD playlist
        Assert.DoesNotContain("-output_ts_offset", args);
        Assert.Equal("copy", After("-c:v"));
        Assert.Equal("/tmp/s/index.m3u8", args[^1]);
    }

    [Fact]
    public void AReconnectAppendsToThePlaylist_AndMarksTheDiscontinuity()
    {
        var plan = HlsPlanner.DecideLive(Chan(), O)!;
        var args = HlsCommandBuilder.Build("http://src/live", "/tmp/s", plan, O, resume: true);
        Assert.Equal("delete_segments+independent_segments+temp_file+append_list+discont_start", args[args.IndexOf("-hls_flags") + 1]);
    }

    [Fact]
    public void InterlacedEncode_DeinterlacesBeforeScaling_AndForcesSegmentAlignedKeyframes()
    {
        var plan = HlsPlanner.DecideLive(Chan(field: "tt"), O)!;
        var args = HlsCommandBuilder.Build("http://src/live", "/tmp/s", plan, O);
        Assert.Equal("yadif=0:-1:1,scale=-2:min(1080\\,ih)", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("expr:gte(t,n_forced*2)", args[args.IndexOf("-force_key_frames") + 1]);   // live segment length, not the VOD one
        Assert.DoesNotContain("-bf", args);                                                      // B-frame trick is for seekable VOD runs only
    }

    [Fact]
    public void LibraryCommandsAreUnchanged_NoLiveOptionsLeakIn()
    {
        var vod = HlsPlanner.Decide(new MediaInfo("mkv", 600, 1, 1, Chan(vcodec: "hevc").Video, [], []), O)!;
        var args = HlsCommandBuilder.Build("/a.mkv", "/tmp/s", vod, O);
        Assert.DoesNotContain("-protocol_whitelist", args); Assert.DoesNotContain("-reconnect", args); Assert.DoesNotContain("delete_segments+independent_segments+temp_file", args);
    }

    [Fact]
    public void FieldOrderIsParsedFromFfprobe()
    {
        var m = FfprobeParser.Parse("""{"streams":[{"index":0,"codec_type":"video","codec_name":"mpeg2video","field_order":"tt","pix_fmt":"yuv420p"}]}""");
        Assert.Equal("tt", m.Video.Single().FieldOrder);
    }
}

/// <summary>Reconnect, single-live-stream and redaction behaviour, with a fake ffmpeg that logs every run.</summary>
public sealed class LiveSessionManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("livemgr").FullName;
    private readonly string _tmp;
    public LiveSessionManagerTests() => _tmp = Path.Combine(_dir, "hls");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Header = """
        for a in "$@"; do last=$a; done
        dir=$(dirname "$last")
        case " $* " in *append_list*) ap=yes;; *) ap=no;; esac
        echo "pid=$$ append=$ap" >> "$(dirname "$dir")/runs.log"
        """;
    private static string AliveBody => Header + "\nprintf '#EXTM3U\\n#EXTINF:2.0,\\nseg00000.ts\\n' > \"$last\"; printf SEG > \"$dir/seg00000.ts\"\nexec sleep 120\n";
    private static string DiesBody => Header + "\nprintf '#EXTM3U\\n#EXTINF:2.0,\\nseg00000.ts\\n' > \"$last\"; printf SEG > \"$dir/seg00000.ts\"\nexit 0\n";
    private static string LeaksUrlBody => Header + "\necho 'Error opening input: http://secret.example/live/user:hunter2/123.ts: Connection refused' >&2\nexit 1\n";

    private HlsSessionManager Mgr(string body, int max = 3)
    {
        var script = Path.Combine(_dir, $"ffmpeg-{body.GetHashCode():x}.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new HlsSessionManager(new NoFiles(), new NoProbe(), new HlsOptions { FfmpegPath = script, TempDir = _tmp, MaxSessions = max, StartTimeoutSeconds = 5 },
            TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
    }
    private sealed class NoFiles : IMediaResolver { public string? ResolvePath(long id) => null; }
    private sealed class NoProbe : IMediaProbe { public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => throw new NotSupportedException(); }

    private static readonly MediaInfo H264Ac3 = new("mpegts", null, null, null, [new VideoTrack(0, "h264", null, 1920, 1080, 30, "yuv420p", null, null, "progressive")],
        [new AudioTrack(1, "ac3", 2, null, 48000, null, null, true, null)], []);

    private string[] Runs() => File.Exists(Path.Combine(_tmp, "runs.log")) ? File.ReadAllLines(Path.Combine(_tmp, "runs.log")) : [];
    private static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }

    [Fact]
    public async Task StartLive_CreatesAnUnguessableLiveSession_KeyedAwayFromLibraryMedia()
    {
        using var m = Mgr(AliveBody);
        var s = await m.StartLiveAsync(7, "http://src/live/7", H264Ac3, default);
        Assert.True(s.Live); Assert.False(s.Vod);
        Assert.Equal(-7, s.MediaId);
        Assert.Matches("^[0-9a-f]{32}$", s.Id);
        Assert.Equal(("remux", "Live · direct stream, video untouched"), (s.Mode, s.Reason));
        Assert.True(s.IsRunning);
        Assert.Contains("#EXTINF", File.ReadAllText(Path.Combine(s.Dir, "index.m3u8")));
        Assert.EndsWith("append=no", Assert.Single(Runs()));
    }

    [Fact]
    public async Task ChangingChannel_StopsThePreviousChannel_OneLiveStreamAtATime()
    {
        using var m = Mgr(AliveBody);
        var a = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        var pidA = a.ProcessId;
        var b = await m.StartLiveAsync(2, "http://src/2", H264Ac3, default);
        Assert.False(Alive(pidA));
        Assert.Null(m.Get(a.Id));
        Assert.Equal(1, m.ActiveCount);
        Assert.True(b.IsRunning);
        var again = await m.StartLiveAsync(2, "http://src/2", H264Ac3, default);     // same channel again (e.g. a reload) replaces too
        Assert.Equal(1, m.ActiveCount); Assert.NotEqual(b.Id, again.Id);
    }

    [Fact]
    public async Task StartingLive_DoesNotDisturbALibrarySession_UnlessTheCapIsReached()
    {
        using var m = Mgr(AliveBody, max: 3);
        var live = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        Assert.Equal(1, m.ActiveCount);
        await m.StopAsync(live.Id);
        Assert.Equal(0, m.ActiveCount);
    }

    [Fact]
    public async Task WhenTheSourceDrops_AViewerRequestingThePlaylistReconnects_AppendingToTheSamePlaylist()
    {
        using var m = Mgr(DiesBody);
        var s = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        await Task.Delay(400);
        Assert.False(s.IsRunning);                                                     // ffmpeg exited (the source went away)
        Assert.NotNull(await m.GetFileAsync(s, "index.m3u8", default));                 // the player's next playlist refresh...
        for (var i = 0; i < 50 && Runs().Length < 2; i++) await Task.Delay(50);         // (the new process writes its log line a moment after launch)
        var runs = Runs();
        Assert.Equal(2, runs.Length);
        Assert.EndsWith("append=yes", runs[1]);                                         // ...brings it back, continuing the playlist
    }

    [Fact]
    public async Task ReconnectsAreCapped_SoADeadChannelCannotLoopForever()
    {
        using var m = Mgr(DiesBody);
        var s = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        for (var i = 0; i < 12; i++) { await Task.Delay(250); await m.GetFileAsync(s, "index.m3u8", default); }
        Assert.Equal(1 + 5, Runs().Length);
    }

    [Fact]
    public async Task AHealthyStream_IsNeverRelaunched()
    {
        using var m = Mgr(AliveBody);
        var s = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        for (var i = 0; i < 5; i++) await m.GetFileAsync(s, "index.m3u8", default);
        Assert.Single(Runs());
    }

    [Fact]
    public async Task ErrorTextNeverContainsTheStreamUrlOrItsCredentials()
    {
        using var m = Mgr(LeaksUrlBody);
        var ex = await Assert.ThrowsAsync<HlsStartException>(() => m.StartLiveAsync(1, "http://secret.example/live/user:hunter2/123.ts", H264Ac3, default));
        Assert.DoesNotContain("secret.example", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("123.ts", ex.Message);                                   // not even the last path segment (could be a token)
        Assert.Contains("<stream>", ex.Message);
        Assert.Equal(0, m.ActiveCount);
        Assert.Empty(Directory.GetDirectories(_tmp));
    }

    [Fact]
    public async Task AStreamWithoutVideo_IsRefused_WithoutStartingFfmpeg()
    {
        using var m = Mgr(AliveBody);
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.StartLiveAsync(1, "http://src/1", new MediaInfo("mpegts", null, null, null, [], [], []), default));
        Assert.Empty(Runs());
    }

    [Fact]
    public async Task ReapingAnIdleLiveSession_StopsTheSourceConnection()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var script = Path.Combine(_dir, "ffmpeg-idle.sh"); File.WriteAllText(script, "#!/bin/sh\n" + AliveBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var m = new HlsSessionManager(new NoFiles(), new NoProbe(), new HlsOptions { FfmpegPath = script, TempDir = _tmp, IdleSeconds = 30 }, time, NullLogger<HlsSessionManager>.Instance);
        var s = await m.StartLiveAsync(1, "http://src/1", H264Ac3, default);
        var pid = s.ProcessId;
        time.Advance(TimeSpan.FromSeconds(31));
        await m.ReapIdleAsync();
        Assert.False(Alive(pid));
        Assert.Equal(0, m.ActiveCount);
    }
}

/// <summary>A real, live-like MPEG-TS source served over HTTP (H.264 + AC3 like the real channels), so ffprobe/ffmpeg see what they'd see in production.</summary>
internal sealed class FakeLiveSource : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<Process> _procs = [];
    public string Url { get; }
    public int Connections;

    public FakeLiveSource(string extra = "")
    {
        var port = ((IPEndPoint)new Func<EndPoint>(() => { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var ep = l.LocalEndpoint; l.Stop(); return ep; })()).Port;
        Url = $"http://127.0.0.1:{port}/live.ts";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () => { while (_listener.IsListening) { HttpListenerContext c; try { c = await _listener.GetContextAsync(); } catch { break; } _ = Task.Run(() => Serve(c, extra)); } });
    }

    private void Serve(HttpListenerContext ctx, string extra)
    {
        Interlocked.Increment(ref Connections);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in ("-v error -re -f lavfi -i testsrc2=s=640x360:r=25 -f lavfi -i sine=f=440 -c:v libx264 -preset ultrafast -tune zerolatency -g 25 -pix_fmt yuv420p " + extra +
                           " -c:a ac3 -f mpegts pipe:1").Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!; lock (_procs) _procs.Add(p);
        _ = p.StandardError.ReadToEndAsync();
        try { ctx.Response.ContentType = "video/mp2t"; ctx.Response.SendChunked = true; p.StandardOutput.BaseStream.CopyTo(ctx.Response.OutputStream); }
        catch { /* client disconnected */ }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } try { ctx.Response.Abort(); } catch { } }
    }

    /// <summary>Simulates the provider dropping the connection: ends every running source.</summary>
    public void DropAll() { lock (_procs) foreach (var p in _procs) try { if (!p.HasExited) p.Kill(true); } catch { } }
    public bool AnyAlive() { lock (_procs) return _procs.Any(p => { try { return !p.HasExited; } catch { return false; } }); }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } DropAll(); }
}

public sealed class LiveRealFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("livereal").FullName;
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }
    private static bool Have(string t) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, t)));

    private static async Task<bool> Until(Func<bool> cond, int seconds = 25) { for (var i = 0; i < seconds * 10; i++) { if (cond()) return true; await Task.Delay(100); } return false; }
    private static int Segments(string playlist) => File.Exists(playlist) ? File.ReadAllText(playlist).Split('\n').Count(l => l.StartsWith("#EXTINF")) : 0;

    [Fact]
    public async Task ProbesPlaysAndRemuxesALiveStream_ThenReconnectsAfterTheSourceDrops()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        using var source = new FakeLiveSource();
        var runner = new ProcessRunner();
        var probe = new FfprobeMediaProbe(runner, new ConfigurationBuilder().Build());

        // 1. the live probe sees what production would: progressive H.264 + AC3
        var info = await probe.ProbeLiveAsync(source.Url, default);
        Assert.Equal(("h264", "yuv420p"), (info.Video[0].Codec, info.Video[0].PixelFormat));
        Assert.Equal("ac3", info.Audio[0].Codec);

        using var mgr = new HlsSessionManager(new NoFiles(), probe, new HlsOptions { TempDir = Path.Combine(_dir, "hls"), LiveSegmentSeconds = 1, StartTimeoutSeconds = 30 }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);

        // 2. video is copied, audio becomes AAC: no re-encode of the picture
        var s = await mgr.StartLiveAsync(1, source.Url, info, default);
        Assert.Equal("remux", s.Mode);
        var playlist = Path.Combine(s.Dir, "index.m3u8");
        Assert.True(await Until(() => Segments(playlist) >= 3), "live playlist never grew");
        var text = File.ReadAllText(playlist);
        Assert.DoesNotContain("#EXT-X-ENDLIST", text);                                         // a live playlist never ends
        Assert.DoesNotContain("#EXT-X-PLAYLIST-TYPE", text);
        var segs = Directory.GetFiles(s.Dir, "seg*.ts").OrderBy(x => x).ToList();
        var pr = await runner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_name,codec_type,pix_fmt", "-of", "csv=p=0", segs[0]], default);
        Assert.Contains("h264", pr.StdOut); Assert.Contains("aac", pr.StdOut); Assert.DoesNotContain("ac3", pr.StdOut);
        var dec = await runner.RunAsync("ffmpeg", ["-v", "error", "-i", segs[0], "-f", "null", "-"], default);
        Assert.Equal("", dec.StdErr.Trim());                                                    // the segment decodes cleanly

        // 3. the provider drops us: the viewer's next playlist refresh reconnects, continuing the same playlist
        var before = Directory.GetFiles(s.Dir, "seg*.ts").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)[3..])).Max();
        source.DropAll();
        Assert.True(await Until(() => !s.IsRunning, 15), "our ffmpeg did not notice the source ended");
        Assert.NotNull(await mgr.GetFileAsync(s, "index.m3u8", default));
        Assert.True(await Until(() => s.IsRunning && source.Connections >= 2, 15), "no reconnect");
        Assert.True(await Until(() => Directory.GetFiles(s.Dir, "seg*.ts").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)[3..])).Max() > before + 1, 25), "numbering did not continue");
        // the playlist is rewritten after a segment file appears, so give the seam marker a moment to land
        Assert.True(await Until(() => { try { return File.ReadAllText(playlist).Contains("#EXT-X-DISCONTINUITY"); } catch (IOException) { return false; } }, 15), "the seam was not marked for the player");

        // 4. stopping releases the source connection
        var pid = s.ProcessId;
        await mgr.StopAsync(s.Id);
        Assert.False(Directory.Exists(s.Dir));
        Assert.True(await Until(() => !source.AnyAlive(), 10), "the source connection was not released");
    }

    [Fact]
    public async Task AnInterlacedChannel_IsDeinterlacedAndEncoded()
    {
        if (!Have("ffmpeg") || !Have("ffprobe")) return;
        // Genuinely interlaced MPEG-2 (what OTA/cable channels often are).
        using var source = new FakeLiveSource();
        var probe = new FfprobeMediaProbe(new ProcessRunner(), new ConfigurationBuilder().Build());
        var info = await probe.ProbeLiveAsync(source.Url, default);
        // The generator above is progressive, so tell the planner the channel is interlaced, as ffprobe would report for a real one.
        var interlaced = info with { Video = [info.Video[0] with { FieldOrder = "tt" }] };
        using var mgr = new HlsSessionManager(new NoFiles(), probe, new HlsOptions { TempDir = Path.Combine(_dir, "hls2"), LiveSegmentSeconds = 1, StartTimeoutSeconds = 30 }, TimeProvider.System, NullLogger<HlsSessionManager>.Instance);
        var s = await mgr.StartLiveAsync(2, source.Url, interlaced, default);
        Assert.Equal("transcode", s.Mode);
        Assert.Contains("interlaced", s.Reason);
        Assert.True(await Until(() => Segments(Path.Combine(s.Dir, "index.m3u8")) >= 2));
        var seg = Directory.GetFiles(s.Dir, "seg*.ts").OrderBy(x => x).First();
        var r = await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-i", seg, "-f", "null", "-"], default);
        Assert.Equal("", r.StdErr.Trim());
        await mgr.StopAsync(s.Id);
    }

    private sealed class NoFiles : IMediaResolver { public string? ResolvePath(long id) => null; }
}
