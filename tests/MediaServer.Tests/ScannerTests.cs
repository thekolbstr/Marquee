using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("scan").FullName;
    private readonly string _root;
    private readonly Database _db;
    private readonly CountingProbe _probe = new();
    private readonly LibraryScanner _scanner;
    private readonly DbMediaResolver _resolver;
    private int _seed;

    public ScannerTests()
    {
        _root = Path.Combine(_tmp, "lib");
        Directory.CreateDirectory(_root);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_tmp, "db", "t.db"),
            ["Library:Root"] = _root,
        }).Build();
        _db = new Database(cfg);
        _db.Migrate();
        _scanner = new LibraryScanner(_db, _probe, cfg, NullLogger<LibraryScanner>.Instance);
        _resolver = new DbMediaResolver(_db, cfg);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmp, true);
    }

    private string Make(string rel, int size = 100, DateTime? mtime = null)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[size];
        new Random(_seed++).NextBytes(data);
        File.WriteAllBytes(p, data);
        File.SetLastWriteTimeUtc(p, mtime ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_seed));
        return p;
    }

    private long IdOf(string rel)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM media_files WHERE path=@p";
        cmd.Parameters.AddWithValue("@p", rel);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private async Task<ScanResult> Scan() => (await _scanner.ScanAsync(default))!;

    [Fact]
    public async Task InitialScan_AddsMediaOnly_AndSkipsHiddenAndNonMedia()
    {
        Make("Movie (2020).mkv"); Make("Shows/S/ep.mp4");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        Make(".hidden/secret.mkv"); Make("@eaDir/thumb.mkv"); Make(".dot.mkv");
        var r = await Scan();
        Assert.Equal(2, r.Added);
        Assert.Equal(2, _probe.Calls);
    }

    [Fact]
    public async Task Rescan_Unchanged_DoesNotProbe()
    {
        Make("a.mkv"); Make("b.mkv");
        await Scan();
        _probe.Calls = 0;
        var r = await Scan();
        Assert.Equal(new ScanResult(0, 0, 0, 0, 0, 2, 0), r);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task ChangedFile_IsReprobed_KeepsId()
    {
        var p = Make("a.mkv", 100);
        await Scan();
        var id = IdOf("a.mkv");
        File.WriteAllBytes(p, new byte[250]);
        var r = await Scan();
        Assert.Equal(1, r.Updated);
        Assert.Equal(id, IdOf("a.mkv"));
        Assert.Equal(2, _probe.Calls);
    }

    [Fact]
    public async Task MovedFile_KeepsId_NoReprobe()
    {
        Make("old/Movie.mkv", 500);
        await Scan();
        var id = IdOf("old/Movie.mkv");
        Directory.CreateDirectory(Path.Combine(_root, "new"));
        File.Move(Path.Combine(_root, "old/Movie.mkv"), Path.Combine(_root, "new/Movie.mkv"));
        _probe.Calls = 0;
        var r = await Scan();
        Assert.Equal(1, r.Moved);
        Assert.Equal(0, r.Added + r.Removed);
        Assert.Equal(0, _probe.Calls);
        Assert.Equal(id, IdOf("new/Movie.mkv"));
        Assert.EndsWith("new/Movie.mkv", _resolver.ResolvePath(id));
    }

    [Fact]
    public async Task RenamedFile_IsAMove()
    {
        Make("a.mkv", 321);
        await Scan();
        var id = IdOf("a.mkv");
        File.Move(Path.Combine(_root, "a.mkv"), Path.Combine(_root, "b.mkv"));
        var r = await Scan();
        Assert.Equal(1, r.Moved);
        Assert.Equal(id, IdOf("b.mkv"));
    }

    [Fact]
    public async Task TwoIdenticalStampFiles_PreferSameFileNameWhenMoving()
    {
        var t = new DateTime(2023, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        Make("x/one.mkv", 100, t); Make("x/two.mkv", 100, t);
        await Scan();
        var one = IdOf("x/one.mkv"); var two = IdOf("x/two.mkv");
        Directory.CreateDirectory(Path.Combine(_root, "y"));
        File.Move(Path.Combine(_root, "x/two.mkv"), Path.Combine(_root, "y/two.mkv"));
        File.Move(Path.Combine(_root, "x/one.mkv"), Path.Combine(_root, "y/one.mkv"));
        await Scan();
        Assert.Equal(one, IdOf("y/one.mkv"));
        Assert.Equal(two, IdOf("y/two.mkv"));
    }

    [Fact]
    public async Task DeletedFile_IsSoftDeleted_AndUnresolvable_ThenRestored()
    {
        var p = Make("a.mkv");
        await Scan();
        var id = IdOf("a.mkv");
        Assert.NotNull(_resolver.ResolvePath(id));

        var saved = File.ReadAllBytes(p); var mtime = File.GetLastWriteTimeUtc(p);
        File.Delete(p);
        var r = await Scan();
        Assert.Equal(1, r.Removed);
        Assert.Null(_resolver.ResolvePath(id));           // gone from streaming...
        Assert.Equal(id, IdOf("a.mkv"));                  // ...but the row survives (soft delete)

        File.WriteAllBytes(p, saved); File.SetLastWriteTimeUtc(p, mtime);
        _probe.Calls = 0;
        r = await Scan();
        Assert.Equal(1, r.Restored);
        Assert.Equal(0, _probe.Calls);
        Assert.NotNull(_resolver.ResolvePath(id));
    }

    [Fact]
    public async Task ParsedFieldsArePersisted_AndReparsedOnMoveOrParserVersionChange()
    {
        Make("Movies/The.Matrix.1999.1080p.mkv");
        await Scan();
        string Q(string col, string rel) { using var c = _db.Open(); return Convert.ToString(Database.Scalar(c, $"SELECT {col} FROM media_files WHERE path='{rel}'"))!; }
        Assert.Equal(("Movie", "The Matrix", "1999"), (Q("kind", "Movies/The.Matrix.1999.1080p.mkv"), Q("title", "Movies/The.Matrix.1999.1080p.mkv"), Q("year", "Movies/The.Matrix.1999.1080p.mkv")));

        File.Move(Path.Combine(_root, "Movies/The.Matrix.1999.1080p.mkv"), Path.Combine(_root, "Movies/Matrix.Reloaded.2003.mkv"));
        await Scan();
        Assert.Equal("Matrix Reloaded", Q("title", "Movies/Matrix.Reloaded.2003.mkv")); // move => reparsed

        using (var c = _db.Open()) Database.Exec(c, "UPDATE media_files SET title='stale', parser_version=-1");
        await Scan();
        Assert.Equal("Matrix Reloaded", Q("title", "Movies/Matrix.Reloaded.2003.mkv")); // version mismatch => reparsed
    }

    [Fact]
    public async Task AlreadyDeleted_IsNotRemovedAgain()
    {
        var p = Make("a.mkv"); await Scan(); File.Delete(p);
        await Scan();
        var r = await Scan();
        Assert.Equal(0, r.Removed);
    }

    [Fact]
    public async Task ProbeFailure_StillIndexesFile_AndIsNotRetriedUntilChanged()
    {
        Make("bad.mkv"); _probe.Fail = true;
        var r = await Scan();
        Assert.Equal(1, r.Added); Assert.Equal(1, r.ProbeErrors);
        _probe.Calls = 0;
        await Scan();
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task ConcurrentScan_ReturnsNullForSecond()
    {
        Make("a.mkv"); _probe.Delay = TimeSpan.FromMilliseconds(400);
        var first = _scanner.ScanAsync(default);
        await Task.Delay(100);
        Assert.Null(await _scanner.ScanAsync(default));
        Assert.NotNull(await first);
    }

    [Fact]
    public void Resolver_RefusesRowsEscapingTheRoot()
    {
        using (var c = _db.Open())
            Database.Exec(c, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned) VALUES('../../etc/passwd',1,1,'x','x')");
        Assert.Null(_resolver.ResolvePath(1));
    }

    [Fact]
    public void Database_UsesWalAndForeignKeys()
    {
        using var c = _db.Open();
        Assert.Equal("wal", Database.Scalar(c, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Database.Scalar(c, "PRAGMA foreign_keys;"));
    }

    private sealed class CountingProbe : IMediaProbe
    {
        public int Calls; public bool Fail; public TimeSpan Delay;
        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Fail) throw new ProbeFailedException("boom");
            return new MediaInfo("matroska", 60, 1000, 100, [], [], []);
        }
    }
}
