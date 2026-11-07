using System.Collections.Concurrent;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Probing;
using Microsoft.Data.Sqlite;

namespace MediaServer.Api.Library;

public sealed record ScanResult(int Added, int Updated, int Moved, int Restored, int Removed, int Unchanged, int ProbeErrors);

/// <summary>
/// Incremental, idempotent scanner. Change detection is size+mtime; unchanged files are never re-probed.
/// A vanished path whose (size, mtime) matches a new path is treated as a move (ID preserved).
/// Files that disappear are soft-deleted and are restored if they come back.
/// </summary>
public sealed class LibraryScanner(Database db, IMediaProbe probe, IConfiguration config, ILogger<LibraryScanner> log, LibraryFolders? folders = null)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg" };

    private readonly SemaphoreSlim _single = new(1, 1);

    public string Root => Path.GetFullPath(config["Library:Root"] ?? throw new InvalidOperationException("Library:Root is not configured"));

    /// <returns>null if a scan is already running.</returns>
    public async Task<ScanResult?> ScanAsync(CancellationToken ct)
    {
        if (!await _single.WaitAsync(0, ct)) return null;
        try { return await ScanCoreAsync(ct); }
        finally { _single.Release(); }
    }

    private readonly record struct Stamp(long Size, long MtimeTicks);
    private sealed record Row(long Id, string Path, Stamp Stamp, bool Deleted, int ProbeVersion);

    private async Task<ScanResult> ScanCoreAsync(CancellationToken ct)
    {
        // The primary folder must be there (as before: a missing mount fails the scan rather than wiping the library). An added folder
        // that's currently unavailable (an unplugged drive, an offline share) is skipped and its files are left exactly as they are.
        var roots = folders is null ? [new LibraryRoot("", Root, null)] : folders.EnabledRoots();
        if (roots.Count == 0) throw new InvalidOperationException("No library folders are set up yet");
        var found = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        foreach (var r in roots)
        {
            if (r.FolderId is not null && !Directory.Exists(r.Path))
            {
                unavailable.Add(r.Prefix);
                if (r.Prefix != LibraryFolders.RecordingsPrefix) log.LogWarning("Library folder {Folder} is unavailable; skipping it", r.Path);   // no recordings yet is normal
                continue;
            }
            foreach (var (rel, stamp) in Enumerate(r.Path, ct, ScanFilter.From(config))) found[r.Prefix + rel] = stamp;
        }

        using var conn = db.Open();
        var existing = LoadRows(conn);
        var byPath = existing.ToDictionary(r => r.Path, StringComparer.Ordinal);
        var missing = existing.Where(r => !found.ContainsKey(r.Path) && !unavailable.Any(u => r.Path.StartsWith(u, StringComparison.Ordinal))).ToList();
        var claimed = new HashSet<long>();

        int unchanged = 0;
        var restores = new List<long>();
        var moves = new List<(long Id, string NewPath)>();
        var probes = new List<(long? Id, string Path, Stamp Stamp)>(); // Id null => insert

        foreach (var (rel, stamp) in found)
        {
            if (byPath.TryGetValue(rel, out var row))
            {
                if (row.Stamp == stamp)
                {
                    if (row.Deleted) restores.Add(row.Id);
                    else if (row.ProbeVersion < FfprobeParser.Version) probes.Add((row.Id, rel, stamp)); // parser learned new fields
                    else unchanged++;
                }
                else probes.Add((row.Id, rel, stamp));
                continue;
            }

            var match = FindMove(missing, claimed, rel, stamp);
            if (match is not null) { claimed.Add(match.Id); moves.Add((match.Id, rel)); }
            else probes.Add((null, rel, stamp));
        }

        var removed = missing.Where(r => !claimed.Contains(r.Id) && !r.Deleted).Select(r => r.Id).ToList();

        // Probe only new/changed files, in parallel (the process runner bounds actual ffprobe concurrency).
        var probed = new ConcurrentDictionary<string, (MediaInfo? Info, string? Error)>();
        await Parallel.ForEachAsync(probes,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = config.GetValue("Library:ProbeParallelism", 4) },
            async (p, token) =>
            {
                try { probed[p.Path] = (await probe.ProbeAsync(Absolute(roots, p.Path), token), null); }
                catch (ProbeFailedException ex) { log.LogWarning("Probe failed for {Path}: {Error}", p.Path, ex.Message); probed[p.Path] = (null, ex.Message); }
            });

        var now = DateTime.UtcNow.ToString("O");
        int added = 0, updated = 0;
        using (var tx = conn.BeginTransaction())
        {
            foreach (var (id, newPath) in moves)
                Run(conn, tx, "UPDATE media_files SET path=@p, deleted_at=NULL, last_scanned=@now, parser_version=0 WHERE id=@id", ("@p", newPath), ("@now", now), ("@id", id));
            foreach (var id in restores)
                Run(conn, tx, "UPDATE media_files SET deleted_at=NULL, last_scanned=@now WHERE id=@id", ("@now", now), ("@id", id));
            foreach (var id in removed)
                Run(conn, tx, "UPDATE media_files SET deleted_at=@now WHERE id=@id", ("@now", now), ("@id", id));

            foreach (var (id, path, stamp) in probes)
            {
                var (info, error) = probed[path];
                object? infoJson = info is null ? DBNull.Value : JsonSerializer.Serialize(info);
                object? duration = info?.DurationSeconds is { } d ? d : DBNull.Value;
                object? err = error is null ? DBNull.Value : error;
                if (id is null)
                {
                    Run(conn, tx, """
                        INSERT INTO media_files(path,size,mtime_ticks,duration_seconds,info_json,probe_error,first_seen,last_scanned,probe_version)
                        VALUES(@p,@s,@m,@d,@i,@e,@now,@now,@pv)
                        """, ("@p", path), ("@s", stamp.Size), ("@m", stamp.MtimeTicks), ("@d", duration), ("@i", infoJson), ("@e", err), ("@now", now), ("@pv", FfprobeParser.Version));
                    added++;
                }
                else
                {
                    Run(conn, tx, """
                        UPDATE media_files SET size=@s,mtime_ticks=@m,duration_seconds=@d,info_json=@i,probe_error=@e,last_scanned=@now,deleted_at=NULL,probe_version=@pv
                        WHERE id=@id
                        """, ("@s", stamp.Size), ("@m", stamp.MtimeTicks), ("@d", duration), ("@i", infoJson), ("@e", err), ("@now", now), ("@pv", FfprobeParser.Version), ("@id", id.Value));
                    updated++;
                }
            }
            ReparseStale(conn, tx);
            tx.Commit();
        }

        var result = new ScanResult(added, updated, moves.Count, restores.Count, removed.Count, unchanged, probed.Values.Count(v => v.Error is not null));
        log.LogInformation("Scan complete: {@Result}", result);
        return result;
    }

    /// <summary>Parses names for new/moved rows and for any row parsed by an older parser version.</summary>
    private static void ReparseStale(SqliteConnection c, SqliteTransaction tx)
    {
        var stale = new List<(long Id, string Path)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id,path FROM media_files WHERE parser_version<>@v";
            q.Parameters.AddWithValue("@v", FilenameParser.Version);
            using var r = q.ExecuteReader();
            while (r.Read()) stale.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (id, path) in stale)
        {
            var p = path.StartsWith(LibraryFolders.RecordingsPrefix, StringComparison.Ordinal) ? RecordingName(path) : FilenameParser.Parse(LibraryFolders.StripPrefix(path));
            object N(object? v) => v ?? DBNull.Value;
            Run(c, tx, """
                UPDATE media_files SET parser_version=@v, kind=@k, title=@t, year=@y, season=@s, episode=@e,
                    episode_end=@ee, episode_title=@et, air_date=@ad WHERE id=@id
                """, ("@v", FilenameParser.Version), ("@k", p.Kind.ToString()), ("@t", p.Title), ("@y", N(p.Year)), ("@s", N(p.Season)),
                ("@e", N(p.Episode)), ("@ee", N(p.EpisodeEnd)), ("@et", N(p.EpisodeTitle)), ("@ad", N(p.AirDate)), ("@id", id));
        }
    }

    /// <summary>"News at Six [2026-11-06 1800].ts" is a recording called "News at Six": never mistaken for a movie or an episode.</summary>
    internal static ParsedName RecordingName(string stored)
    {
        var stem = Path.GetFileNameWithoutExtension(LibraryFolders.StripPrefix(stored));
        var cut = stem.LastIndexOf(" [", StringComparison.Ordinal);
        var title = (cut > 0 ? stem[..cut] : stem).Trim();
        return new ParsedName(MediaKind.Recording, title.Length == 0 ? "Recording" : title, null, null, null, null, null, null, null, null);
    }

    private static string Absolute(IReadOnlyList<LibraryRoot> roots, string stored)
    {
        var root = roots.Where(r => r.Prefix.Length > 0 && stored.StartsWith(r.Prefix, StringComparison.Ordinal)).FirstOrDefault() ?? roots.First(r => r.Prefix.Length == 0);
        return Path.Combine(root.Path, stored[root.Prefix.Length..]);
    }

    private static Row? FindMove(List<Row> missing, HashSet<long> claimed, string newPath, Stamp stamp)
    {
        var name = Path.GetFileName(newPath);
        Row? first = null;
        foreach (var r in missing)
        {
            if (claimed.Contains(r.Id) || r.Stamp != stamp) continue;
            if (Path.GetFileName(r.Path) == name) return r; // best: same file name, new folder
            first ??= r;                                     // otherwise: renamed (same size+mtime)
        }
        return first;
    }

    private static Dictionary<string, Stamp> Enumerate(string root, CancellationToken ct, ScanFilter filter)
    {
        var result = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The library folder is missing or not mounted");
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(fi.Extension) || fi.Name.StartsWith('.')) continue;
            var rel = Path.GetRelativePath(root, fi.FullName);
            if (rel.Split(Path.DirectorySeparatorChar).Any(seg => seg.StartsWith('.') || seg == "@eaDir")) continue;
            if (!filter.Allows(rel, fi.Length)) continue;
            result[rel] = new Stamp(fi.Length, fi.LastWriteTimeUtc.Ticks);
        }
        return result;
    }

    private static List<Row> LoadRows(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,path,size,mtime_ticks,deleted_at IS NOT NULL,probe_version FROM media_files";
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read()) rows.Add(new Row(r.GetInt64(0), r.GetString(1), new Stamp(r.GetInt64(2), r.GetInt64(3)), r.GetBoolean(4), r.GetInt32(5)));
        return rows;
    }

    private static void Run(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }
}
