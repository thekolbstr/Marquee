using System.Globalization;
using System.Text.RegularExpressions;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

public sealed record LibraryRoot(string Prefix, string Path, long? FolderId);
public sealed record LibraryFolderInfo(long? Id, string Path, string Name, bool Enabled, string Source, bool Exists, int Files);
public sealed record BrowseEntry(string Name, string Path);
public sealed record BrowseResult(string Path, string? Parent, IReadOnlyList<BrowseEntry> Entries, bool Truncated);

public sealed class FolderException(string message) : Exception(message);

/// <summary>
/// The folders that make up the library: the one the deployment mounts (<c>Library:Root</c>) plus any added in the web UI.
/// Files in the primary folder keep their plain relative path in the database; files in an added folder are stored as
/// <c>@f&lt;id&gt;/relative/path</c>, so paths stay unique across folders and clients still only ever see media IDs.
/// </summary>
public sealed partial class LibraryFolders(Database db, IConfiguration config, TimeProvider time)
{
    private static readonly string[] Denied = ["/proc", "/sys", "/dev", "/etc", "/run", "/var/run", "/boot", "/root", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/app", "/System", "/private", "/Library"];
    private const int MaxEntries = 500;

    [GeneratedRegex(@"^@f(\d{1,9})/")] private static partial Regex PrefixRx();

    public static string Prefix(long id) => $"@f{id.ToString(CultureInfo.InvariantCulture)}/";

    public string? PrimaryRoot => config["Library:Root"] is { Length: > 0 } r ? System.IO.Path.GetFullPath(r) : null;

    /// <summary>Where finished live-TV recordings are kept. It is part of the library (stored paths start with <c>@rec/</c>).</summary>
    public const string RecordingsPrefix = "@rec/";
    public string RecordingsRoot => System.IO.Path.GetFullPath(config["Recordings:Directory"] ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db"))!, "recordings"));

    private IReadOnlyList<(long Id, string Path, string Name, bool Enabled)> Rows()
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT id, path, name, enabled FROM library_folders ORDER BY id", r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1));
    }

    /// <summary>Everything the scanner should walk.</summary>
    public IReadOnlyList<LibraryRoot> EnabledRoots()
    {
        var roots = new List<LibraryRoot>();
        if (PrimaryRoot is { } p) roots.Add(new LibraryRoot("", p, null));
        roots.AddRange(Rows().Where(r => r.Enabled).Select(r => new LibraryRoot(Prefix(r.Id), r.Path, r.Id)));
        roots.Add(new LibraryRoot(RecordingsPrefix, RecordingsRoot, -1));          // optional: skipped while the folder doesn't exist yet
        return roots;
    }

    public bool HasAny => PrimaryRoot is not null || Rows().Any(r => r.Enabled);

    /// <summary>Which folder a stored path lives in, and the path inside it. Null if the folder has been removed.</summary>
    public (string Root, string Relative)? Split(string stored)
    {
        if (stored.StartsWith(RecordingsPrefix, StringComparison.Ordinal)) return (RecordingsRoot, stored[RecordingsPrefix.Length..]);
        var m = PrefixRx().Match(stored);
        if (!m.Success) return PrimaryRoot is { } p ? (p, stored) : null;
        var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var row = Rows().FirstOrDefault(r => r.Id == id);
        return row.Path is null ? null : (row.Path, stored[m.Length..]);
    }

    /// <summary>The path as the filename parser should see it (without the folder prefix).</summary>
    public static string StripPrefix(string stored) => stored.StartsWith(RecordingsPrefix, StringComparison.Ordinal) ? stored[RecordingsPrefix.Length..] : PrefixRx().Replace(stored, "", 1);

    public IReadOnlyList<LibraryFolderInfo> List()
    {
        var list = new List<LibraryFolderInfo>();
        using var c = db.Open();
        int Count(string where) => Convert.ToInt32(Database.Scalar(c, $"SELECT COUNT(*) FROM media_files WHERE deleted_at IS NULL AND {where}"), CultureInfo.InvariantCulture);
        if (PrimaryRoot is { } p) list.Add(new LibraryFolderInfo(null, p, "Server library", true, "deployment", Directory.Exists(p), Count("path NOT LIKE '@f%' AND path NOT LIKE '@rec/%'")));
        foreach (var r in Rows())
            list.Add(new LibraryFolderInfo(r.Id, r.Path, r.Name, r.Enabled, "ui", Directory.Exists(r.Path), Count($"path LIKE '@f{r.Id.ToString(CultureInfo.InvariantCulture)}/%'")));
        return list;
    }

    // ------------------------------------------------------------------ changes (all validated; the caller must be signed in)

    public LibraryFolderInfo Add(string? rawPath, string? name)
    {
        var path = Check(rawPath);
        var label = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) : name.Trim();
        if (label.Length == 0) label = path;
        if (label.Length > 60 || label.Any(char.IsControl)) throw new FolderException("That name is too long.");
        using var c = db.Open();
        Database.Execute(c, null, "INSERT INTO library_folders(path,name,enabled,added_at) VALUES(@p,@n,1,@t)", ("@p", path), ("@n", label), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        var id = Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        return new LibraryFolderInfo(id, path, label, true, "ui", true, 0);
    }

    public bool Update(long id, bool? enabled, string? name)
    {
        if (name is not null && (name.Trim().Length is 0 or > 60 || name.Any(char.IsControl))) throw new FolderException("Names are 1–60 characters.");
        using var c = db.Open();
        var n = 0;
        if (enabled is { } e) n += Database.Execute(c, null, "UPDATE library_folders SET enabled=@e WHERE id=@i", ("@e", e ? 1 : 0), ("@i", id));
        if (name is not null) n += Database.Execute(c, null, "UPDATE library_folders SET name=@n WHERE id=@i", ("@n", name.Trim()), ("@i", id));
        return n > 0 || Database.Scalar(c, $"SELECT 1 FROM library_folders WHERE id={id.ToString(CultureInfo.InvariantCulture)}") is not null;
    }

    public bool Remove(long id)
    {
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM library_folders WHERE id=@i", ("@i", id)) > 0;
    }

    /// <summary>Normalises a path and refuses anything that isn't a sensible library folder.</summary>
    internal string Check(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1000 || raw.Any(char.IsControl)) throw new FolderException("Enter a full folder path, such as /media/movies.");
        if (!System.IO.Path.IsPathRooted(raw)) throw new FolderException("The path must be absolute (start with /).");
        var path = Normalize(raw);
        if (!Directory.Exists(path)) throw new FolderException("That folder doesn't exist on the server (or isn't mounted into it).");
        if (IsDenied(path)) throw new FolderException("That location can't be used as a library.");
        if (!Allowed(path)) throw new FolderException("That folder is outside the locations this server is allowed to use.");
        try { using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); e.MoveNext(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { throw new FolderException("The server isn't allowed to read that folder."); }
        foreach (var own in OwnDirectories())
            if (Overlaps(path, own)) throw new FolderException("That folder holds this server's own data.");
        var existing = new List<string>();
        if (PrimaryRoot is { } p) existing.Add(p);
        existing.AddRange(Rows().Select(r => r.Path));
        foreach (var other in existing)
            if (Overlaps(path, other)) throw new FolderException(string.Equals(path, other, StringComparison.Ordinal) ? "That folder is already in the library." : $"That folder overlaps one that's already in the library ({other}), which would list every file twice.");
        return path;
    }

    private static string Normalize(string p)
    {
        var full = System.IO.Path.GetFullPath(p);
        return full.Length > 1 ? full.TrimEnd('/', '\\') : full;
    }

    private IEnumerable<string> OwnDirectories()
    {
        yield return RecordingsRoot;
        foreach (var key in new[] { "Database:Path", "Images:CacheDir", "Hls:TempDir" })
        {
            var v = config[key];
            if (string.IsNullOrWhiteSpace(v)) continue;
            var full = System.IO.Path.GetFullPath(v);
            yield return key == "Database:Path" ? System.IO.Path.GetDirectoryName(full) ?? full : full;
        }
    }

    private static bool Overlaps(string a, string b) => Inside(a, b) || Inside(b, a);
    private static bool Inside(string child, string parent) =>
        string.Equals(child, parent, StringComparison.Ordinal) || child.StartsWith(parent.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsDenied(string path) => path.Length <= 1 || Denied.Any(d => Inside(path, d));

    private bool Allowed(string path)
    {
        var allowed = (config["Library:AllowedRoots"] ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0 || allowed.Any(a => Inside(path, Normalize(a)));
    }

    // ------------------------------------------------------------------ browsing (folder names only, never files)

    public BrowseResult Browse(string? raw)
    {
        var path = string.IsNullOrWhiteSpace(raw) ? (PrimaryRoot is { } p && Directory.Exists(p) ? p : "/") : raw;
        if (path.Length > 1000 || path.Any(char.IsControl) || !System.IO.Path.IsPathRooted(path)) throw new FolderException("Not a valid path.");
        path = Normalize(path);
        if (!Directory.Exists(path)) throw new FolderException("That folder doesn't exist.");
        if (path.Length > 1 && IsDenied(path)) throw new FolderException("That location can't be browsed.");
        var allowedRoots = (config["Library:AllowedRoots"] ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedRoots.Length > 0 && !Allowed(path)) path = Normalize(allowedRoots[0]);

        var entries = new List<BrowseEntry>();
        var truncated = false;
        try
        {
            foreach (var d in new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                         .Where(d => !d.Name.StartsWith('.') && d.Name != "@eaDir").OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var full = Normalize(d.FullName);
                if (IsDenied(full) || !Allowed(full)) continue;
                if (entries.Count >= MaxEntries) { truncated = true; break; }
                entries.Add(new BrowseEntry(d.Name, full));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { throw new FolderException("The server isn't allowed to read that folder."); }

        var parent = System.IO.Path.GetDirectoryName(path);
        if (parent is not null && (IsDenied(parent) || !Allowed(parent))) parent = null;
        return new BrowseResult(path, parent, entries, truncated);
    }
}
