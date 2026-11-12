using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Maintenance;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Category, string Message);

/// <summary>The last few hundred log lines, kept in memory so the web UI can show them without anyone opening a terminal.</summary>
public sealed class MemoryLogStore
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(LogEntry e)
    {
        _entries.Enqueue(e);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    public IReadOnlyList<LogEntry> Query(string? minLevel, string? contains, int limit)
    {
        var min = Rank(minLevel);
        var q = _entries.Where(e => Rank(e.Level) >= min);
        if (!string.IsNullOrWhiteSpace(contains) && contains.Length <= 100)
            q = q.Where(e => e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(contains, StringComparison.OrdinalIgnoreCase));
        return q.TakeLast(Math.Clamp(limit, 1, 500)).ToList();
    }

    private static int Rank(string? level) => level?.ToLowerInvariant() switch { "error" => 3, "warning" => 2, _ => 1 };
}

public sealed class MemoryLogProvider(MemoryLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new L(store, categoryName);
    public void Dispose() { }

    private sealed class L(MemoryLogStore store, string category) : ILogger
    {
        // Framework chatter (every request, every HTTP call) would bury what matters; keep it for warnings and errors.
        private readonly bool _noisy = category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= (_noisy ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, null).Replace("\r", " ").Replace("\n", " ⏎ ");
            if (exception is not null) msg += $" — {exception.GetType().Name}: {exception.Message.Replace("\r", " ").Replace("\n", " ")}";
            if (msg.Length > 2000) msg = msg[..2000] + "…";
            var cat = category[(category.LastIndexOf('.') + 1)..];
            store.Add(new LogEntry(DateTimeOffset.UtcNow, logLevel switch { LogLevel.Warning => "warning", LogLevel.Error or LogLevel.Critical => "error", _ => "info" }, cat, msg));
        }
    }
}

public sealed record AboutInfo(string Version, string Runtime, string Os, int Cpus, long MemoryBytes, string? Ffmpeg, long DatabaseBytes);

/// <summary>Housekeeping the person running the server might want: clearing caches, refreshing metadata, compacting and backing up the database.</summary>
public sealed class MaintenanceService(Database db, IConfiguration config, HlsSessionManager hls, IProcessRunner runner, MediaServer.Api.Auth.IUserContext? users = null)
{
    private string ImagesDir => Path.GetFullPath(config["Images:CacheDir"] ?? "data/images");
    private string DbPath => Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db");
    private string? _ffmpeg;

    public static long DirSize(string dir)
    {
        long total = 0; var n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                total += f.Length;
                if (++n >= 200_000) break;
            }
        }
        catch (IOException) { }
        return total;
    }

    /// <summary>Deletes cached artwork, video-frame thumbnails, converted captions and channel logos. All of it is rebuilt on demand.</summary>
    public long ClearImageCache()
    {
        var root = ImagesDir;
        if (!Directory.Exists(root)) return 0;
        var before = DirSize(root);
        foreach (var d in Directory.EnumerateDirectories(root)) TryDelete(d);
        foreach (var f in Directory.EnumerateFiles(root)) { try { File.Delete(f); } catch (IOException) { } }
        return Math.Max(0, before - DirSize(root));
    }

    /// <summary>Removes transcode folders that no running stream owns (left behind by a crash or a forced stop).</summary>
    public (int Folders, long Bytes) CleanTranscodes()
    {
        var root = Path.GetFullPath(config["Hls:TempDir"] ?? Path.Combine(Path.GetTempPath(), "mediaserver-hls"));
        if (!Directory.Exists(root)) return (0, 0);
        var live = hls.Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        int n = 0; long bytes = 0;
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            if (live.Contains(Path.GetFileName(d))) continue;
            bytes += DirSize(d);
            if (TryDelete(d)) n++;
        }
        return (n, bytes);
    }

    public (long Before, long After) OptimizeDatabase()
    {
        long Size() => new[] { DbPath, DbPath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        var before = Size();
        using var c = db.Open();
        Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        Database.Exec(c, "VACUUM;");
        Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");   // VACUUM goes through the write-ahead log; fold it back so the sizes mean something
        Database.Exec(c, "PRAGMA optimize;");
        return (before, Size());
    }

    public int ClearWatchHistory()
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM media_progress WHERE user_id=@u", ("@u", users?.UserId ?? 1));   // your own history, not other people's
    }

    /// <summary>Forgets every TMDB match and cached answer so the next scan fetches everything again (manual matches are re-applied).</summary>
    public int ResetMetadata()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Execute(c, tx, "DELETE FROM tmdb_cache");
        Database.Execute(c, tx, "DELETE FROM metadata");
        var n = Database.Execute(c, tx, "UPDATE media_files SET match_state=NULL, tmdb_type=NULL, tmdb_id=NULL, matched_at=NULL WHERE kind IN ('Movie','Episode')");
        tx.Commit();
        return n;
    }

    /// <summary>A consistent copy of the database in a temporary file. The caller sends it and deletes it.</summary>
    public string CreateBackup()
    {
        var path = Path.Combine(Path.GetDirectoryName(DbPath)!, $"backup-{Guid.NewGuid():N}.db");
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO @p";
        cmd.Parameters.AddWithValue("@p", path);
        cmd.ExecuteNonQuery();
        return path;
    }

    public async Task<AboutInfo> AboutAsync(CancellationToken ct)
    {
        var version = AppVersion.Current;
        long dbBytes = new[] { DbPath, DbPath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        return new AboutInfo(version, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, Environment.ProcessorCount,
            Environment.WorkingSet, await FfmpegVersionAsync(ct), dbBytes);
    }

    private async Task<string?> FfmpegVersionAsync(CancellationToken ct)
    {
        if (_ffmpeg is not null) return _ffmpeg.Length == 0 ? null : _ffmpeg;
        try
        {
            var r = await runner.RunAsync(config["Tools:FfmpegPath"] ?? "ffmpeg", ["-hide_banner", "-version"], ct);
            var first = r.StdOut.Split('\n')[0];
            var m = System.Text.RegularExpressions.Regex.Match(first, @"version\s+(\S+)");
            _ffmpeg = m.Success ? m.Groups[1].Value : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { _ffmpeg = ""; }
        return _ffmpeg.Length == 0 ? null : _ffmpeg;
    }

    private static bool TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
