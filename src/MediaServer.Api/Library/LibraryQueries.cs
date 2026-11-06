using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probing;

namespace MediaServer.Api.Library;

/// <param name="ImageUrl">Landscape card art: the TMDB backdrop when matched, otherwise a frame from the video.</param>
/// <param name="HeroUrl">Large banner art (same source, bigger).</param>
public sealed record ItemSummary(string Type, string Key, string Title, int? Year, long MediaId, string? PosterUrl, string ImageUrl,
    string HeroUrl, string? Overview, int? EpisodeCount, bool Matched, bool IsNew, double? Rating = null, string? Genres = null);
public sealed record ItemList(int Total, IReadOnlyList<ItemSummary> Items);

public sealed record ContinueItem(string Type, long MediaId, string Title, string? Subtitle, string ImageUrl, double PositionSeconds,
    double? DurationSeconds, string? SeriesKey, bool NextUp);

public sealed record MediaFileInfo(long MediaId, string FileName, double? DurationSeconds, string? Container, string? VideoCodec,
    int? Width, int? Height, IReadOnlyList<AudioTrack> Audio, IReadOnlyList<SubtitleTrack> Subtitles, bool DirectPlay,
    string StreamUrl, string? SeriesKey, long? NextMediaId, string Title, string Mode, string Reason,
    double ResumeSeconds, string ThumbUrl, string? NextTitle);

public sealed record VersionEntry(long MediaId, string FileName, int? Width, int? Height);
public sealed record MovieDetail(string Title, int? Year, string? Overview, string? Genres, double? Rating,
    string? PosterUrl, string BackdropUrl, MediaFileInfo File, IReadOnlyList<VersionEntry> OtherVersions);

public sealed record EpisodeEntry(long MediaId, int? Season, int? Episode, int? EpisodeEnd, string Title, double? DurationSeconds,
    string ThumbUrl, double PositionSeconds, bool Watched);
public sealed record SeasonEntry(int? Season, IReadOnlyList<EpisodeEntry> Episodes);
public sealed record SeriesDetail(string Title, int? Year, string? Overview, string? Genres, double? Rating,
    string? PosterUrl, string BackdropUrl, long? ResumeMediaId, IReadOnlyList<SeasonEntry> Seasons, long MatchMediaId);

public sealed class LibraryQueries(Database db)
{
    private static readonly long NewWindowTicks = TimeSpan.FromDays(21).Ticks;

    private sealed record R(long Id, string Path, string Kind, string Title, int? Year, int? Season, int? Episode, int? EpisodeEnd,
        string? EpisodeTitle, string? AirDate, double? Duration, string? InfoJson, long MtimeTicks, string? TmdbType, int? TmdbId,
        string? MTitle, int? MYear, string? Overview, string? Poster, string? Backdrop, double? Rating, string? Genres,
        double Pos, bool Watched, string? ProgressAt)
    {
        public string Display => !string.IsNullOrEmpty(MTitle) ? MTitle : Title.Length > 0 ? Title : System.IO.Path.GetFileNameWithoutExtension(Path);
        public bool Matched => MTitle is not null;
        /// <summary>Set after loading: lets an unmatched episode join the series its same-titled siblings were matched to.</summary>
        public string? Group { get; init; }
        public string SeriesKey => Group ?? (TmdbId is { } t && TmdbType == "tv" ? $"tmdb:{t}" : $"title:{TmdbMatcher.Key(Title)}");
        public MediaInfo? Info => InfoJson is null ? null : JsonSerializer.Deserialize<MediaInfo>(InfoJson);
    }

    private sealed record Entry(ItemSummary S, long Added, string GroupKey);

    private List<R> Load()
    {
        var rows = LoadRows();
        // A new episode can arrive before the next enrichment (or the lookup can fail for it alone). It must still show up under
        // its show, not as a second, unmatched copy of it: join the group that same-titled episodes were matched to.
        var showByTitle = rows.Where(r => r.Kind == "Episode" && r.TmdbType == "tv" && r.TmdbId is not null)
            .GroupBy(r => TmdbMatcher.Key(r.Title)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => $"tmdb:{g.First().TmdbId}");
        if (showByTitle.Count == 0) return rows;
        return rows.Select(r => r.Kind == "Episode" && r.TmdbId is null && showByTitle.TryGetValue(TmdbMatcher.Key(r.Title), out var g) ? r with { Group = g } : r).ToList();
    }

    private List<R> LoadRows()
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT f.id, f.path, COALESCE(f.kind,'Unknown'), COALESCE(f.title,''), f.year, f.season, f.episode, f.episode_end,
                   f.episode_title, f.air_date, f.duration_seconds, f.info_json, f.mtime_ticks, f.tmdb_type, f.tmdb_id,
                   m.title, m.year, m.overview, m.poster_path, m.backdrop_path, m.rating, m.genres,
                   COALESCE(p.position_seconds, 0), COALESCE(p.watched, 0), p.updated_at
            FROM media_files f
            LEFT JOIN metadata m ON m.tmdb_type=f.tmdb_type AND m.tmdb_id=f.tmdb_id
            LEFT JOIN media_progress p ON p.media_id=f.id
            WHERE f.deleted_at IS NULL AND COALESCE(f.kind,'Unknown') <> 'Extra'
            """, r =>
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            return new R(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), I(4), I(5), I(6), I(7), S(8), S(9), D(10), S(11),
                r.GetInt64(12), S(13), I(14), S(15), I(16), S(17), S(18), S(19), D(20), S(21), r.GetDouble(22), r.GetInt64(23) != 0, S(24));
        });
    }

    // ------------------------------------------------------------------ browsing

    public ItemList List(string? q, string? type, string sort, int limit, int offset, bool? matched = null, string? genre = null)
    {
        var rows = Load();
        var now = DateTime.UtcNow.Ticks;
        var items = new List<Entry>();

        // Episodes -> one item per series.
        foreach (var g in rows.Where(r => r.Kind == "Episode").GroupBy(r => r.SeriesKey))
        {
            var rep = Ordered(g).First();
            var meta = g.FirstOrDefault(r => r.Matched) ?? rep;
            var added = g.Max(r => r.MtimeTicks);
            items.Add(new Entry(new ItemSummary("series", g.Key, meta.Display, meta.MYear ?? meta.Year, rep.Id, Poster(meta, rep.Id),
                Art(meta, rep.Id, 640), Art(meta, rep.Id, 1280), meta.Overview, g.Count(), meta.Matched, now - added < NewWindowTicks, meta.Rating, meta.Genres), added, g.Key));
        }
        // Movies (and unparseable files, so everything stays playable) -> one item per TMDB movie / per file.
        foreach (var g in rows.Where(r => r.Kind != "Episode").GroupBy(r => r.Matched && r.TmdbType == "movie" ? $"tmdb:{r.TmdbId}" : $"file:{r.Id}"))
        {
            var rep = g.OrderBy(r => r.Id).First();
            var added = g.Max(r => r.MtimeTicks);
            items.Add(new Entry(new ItemSummary("movie", rep.Id.ToString(CultureInfo.InvariantCulture), rep.Display, rep.MYear ?? rep.Year, rep.Id,
                Poster(rep, rep.Id), Art(rep, rep.Id, 640), Art(rep, rep.Id, 1280), rep.Overview, null, rep.Matched, now - added < NewWindowTicks, rep.Rating, rep.Genres), added, g.Key));
        }

        IEnumerable<Entry> query = items;
        if (type is "movie" or "series") query = query.Where(i => i.S.Type == type);
        if (matched is { } m) query = query.Where(i => i.S.Matched == m);
        if (!string.IsNullOrWhiteSpace(genre)) query = query.Where(i => HasGenre(i.S.Genres, genre));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var tokens = TmdbMatcher.Key(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return new ItemList(0, []); // e.g. "%": nothing searchable, so nothing matches
            var haystack = rows.GroupBy(r => r.Kind == "Episode" ? r.SeriesKey : (r.Matched && r.TmdbType == "movie" ? $"tmdb:{r.TmdbId}" : $"file:{r.Id}"))
                .ToDictionary(g => g.Key, g => TmdbMatcher.Key(string.Join(' ', g.SelectMany(r => new[] { r.Display, r.Title, r.MTitle ?? "", System.IO.Path.GetFileNameWithoutExtension(r.Path) }))));
            query = query.Where(i => haystack.TryGetValue(i.GroupKey, out var h) && tokens.All(t => h.Contains(t, StringComparison.Ordinal)));
        }

        var sorted = sort switch
        {
            "recent" => query.OrderByDescending(i => i.Added).ThenBy(i => SortKey(i.S.Title), StringComparer.Ordinal),   // newest file first
            "year" => query.OrderByDescending(i => i.S.Year ?? 0).ThenBy(i => SortKey(i.S.Title), StringComparer.Ordinal),
            _ => query.OrderBy(i => SortKey(i.S.Title), StringComparer.Ordinal).ThenBy(i => i.S.Year ?? 0),
        };
        var all = sorted.Select(i => i.S).ToList();
        return new ItemList(all.Count, all.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)).ToList());
    }

    private static bool HasGenre(string? genres, string wanted) =>
        genres is not null && genres.Split(',', StringSplitOptions.TrimEntries).Any(g => string.Equals(g, wanted.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Genres present in the library (most common first), optionally for movies or series only.</summary>
    public IReadOnlyList<(string Name, int Count)> Genres(string? type)
    {
        var all = List(null, type, "title", 100_000, 0).Items;
        return all.Where(i => i.Genres is not null).SelectMany(i => i.Genres!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Item1, StringComparer.OrdinalIgnoreCase).Take(40).ToList();
    }

    public sealed record RandomPick(long MediaId, string Title, string Kind);

    /// <summary>Something to watch when you can't decide: a random movie or episode, preferring things you haven't seen.</summary>
    public RandomPick? Random(string kind, string? genre, Random? rng = null)
    {
        rng ??= System.Random.Shared;
        var want = kind == "episode" ? "Episode" : "Movie";
        var pool = Load().Where(r => r.Kind == want).ToList();
        if (!string.IsNullOrWhiteSpace(genre))
            pool = pool.Where(r => r.Genres is not null && r.Genres.Split(',', StringSplitOptions.TrimEntries).Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        var fresh = pool.Where(r => !r.Watched).ToList();
        var from = fresh.Count > 0 ? fresh : pool;
        if (from.Count == 0) return null;
        var r = from[rng.Next(from.Count)];
        return new RandomPick(r.Id, want == "Episode" ? $"{r.Display} – {EpisodeLabel(r)}" : r.Display, want.ToLowerInvariant());
    }

    public sealed record LibraryStats(int Movies, int Shows, int Episodes, int Files, int Unmatched);

    public LibraryStats Stats()
    {
        var rows = Load();
        var movies = List(null, "movie", "title", 1, 0).Total;
        var shows = List(null, "series", "title", 1, 0).Total;
        return new LibraryStats(movies, shows, rows.Count(r => r.Kind == "Episode"), rows.Count, UnmatchedCount());
    }

    /// <summary>Titles (movies and series) that have no TMDB match yet.</summary>
    public int UnmatchedCount() => List(null, null, "title", 1, 0, matched: false).Total;

    public MovieDetail? Movie(long mediaId, ClientCaps caps)
    {
        var rows = Load();
        var rep = rows.FirstOrDefault(r => r.Id == mediaId && r.Kind != "Episode");
        if (rep is null) return null;
        var versions = rep.Matched && rep.TmdbType == "movie"
            ? rows.Where(r => r.Id != rep.Id && r.Kind != "Episode" && r.TmdbType == "movie" && r.TmdbId == rep.TmdbId).OrderBy(r => r.Id).ToList()
            : [];
        return new MovieDetail(rep.Display, rep.MYear ?? rep.Year, rep.Overview, rep.Genres, rep.Rating, Poster(rep, rep.Id), Art(rep, rep.Id, 1280),
            FileInfo(rep, rows, caps), versions.Select(v => new VersionEntry(v.Id, System.IO.Path.GetFileName(v.Path), v.Info?.Video.FirstOrDefault()?.Width, v.Info?.Video.FirstOrDefault()?.Height)).ToList());
    }

    public SeriesDetail? Series(string key)
    {
        var eps = Load().Where(r => r.Kind == "Episode" && r.SeriesKey == key).ToList();
        if (eps.Count == 0) return null;
        var meta = eps.FirstOrDefault(r => r.Matched) ?? eps[0];
        var first = Ordered(eps).First();
        var seasons = eps.GroupBy(r => r.Season).OrderBy(g => g.Key ?? int.MaxValue)
            .Select(g => new SeasonEntry(g.Key, Ordered(g).Select(r => new EpisodeEntry(r.Id, r.Season, r.Episode, r.EpisodeEnd, EpisodeLabel(r), r.Duration,
                $"/api/media/{r.Id}/thumb?w=320", r.Pos, r.Watched)).ToList()))
            .ToList();
        return new SeriesDetail(meta.Display, meta.MYear ?? meta.Year, meta.Overview, meta.Genres, meta.Rating, Poster(meta, first.Id), Art(meta, first.Id, 1280),
            ResumeTarget(Ordered(eps).ToList())?.Id, seasons, first.Id);
    }

    /// <summary>Playback info for any media ID (movie or episode), including the next episode and where to resume.</summary>
    public MediaFileInfo? Play(long mediaId, ClientCaps caps)
    {
        var rows = Load();
        var r = rows.FirstOrDefault(x => x.Id == mediaId);
        return r is null ? null : FileInfo(r, rows, caps);
    }

    // ------------------------------------------------------------------ continue watching

    /// <summary>Unfinished movies/episodes plus "next up" for series whose latest episode is finished; most recent first.</summary>
    public IReadOnlyList<ContinueItem> Continue(int limit)
    {
        var rows = Load();
        var withProgress = rows.Where(r => r.ProgressAt is not null).ToList();
        var found = new List<(string At, ContinueItem Item)>();

        foreach (var r in withProgress.Where(r => r.Kind != "Episode" && !r.Watched && r.Pos >= ProgressService.MinResumeSeconds))
            found.Add((r.ProgressAt!, new ContinueItem("movie", r.Id, r.Display, r.MYear?.ToString(CultureInfo.InvariantCulture) ?? r.Year?.ToString(CultureInfo.InvariantCulture),
                Art(r, r.Id, 640), r.Pos, r.Duration, null, false)));

        foreach (var g in withProgress.Where(r => r.Kind == "Episode").GroupBy(r => r.SeriesKey))
        {
            var last = g.OrderByDescending(r => r.ProgressAt, StringComparer.Ordinal).First();
            var meta = rows.Where(r => r.Kind == "Episode" && r.SeriesKey == g.Key).FirstOrDefault(r => r.Matched) ?? last;
            if (!last.Watched)
            {
                if (last.Pos >= ProgressService.MinResumeSeconds)
                    found.Add((last.ProgressAt!, new ContinueItem("episode", last.Id, meta.Display, EpisodeTag(last), Art(meta, last.Id, 640), last.Pos, last.Duration, g.Key, false)));
            }
            else if (NextEpisode(rows, last) is { } next && !next.Watched)
            {
                found.Add((last.ProgressAt!, new ContinueItem("episode", next.Id, meta.Display, EpisodeTag(next), Art(meta, next.Id, 640), 0, next.Duration, g.Key, true)));
            }
        }
        return found.OrderByDescending(f => f.At, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 50)).Select(f => f.Item).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static MediaFileInfo FileInfo(R r, List<R> all, ClientCaps caps)
    {
        var info = r.Info;
        var v = info?.Video.FirstOrDefault();
        R? next = r.Kind == "Episode" ? NextEpisode(all, r) : null;
        var decision = PlaybackPolicy.Decide(info, System.IO.Path.GetExtension(r.Path), caps);
        var title = r.Kind == "Episode" ? $"{r.Display} – {EpisodeLabel(r)}" : r.Display;
        // Resume only if meaningfully started and not finished.
        var resume = !r.Watched && r.Pos >= ProgressService.MinResumeSeconds && (r.Duration is not { } d || r.Pos < d - 30) ? r.Pos : 0;
        return new MediaFileInfo(r.Id, System.IO.Path.GetFileName(r.Path), r.Duration, info?.Container, v?.Codec, v?.Width, v?.Height,
            info?.Audio ?? [], info?.Subtitles ?? [], decision.Mode == PlayMode.Direct, $"/stream/{r.Id}",
            r.Kind == "Episode" ? r.SeriesKey : null, next?.Id, title, decision.ModeName, decision.Reason,
            resume, $"/api/media/{r.Id}/thumb?w=1280", next is null ? null : EpisodeTag(next));
    }

    private static R? NextEpisode(List<R> all, R r)
    {
        var ordered = Ordered(all.Where(x => x.Kind == "Episode" && x.SeriesKey == r.SeriesKey)).ToList();
        var idx = ordered.FindIndex(x => x.Id == r.Id);
        return idx >= 0 && idx + 1 < ordered.Count ? ordered[idx + 1] : null;
    }

    /// <summary>What "Play" on a series should start: the episode in progress, otherwise the first unwatched one.</summary>
    private static R? ResumeTarget(List<R> ordered)
    {
        var inProgress = ordered.Where(r => !r.Watched && r.Pos >= ProgressService.MinResumeSeconds)
            .OrderByDescending(r => r.ProgressAt, StringComparer.Ordinal).FirstOrDefault();
        if (inProgress is not null) return inProgress;
        var lastWatched = ordered.Where(r => r.Watched).OrderByDescending(r => r.ProgressAt, StringComparer.Ordinal).FirstOrDefault();
        if (lastWatched is not null && NextEpisode(ordered, lastWatched) is { } n) return n;
        return ordered.FirstOrDefault(r => !r.Watched) ?? ordered.FirstOrDefault();
    }

    private static IEnumerable<R> Ordered(IEnumerable<R> eps) =>
        eps.OrderBy(r => r.Season ?? int.MaxValue).ThenBy(r => r.Episode ?? int.MaxValue).ThenBy(r => r.AirDate, StringComparer.Ordinal).ThenBy(r => r.Id);

    private static string EpisodeLabel(R r)
    {
        var num = r.Episode is { } e ? (r.EpisodeEnd is { } end && end != e ? $"E{e}–E{end}" : $"E{e}") : null;
        var name = r.EpisodeTitle ?? r.AirDate;
        return num is null ? name ?? System.IO.Path.GetFileNameWithoutExtension(r.Path) : name is null ? $"Episode {r.Episode}" : name;
    }

    private static string EpisodeTag(R r) =>
        r.Season is { } s && r.Episode is { } e ? $"S{s}:E{e} · {EpisodeLabel(r)}" : EpisodeLabel(r);

    private static string? Poster(R meta, long mediaId) => meta.Poster is null ? null : $"/api/media/{mediaId}/poster";

    /// <summary>TMDB backdrop if we have one, otherwise a frame grabbed from the video itself.</summary>
    private static string Art(R meta, long mediaId, int width) =>
        meta.Backdrop is not null ? $"/api/media/{mediaId}/backdrop?size={(width >= 1280 ? "w1280" : "w780")}" : $"/api/media/{mediaId}/thumb?w={width}";

    /// <summary>Case-insensitive sort key that ignores leading articles ("The Matrix" sorts under M).</summary>
    private static string SortKey(string title)
    {
        var k = TmdbMatcher.Key(title);
        foreach (var a in new[] { "the ", "a ", "an " })
            if (k.StartsWith(a, StringComparison.Ordinal) && k.Length > a.Length) return k[a.Length..];
        return k;
    }
}
