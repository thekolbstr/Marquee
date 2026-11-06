using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;

namespace MediaServer.Api.Hls;

public sealed record HlsSessionInfo(string Mode, string Reason, string Encoder, string Audio, string Container, bool Vod, bool Live, bool Running,
    int SegmentsReady, int SegmentsTotal, int Relaunches, int AgeSeconds, int? MaxWidth, int? VideoKbps, bool BurnedInSubtitles);

public sealed class HlsStartException(string message) : Exception(message);

/// <summary>One ffmpeg process. A session has at most one at a time; a far seek replaces it.</summary>
internal sealed class HlsRun
{
    public HlsRun(Process process, int startIndex) { Process = process; StartIndex = startIndex; Pid = process.Id; StartedAt = DateTimeOffset.UtcNow; }
    public DateTimeOffset StartedAt { get; }
    public Process Process { get; }
    public int StartIndex { get; }
    public int Pid { get; }
    public readonly StringBuilder StderrTail = new();
    public int Killed;
    public bool IsAlive { get { try { return Volatile.Read(ref Killed) == 0 && !Process.HasExited; } catch (InvalidOperationException) { return false; } } }
    public string StderrText { get { lock (StderrTail) return StderrTail.ToString(); } }
}

public sealed class HlsSession
{
    internal HlsSession(string id, long mediaId, string dir, HlsPlan plan, string inputPath, int segmentCount, DateTimeOffset now)
    { Id = id; MediaId = mediaId; Dir = dir; Plan = plan; InputPath = inputPath; SegmentCount = segmentCount; LastAccessTicks = now.UtcTicks; }

    public string Id { get; }
    public long MediaId { get; }
    public string Dir { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string Mode => Plan.Mode;
    public string Reason => Plan.Reason;
    /// <summary>True for segmented transcodes (full playlist up front, start anywhere, restart on a far seek).</summary>
    public bool Vod => Plan.Vod;
    /// <summary>A live channel (sliding window; reconnects if the source drops).</summary>
    public bool Live => Plan.Live;
    internal int Relaunches;
    public int SegmentCount { get; }
    public double DurationSeconds => Plan.DurationSeconds;
    internal HlsPlan Plan { get; }
    internal string InputPath { get; }
    internal long LastAccessTicks;
    internal int Stopped; // 0/1
    internal HlsRun? Run;
    /// <summary>Serializes decisions about starting/restarting ffmpeg.</summary>
    internal readonly SemaphoreSlim RunGate = new(1, 1);

    internal int LastPid;
    /// <summary>The current run's pid, or the last one if the session has since been stopped.</summary>
    public int ProcessId => Run?.Pid ?? LastPid;
    public bool IsRunning => Volatile.Read(ref Stopped) == 0 && Run is { IsAlive: true };
    public string StderrText => Run?.StderrText ?? "";
}

/// <summary>
/// Owns every ffmpeg child process. A transcode ends via exactly one of: explicit stop (tab closed / navigated away),
/// idle timeout (no pings or segment requests), eviction by a newer session, or server shutdown. All paths kill the
/// process tree and delete the session directory.
/// </summary>
public sealed partial class HlsSessionManager : IDisposable
{
    private const int StderrCap = 8 * 1024;
    /// <summary>A segment this far ahead of the encoder is just waited for; further than that (or behind) restarts ffmpeg there.</summary>
    private const int LookAheadSegments = 5;

    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex IdRx();
    [GeneratedRegex(@"^(index\.m3u8|init\.mp4|seg\d{5}\.(ts|m4s))$")] private static partial Regex FileRx();
    [GeneratedRegex(@"^seg(\d{5})\.ts$")] private static partial Regex TsSegmentRx();

    private readonly IMediaResolver _resolver;
    private readonly IMediaProbe _probe;
    private readonly HlsOptions _opts;
    private readonly Func<HlsOptions>? _live;
    /// <summary>The options right now: settings changed in the web UI (stream limit, encoder, ...) apply to the next stream without a restart.</summary>
    private HlsOptions Opts => _live?.Invoke() ?? _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<HlsSessionManager> _log;
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public HlsSessionManager(IMediaResolver resolver, IMediaProbe probe, HlsOptions opts, TimeProvider time, ILogger<HlsSessionManager> log, Func<HlsOptions>? live = null)
    {
        _resolver = resolver; _probe = probe; _opts = opts; _live = live; _time = time; _log = log;
        CleanTempDir();
    }

    public int ActiveCount => _sessions.Count;
    public IReadOnlyCollection<HlsSession> Sessions => _sessions.Values.ToList();

    /// <param name="startSeconds">Where playback begins. Honoured by segmented transcodes (ffmpeg starts there); copied streams start at 0.</param>
    /// <param name="quality">Optional "1080p" | "720p" | "480p" | "360p": re-encode to that size and bitrate.</param>
    /// <exception cref="KeyNotFoundException">Unknown / deleted media.</exception>
    /// <exception cref="InvalidOperationException">No video stream.</exception>
    /// <exception cref="HlsStartException">ffmpeg failed to start or produce output.</exception>
    public async Task<HlsSession> StartAsync(long mediaId, CancellationToken ct, ClientCaps? caps = null, bool forceTranscode = false,
        double startSeconds = 0, string? quality = null, int? audioIndex = null, int? burnSubtitle = null)
    {
        var path = _resolver.ResolvePath(mediaId) ?? throw new KeyNotFoundException($"No media with id {mediaId}");
        var info = await _probe.ProbeAsync(path, ct);
        if (audioIndex is { } ai && info.Audio.All(x => x.Index != ai)) throw new InvalidOperationException("That audio track doesn't exist in this file.");
        if (burnSubtitle is { } bs && info.Subtitles.All(x => x.Index != bs)) throw new InvalidOperationException("That subtitle track doesn't exist in this file.");
        var plan = HlsPlanner.Decide(info, Opts, caps, Path.GetExtension(path), forceTranscode, quality, audioIndex, burnSubtitle) ?? throw new InvalidOperationException("The file has no video stream");
        if (plan.Video == VideoMode.Encode && !Opts.AllowTranscoding)
            throw new InvalidOperationException("This video needs transcoding for this device, and transcoding is turned off on this server.");

        HlsSession session;
        var startIndex = 0;
        await _startGate.WaitAsync(ct);
        try
        {
            // Single user: a second session for the same file is a reload/quality change/leak, so replace it.
            foreach (var s in _sessions.Values.Where(s => s.MediaId == mediaId)) await StopAsync(s.Id);
            while (_sessions.Count >= Opts.MaxSessions)
            {
                var oldest = _sessions.Values.OrderBy(s => Interlocked.Read(ref s.LastAccessTicks)).First();
                _log.LogInformation("Evicting HLS session {Id} (limit {Max})", oldest.Id, Opts.MaxSessions);
                await StopAsync(oldest.Id);
            }
            session = CreateSession(mediaId, path, plan);
            _sessions[session.Id] = session;
            if (plan.Vod) startIndex = Math.Clamp((int)Math.Floor(Math.Max(0, startSeconds) / Opts.SegmentSeconds), 0, session.SegmentCount - 1);
            try { await LaunchRunAsync(session, startIndex); }
            catch { await StopAsync(session.Id); throw; }
        }
        finally { _startGate.Release(); }

        try { await WaitUntilPlayableAsync(session, startIndex, ct); }
        catch
        {
            await StopAsync(session.Id);
            throw;
        }
        _log.LogInformation("HLS session {Id} started for media {Media} ({Mode}{Vod}, pid {Pid}): {Reason}", session.Id, mediaId, session.Mode,
            plan.Vod ? $", segmented from #{startIndex}" : "", session.ProcessId, session.Reason);
        return session;
    }

    /// <summary>
    /// Starts a live channel. Channels are keyed by a negative id so they never collide with library media. One live stream at a
    /// time: a provider connection is precious, and the previous channel is stopped when you change channel.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stream has no video.</exception>
    /// <exception cref="HlsStartException">ffmpeg could not open the stream or produced nothing in time.</exception>
    public async Task<HlsSession> StartLiveAsync(long channelId, string url, MediaInfo info, CancellationToken ct, ClientCaps? caps = null, bool forceTranscode = false)
    {
        var plan = HlsPlanner.DecideLive(info, Opts, caps, forceTranscode) ?? throw new InvalidOperationException("The channel has no video");
        if (plan.Video == VideoMode.Encode && !Opts.AllowTranscoding)
            throw new InvalidOperationException("This channel needs transcoding for this device, and transcoding is turned off on this server.");
        HlsSession session;
        await _startGate.WaitAsync(ct);
        try
        {
            foreach (var s in _sessions.Values.Where(s => s.MediaId < 0)) await StopAsync(s.Id);
            while (_sessions.Count >= Opts.MaxSessions)
            {
                var oldest = _sessions.Values.OrderBy(s => Interlocked.Read(ref s.LastAccessTicks)).First();
                _log.LogInformation("Evicting HLS session {Id} (limit {Max})", oldest.Id, Opts.MaxSessions);
                await StopAsync(oldest.Id);
            }
            session = CreateSession(-channelId, url, plan);
            _sessions[session.Id] = session;
            try { await LaunchRunAsync(session, 0); }
            catch { await StopAsync(session.Id); throw; }
        }
        finally { _startGate.Release(); }

        try { await WaitUntilPlayableAsync(session, 0, ct); }
        catch { await StopAsync(session.Id); throw; }
        _log.LogInformation("Live session {Id} started for channel {Channel} ({Mode}, pid {Pid}): {Reason}", session.Id, channelId, session.Mode, session.ProcessId, session.Reason);
        return session;
    }

    private const int MaxLiveRelaunches = 5;

    /// <summary>
    /// IPTV sources drop. If ffmpeg has exited while someone is still watching, bring it back (the playlist continues and the player
    /// resynchronises at the seam). A run that lasted a while resets the budget; one that dies at once repeatedly gives up.
    /// </summary>
    private async Task EnsureLiveRunningAsync(HlsSession s, CancellationToken ct)
    {
        if (s.IsRunning || Volatile.Read(ref s.Stopped) != 0) return;
        await s.RunGate.WaitAsync(ct);
        try
        {
            if (s.IsRunning || Volatile.Read(ref s.Stopped) != 0) return;
            if (s.Run is { } last && DateTimeOffset.UtcNow - last.StartedAt > TimeSpan.FromSeconds(60)) s.Relaunches = 0;
            if (s.Relaunches >= MaxLiveRelaunches) return;
            s.Relaunches++;
            _log.LogWarning("Live session {Id}: ffmpeg exited ({Detail}); reconnecting ({N}/{Max})", s.Id, Tail(s).Split('\n')[0], s.Relaunches, MaxLiveRelaunches);
            await LaunchRunAsync(s, 0, append: true);
        }
        finally { s.RunGate.Release(); }
    }

    private HlsSession CreateSession(long mediaId, string inputPath, HlsPlan plan)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // unguessable
        var dir = Path.Combine(Opts.TempDir, id);
        Directory.CreateDirectory(dir);
        var count = plan.Vod ? HlsCommandBuilder.SegmentCount(plan.DurationSeconds, Opts.SegmentSeconds) : 0;
        if (plan.Vod) File.WriteAllText(Path.Combine(dir, HlsCommandBuilder.PlaylistName), HlsCommandBuilder.BuildVodPlaylist(plan.DurationSeconds, Opts.SegmentSeconds));
        return new HlsSession(id, mediaId, dir, plan, inputPath, count, _time.GetUtcNow());
    }

    /// <summary>Starts (or restarts) ffmpeg at a segment. Any previous run is killed first, so only one ffmpeg runs per session.</summary>
    private async Task LaunchRunAsync(HlsSession s, int startIndex, bool append = false)
    {
        if (s.Run is { } old) { s.Run = null; await KillRunAsync(old); }

        var psi = new ProcessStartInfo(Opts.FfmpegPath)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in HlsCommandBuilder.Build(s.InputPath, s.Dir, s.Plan, Opts, startIndex, append)) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new HlsStartException($"Could not start ffmpeg ('{Opts.FfmpegPath}'): {ex.Message}");
        }
        proc.StandardInput.Close();

        var run = new HlsRun(proc, startIndex);
        s.LastPid = run.Pid;
        _ = Task.Run(() => proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null));
        _ = Task.Run(async () =>
        {
            var buf = new char[1024];
            try
            {
                int n;
                while ((n = await proc.StandardError.ReadAsync(buf)) > 0)
                    lock (run.StderrTail)
                    {
                        run.StderrTail.Append(buf, 0, n);
                        if (run.StderrTail.Length > StderrCap) run.StderrTail.Remove(0, run.StderrTail.Length - StderrCap);
                    }
            }
            catch (Exception) { /* process torn down */ }
        });
        s.Run = run;
    }

    private async Task KillRunAsync(HlsRun run)
    {
        Interlocked.Exchange(ref run.Killed, 1);
        try
        {
            if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await run.Process.WaitForExitAsync(cts.Token);
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (OperationCanceledException) { _log.LogWarning("ffmpeg pid {Pid} did not exit within 5s of kill", run.Pid); }
        finally { run.Process.Dispose(); }
    }

    private async Task WaitUntilPlayableAsync(HlsSession s, int startIndex, CancellationToken ct)
    {
        var playlist = Path.Combine(s.Dir, HlsCommandBuilder.PlaylistName);
        var firstSegment = SegmentPath(s, startIndex);
        bool Ready() => s.Vod ? File.Exists(firstSegment) : HasSegment(playlist);
        var deadline = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Opts.StartTimeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Ready()) return;
            if (!s.IsRunning)
            {
                await Task.Delay(100, ct); // let stderr drain
                if (Ready()) return;       // finished very quickly
                throw new HlsStartException($"ffmpeg exited before producing output: {Tail(s)}");
            }
            if (deadline.Elapsed > timeout) throw new HlsStartException($"ffmpeg produced no output within {Opts.StartTimeoutSeconds}s: {Tail(s)}");
            await Task.Delay(100, ct);
        }
    }

    private static bool HasSegment(string playlist)
    {
        try { return File.Exists(playlist) && File.ReadAllText(playlist).Contains("#EXTINF", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private static string Tail(HlsSession s)
    {
        // A channel's URL can carry credentials (and ffmpeg echoes URLs it follows): for live, the whole URL becomes "<stream>"
        // before any other scrubbing, since the generic scrub keeps a path's last segment, which a provider might use as a token.
        var raw = s.StderrText;
        if (s.Live) raw = System.Text.RegularExpressions.Regex.Replace(raw.Replace(s.InputPath, "<stream>"), @"https?://\S+", "<stream>");
        var t = ErrorText.Scrub(raw, s.Live ? "" : s.InputPath, s.Dir);
        return t.Length == 0 ? "(no output)" : t;
    }

    public static bool IsValidSessionId(string id) => IdRx().IsMatch(id);
    public static bool IsValidFileName(string name) => FileRx().IsMatch(name);

    /// <summary>"nvenc" or "libx264": what the server would use for a transcode right now.</summary>
    public string Encoder => Opts.Encoder;

    /// <summary>A snapshot for the player's "stats for nerds": what this stream is doing on the server. Never includes paths or URLs.</summary>
    public HlsSessionInfo? Info(string id)
    {
        if (Get(id) is not { } s) return null;
        var produced = 0;
        try { produced = Directory.Exists(s.Dir) ? Directory.EnumerateFiles(s.Dir, "seg*").Take(100_000).Count() : 0; } catch (IOException) { }
        return new HlsSessionInfo(s.Mode, s.Reason, s.Plan.Video == VideoMode.Encode ? Opts.Encoder : "none (video copied)", s.Plan.Audio.ToString(), s.Plan.Segments == HlsSegments.Fmp4 ? "fMP4" : "MPEG-TS",
            s.Vod, s.Live, s.IsRunning, produced, s.SegmentCount, s.Relaunches, (int)(DateTimeOffset.UtcNow - s.StartedAt).TotalSeconds, s.Plan.MaxWidth, s.Plan.VideoKbps, s.Plan.BurnSubtitle is not null);
    }

    public HlsSession? Get(string id) => IsValidSessionId(id) && _sessions.TryGetValue(id, out var s) ? s : null;

    public void Touch(HlsSession s) => Interlocked.Exchange(ref s.LastAccessTicks, _time.GetUtcNow().UtcTicks);

    private static string SegmentPath(HlsSession s, int index) => Path.Combine(s.Dir, $"seg{index.ToString("00000", CultureInfo.InvariantCulture)}.ts");

    /// <summary>Resolves a whitelisted file in the session dir, waiting for (or restarting ffmpeg towards) a segment that isn't there yet.</summary>
    public async Task<string?> GetFileAsync(HlsSession s, string name, CancellationToken ct)
    {
        if (!IsValidFileName(name)) return null;
        Touch(s);
        var path = Path.Combine(s.Dir, name);

        if (s.Live && name == HlsCommandBuilder.PlaylistName) await EnsureLiveRunningAsync(s, ct);

        if (s.Vod && TsSegmentRx().Match(name) is { Success: true } m)
        {
            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return index < s.SegmentCount && await EnsureSegmentAsync(s, index, path, ct) ? path : null;
        }

        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (!s.IsRunning || wait.Elapsed > TimeSpan.FromSeconds(10) || name == HlsCommandBuilder.PlaylistName) return null;
            await Task.Delay(100, ct);
        }
        Touch(s);
        return path;
    }

    /// <summary>
    /// Segmented mode. If ffmpeg is already heading for this segment (it is the next few), wait for it. If it is behind the
    /// run's start or far ahead of it, a seek happened: restart ffmpeg at this segment instead of waiting for it to crawl there.
    /// </summary>
    private async Task<bool> EnsureSegmentAsync(HlsSession s, int index, string path, CancellationToken ct)
    {
        if (File.Exists(path)) return true;

        await s.RunGate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return true;
            if (Volatile.Read(ref s.Stopped) != 0) return false;
            var run = s.Run;
            var next = run is null ? -1 : NextIndex(s, run);
            var willProduce = run is { IsAlive: true } && index >= run.StartIndex && index <= next + LookAheadSegments;
            if (!willProduce)
            {
                _log.LogInformation("HLS session {Id}: seek to segment {Index} (encoder was at {Next}), restarting ffmpeg there", s.Id, index, next);
                await LaunchRunAsync(s, index);
            }
        }
        finally { s.RunGate.Release(); }

        var sw = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(Opts.SegmentWaitSeconds);
        while (!File.Exists(path))
        {
            if (Volatile.Read(ref s.Stopped) != 0) return false;
            if (s.Run is not { IsAlive: true })
            {
                await Task.Delay(150, ct); // the run ended: give a final rename a moment, then give up
                return File.Exists(path);
            }
            if (sw.Elapsed > limit) return false;
            await Task.Delay(100, ct);
        }
        Touch(s);
        return true;
    }

    /// <summary>The first segment (from the run's start onward) that does not exist yet.</summary>
    private static int NextIndex(HlsSession s, HlsRun run)
    {
        var i = run.StartIndex;
        while (i < s.SegmentCount && File.Exists(SegmentPath(s, i))) i++;
        return i;
    }

    /// <summary>Idempotent. Kills ffmpeg (whole process tree) and removes the session directory.</summary>
    public async Task StopAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return;
        if (Interlocked.Exchange(ref s.Stopped, 1) != 0) return;
        try
        {
            // Wait out any restart in progress so it can't leave a process behind.
            var gated = await s.RunGate.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                if (s.Run is { } run) { s.Run = null; await KillRunAsync(run); }
            }
            finally { if (gated) s.RunGate.Release(); }
        }
        finally
        {
            TryDeleteDir(s.Dir);
            _log.LogInformation("HLS session {Id} stopped", id);
        }
    }

    /// <summary>Called periodically: stops sessions nobody has touched within the idle window.</summary>
    public async Task ReapIdleAsync()
    {
        var cutoff = _time.GetUtcNow().UtcTicks - TimeSpan.FromSeconds(Opts.IdleSeconds).Ticks;
        foreach (var s in _sessions.Values.Where(s => Interlocked.Read(ref s.LastAccessTicks) < cutoff))
        {
            _log.LogInformation("Reaping idle HLS session {Id}", s.Id);
            await StopAsync(s.Id);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await StopAsync(id);
    }

    // Run on the thread pool: blocking on async work from a thread that has a synchronization context (a test runner, a UI thread)
    // deadlocks as soon as an await inside needs to resume on that same thread.
    public void Dispose() => Task.Run(StopAllAsync).GetAwaiter().GetResult();

    private void CleanTempDir()
    {
        // Leftovers from a previous crash.
        try
        {
            Directory.CreateDirectory(Opts.TempDir);
            foreach (var d in Directory.GetDirectories(Opts.TempDir)) TryDeleteDir(d);
        }
        catch (IOException ex) { _log.LogWarning("Could not clean HLS temp dir: {Error}", ex.Message); }
    }

    private void TryDeleteDir(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        _log.LogWarning("Could not delete HLS dir {Dir}", dir);
    }
}

public sealed class HlsReaper(HlsSessionManager manager, HlsOptions opts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.ReapIntervalSeconds));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await manager.ReapIdleAsync(); }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await manager.StopAllAsync(); // no orphaned ffmpeg after a graceful shutdown
    }
}
