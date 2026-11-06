using System.Globalization;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

/// <summary>Where you stopped watching. Stored on the server so it follows you between browsers and devices.</summary>
public sealed class ProgressService(Database db, TimeProvider time, IConfiguration config)
{
    public const double MinResumeSeconds = 30;

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
            INSERT INTO media_progress(media_id, position_seconds, duration_seconds, watched, updated_at) VALUES(@m,@p,@d,@w,@t)
            ON CONFLICT(media_id) DO UPDATE SET position_seconds=excluded.position_seconds,
                duration_seconds=COALESCE(excluded.duration_seconds, duration_seconds), watched=excluded.watched, updated_at=excluded.updated_at
            """, ("@m", mediaId), ("@p", position), ("@d", duration), ("@w", isWatched ? 1 : 0), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return true;
    }

    public bool Clear(long mediaId)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM media_progress WHERE media_id=@m", ("@m", mediaId)) > 0;
    }
}
