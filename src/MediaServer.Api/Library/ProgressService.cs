using System.Globalization;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

/// <summary>Where you stopped watching. Stored on the server so it follows you between browsers and devices.</summary>
public sealed class ProgressService(Database db, TimeProvider time, IConfiguration config, MediaServer.Api.Auth.IUserContext? users = null)
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
        Database.Execute(c, null, """
            INSERT INTO media_progress(user_id, media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(@u,@m,@p,@d,@w,@t)
            ON CONFLICT(user_id, media_id) DO UPDATE SET position_seconds=excluded.position_seconds,
                duration_seconds=COALESCE(excluded.duration_seconds, duration_seconds), watched=excluded.watched, updated_at=excluded.updated_at
            """, ("@u", UserId), ("@m", mediaId), ("@p", position), ("@d", duration), ("@w", isWatched ? 1 : 0), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return true;
    }

    /// <summary>Marks a batch of files watched (position = the end) or, for <c>watched=false</c>, forgets their progress.</summary>
    public int SetWatched(IReadOnlyList<(long Id, double? Duration)> files, bool watched)
    {
        if (files.Count == 0) return 0;
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = db.Open();
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
        return files.Count;
    }

    public bool Clear(long mediaId)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM media_progress WHERE user_id=@u AND media_id=@m", ("@u", UserId), ("@m", mediaId)) > 0;
    }
}
