using System.Globalization;
using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;

namespace MediaServer.Api.Metadata;

public sealed record EnrichResult(int Groups, int Matched, int Unmatched, int OverridesApplied, int Errors);

public sealed record MatchInfo(string? State, string? TmdbType, int? TmdbId, string? Title, int? Year, string? Overview,
    string? PosterPath, string? BackdropPath, double? Rating, string? Genres);

/// <summary>Matches library rows to TMDB entries. Manual overrides always beat automatic matching.</summary>
public sealed class MetadataService(Database db, TmdbClient tmdb, TimeProvider time, IConfiguration config, ILogger<MetadataService> log)
{
    private static readonly SemaphoreSlim Single = new(1, 1); // process-wide: one enrichment at a time

    private sealed record Row(long Id, string Kind, string Title, int? Year, string? State);

    /// <returns>null if an enrichment is already running.</returns>
    public async Task<EnrichResult?> EnrichAsync(CancellationToken ct)
    {
        if (!tmdb.IsConfigured) throw new TmdbNotConfiguredException();
        if (!await Single.WaitAsync(0, ct)) return null;
        try { return await EnrichCoreAsync(ct); }
        finally { Single.Release(); }
    }

    private async Task<EnrichResult> EnrichCoreAsync(CancellationToken ct)
    {
        int matched = 0, unmatched = 0, overrides = 0, errors = 0;
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var retryCutoff = time.GetUtcNow().AddDays(-config.GetValue("Metadata:RetryUnmatchedDays", 7)).ToString("O", CultureInfo.InvariantCulture);

        using var conn = db.Open();

        // 1+2. Manual overrides (file scope, then title scope) for rows not already manually matched.
        var fileOv = Database.Query(conn, "SELECT media_id, tmdb_type, tmdb_id FROM match_overrides WHERE media_id IS NOT NULL",
            r => (Id: r.GetInt64(0), Type: r.GetString(1), TmdbId: r.GetInt32(2)));
        var titleOv = Database.Query(conn, "SELECT kind, title_key, tmdb_type, tmdb_id FROM match_overrides WHERE title_key IS NOT NULL",
            r => (Kind: r.GetString(0), Key: r.GetString(1), Type: r.GetString(2), TmdbId: r.GetInt32(3)));

        foreach (var o in fileOv)
        {
            ct.ThrowIfCancellationRequested();
            var state = Database.Query(conn, "SELECT match_state, tmdb_type, tmdb_id FROM media_files WHERE id=@i",
                r => (State: r.IsDBNull(0) ? null : r.GetString(0), Type: r.IsDBNull(1) ? null : r.GetString(1), Id: r.IsDBNull(2) ? (int?)null : r.GetInt32(2)), ("@i", o.Id));
            if (state.Count == 0 || (state[0].State == "manual" && state[0].Type == o.Type && state[0].Id == o.TmdbId)) continue;
            if (await EnsureMetadataAsync(conn, o.Type, o.TmdbId, ct)) { SetMatch(conn, null, [o.Id], o.Type, o.TmdbId, "manual", now); overrides++; }
        }

        var candidates = Database.Query(conn, """
            SELECT id, kind, title, year, match_state FROM media_files
            WHERE deleted_at IS NULL AND kind IN ('Movie','Episode') AND title <> ''
              AND (match_state IS NULL OR (match_state='unmatched' AND matched_at < @cut))
            """, r => new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)),
            ("@cut", retryCutoff));

        var remaining = new List<Row>();
        foreach (var g in candidates.GroupBy(r => (r.Kind, Key: TmdbMatcher.Key(r.Title))))
        {
            var ov = titleOv.FirstOrDefault(o => o.Kind == g.Key.Kind && o.Key == g.Key.Key);
            if (ov.Key is not null && await EnsureMetadataAsync(conn, ov.Type, ov.TmdbId, ct))
            {
                SetMatch(conn, null, g.Select(r => r.Id).ToList(), ov.Type, ov.TmdbId, "manual", now);
                overrides += g.Count();
            }
            else remaining.AddRange(g);
        }

        // 3. Automatic matching: one TMDB lookup per (kind, title, year) group, not per file.
        var groups = remaining.GroupBy(r => (r.Kind, Key: TmdbMatcher.Key(r.Title), r.Year)).ToList();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var first = g.First();
            var type = first.Kind == "Episode" ? "tv" : "movie";
            try
            {
                var hit = await FindAsync(type, first.Title, first.Year, ct);
                var ids = g.Select(r => r.Id).ToList();
                if (hit is not null && await EnsureMetadataAsync(conn, type, hit.Id, ct))
                { SetMatch(conn, null, ids, type, hit.Id, "matched", now); matched += ids.Count; }
                else
                { SetMatch(conn, null, ids, null, null, "unmatched", now); unmatched += ids.Count; }
            }
            catch (TmdbException ex) when (ex.Status != System.Net.HttpStatusCode.Unauthorized)
            {
                log.LogWarning("TMDB lookup failed for {Title}: {Error}", first.Title, ex.Message);
                errors++; // leave pending; next run retries
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("TMDB unreachable for {Title}: {Error}", first.Title, ex.Message);
                errors++;
            }
        }

        var result = new EnrichResult(groups.Count, matched, unmatched, overrides, errors);
        log.LogInformation("Enrichment complete: {@Result}", result);
        return result;
    }

    private async Task<TmdbHit?> FindAsync(string type, string title, int? year, CancellationToken ct)
    {
        // With a year first; then "title year" (covers "Wonder Woman 1984", where the year is part of the title); then bare title.
        var attempts = new List<(string Query, int? Year)>();
        if (year is not null) { attempts.Add((title, year)); attempts.Add(($"{title} {year}", null)); }
        attempts.Add((title, null));
        foreach (var (q, y) in attempts)
        {
            var hits = await tmdb.SearchAsync(type, q, y, ct);
            // Judge by the original parsed title+year, not the widened query.
            if (TmdbMatcher.Pick(hits, title, year) is { } pick) return pick;
            if (q != title && TmdbMatcher.Pick(hits, q, null) is { } pick2) return pick2;
        }
        return null;
    }

    private async Task<bool> EnsureMetadataAsync(SqliteConnection conn, string type, int id, CancellationToken ct)
    {
        var details = await tmdb.GetDetailsAsync(type, id, ct);
        if (details is null) return false;
        Upsert(conn, null, details);
        return true;
    }

    private void Upsert(SqliteConnection conn, SqliteTransaction? tx, TmdbDetails d) =>
        Database.Execute(conn, tx, """
            INSERT INTO metadata(tmdb_type,tmdb_id,title,original_title,year,release_date,overview,poster_path,backdrop_path,rating,genres,fetched_at)
            VALUES(@t,@i,@title,@ot,@y,@rd,@ov,@pp,@bp,@r,@g,@now)
            ON CONFLICT(tmdb_type,tmdb_id) DO UPDATE SET title=excluded.title, original_title=excluded.original_title, year=excluded.year,
                release_date=excluded.release_date, overview=excluded.overview, poster_path=excluded.poster_path,
                backdrop_path=excluded.backdrop_path, rating=excluded.rating, genres=excluded.genres, fetched_at=excluded.fetched_at
            """, ("@t", d.Type), ("@i", d.Id), ("@title", d.Title), ("@ot", d.OriginalTitle), ("@y", d.Year), ("@rd", d.ReleaseDate),
            ("@ov", d.Overview), ("@pp", d.PosterPath), ("@bp", d.BackdropPath), ("@r", d.Rating), ("@g", string.Join(", ", d.Genres)),
            ("@now", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));

    private static void SetMatch(SqliteConnection conn, SqliteTransaction? outer, IReadOnlyList<long> ids, string? type, int? tmdbId, string? state, string now)
    {
        using var tx = outer is null ? conn.BeginTransaction() : null;
        foreach (var id in ids)
            Database.Execute(conn, outer ?? tx, "UPDATE media_files SET tmdb_type=@t, tmdb_id=@i, match_state=@s, matched_at=@n WHERE id=@id",
                ("@t", type), ("@i", tmdbId), ("@s", state), ("@n", state is null ? null : now), ("@id", id));
        tx?.Commit();
    }

    // ---- Manual overrides -------------------------------------------------------------------------------------

    /// <param name="scope">"file" (just this file) or "title" (every file with the same kind + parsed title, e.g. a whole series).</param>
    /// <returns>Number of rows re-matched, or null if the media ID is unknown.</returns>
    /// <exception cref="KeyNotFoundException">TMDB has no such ID.</exception>
    public async Task<int?> SetOverrideAsync(long mediaId, string type, int tmdbId, string scope, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) throw new ArgumentException("tmdbType must be 'movie' or 'tv'");
        if (scope is not ("file" or "title")) throw new ArgumentException("scope must be 'file' or 'title'");

        using var conn = db.Open();
        var row = LoadRow(conn, mediaId);
        if (row is null) return null;
        if (scope == "title" && (row.Title.Length == 0 || row.Kind is not ("Movie" or "Episode")))
            throw new ArgumentException("This file has no parsed title; use scope 'file'");

        var details = await tmdb.GetDetailsAsync(type, tmdbId, ct) ?? throw new KeyNotFoundException($"TMDB has no {type} with id {tmdbId}");
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        using var tx = conn.BeginTransaction();
        Upsert(conn, tx, details);
        List<long> affected;
        if (scope == "file")
        {
            Database.Execute(conn, tx, "DELETE FROM match_overrides WHERE media_id=@m", ("@m", mediaId));
            Database.Execute(conn, tx, "INSERT INTO match_overrides(media_id,tmdb_type,tmdb_id,created_at) VALUES(@m,@t,@i,@n)",
                ("@m", mediaId), ("@t", type), ("@i", tmdbId), ("@n", now));
            affected = [mediaId];
        }
        else
        {
            var key = TmdbMatcher.Key(row.Title);
            Database.Execute(conn, tx, "DELETE FROM match_overrides WHERE kind=@k AND title_key=@tk", ("@k", row.Kind), ("@tk", key));
            Database.Execute(conn, tx, "INSERT INTO match_overrides(kind,title_key,tmdb_type,tmdb_id,created_at) VALUES(@k,@tk,@t,@i,@n)",
                ("@k", row.Kind), ("@tk", key), ("@t", type), ("@i", tmdbId), ("@n", now));
            affected = GroupIds(conn, row);
        }
        SetMatch(conn, tx, affected, type, tmdbId, "manual", now);
        tx.Commit();
        return affected.Count;
    }

    /// <summary>Removes the override and returns affected rows to the automatic matcher's queue.</summary>
    public int? ClearOverride(long mediaId, string scope)
    {
        if (scope is not ("file" or "title")) throw new ArgumentException("scope must be 'file' or 'title'");
        using var conn = db.Open();
        var row = LoadRow(conn, mediaId);
        if (row is null) return null;

        using var tx = conn.BeginTransaction();
        List<long> affected;
        if (scope == "file")
        {
            Database.Execute(conn, tx, "DELETE FROM match_overrides WHERE media_id=@m", ("@m", mediaId));
            affected = [mediaId];
        }
        else
        {
            Database.Execute(conn, tx, "DELETE FROM match_overrides WHERE kind=@k AND title_key=@tk", ("@k", row.Kind), ("@tk", TmdbMatcher.Key(row.Title)));
            affected = GroupIds(conn, row);
        }
        SetMatch(conn, tx, affected, null, null, null, "");
        tx.Commit();
        return affected.Count;
    }

    public MatchInfo? GetMatch(long mediaId)
    {
        using var conn = db.Open();
        var rows = Database.Query(conn, """
            SELECT f.match_state, f.tmdb_type, f.tmdb_id, m.title, m.year, m.overview, m.poster_path, m.backdrop_path, m.rating, m.genres
            FROM media_files f LEFT JOIN metadata m ON m.tmdb_type=f.tmdb_type AND m.tmdb_id=f.tmdb_id WHERE f.id=@i
            """, r => new MatchInfo(S(r, 0), S(r, 1), r.IsDBNull(2) ? null : r.GetInt32(2), S(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4),
            S(r, 5), S(r, 6), S(r, 7), r.IsDBNull(8) ? null : r.GetDouble(8), S(r, 9)), ("@i", mediaId));
        return rows.Count == 0 ? null : rows[0];

        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static Row? LoadRow(SqliteConnection c, long id)
    {
        var rows = Database.Query(c, "SELECT id, COALESCE(kind,''), COALESCE(title,''), year, match_state FROM media_files WHERE id=@i",
            r => new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)), ("@i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    private static List<long> GroupIds(SqliteConnection c, Row row)
    {
        var key = TmdbMatcher.Key(row.Title);
        return Database.Query(c, "SELECT id, title FROM media_files WHERE kind=@k AND title<>''", r => (Id: r.GetInt64(0), Title: r.GetString(1)), ("@k", row.Kind))
            .Where(x => TmdbMatcher.Key(x.Title) == key).Select(x => x.Id).ToList();
    }
}
