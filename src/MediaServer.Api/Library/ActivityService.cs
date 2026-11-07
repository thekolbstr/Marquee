using System.Globalization;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

public sealed record ActivityDay(string Day, double Seconds);
public sealed record ActivityPerson(long UserId, string Name, double Seconds, int Plays, string? LastAt);
public sealed record ActivityTitle(string Title, string Kind, double Seconds, int Plays, int Viewers);
public sealed record ActivityRecent(string Person, string Title, string? Detail, string Day, double Seconds, string LastAt);
public sealed record ActivityNow(string Person, string Title, string? Detail, double Position, double? Duration, int AgoSeconds);
public sealed record ActivityReport(int Days, double TotalSeconds, int TotalPlays, int ActivePeople, IReadOnlyList<ActivityDay> PerDay, IReadOnlyList<ActivityPerson> People,
    IReadOnlyList<ActivityTitle> Top, IReadOnlyList<ActivityRecent> Recent, IReadOnlyList<ActivityNow> Now);

/// <summary>What has been watched, by whom and when. Read-only: the numbers are written by <see cref="ProgressService"/> as people watch.</summary>
public sealed class ActivityService(Database db, TimeProvider time)
{
    /// <summary>A heartbeat newer than this means the person is still watching (the player saves every 10 s).</summary>
    public const int NowWindowSeconds = 45;
    public const int MaxDays = 365;

    public ActivityReport Report(int days)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var since = today.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var c = db.Open();
        var p = ("@since", (object?)since);

        var byDay = Database.Query(c, "SELECT day, SUM(seconds) FROM watch_activity WHERE day >= @since GROUP BY day",
            r => (r.GetString(0), r.GetDouble(1)), p).ToDictionary(x => x.Item1, x => x.Item2);
        var perDay = Enumerable.Range(0, days).Select(i => today.AddDays(-(days - 1) + i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Select(d => new ActivityDay(d, byDay.GetValueOrDefault(d))).ToList();

        var people = Database.Query(c, $"""
            SELECT a.user_id, {PersonName("u", "a.user_id")}, SUM(a.seconds), SUM(a.plays), MAX(a.last_at)
            FROM watch_activity a LEFT JOIN users u ON u.id=a.user_id
            WHERE a.day >= @since GROUP BY a.user_id ORDER BY SUM(a.seconds) DESC
            """, r => new ActivityPerson(r.GetInt64(0), r.GetString(1), r.GetDouble(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)), p);

        var top = Database.Query(c, $"""
            SELECT {TitleKey}, CASE WHEN f.kind='Episode' THEN 'series' ELSE 'movie' END, SUM(a.seconds), SUM(a.plays), COUNT(DISTINCT a.user_id)
            FROM watch_activity a JOIN media_files f ON f.id=a.media_id LEFT JOIN metadata m ON m.tmdb_type=f.tmdb_type AND m.tmdb_id=f.tmdb_id
            WHERE a.day >= @since
            GROUP BY CASE WHEN f.kind='Episode' THEN 'S:' || {TitleKey} ELSE 'M:' || a.media_id END
            ORDER BY SUM(a.seconds) DESC LIMIT 10
            """, r => new ActivityTitle(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetInt32(3), r.GetInt32(4)), p);

        var recent = Database.Query(c, $"""
            SELECT {PersonName("u", "a.user_id")}, {TitleKey}, f.kind, f.season, f.episode, f.episode_title, a.day, a.seconds, a.last_at
            FROM watch_activity a JOIN media_files f ON f.id=a.media_id LEFT JOIN users u ON u.id=a.user_id
            LEFT JOIN metadata m ON m.tmdb_type=f.tmdb_type AND m.tmdb_id=f.tmdb_id
            WHERE a.day >= @since AND a.seconds >= 1 ORDER BY a.last_at DESC LIMIT 25
            """, r => new ActivityRecent(r.GetString(0), r.GetString(1), EpisodeLabel(r.GetString(2), Int(r, 3), Int(r, 4), r.IsDBNull(5) ? null : r.GetString(5)),
                r.GetString(6), r.GetDouble(7), r.GetString(8)), p);

        var nowCutoff = time.GetUtcNow().AddSeconds(-NowWindowSeconds).ToString("O", CultureInfo.InvariantCulture);
        var now = Database.Query(c, $"""
            SELECT {PersonName("u", "g.user_id")}, {TitleKey}, f.kind, f.season, f.episode, f.episode_title, g.position_seconds, g.duration_seconds, g.updated_at
            FROM media_progress g JOIN media_files f ON f.id=g.media_id LEFT JOIN users u ON u.id=g.user_id
            LEFT JOIN metadata m ON m.tmdb_type=f.tmdb_type AND m.tmdb_id=f.tmdb_id
            WHERE g.updated_at >= @cut ORDER BY g.updated_at DESC LIMIT 20
            """, r => new ActivityNow(r.GetString(0), r.GetString(1), EpisodeLabel(r.GetString(2), Int(r, 3), Int(r, 4), r.IsDBNull(5) ? null : r.GetString(5)),
                r.GetDouble(6), r.IsDBNull(7) ? null : r.GetDouble(7),
                (int)Math.Max(0, (time.GetUtcNow() - DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture)).TotalSeconds)), ("@cut", nowCutoff));

        return new ActivityReport(days, perDay.Sum(d => d.Seconds), people.Sum(x => x.Plays), people.Count, perDay, people, top, recent, now);
    }

    // The series/film name: TMDB's when matched, otherwise what the filename said.
    private const string TitleKey = "COALESCE(m.title, f.title)";
    // With sign-in off there is no users row; everything belongs to one anonymous person.
    private static string PersonName(string u, string id) => $"COALESCE(NULLIF({u}.display_name,''), {u}.username, 'User ' || {id})";
    private static int? Int(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    internal static string? EpisodeLabel(string kind, int? season, int? episode, string? title)
    {
        if (kind != "Episode") return null;
        var code = season is { } s && episode is { } e ? $"S{s}E{e}" : episode is { } e2 ? $"E{e2}" : null;
        return string.Join(" · ", new[] { code, title }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } label ? label : null;
    }
}
