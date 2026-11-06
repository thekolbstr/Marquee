using System.Globalization;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

public sealed record WatchlistKey(string Type, string Key);

/// <summary>"My List": titles you want to watch later. Each person has their own.</summary>
public sealed class WatchlistService(Database db, TimeProvider time, IUserContext? users = null)
{
    private long UserId => users?.UserId ?? 1;
    private static bool ValidType(string t) => t is "movie" or "series";

    public IReadOnlyList<WatchlistKey> Keys()
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT kind, item_key FROM watchlist WHERE user_id=@u ORDER BY added_at DESC", r => new WatchlistKey(r.GetString(0), r.GetString(1)), ("@u", UserId));
    }

    /// <returns>False if the type or key is invalid.</returns>
    public bool Add(string type, string key)
    {
        if (!ValidType(type) || string.IsNullOrWhiteSpace(key) || key.Length > 200) return false;
        using var c = db.Open();
        Database.Execute(c, null, "INSERT OR IGNORE INTO watchlist(user_id, kind, item_key, added_at) VALUES(@u,@k,@i,@t)",
            ("@u", UserId), ("@k", type), ("@i", key), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return true;
    }

    public bool Remove(string type, string key)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM watchlist WHERE user_id=@u AND kind=@k AND item_key=@i", ("@u", UserId), ("@k", type), ("@i", key)) > 0;
    }
}
