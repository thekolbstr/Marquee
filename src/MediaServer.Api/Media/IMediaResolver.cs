using MediaServer.Api.Data;
using MediaServer.Api.Library;

namespace MediaServer.Api.Media;

/// <summary>
/// Maps a database ID to a file on disk. Clients never supply paths (principle 3).
/// </summary>
public interface IMediaResolver
{
    /// <returns>The absolute path, or null if the ID is unknown or the file is soft-deleted.</returns>
    string? ResolvePath(long id);
}

public sealed class DbMediaResolver(Database db, IConfiguration config, LibraryFolders? folders = null) : IMediaResolver
{
    public string? ResolvePath(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM media_files WHERE id=@id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("@id", id);
        if (cmd.ExecuteScalar() is not string rel) return null;

        // Defense in depth: even a corrupted DB row can't escape the folder it belongs to.
        string rootPath, within;
        if (folders is not null) { if (folders.Split(rel) is not var (r, w)) return null; rootPath = r; within = w; }
        else { rootPath = config["Library:Root"] ?? throw new InvalidOperationException("Library:Root is not configured"); within = rel; }
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, within));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
