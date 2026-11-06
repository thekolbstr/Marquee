using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Processes;

namespace MediaServer.Api.Library;

public sealed record TrickplayInfo(double Interval, int Width, int Height, int Columns, int Rows, int Count, int Sheets);

/// <summary>
/// Preview pictures for the seek bar: one pass over the file produces sprite sheets (a grid of small frames every few seconds). It reads the
/// whole file, so it runs on demand, one title at a time, in the background; the player shows previews once they exist.
/// </summary>
public sealed class TrickplayService(Database db, IMediaResolver media, IProcessRunner runner, IConfiguration config, ILogger<TrickplayService> log)
{
    public const int Columns = 10, Rows = 10, ThumbWidth = 160;
    private readonly string _dir = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "trickplay");
    private readonly string _ffmpeg = config["Tools:FfmpegPath"] ?? "ffmpeg";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private static readonly TimeSpan FailureMemory = TimeSpan.FromHours(24);

    public bool Enabled => config.GetValue("Playback:Trickplay", true);

    /// <summary>Seconds between preview frames: finer for short videos, coarser for very long ones (at most about 800 frames).</summary>
    public static double IntervalFor(double duration) => duration <= 0 ? 10 : Math.Max(5, Math.Ceiling(duration / 800 / 5) * 5);

    public enum State { Ready, Generating, Unavailable }

    public (State State, TrickplayInfo? Info) Get(long mediaId)
    {
        if (!Enabled) return (State.Unavailable, null);
        var dir = Dir(mediaId, out var duration, out var size);
        if (dir is null) return (State.Unavailable, null);
        var infoFile = Path.Combine(dir, "info.json");
        if (File.Exists(infoFile))
        {
            try { return (State.Ready, JsonSerializer.Deserialize<TrickplayInfo>(File.ReadAllText(infoFile))); }
            catch (Exception ex) when (ex is JsonException or IOException) { /* regenerate */ }
        }
        var failed = Path.Combine(dir, "failed");
        if (File.Exists(failed) && DateTime.UtcNow - File.GetLastWriteTimeUtc(failed) < FailureMemory) return (State.Unavailable, null);
        if (duration < 60) return (State.Unavailable, null);                                                // nothing worth previewing in a short clip
        if (_running.ContainsKey(mediaId)) return (State.Generating, null);
        var path = media.ResolvePath(mediaId);
        if (path is null) return (State.Unavailable, null);
        _running[mediaId] = Task.Run(async () => { try { await GenerateAsync(mediaId, path, dir, duration, size); } finally { _running.TryRemove(mediaId, out _); } });
        return (State.Generating, null);
    }

    public string? SheetPath(long mediaId, int n)
    {
        var dir = Dir(mediaId, out _, out _);
        if (dir is null || n < 0 || n > 2000) return null;
        var p = Path.Combine(dir, $"sheet{n:000}.jpg");
        return File.Exists(p) ? p : null;
    }

    private string? Dir(long mediaId, out double duration, out (int W, int H) size)
    {
        duration = 0; size = (16, 9);
        var path = media.ResolvePath(mediaId);
        if (path is null) return null;
        try { var fi = new FileInfo(path); if (!fi.Exists) return null; }
        catch (IOException) { return null; }
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT duration_seconds, info_json, mtime_ticks FROM media_files WHERE id=@i AND deleted_at IS NULL",
            r => (r.IsDBNull(0) ? 0.0 : r.GetDouble(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2)), ("@i", mediaId));
        if (rows.Count == 0) return null;
        duration = rows[0].Item1;
        if (rows[0].Item2 is { } json)
            try
            {
                var v = JsonSerializer.Deserialize<Probing.MediaInfo>(json)?.Video.FirstOrDefault();
                if (v is { Width: > 0, Height: > 0 }) size = (v.Width.Value, v.Height.Value);
            }
            catch (JsonException) { }
        return Path.Combine(_dir, $"{mediaId}-{rows[0].Item3:x}");
    }

    private async Task GenerateAsync(long mediaId, string input, string dir, double duration, (int W, int H) size)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch (IOException) { } }
            var interval = IntervalFor(duration);
            var h = Math.Max(2, (int)Math.Round(ThumbWidth * (double)size.H / Math.Max(1, size.W) / 2) * 2);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            // Keyframes only (fast), one picture per interval; the fps filter fills the gaps with the previous picture.
            var r = await runner.RunAsync(_ffmpeg, ["-nostdin", "-v", "error", "-y", "-skip_frame", "nokey", "-i", input, "-an", "-sn", "-dn", "-threads", "2",
                "-vf", $"fps=1/{interval.ToString(CultureInfo.InvariantCulture)},scale={ThumbWidth}:{h},tile={Columns}x{Rows}", "-q:v", "7", "-fps_mode", "passthrough", Path.Combine(dir, "sheet%03d.jpg")], cts.Token);
            var sheets = Directory.GetFiles(dir, "sheet*.jpg").Length;
            if (r.ExitCode != 0 || sheets == 0) throw new InvalidOperationException(r.StdErr.Length > 200 ? r.StdErr[..200] : r.StdErr);
            // ffmpeg numbers sheets from 1; the page counts from 0.
            for (var i = 0; i < sheets; i++) File.Move(Path.Combine(dir, $"sheet{i + 1:000}.jpg"), Path.Combine(dir, $"sheet{i:000}.tmp"), true);
            for (var i = 0; i < sheets; i++) File.Move(Path.Combine(dir, $"sheet{i:000}.tmp"), Path.Combine(dir, $"sheet{i:000}.jpg"), true);
            var count = (int)Math.Floor(duration / interval) + 1;
            File.WriteAllText(Path.Combine(dir, "info.json"), JsonSerializer.Serialize(new TrickplayInfo(interval, ThumbWidth, h, Columns, Rows, count, sheets)));
        }
        catch (Exception ex)
        {
            log.LogWarning("Preview thumbnails for media {Id} failed: {Error}", mediaId, ex.Message);
            try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "failed"), ex.Message); } catch (IOException) { }
        }
        finally { _gate.Release(); }
    }
}
