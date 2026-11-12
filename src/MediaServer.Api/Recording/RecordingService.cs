using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;

namespace MediaServer.Api.Recording;

public sealed record RecordingInfo(long Id, long ChannelId, string ChannelName, string Title, string? Description, long StartTs, long StopTs, string Status, string? Error,
    long? MediaId, long? SizeBytes, string? OwnerName);

public sealed class RecordingException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

/// <summary>
/// Live TV recording (DVR). A recording is scheduled for a channel and time (or started "now, until the show ends"); when it is due the
/// server copies the channel's stream to a file (no re-encoding), with a little padding either side. Finished recordings land in the
/// recordings folder, which is part of the library, so they play, resume and get preview thumbnails like anything else.
/// </summary>
public sealed partial class RecordingService(Database db, LiveTvService live, IConfiguration config, TimeProvider time, ILogger<RecordingService> log,
    IUserContext? users = null, ScanCoordinator? scans = null, MediaServer.Api.Notifications.NotificationService? notify = null)
{
    private sealed class Running(Process process, string partFile, string finalFile, string streamUrl)
    {
        public Process Process { get; } = process;
        public string PartFile { get; } = partFile;
        public string FinalFile { get; } = finalFile;
        public string StreamUrl { get; } = streamUrl;
        public StringBuilder Stderr { get; } = new();
        public bool StopRequested { get; set; }
    }

    private readonly ConcurrentDictionary<long, Running> _running = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    private long UserId => users?.UserId ?? 1;
    public string Directory_ => Path.GetFullPath(config["Recordings:Directory"] ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db"))!, "recordings"));
    private int MaxConcurrent => Math.Clamp(config.GetValue("Recordings:MaxConcurrent", 2), 1, 8);
    private int Before => Math.Clamp(config.GetValue("Recordings:BeforeSeconds", 60), 0, 1800);
    private int After => Math.Clamp(config.GetValue("Recordings:AfterSeconds", 120), 0, 3600);
    private long MinFreeBytes => Math.Max(0, config.GetValue("Recordings:MinFreeGb", 2)) * 1024L * 1024 * 1024;
    private const int MaxHours = 8;
    private string Ffmpeg => config["Tools:FfmpegPath"] ?? "ffmpeg";

    // ------------------------------------------------------------------ scheduling

    /// <summary>Record a channel from now until its current programme ends (or for an hour when there's no guide).</summary>
    public RecordingInfo RecordNow(long channelId)
    {
        var detail = live.Channel(channelId) ?? throw new RecordingException("No such channel.", 404);
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var p = detail.Channel.Now;
        return Insert(channelId, detail.Channel.Name, p?.Title ?? detail.Channel.Name, p?.Description, now, p is not null && p.Stop > now + 60 ? p.Stop : now + 3600, padBefore: false);
    }

    /// <summary>Schedule a programme from the guide.</summary>
    public RecordingInfo ScheduleProgramme(long channelId, long programmeStart)
    {
        var detail = live.Channel(channelId) ?? throw new RecordingException("No such channel.", 404);
        var p = live.ChannelPrograms(channelId, programmeStart - 60, programmeStart + 60).FirstOrDefault(x => x.Start == programmeStart)
            ?? throw new RecordingException("That programme isn't in the guide any more.", 404);
        return Insert(channelId, detail.Channel.Name, p.Title, p.Description, p.Start, p.Stop, padBefore: true);
    }

    /// <summary>Schedule an arbitrary time slot.</summary>
    public RecordingInfo ScheduleManual(long channelId, long start, long stop, string? title)
    {
        var detail = live.Channel(channelId) ?? throw new RecordingException("No such channel.", 404);
        var t = string.IsNullOrWhiteSpace(title) ? detail.Channel.Name : title.Trim();
        if (t.Length > 120 || t.Any(char.IsControl)) throw new RecordingException("That title is too long.");
        return Insert(channelId, detail.Channel.Name, t, null, start, stop, padBefore: true);
    }

    private RecordingInfo Insert(long channelId, string channelName, string title, string? description, long start, long stop, bool padBefore)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (stop <= start) throw new RecordingException("The end must be after the start.");
        if (stop - start > MaxHours * 3600L) throw new RecordingException($"Recordings can be at most {MaxHours} hours.");
        if (stop + After < now) throw new RecordingException("That time has already passed.");
        if (start > now + 60L * 24 * 3600) throw new RecordingException("That's too far ahead (at most 60 days).");
        using var c = db.Open();
        if (Database.Scalar(c, $"SELECT 1 FROM recordings WHERE user_id={UserId.ToString(CultureInfo.InvariantCulture)} AND channel_id={channelId.ToString(CultureInfo.InvariantCulture)} AND start_ts={start.ToString(CultureInfo.InvariantCulture)} AND status IN ('scheduled','recording')") is not null)
            throw new RecordingException("That is already scheduled.", 409);
        Database.Execute(c, null, """
            INSERT INTO recordings(user_id, channel_id, channel_name, title, description, start_ts, stop_ts, status, created_at)
            VALUES(@u,@c,@cn,@t,@d,@s,@e,'scheduled',@now)
            """, ("@u", UserId), ("@c", channelId), ("@cn", channelName), ("@t", title.Length > 200 ? title[..200] : title), ("@d", description is { Length: > 1000 } ? description[..1000] : description),
            ("@s", start), ("@e", stop), ("@now", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        var id = Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        _ = padBefore;
        return Get(id)!;
    }

    public IReadOnlyList<RecordingInfo> List(bool all = false)
    {
        using var c = db.Open();
        return Database.Query(c, $"""
            SELECT r.id, r.channel_id, r.channel_name, r.title, r.description, r.start_ts, r.stop_ts, r.status, r.error,
                   (SELECT f.id FROM media_files f WHERE f.path = '@rec/' || r.file_name AND f.deleted_at IS NULL),
                   (SELECT f.size FROM media_files f WHERE f.path = '@rec/' || r.file_name AND f.deleted_at IS NULL),
                   (SELECT COALESCE(u.display_name, u.username) FROM users u WHERE u.id = r.user_id)
            FROM recordings r {(all ? "" : "WHERE r.user_id=@u")} ORDER BY CASE r.status WHEN 'recording' THEN 0 WHEN 'scheduled' THEN 1 ELSE 2 END, r.start_ts DESC
            """, Map, ("@u", UserId));
    }

    public RecordingInfo? Get(long id)
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT r.id, r.channel_id, r.channel_name, r.title, r.description, r.start_ts, r.stop_ts, r.status, r.error,
                   (SELECT f.id FROM media_files f WHERE f.path = '@rec/' || r.file_name AND f.deleted_at IS NULL),
                   (SELECT f.size FROM media_files f WHERE f.path = '@rec/' || r.file_name AND f.deleted_at IS NULL),
                   (SELECT COALESCE(u.display_name, u.username) FROM users u WHERE u.id = r.user_id)
            FROM recordings r WHERE r.id=@i
            """, Map, ("@i", id)).FirstOrDefault();
    }

    private static RecordingInfo Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt64(6),
        r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11));

    private (long UserId, string Status, string? File)? Owner(long id)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT user_id, status, file_name FROM recordings WHERE id=@i", r => (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ("@i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Stop a recording that's in progress (keeping what was captured), or cancel one that hasn't started.</summary>
    public async Task StopAsync(long id, bool isAdmin)
    {
        var o = Owner(id) ?? throw new RecordingException("No such recording.", 404);
        if (o.UserId != UserId && !isAdmin) throw new RecordingException("That recording belongs to someone else.", 403);
        if (_running.TryGetValue(id, out var run)) { run.StopRequested = true; Kill(run.Process); await WaitFinishedAsync(id); return; }
        if (o.Status == "scheduled") SetStatus(id, "cancelled", null);
    }

    /// <summary>Remove a recording completely: cancels it if needed and deletes the file.</summary>
    public async Task DeleteAsync(long id, bool isAdmin)
    {
        var o = Owner(id) ?? throw new RecordingException("No such recording.", 404);
        if (o.UserId != UserId && !isAdmin) throw new RecordingException("That recording belongs to someone else.", 403);
        if (_running.TryGetValue(id, out var run)) { run.StopRequested = true; Kill(run.Process); await WaitFinishedAsync(id); o = Owner(id)!.Value; }
        DeleteFiles(o.File);
        using var c = db.Open();
        Database.Execute(c, null, "DELETE FROM recordings WHERE id=@i", ("@i", id));
        scans?.TryStart();   // the file leaves the library
    }

    private void DeleteFiles(string? file)
    {
        if (string.IsNullOrEmpty(file) || file.Contains('/') || file.Contains('\\') || file.Contains("..")) return;
        foreach (var p in new[] { Path.Combine(Directory_, file), Path.Combine(Directory_, file + ".part") })
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
    }

    private async Task WaitFinishedAsync(long id)
    {
        for (var i = 0; i < 100 && _running.ContainsKey(id); i++) { await Task.Delay(100); await TickAsync(default); }
    }

    // ------------------------------------------------------------------ the worker's step

    /// <summary>One pass: finish what has ended, start what is due, mark what was missed, tidy old files.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _tick.WaitAsync(0, ct)) return;
        try
        {
            var now = time.GetUtcNow();
            var nowTs = now.ToUnixTimeSeconds();

            foreach (var (id, run) in _running.ToArray())
            {
                if (!run.Process.HasExited)
                {
                    var stop = StopOf(id);
                    if (stop is { } s && nowTs > s + After + 90) { run.StopRequested = true; Kill(run.Process); }   // it should have ended itself
                    continue;
                }
                await FinishAsync(id, run);
            }

            using var c = db.Open();
            // missed: the server was off (or busy) for the whole window
            Database.Execute(c, null, "UPDATE recordings SET status='failed', error='This was due while the server wasn''t recording (it was off, or all recorders were busy).' WHERE status='scheduled' AND stop_ts + @after <= @now",
                ("@after", After), ("@now", nowTs));

            var due = Database.Query(c, "SELECT id, channel_id, start_ts, stop_ts, title FROM recordings WHERE status='scheduled' AND start_ts - @before <= @now AND stop_ts + @after > @now ORDER BY start_ts",
                r => (Id: r.GetInt64(0), Channel: r.GetInt64(1), Start: r.GetInt64(2), Stop: r.GetInt64(3), Title: r.GetString(4)), ("@before", Before), ("@now", nowTs), ("@after", After));
            foreach (var r in due)
            {
                if (_running.Count >= MaxConcurrent) break;     // stays scheduled; tried again next pass while its window lasts
                Start(r.Id, r.Channel, r.Stop, r.Title, nowTs);
            }

            if (now - _lastCleanup > TimeSpan.FromHours(1)) { _lastCleanup = now; Cleanup(now); }
        }
        finally { _tick.Release(); }
    }

    private long? StopOf(long id)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT stop_ts FROM recordings WHERE id=@i", r => r.GetInt64(0), ("@i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    private void Start(long id, long channelId, long stop, string title, long nowTs)
    {
        try
        {
            var stream = live.GetStream(channelId) ?? throw new RecordingException("That channel is no longer in the playlist.");
            var dir = Directory_;
            System.IO.Directory.CreateDirectory(dir);
            if (MinFreeBytes > 0 && FreeSpace(dir) is { } free && free < MinFreeBytes) throw new RecordingException("Not enough free disk space to record.");
            var seconds = (int)Math.Clamp(stop + After - nowTs, 30, MaxHours * 3600L + 3600);
            var name = UniqueName(dir, title, nowTs);
            var final = Path.Combine(dir, name); var part = final + ".part";
            var psi = new ProcessStartInfo(Ffmpeg)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "http,https,tcp,tls,crypto", "-rw_timeout", "15000000",
                "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5", "-fflags", "+genpts+discardcorrupt", "-i", stream.Url,
                "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-t", seconds.ToString(CultureInfo.InvariantCulture), "-f", "mpegts", part }) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi) ?? throw new RecordingException("Could not start ffmpeg.");
            proc.StandardInput.Close();
            var run = new Running(proc, part, final, stream.Url);
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (run.Stderr) { if (run.Stderr.Length < 4000) run.Stderr.AppendLine(e.Data); } };
            proc.BeginErrorReadLine();
            _ = proc.StandardOutput.ReadToEndAsync();                 // drained so ffmpeg can never block on a full pipe
            _running[id] = run;
            using var c = db.Open();
            Database.Execute(c, null, "UPDATE recordings SET status='recording', started_at=@t, file_name=@f, error=NULL WHERE id=@i",
                ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("@f", name), ("@i", id));
            log.LogInformation("Recording started: {Title} on channel {Channel} for {Seconds}s", title, channelId, seconds);
        }
        catch (Exception ex) when (ex is RecordingException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not start recording {Id}: {Error}", id, ex.Message);
            SetStatus(id, "failed", ex is RecordingException ? ex.Message : "Could not start the recorder.");
        }
    }

    private async Task FinishAsync(long id, Running run)
    {
        _running.TryRemove(id, out _);
        try { await run.Process.WaitForExitAsync(); } catch (InvalidOperationException) { }
        var exit = SafeExit(run.Process);
        run.Process.Dispose();
        long size = 0;
        try { size = File.Exists(run.PartFile) ? new FileInfo(run.PartFile).Length : 0; } catch (IOException) { }
        string err;
        lock (run.Stderr) err = run.Stderr.ToString();
        err = Redact(err, run.StreamUrl).Trim();

        // Whatever was captured is kept; only an (almost) empty file counts as a failure.
        if (size >= 100_000)
        {
            try { File.Move(run.PartFile, run.FinalFile, true); }
            catch (IOException ex) { SetStatus(id, "failed", $"Could not save the recording: {ex.Message}"); return; }
            SetStatus(id, "done", exit != 0 && !run.StopRequested && err.Length > 0 ? "It ended early: " + FirstLine(err) : null);
            scans?.TryStart();
        }
        else
        {
            try { if (File.Exists(run.PartFile)) File.Delete(run.PartFile); } catch (IOException) { }
            SetStatus(id, run.StopRequested ? "cancelled" : "failed", run.StopRequested ? null : (err.Length > 0 ? FirstLine(err) : "Nothing was recorded. Is the channel working?"));
        }
        log.LogInformation("Recording {Id} finished ({Bytes} bytes)", id, size);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim() is { Length: > 200 } l ? l[..200] : s.Split('\n')[0].Trim();
    private static int SafeExit(Process p) { try { return p.ExitCode; } catch (InvalidOperationException) { return -1; } }

    private void SetStatus(long id, string status, string? error)
    {
        using var c = db.Open();
        Database.Execute(c, null, "UPDATE recordings SET status=@s, error=@e, finished_at=@t WHERE id=@i", ("@s", status), ("@e", error), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("@i", id));
        if (status is "done" or "failed" && notify is not null) _ = TellAsync(id, status, error);
    }

    // A message that a recording finished (or failed). Fire and forget: nothing here may disturb the recorder.
    private async Task TellAsync(long id, string status, string? error)
    {
        try
        {
            if (!notify!.Wants(MediaServer.Api.Notifications.NotifyEvent.Recording)) return;
            string title;
            using (var c = db.Open()) title = Database.Query(c, "SELECT title FROM recordings WHERE id=@i", r => r.GetString(0), ("@i", id)).FirstOrDefault() ?? "A recording";
            await notify.NotifyAsync(MediaServer.Api.Notifications.NotifyEvent.Recording, status == "done" ? "Recording finished" : "Recording failed",
                status == "done" ? $"“{title}” is ready to watch." + (error is null ? "" : " " + error) : $"“{title}” could not be recorded." + (error is null ? "" : " " + error));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation("Recording notification failed: {Reason}", ex.GetType().Name); }
    }

    /// <summary>A stream URL (which may carry a password) must never reach a log or the page.</summary>
    internal static string Redact(string text, string url) => string.IsNullOrEmpty(url) ? text : text.Replace(url, "<stream>");

    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }

    private static long? FreeSpace(string dir) { try { return new DriveInfo(Path.GetPathRoot(dir) ?? dir).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; } }

    [GeneratedRegex(@"[^\p{L}\p{N} ._()\-]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"\.{2,}")] private static partial Regex Dots();

    internal static string UniqueName(string dir, string title, long nowTs)
    {
        var safe = Dots().Replace(Unsafe().Replace(title, "_"), ".").Trim(' ', '.');
        if (safe.Length == 0) safe = "Recording";
        if (safe.Length > 80) safe = safe[..80].TrimEnd();
        var stamp = DateTimeOffset.FromUnixTimeSeconds(nowTs).ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var name = $"{safe} [{stamp}].ts"; var n = 2;
        while (File.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".part"))) name = $"{safe} [{stamp}] {n++}.ts";
        return name;
    }

    private void Cleanup(DateTimeOffset now)
    {
        var days = config.GetValue("Recordings:KeepDays", 0);
        if (days <= 0) return;
        var cutoff = now.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);
        using var c = db.Open();
        var old = Database.Query(c, "SELECT id, file_name FROM recordings WHERE status IN ('done','failed','cancelled') AND finished_at < @c", r => (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)), ("@c", cutoff));
        foreach (var (id, file) in old) { DeleteFiles(file); Database.Execute(c, null, "DELETE FROM recordings WHERE id=@i", ("@i", id)); }
        if (old.Count > 0) scans?.TryStart();
    }

    /// <summary>Called when the server stops: every recorder is ended and what it captured so far is kept.</summary>
    public async Task StopAllAsync()
    {
        foreach (var (_, run) in _running.ToArray()) { run.StopRequested = true; Kill(run.Process); }
        for (var i = 0; i < 50 && !_running.IsEmpty; i++) { await Task.Delay(100); await TickAsync(default); }
    }

    public int ActiveCount => _running.Count;
}

/// <summary>Drives the recorder every few seconds, and ends all recordings cleanly on shutdown.</summary>
public sealed class RecordingWorker(RecordingService recordings, IConfiguration config, ILogger<RecordingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await recordings.TickAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Recording pass failed: {Error}", ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Recordings:PollSeconds", 10))), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await recordings.StopAllAsync();
    }
}
