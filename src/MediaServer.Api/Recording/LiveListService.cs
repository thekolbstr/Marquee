using System.Globalization;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;

namespace MediaServer.Api.Recording;

public sealed record LiveListInfo(long Id, string Name, int Channels, IReadOnlyList<long> ChannelIds);

/// <summary>Your own named groups of channels ("Sports", "Kids"), separate from the provider's groups. Each person has their own.</summary>
public sealed class LiveListService(Database db, TimeProvider time, IUserContext? users = null)
{
    private long UserId => users?.UserId ?? 1;

    public IReadOnlyList<LiveListInfo> Lists()
    {
        using var c = db.Open();
        var lists = Database.Query(c, "SELECT id, name FROM live_lists WHERE user_id=@u ORDER BY name COLLATE NOCASE", r => (r.GetInt64(0), r.GetString(1)), ("@u", UserId));
        var members = Database.Query(c, "SELECT m.list_id, m.channel_id FROM live_list_channels m JOIN live_lists l ON l.id=m.list_id WHERE l.user_id=@u", r => (r.GetInt64(0), r.GetInt64(1)), ("@u", UserId))
            .GroupBy(m => m.Item1).ToDictionary(g => g.Key, g => g.Select(m => m.Item2).ToList());
        return lists.Select(l => new LiveListInfo(l.Item1, l.Item2, members.GetValueOrDefault(l.Item1)?.Count ?? 0, members.GetValueOrDefault(l.Item1) ?? [])).ToList();
    }

    public bool Owns(long listId)
    {
        using var c = db.Open();
        return Database.Scalar(c, $"SELECT 1 FROM live_lists WHERE id={listId.ToString(CultureInfo.InvariantCulture)} AND user_id={UserId.ToString(CultureInfo.InvariantCulture)}") is not null;
    }

    public LiveListInfo Create(string? name)
    {
        var n = Clean(name);
        using var c = db.Open();
        if (Convert.ToInt32(Database.Scalar(c, $"SELECT COUNT(*) FROM live_lists WHERE user_id={UserId.ToString(CultureInfo.InvariantCulture)}"), CultureInfo.InvariantCulture) >= 50) throw new InvalidOperationException("That's plenty of lists. Delete one first.");
        try { Database.Execute(c, null, "INSERT INTO live_lists(user_id, name, created_at) VALUES(@u,@n,@t)", ("@u", UserId), ("@n", n), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("You already have a list with that name."); }
        return Lists().First(l => l.Name == n);
    }

    public bool Rename(long id, string? name)
    {
        var n = Clean(name);
        using var c = db.Open();
        try { return Database.Execute(c, null, "UPDATE live_lists SET name=@n WHERE id=@i AND user_id=@u", ("@n", n), ("@i", id), ("@u", UserId)) > 0; }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("You already have a list with that name."); }
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        Database.Execute(c, null, "DELETE FROM live_list_channels WHERE list_id IN (SELECT id FROM live_lists WHERE id=@i AND user_id=@u)", ("@i", id), ("@u", UserId));
        return Database.Execute(c, null, "DELETE FROM live_lists WHERE id=@i AND user_id=@u", ("@i", id), ("@u", UserId)) > 0;
    }

    /// <returns>False if the list isn't yours or the channel doesn't exist.</returns>
    public bool SetMember(long listId, long channelId, bool member)
    {
        if (!Owns(listId)) return false;
        using var c = db.Open();
        if (Database.Scalar(c, $"SELECT 1 FROM live_channels WHERE id={channelId.ToString(CultureInfo.InvariantCulture)}") is null) return false;
        if (member) Database.Execute(c, null, "INSERT OR IGNORE INTO live_list_channels(list_id, channel_id) VALUES(@l,@c)", ("@l", listId), ("@c", channelId));
        else Database.Execute(c, null, "DELETE FROM live_list_channels WHERE list_id=@l AND channel_id=@c", ("@l", listId), ("@c", channelId));
        return true;
    }

    private static string Clean(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is 0 or > 40 || n.Any(char.IsControl)) throw new InvalidOperationException("List names are 1–40 characters.");
        return n;
    }
}
