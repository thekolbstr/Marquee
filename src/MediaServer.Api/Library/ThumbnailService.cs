using System.Collections.Concurrent;
using System.Globalization;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;

namespace MediaServer.Api.Library;

/// <summary>
/// Artwork without needing TMDB: grabs one representative frame from the video itself and caches it on disk.
/// Clients only ever pass a media ID and a whitelisted width; the file path comes from the database.
/// </summary>
public sealed class ThumbnailService(Database db, IMediaResolver resolver, IProcessRunner runner, IConfiguration config, ILogger<ThumbnailService> log)
{
    private static readonly int[] Widths = [320, 640, 1280];
    private readonly string _dir = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "thumbs");
    private readonly string _ffmpeg = config["Tools:FfmpegPath"] ?? "ffmpeg";
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    // Thumbnails are a nice-to-have: keep them from competing with playback and scans for CPU and memory.
    private readonly SemaphoreSlim _gate = new(Math.Max(1, config.GetValue("Thumbnails:MaxConcurrency", 2)));
    private readonly TimeSpan _attemptTimeout = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Thumbnails:AttemptTimeoutSeconds", 15)));
    private static readonly TimeSpan FailureMemory = TimeSpan.FromDays(1);
    private static readonly TimeSpan TimeoutMemory = TimeSpan.FromMinutes(10); // a stalled NFS read is probably transient

    public static bool IsValidWidth(int w) => Widths.Contains(w);

    /// <returns>Path of a cached JPEG, or null if the media is unknown or no frame could be extracted.</returns>
    public async Task<string?> GetAsync(long mediaId, int width, CancellationToken ct)
    {
        if (!IsValidWidth(width)) return null;
        var path = resolver.ResolvePath(mediaId);
        if (path is null) return null;

        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return null; }
        catch (IOException) { return null; }

        // The file's mtime is part of the name: a replaced file gets a fresh thumbnail.
        var cache = Path.Combine(_dir, $"{mediaId}-{width}-{fi.LastWriteTimeUtc.Ticks:x}.jpg");
        if (File.Exists(cache)) return cache;
        if (RecentFailure(cache + ".fail")) return null;

        // Concurrent requests for the same thumbnail share one ffmpeg run; one caller leaving doesn't cancel it for the rest.
        var work = _inflight.GetOrAdd(cache, key => Task.Run(async () =>
        {
            try { return await GenerateAsync(mediaId, path, width, key); }
            finally { _inflight.TryRemove(key, out Task<string?>? _); }
        }));
        return await work.WaitAsync(ct);
    }

    private async Task<string?> GenerateAsync(long mediaId, string path, int width, string cache)
    {
        Directory.CreateDirectory(_dir);
        await _gate.WaitAsync();
        try
        {
            var duration = DurationOf(mediaId);
            // ~12% in skips studio logos and cold opens; `thumbnail` then picks a representative frame from the next few.
            var at = duration > 0 ? Math.Clamp(duration * 0.12, Math.Min(5, duration * 0.3), Math.Max(duration - 5, 1)) : 30;
            var tmp = $"{cache}.{Guid.NewGuid():N}.tmp.jpg";
            var timedOut = false;
            try
            {
                // If seeking stalls or lands past the end, fall back to the start of the file.
                foreach (var seek in new[] { at, 0.0 })
                {
                    using var cts = new CancellationTokenSource(_attemptTimeout);
                    try
                    {
                        var r = await runner.RunAsync(_ffmpeg, Args(path, seek, width, tmp), cts.Token);
                        if (r.ExitCode == 0 && File.Exists(tmp))
                        {
                            // A near-solid frame (black fade, credits) makes poor artwork: the generated gradient looks better.
                            if (new FileInfo(tmp).Length >= width * 4L) { File.Move(tmp, cache, overwrite: true); return cache; }
                            log.LogInformation("Thumbnail for media {Id} was a blank frame; using the placeholder", mediaId);
                        }
                        else if (r.ExitCode != 0) log.LogWarning("Thumbnail failed for media {Id}: {Error}", mediaId, ErrorText.Scrub(r.StdErr, path));
                    }
                    catch (OperationCanceledException) { timedOut = true; log.LogWarning("Thumbnail attempt timed out for media {Id}", mediaId); }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
                }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }

            // Remember the failure so every page view doesn't retry a broken file, but not forever.
            try { await File.WriteAllTextAsync(cache + ".fail", timedOut ? "timeout" : "failed"); } catch (IOException) { }
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Memory matters here: `thumbnail` buffers N frames, so frames are scaled down BEFORE it (a 4K 10-bit frame is ~25 MB; the
    /// scaled one is ~0.3 MB), only keyframes are decoded, and decoder threads are capped. Without this, four parallel extractions
    /// from 4K HEVC exceeded a 3 GiB container limit.
    /// </summary>
    internal static string[] Args(string path, double seek, int width, string output)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-threads", "2", "-skip_frame", "nokey" };
        if (seek > 0) args.AddRange(["-ss", seek.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-vf", $"scale={width}:-2,thumbnail=12", "-frames:v", "1", "-q:v", "4", "-y", output]);
        return [.. args];
    }

    private static bool RecentFailure(string marker)
    {
        try
        {
            if (!File.Exists(marker)) return false;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(marker);
            var limit = File.ReadAllText(marker) == "timeout" ? TimeoutMemory : FailureMemory;
            if (age < limit) return true;
            File.Delete(marker); // stale: try again
        }
        catch (IOException) { }
        return false;
    }

    private double DurationOf(long mediaId)
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT duration_seconds FROM media_files WHERE id=@i", r => r.IsDBNull(0) ? 0.0 : r.GetDouble(0), ("@i", mediaId)).FirstOrDefault();
    }
}
