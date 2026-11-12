using System.Globalization;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

/// <summary>Where you stopped watching. Stored on the server so it follows you between browsers and devices.</summary>
public sealed class ProgressService(Database db, TimeProvider time, IConfiguration config, MediaServer.Api.Auth.IUserContext? users = null, MediaServer.Api.Trakt.TraktService? trakt = null)
{
    public const double MinResumeSeconds = 30;
    private long UserId => users?.UserId ?? 1;

    /// <returns>False if the media doesn't exist or the numbers are nonsense.</returns>
    public bool Save(long mediaId, double position, double? duration, bool? watched)
    {
        if (!double.IsFinite(position) || position < 0 || position > 1_000_000) return false;
        if (duration is { } d && (!double.IsFinite(d) || d <= 0 || d > 1_000_000)) return false;

        // "Watched" = past the configured share of the runtime, or within the credits window, unless the caller says so explicitly.
        var percent = Math.Clamp(config.GetValue("Playback:WatchedPercent", 92), 50, 100) / 100.0;
        var credits = Math.Clamp(config.GetValue("Playback:CreditsSeconds", 90), 0, 900);
        var isWatched = watched ?? (duration is > 0 && (position / duration >= percent || duration - position <= credits));

        using var c = db.Open();
        if (Database.Scalar(c, $"SELECT 1 FROM media_files WHERE id={mediaId.ToString(CultureInfo.InvariantCulture)}") is null) return false;
        RecordActivity(c, mediaId, position);
        var wasWatched = isWatched && trakt is not null && Database.Query(c, "SELECT watched FROM media_progress WHERE user_id=@u AND media_id=@m", r => r.GetInt32(0) == 1, ("@u", UserId), ("@m", mediaId)).FirstOrDefault();
        Database.Execute(c, null, """
            INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(@u,@m,@p,@d,@w,@t)
            ON CONFLICT(user_id, media_id) DO UPDATE SET position_seconds=excluded.position_seconds,
                duration_seconds=COALESCE(excluded.duration_seconds, duration_seconds), watched=excluded.watched, updated_at=excluded.updated_at
            """, ("@u", UserId), ("@m", mediaId), ("@p", position), ("@d", duration), ("@w", isWatched ? 1 : 0), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        if (isWatched && !wasWatched) trakt?.QueueWatched(UserId, [mediaId]);   // only the moment it becomes watched, so a rewatch does not add it again
        return true;
    }

    /// <summary>
    /// Watch time comes from the player's heartbeats: how far the position moved since the previous one, capped by the time that passed (so
    /// seeking forward doesn't count as watching). A heartbeat after a long gap, or the first for a title, starts a new "play".
    /// </summary>
    private void RecordActivity(Microsoft.Data.Sqlite.SqliteConnection c, long mediaId, double position)
    {
        var now = time.GetUtcNow();
        var prev = Database.Query(c, "SELECT position_seconds, updated_at FROM media_progress WHERE user_id=@u AND media_id=@m",
            r => (Pos: r.GetDouble(0), At: DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTimeOffset?)null),
            ("@u", UserId), ("@m", mediaId));
        var gap = prev.Count > 0 && prev[0].At is { } at ? (now - at).TotalSeconds : double.PositiveInfinity;
        var newPlay = gap > SessionGapSeconds || gap < 0;
        var delta = prev.Count > 0 ? position - prev[0].Pos : 0;
        var seconds = !newPlay && delta > 0 ? Math.Min(delta, gap * 2 + 1) : 0;   // up to 2x speed
        if (!newPlay && seconds <= 0) return;
        Database.Execute(c, null, """
            INSERT INTO watch_activity(user_id, media_id, day, seconds, plays, last_at) VALUES(@u,@m,@day,@s,@p,@t)
            ON CONFLICT(user_id, media_id, day) DO UPDATE SET seconds=seconds+excluded.seconds, plays=plays+excluded.plays, last_at=excluded.last_at
            """, ("@u", UserId), ("@m", mediaId), ("@day", time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("@s", seconds),
            ("@p", newPlay ? 1 : 0), ("@t", now.ToString("O", CultureInfo.InvariantCulture)));
    }

    /// <summary>Heartbeats further apart than this (a paused tab, a closed player) end a play.</summary>
    public const double SessionGapSeconds = 180;

    /// <summary>Marks a batch of files watched (position = the end) or, for <c>watched=false</c>, forgets their progress.</summary>
    public int SetWatched(IReadOnlyList<(long Id, double? Duration)> files, bool watched)
    {
        if (files.Count == 0) return 0;
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = db.Open();
        var alreadyWatched = watched && trakt is not null
            ? Database.Query(c, "SELECT media_id FROM media_progress WHERE user_id=@u AND watched=1", r => r.GetInt64(0), ("@u", UserId)).ToHashSet() : [];
        using var tx = c.BeginTransaction();
        foreach (var (id, duration) in files)
        {
            if (!watched) { Database.Execute(c, tx, "DELETE FROM media_progress WHERE user_id=@u AND media_id=@m", ("@u", UserId), ("@m", id)); continue; }
            var d = duration is > 0 ? duration.Value : 1;
            Database.Execute(c, tx, """
                INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(@u,@m,@d,@d,1,@t)
                ON CONFLICT(user_id, media_id) DO UPDATE SET position_seconds=excluded.position_seconds, duration_seconds=excluded.duration_seconds, watched=1, updated_at=excluded.updated_at
                """, ("@u", UserId), ("@m", id), ("@d", d), ("@t", now));
        }
        tx.Commit();
        if (watched) trakt?.QueueWatched(UserId, files.Select(f => f.Id).Where(i => !alreadyWatched.Contains(i)).ToList());
        return files.Count;
    }

    public bool Clear(long mediaId)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM media_progress WHERE user_id=@u AND media_id=@m", ("@u", UserId), ("@m", mediaId)) > 0;
    }
}
