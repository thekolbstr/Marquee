using System.Globalization;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

public sealed record CollectionInfo(long Id, string Name, int Count);

/// <summary>Collections: named groups of movies and shows, such as "Christmas films" or "Studio Ghibli". Each person has their own.</summary>
public sealed class CollectionService(Database db, TimeProvider time, IUserContext? users = null)
{
    public const int MaxCollections = 100, MaxNameLength = 60;
    private long UserId => users?.UserId ?? 1;
    private static bool ValidType(string t) => t is "movie" or "series";

    /// <summary>Trims, collapses whitespace and rejects empty or over-long names.</summary>
    public static string? CleanName(string? name)
    {
        var n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return n.Length is > 0 and <= MaxNameLength && !n.Any(char.IsControl) ? n : null;
    }

    public IReadOnlyList<CollectionInfo> List()
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT c.id, c.name, (SELECT COUNT(*) FROM collection_items i WHERE i.collection_id = c.id)
            FROM collections c WHERE c.user_id=@u ORDER BY c.name COLLATE NOCASE
            """, r => new CollectionInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2)), ("@u", UserId));
    }

    public CollectionInfo? Get(long id) => List().FirstOrDefault(c => c.Id == id);

    /// <returns>The new collection, or null if the name is invalid, taken, or the limit is reached.</returns>
    public CollectionInfo? Create(string? name)
    {
        var n = CleanName(name);
        if (n is null) return null;
        using var c = db.Open();
        if (Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM collections WHERE user_id=" + UserId.ToString(CultureInfo.InvariantCulture))) >= MaxCollections) return null;
        if (NameTaken(c, n, 0)) return null;
        Database.Execute(c, null, "INSERT INTO collections(user_id, name, created_at) VALUES(@u,@n,@t)", ("@u", UserId), ("@n", n), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return new CollectionInfo(Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()")), n, 0);
    }

    public bool Rename(long id, string? name)
    {
        var n = CleanName(name);
        if (n is null) return false;
        using var c = db.Open();
        if (NameTaken(c, n, id)) return false;
        return Database.Execute(c, null, "UPDATE collections SET name=@n WHERE id=@i AND user_id=@u", ("@n", n), ("@i", id), ("@u", UserId)) > 0;
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM collections WHERE id=@i AND user_id=@u", ("@i", id), ("@u", UserId)) > 0;
    }

    public IReadOnlyList<WatchlistKey> Keys(long id)
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT i.kind, i.item_key FROM collection_items i JOIN collections c ON c.id = i.collection_id
            WHERE c.id=@i AND c.user_id=@u ORDER BY i.added_at DESC
            """, r => new WatchlistKey(r.GetString(0), r.GetString(1)), ("@i", id), ("@u", UserId));
    }

    /// <summary>The ids of this person's collections that already hold the title.</summary>
    public IReadOnlyList<long> Containing(string type, string key)
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT c.id FROM collections c JOIN collection_items i ON i.collection_id = c.id
            WHERE c.user_id=@u AND i.kind=@k AND i.item_key=@s
            """, r => r.GetInt64(0), ("@u", UserId), ("@k", type), ("@s", key));
    }

    public bool Add(long id, string type, string key)
    {
        if (!ValidType(type) || string.IsNullOrWhiteSpace(key) || key.Length > 200) return false;
        using var c = db.Open();
        if (!Owns(c, id)) return false;
        Database.Execute(c, null, "INSERT OR IGNORE INTO collection_items(collection_id, kind, item_key, added_at) VALUES(@c,@k,@s,@t)",
            ("@c", id), ("@k", type), ("@s", key), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return true;
    }

    public bool Remove(long id, string type, string key)
    {
        using var c = db.Open();
        if (!Owns(c, id)) return false;
        return Database.Execute(c, null, "DELETE FROM collection_items WHERE collection_id=@c AND kind=@k AND item_key=@s", ("@c", id), ("@k", type), ("@s", key)) > 0;
    }

    private bool Owns(Microsoft.Data.Sqlite.SqliteConnection c, long id)
        => Database.Query(c, "SELECT 1 FROM collections WHERE id=@i AND user_id=@u", r => 1, ("@i", id), ("@u", UserId)).Count > 0;

    private bool NameTaken(Microsoft.Data.Sqlite.SqliteConnection c, string name, long exceptId)
        => Database.Query(c, "SELECT 1 FROM collections WHERE user_id=@u AND name=@n COLLATE NOCASE AND id<>@i", r => 1, ("@u", UserId), ("@n", name), ("@i", exceptId)).Count > 0;
}
