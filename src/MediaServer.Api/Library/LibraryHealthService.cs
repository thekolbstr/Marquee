using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Probing;

namespace MediaServer.Api.Library;

public sealed record HealthFile(long Id, string Name, long SizeBytes, string? Detail);
public sealed record DuplicateGroup(string Title, string Kind, IReadOnlyList<HealthFile> Files, long WastedBytes);
public sealed record LibraryHealth(int TotalFiles, bool TmdbConfigured, int Unmatched,
    IReadOnlyList<DuplicateGroup> Duplicates, long DuplicateWastedBytes,
    IReadOnlyList<HealthFile> Unreadable, int UnreadableCount,
    IReadOnlyList<HealthFile> NeedsTranscoding, int NeedsTranscodingCount);

/// <summary>
/// A read-only report on the library: titles that exist twice, files that could not be analysed, files that are not matched to TMDB,
/// and files a typical browser cannot play without the server converting them. Only file names are reported, never folders.
/// </summary>
public sealed class LibraryHealthService(Database db, IConfiguration config)
{
    private const int Listed = 200;

    private sealed record Row(long Id, string Path, long Size, string? Kind, string? Title, int? Year, int? Season, int? Episode, long? TmdbId, string? ProbeError, string? InfoJson);

    public LibraryHealth Report()
    {
        List<Row> rows;
        using (var c = db.Open())
            rows = Database.Query(c, "SELECT id, path, size, kind, title, year, season, episode, tmdb_id, probe_error, info_json FROM media_files WHERE deleted_at IS NULL ORDER BY id",
                r => new Row(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));

        var tmdb = !string.IsNullOrWhiteSpace(config["Tmdb:ReadAccessToken"]) || !string.IsNullOrWhiteSpace(config["Tmdb:ApiKey"]);
        var unmatched = tmdb ? rows.Count(r => r.TmdbId is null && r.Kind is "Movie" or "Episode") : 0;

        var dupes = Duplicates(rows);
        var unreadable = rows.Where(r => !string.IsNullOrEmpty(r.ProbeError)).Select(r => File(r, "Could not be analysed")).ToList();
        var heavy = new List<HealthFile>();
        foreach (var r in rows.Where(r => string.IsNullOrEmpty(r.ProbeError) && r.InfoJson is not null))
        {
            MediaInfo? info;
            try { info = JsonSerializer.Deserialize<MediaInfo>(r.InfoJson!); } catch (JsonException) { continue; }
            var d = PlaybackPolicy.Decide(info, System.IO.Path.GetExtension(r.Path), ClientCaps.Baseline);
            if (d.Mode == PlayMode.Transcode) heavy.Add(File(r, d.Reason));
        }
        return new LibraryHealth(rows.Count, tmdb, unmatched, dupes.Take(Listed).ToList(), dupes.Sum(g => g.WastedBytes),
            unreadable.Take(Listed).ToList(), unreadable.Count, heavy.Take(Listed).ToList(), heavy.Count);
    }

    private static HealthFile File(Row r, string? detail) => new(r.Id, System.IO.Path.GetFileName(r.Path), r.Size, detail);

    /// <summary>Two or more files for the same film or the same episode. The wasted space is everything but the largest copy.</summary>
    private static List<DuplicateGroup> Duplicates(List<Row> rows)
    {
        string? Key(Row r)
        {
            if (r.Kind == "Movie" && !string.IsNullOrWhiteSpace(r.Title))
                return r.TmdbId is { } t ? $"m:tmdb:{t}" : $"m:{r.Title!.Trim().ToLowerInvariant()}:{r.Year}";
            if (r.Kind == "Episode" && !string.IsNullOrWhiteSpace(r.Title) && r.Season is not null && r.Episode is not null)
                return $"e:{(r.TmdbId is { } t2 ? "tmdb:" + t2 : r.Title!.Trim().ToLowerInvariant())}:{r.Season}:{r.Episode}";
            return null;
        }
        return rows.Select(r => (Key: Key(r), Row: r)).Where(x => x.Key is not null).GroupBy(x => x.Key!).Where(g => g.Count() > 1)
            .Select(g =>
            {
                var first = g.First().Row;
                var title = first.Kind == "Episode" ? $"{first.Title} S{first.Season:00}E{first.Episode:00}" : first.Year is { } y ? $"{first.Title} ({y})" : first.Title ?? "Untitled";
                var files = g.Select(x => File(x.Row, null)).OrderByDescending(f => f.SizeBytes).ToList();
                return new DuplicateGroup(title, first.Kind ?? "", files, files.Skip(1).Sum(f => f.SizeBytes));
            })
            .OrderByDescending(g => g.WastedBytes).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
