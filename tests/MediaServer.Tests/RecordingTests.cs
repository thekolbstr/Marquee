using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Recording;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MediaServer.Tests;

public sealed class RecordingServiceTests : IDisposable
{
    private readonly LiveTvService _live;
    private readonly TestDb _t;
    private readonly FakeTimeProvider _time;
    private readonly FixedUser _who = new();
    private readonly string _rec, _script, _argsLog;
    private readonly Dictionary<string, string?> _extra = new();
    private RecordingService _svc;

    public RecordingServiceTests()
    {
        var (live, _, t, time) = LiveFixtures.Make();
        _live = live; _t = t; _time = time;
        _rec = Path.Combine(_t.Dir, "recordings"); _argsLog = Path.Combine(_t.Dir, "args.log"); _script = Path.Combine(_t.Dir, "ffmpeg.sh");
        _live.RefreshAsync(default).GetAwaiter().GetResult();
        Script("good");
        _svc = Make();
    }
    public void Dispose() { Kill(); _t.Dispose(); }

    private RecordingService Make(ScanCoordinator? scans = null)
    {
        var d = new Dictionary<string, string?> { ["Tools:FfmpegPath"] = _script, ["Recordings:Directory"] = _rec, ["Recordings:MinFreeGb"] = "0", ["Database:Path"] = Path.Combine(_t.Dir, "t.db") };
        foreach (var (k, v) in _extra) d[k] = v;
        return new RecordingService(_t.Db, _live, new ConfigurationBuilder().AddInMemoryCollection(d).Build(), _time, NullLogger<RecordingService>.Instance, _who, scans);
    }

    /// <summary>A stand-in for ffmpeg: logs its arguments and writes to the last one (the output file).</summary>
    private void Script(string mode)
    {
        var body = mode switch
        {
            "good" => "head -c 300000 /dev/zero > \"$last\"\nsleep 1\nexit 0\n",
            "linger" => "head -c 300000 /dev/zero > \"$last\"\nexec sleep 120\n",
            "tiny" => "printf x > \"$last\"\nsleep 1\nexit 0\n",
            "fail" => "echo \"Connection refused: $url\" 1>&2\nexit 1\n",
            "partial-error" => "head -c 300000 /dev/zero > \"$last\"\necho 'Error in the stream' 1>&2\nexit 1\n",
            _ => throw new ArgumentException(mode),
        };
        File.WriteAllText(_script, $"#!/bin/sh\nfor a; do last=\"$a\"; case \"$prev\" in -i) url=\"$a\";; esac; prev=\"$a\"; done\necho \"$@\" >> '{_argsLog}'\n{body}");
        File.SetUnixFileMode(_script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();
    private async Task Until(Func<bool> cond, int ms = 6000) { for (var i = 0; i < ms / 100 && !cond(); i++) { await Task.Delay(100); await _svc.TickAsync(default); } }
    private void Kill() { try { _svc.StopAllAsync().GetAwaiter().GetResult(); } catch { /* best effort */ } }
    private string Status(long id) => _svc.Get(id)!.Status;
    private Task Captured(int files = 1) => Until(() => Directory.Exists(_rec) && Directory.GetFiles(_rec, "*.part").Count(f => new FileInfo(f).Length >= 100_000) >= files, 4000);

    // ---- scheduling & validation
    [Fact] public void RecordNow_UsesTheCurrentProgramme()
    {
        var r = _svc.RecordNow(2);                                                                         // "Movie Max": Movie A is on until 15:30
        Assert.Equal(("Movie A", "scheduled", "Movie Max"), (r.Title, r.Status, r.ChannelName));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 15, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(), r.StopTs);
        Assert.Equal("First film", r.Description);
    }

    [Fact] public void RecordNow_WithoutAGuide_RecordsAnHour()
    {
        var r = _svc.RecordNow(1);                                                                         // "CBS 12" has no programmes
        Assert.Equal(("CBS 12", Now + 3600), (r.Title, r.StopTs));
    }

    [Fact] public void ARecordingCanBeScheduledFromTheGuide()
    {
        var b = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var r = _svc.ScheduleProgramme(2, b);
        Assert.Equal(("Movie C", b), (r.Title, r.StartTs));
        Assert.Equal(409, Assert.Throws<RecordingException>(() => _svc.ScheduleProgramme(2, b)).Status);   // already scheduled
        Assert.Equal(404, Assert.Throws<RecordingException>(() => _svc.ScheduleProgramme(2, b + 7)).Status);  // not a real programme start
        Assert.Equal(404, Assert.Throws<RecordingException>(() => _svc.ScheduleProgramme(999, b)).Status);
    }

    [Theory] [InlineData(0, -10)] [InlineData(60, 60)] [InlineData(-90000, -86000)] [InlineData(100, 40000)] [InlineData(6000000, 6003600)]
    public void ImpossibleTimeSlotsAreRefused(long startOffset, long stopOffset)
        => Assert.Throws<RecordingException>(() => _svc.ScheduleManual(1, Now + startOffset, Now + stopOffset, "x"));

    [Fact] public void ManualSlots_AreAccepted_AndTitlesChecked()
    {
        Assert.Equal("My show", _svc.ScheduleManual(1, Now + 600, Now + 4200, "  My show ").Title);
        Assert.Equal("CBS 12", _svc.ScheduleManual(1, Now + 7200, Now + 9000, null).Title);
        Assert.Throws<RecordingException>(() => _svc.ScheduleManual(1, Now + 600, Now + 700, new string('x', 200)));
        Assert.Throws<RecordingException>(() => _svc.ScheduleManual(1, Now + 600, Now + 700, "bad\ntitle"));
    }

    // ---- running
    [Fact] public async Task ADueRecording_IsCopiedByFfmpeg_ThenKeptAsAFile()
    {
        var r = _svc.ScheduleManual(1, Now + 30, Now + 600, "News at Six");                                // starts in 30s: inside the one-minute lead-in
        await _svc.TickAsync(default);
        Assert.True(Status(r.Id) == "recording", $"{Status(r.Id)}: {_svc.Get(r.Id)!.Error}"); Assert.Equal(1, _svc.ActiveCount);
        await Until(() => File.Exists(_argsLog) && new FileInfo(_argsLog).Length > 0, 3000);                 // the script has started and logged its arguments
        var args = File.ReadAllText(_argsLog);
        Assert.Contains("-c copy", args); Assert.Contains("-protocol_whitelist http,https,tcp,tls,crypto", args); Assert.Contains("-i http://src.test/proxy/ts/stream/aaa-111", args);
        Assert.Matches(@"-t \d+", args);
        await Until(() => Status(r.Id) != "recording");
        Assert.Equal("done", Status(r.Id));
        var file = Directory.GetFiles(_rec).Single();
        Assert.Matches(@"News at Six \[2026-11-06 1500\]\.ts$", file);
        Assert.Empty(Directory.GetFiles(_rec, "*.part")); Assert.Equal(0, _svc.ActiveCount);
    }

    [Fact] public async Task ARecordingWhoseTimeHasNotComeYet_IsNotStarted()
    {
        _svc.ScheduleManual(1, Now + 3600, Now + 7200, "Later");
        await _svc.TickAsync(default);
        Assert.Equal(0, _svc.ActiveCount); Assert.False(File.Exists(_argsLog));
    }

    [Fact] public async Task OnlyAsManyRecordAtOnceAsAreAllowed_TheRestWait()
    {
        _extra["Recordings:MaxConcurrent"] = "1"; _svc = Make(); Script("linger");
        var a = _svc.ScheduleManual(1, Now, Now + 3000, "A"); var b = _svc.ScheduleManual(2, Now, Now + 3000, "B");
        await _svc.TickAsync(default);
        Assert.Equal(1, _svc.ActiveCount); Assert.Equal(["recording", "scheduled"], new[] { Status(a.Id), Status(b.Id) });
        await _svc.StopAsync(a.Id, false);                                                                  // free the slot
        await Until(() => Status(b.Id) == "recording");
        Assert.Equal("recording", Status(b.Id));
    }

    [Fact] public async Task ARecordingMissedWhileTheServerWasOff_IsMarkedFailed()
    {
        var r = _svc.ScheduleManual(1, Now + 100, Now + 400, "Missed");
        _time.Advance(TimeSpan.FromMinutes(20));
        await _svc.TickAsync(default);
        var after = _svc.Get(r.Id)!;
        Assert.Equal("failed", after.Status); Assert.Contains("wasn't recording", after.Error);
    }

    [Fact] public async Task ABrokenChannel_FailsClearly_WithoutLeakingTheStreamUrl()
    {
        Script("fail");
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Broken");
        await _svc.TickAsync(default); await Until(() => Status(r.Id) != "recording");
        var after = _svc.Get(r.Id)!;
        Assert.Equal("failed", after.Status);
        Assert.DoesNotContain("src.test", after.Error); Assert.Contains("<stream>", after.Error);
    }

    [Fact] public async Task AnAlmostEmptyFile_IsAFailure_AndNothingIsLeftBehind()
    {
        Script("tiny");
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Empty");
        await _svc.TickAsync(default); await Until(() => Status(r.Id) != "recording");
        Assert.Equal("failed", Status(r.Id)); Assert.Empty(Directory.GetFiles(_rec));
    }

    [Fact] public async Task ARecordingThatEndsEarlyWithAnError_KeepsWhatItCaptured()
    {
        Script("partial-error");
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Half");
        await _svc.TickAsync(default); await Until(() => Status(r.Id) != "recording");
        var after = _svc.Get(r.Id)!;
        Assert.Equal("done", after.Status); Assert.Contains("ended early", after.Error); Assert.Single(Directory.GetFiles(_rec, "*.ts"));
    }

    [Fact] public async Task Stopping_KeepsThePartialRecording()
    {
        Script("linger");
        var r = _svc.ScheduleManual(1, Now, Now + 3000, "Live");
        await _svc.TickAsync(default); await Captured();
        await _svc.StopAsync(r.Id, false);
        Assert.Equal("done", Status(r.Id)); Assert.Single(Directory.GetFiles(_rec, "*.ts")); Assert.Equal(0, _svc.ActiveCount);
    }

    [Fact] public async Task CancellingAScheduledRecording_JustCancelsIt()
    {
        var r = _svc.ScheduleManual(1, Now + 3600, Now + 7200, "Later");
        await _svc.StopAsync(r.Id, false);
        Assert.Equal("cancelled", Status(r.Id));
    }

    [Fact] public async Task Deleting_RemovesTheFileAndTheEntry()
    {
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Gone");
        await _svc.TickAsync(default); await Until(() => Status(r.Id) == "done");
        Assert.Single(Directory.GetFiles(_rec, "*.ts"));
        await _svc.DeleteAsync(r.Id, false);
        Assert.Empty(Directory.GetFiles(_rec)); Assert.Null(_svc.Get(r.Id));
    }

    [Fact] public async Task DeletingWhileRecording_StopsTheProcessFirst()
    {
        Script("linger");
        var r = _svc.ScheduleManual(1, Now, Now + 3000, "Live");
        await _svc.TickAsync(default); Assert.Equal(1, _svc.ActiveCount);
        await _svc.DeleteAsync(r.Id, false);
        Assert.Equal(0, _svc.ActiveCount); Assert.Empty(Directory.GetFiles(_rec));
    }

    [Fact] public async Task OnlyTheOwner_OrAnAdmin_CanStopOrDelete()
    {
        _who.UserId = 1; var r = _svc.ScheduleManual(1, Now + 3600, Now + 7200, "Mine");
        _who.UserId = 2;
        Assert.Equal(403, (await Assert.ThrowsAsync<RecordingException>(() => _svc.StopAsync(r.Id, false))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<RecordingException>(() => _svc.DeleteAsync(r.Id, false))).Status);
        Assert.Empty(_svc.List());                                                                          // and it isn't in their list
        await _svc.DeleteAsync(r.Id, isAdmin: true);
        Assert.Null(_svc.Get(r.Id));
        Assert.Equal(404, (await Assert.ThrowsAsync<RecordingException>(() => _svc.DeleteAsync(12345, true))).Status);
    }

    [Fact] public async Task NotEnoughDiskSpace_FailsInsteadOfFillingTheDisk()
    {
        _extra["Recordings:MinFreeGb"] = "99999999"; _svc = Make();
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Big");
        await _svc.TickAsync(default);
        Assert.Equal("failed", Status(r.Id)); Assert.Contains("free disk space", _svc.Get(r.Id)!.Error);
    }

    [Fact] public async Task ARunawayRecorder_IsKilledOnceItsWindowHasPassed()
    {
        Script("linger");
        var r = _svc.ScheduleManual(1, Now, Now + 300, "Runaway");
        await _svc.TickAsync(default); Assert.Equal(1, _svc.ActiveCount);
        _time.Advance(TimeSpan.FromMinutes(30));
        await Until(() => _svc.ActiveCount == 0);
        Assert.Equal(0, _svc.ActiveCount); Assert.Equal("done", Status(r.Id));
    }

    [Fact] public async Task ShuttingDown_EndsEveryRecorder_AndKeepsTheFiles()
    {
        Script("linger");
        _svc.ScheduleManual(1, Now, Now + 3000, "A"); _svc.ScheduleManual(2, Now, Now + 3000, "B");
        await _svc.TickAsync(default); Assert.Equal(2, _svc.ActiveCount); await Captured(2);
        await _svc.StopAllAsync();
        Assert.Equal(0, _svc.ActiveCount); Assert.Equal(2, Directory.GetFiles(_rec, "*.ts").Length);
    }

    [Fact] public async Task OldRecordings_AreDeletedAfterTheConfiguredDays()
    {
        _extra["Recordings:KeepDays"] = "7"; _svc = Make();
        var r = _svc.ScheduleManual(1, Now, Now + 600, "Old");
        await _svc.TickAsync(default); await Until(() => Status(r.Id) == "done");
        _time.Advance(TimeSpan.FromDays(8));
        await _svc.TickAsync(default);
        Assert.Null(_svc.Get(r.Id)); Assert.Empty(Directory.GetFiles(_rec));
    }

    // ---- file names
    [Theory] [InlineData("News at Six", "News at Six [2026-11-06 1500].ts")] [InlineData("../../etc/passwd", "_._etc_passwd [2026-11-06 1500].ts")] [InlineData("A/B\\C:D*E?", "A_B_C_D_E_ [2026-11-06 1500].ts")]
    [InlineData("", "Recording [2026-11-06 1500].ts")] [InlineData("...", "Recording [2026-11-06 1500].ts")] [InlineData("Ünïcode Show", "Ünïcode Show [2026-11-06 1500].ts")]
    public void FileNames_AreSafe_WhateverTheTitle(string title, string expected)
    {
        var name = RecordingService.UniqueName(_rec, title, LiveFixtures.Now.ToUnixTimeSeconds());
        Assert.DoesNotContain('/', name); Assert.DoesNotContain('\\', name); Assert.DoesNotContain("..", name);
        Assert.Equal(expected, name);
    }

    [Fact] public void TwoRecordingsWithTheSameNameAndMinute_GetDifferentFiles()
    {
        Directory.CreateDirectory(_rec); File.WriteAllText(Path.Combine(_rec, "Show [2026-11-06 1500].ts"), "x");
        Assert.Equal("Show [2026-11-06 1500] 2.ts", RecordingService.UniqueName(_rec, "Show", LiveFixtures.Now.ToUnixTimeSeconds()));
    }

    [Fact] public void LongTitles_AreShortened() => Assert.True(RecordingService.UniqueName(_rec, new string('x', 400), 0).Length < 120);

    [Fact] public void StreamUrlsAreRedactedFromErrorText() => Assert.Equal("failed to open <stream>: 403", RecordingService.Redact("failed to open http://u:p@h/x?k=1: 403", "http://u:p@h/x?k=1"));
}

public sealed class ChannelListTests : IDisposable
{
    private readonly LiveTvService _live;
    private readonly TestDb _t;
    private readonly FixedUser _who = new();
    private readonly LiveListService _lists;

    public ChannelListTests()
    {
        var (live, _, t, _) = LiveFixtures.Make(); _live = live; _t = t;
        _live.RefreshAsync(default).GetAwaiter().GetResult();
        _lists = new LiveListService(_t.Db, TimeProvider.System, _who);
    }
    public void Dispose() => _t.Dispose();

    [Fact] public void ListsAreCreatedRenamedAndDeleted()
    {
        var l = _lists.Create(" Sports ");
        Assert.Equal(("Sports", 0), (l.Name, l.Channels));
        Assert.True(_lists.Rename(l.Id, "Games")); Assert.Equal("Games", _lists.Lists().Single().Name);
        Assert.True(_lists.Delete(l.Id)); Assert.Empty(_lists.Lists()); Assert.False(_lists.Delete(l.Id));
    }

    [Theory] [InlineData("")] [InlineData("   ")] [InlineData("bad\nname")]
    public void BadNamesAreRefused(string name) => Assert.Throws<InvalidOperationException>(() => _lists.Create(name));
    [Fact] public void LongNamesAreRefused() => Assert.Throws<InvalidOperationException>(() => _lists.Create(new string('x', 41)));
    [Fact] public void DuplicateNames_AreRefused_PerPerson()
    {
        _lists.Create("Kids"); Assert.Throws<InvalidOperationException>(() => _lists.Create("kids"));
        _who.UserId = 2; Assert.Equal("Kids", _lists.Create("Kids").Name);                                    // someone else can use the same name
    }

    [Fact] public void ChannelsCanBeAddedAndRemoved_AndFilterTheGuide()
    {
        var l = _lists.Create("Mine");
        Assert.True(_lists.SetMember(l.Id, 2, true)); Assert.True(_lists.SetMember(l.Id, 2, true)); Assert.True(_lists.SetMember(l.Id, 3, true));
        Assert.Equal([2L, 3L], _lists.Lists().Single().ChannelIds.OrderBy(x => x));
        Assert.Equal(["Movie Max", "Sports One"], _live.Channels(null, null, false, 50, 0, l.Id).Items.Select(c => c.Name));
        Assert.Equal(["Movie Max", "Sports One"], _live.Guide(null, null, false, LiveFixtures.Now.ToUnixTimeSeconds(), 6, 50, 0, l.Id).Channels.Select(c => c.Name));
        Assert.True(_lists.SetMember(l.Id, 2, false));
        Assert.Equal(["Sports One"], _live.Channels(null, null, false, 50, 0, l.Id).Items.Select(c => c.Name));
        Assert.Equal(3, _live.Channels(null, null, false, 50, 0).Total);                                      // no list: everything
    }

    [Fact] public void YouCannotUseSomeoneElsesList_OrANonexistentChannel()
    {
        var l = _lists.Create("Private");
        _who.UserId = 2;
        Assert.False(_lists.Owns(l.Id)); Assert.False(_lists.SetMember(l.Id, 1, true)); Assert.False(_lists.Delete(l.Id)); Assert.False(_lists.Rename(l.Id, "Mine now"));
        _who.UserId = 1; Assert.False(_lists.SetMember(l.Id, 9999, true));
    }

    [Fact] public void DeletingAListLeavesTheChannelsAlone()
    {
        var l = _lists.Create("Temp"); _lists.SetMember(l.Id, 1, true); _lists.Delete(l.Id);
        Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM live_list_channels")); Assert.Equal(3, _live.Channels(null, null, false, 50, 0).Total);
    }
}

public sealed class RecordingsAndTheLibraryTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("recl").FullName;
    private readonly string _root, _rec;
    private readonly IConfiguration _cfg;
    private readonly Database _db;
    private readonly LibraryFolders _folders;
    private readonly LibraryScanner _scanner;
    private readonly MediaServer.Api.Media.DbMediaResolver _resolver;
    private readonly LibraryQueries _q;

    public RecordingsAndTheLibraryTests()
    {
        _root = Path.Combine(_tmp, "lib"); _rec = Path.Combine(_tmp, "data", "recordings"); Directory.CreateDirectory(_root); Directory.CreateDirectory(Path.Combine(_tmp, "data"));
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = Path.Combine(_tmp, "data", "t.db"), ["Library:Root"] = _root, ["Recordings:Directory"] = _rec }).Build();
        _db = new Database(_cfg); _db.Migrate();
        _folders = new LibraryFolders(_db, _cfg, TimeProvider.System);
        _scanner = new LibraryScanner(_db, new StubProbe(), _cfg, NullLogger<LibraryScanner>.Instance, _folders);
        _resolver = new MediaServer.Api.Media.DbMediaResolver(_db, _cfg, _folders);
        _q = new LibraryQueries(_db);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private static void File_(string path, int seed) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[100 + seed]); File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, seed, DateTimeKind.Utc)); }

    [Fact] public async Task ScansWork_BeforeAnyRecordingExists()
    {
        File_(Path.Combine(_root, "Film (2020).mkv"), 1);
        Assert.Equal(1, (await _scanner.ScanAsync(default))!.Added);                                       // no recordings folder: skipped quietly, nothing removed
    }

    [Fact] public async Task RecordedFiles_BecomeRecordings_NamedByTheirTitle_AndStayOutOfTheMovieList()
    {
        File_(Path.Combine(_root, "Film (2020).mkv"), 1);
        File_(Path.Combine(_rec, "News at Six [2026-11-06 1800].ts"), 2);
        Assert.Equal(2, (await _scanner.ScanAsync(default))!.Added);
        using var c = _db.Open();
        Assert.Equal(("Recording", "News at Six"), (Convert.ToString(Database.Scalar(c, "SELECT kind FROM media_files WHERE path LIKE '@rec/%'")), Convert.ToString(Database.Scalar(c, "SELECT title FROM media_files WHERE path LIKE '@rec/%'"))));
        Assert.Equal(["Film"], _q.List(null, null, "title", 50, 0).Items.Select(i => i.Title));
        var id = Convert.ToInt64(Database.Scalar(c, "SELECT id FROM media_files WHERE path LIKE '@rec/%'"));
        Assert.Equal(Path.Combine(_rec, "News at Six [2026-11-06 1800].ts"), _resolver.ResolvePath(id));
        Assert.NotNull(_q.Play(id, ClientCaps.Baseline));                                                  // and it plays like any title
        Assert.Equal(1, _folders.List().First().Files);                                                    // the library folder's own count excludes recordings
    }

    [Fact] public async Task ARecordingDeletedFromDisk_LeavesTheLibrary_AndAMissingFolderDoesNotWipeThem()
    {
        File_(Path.Combine(_rec, "Show [2026-11-06 1800].ts"), 2);
        await _scanner.ScanAsync(default);
        Directory.Delete(_rec, true);                                                                      // the folder is gone (an unmounted volume)
        Assert.Equal(0, (await _scanner.ScanAsync(default))!.Removed);
        File_(Path.Combine(_rec, "Show [2026-11-06 1800].ts"), 2);
        Assert.Equal(0, (await _scanner.ScanAsync(default))!.Added);
    }

    [Fact] public void TheRecordingsFolder_CannotBeAddedAsALibraryFolder()
    {
        Directory.CreateDirectory(_rec);
        Assert.Contains("own data", Assert.Throws<FolderException>(() => _folders.Add(_rec, null)).Message);
    }

    [Fact] public void RecordingNames_ParseWithOrWithoutTheTimestamp()
    {
        Assert.Equal("Show", LibraryScanner.RecordingName("@rec/Show [2026-01-01 1200].ts").Title);
        Assert.Equal("Show 2", LibraryScanner.RecordingName("@rec/Show 2 [2026-01-01 1200] 2.ts").Title);
        Assert.Equal("plain", LibraryScanner.RecordingName("@rec/plain.ts").Title);
        Assert.Equal(MediaKind.Recording, LibraryScanner.RecordingName("@rec/x.ts").Kind);
    }
}

public sealed class RecordingEndpointTests : IDisposable
{
    private readonly AuthFixture _f = new(new() { ["Tools:FfmpegPath"] = "/nonexistent/ffmpeg" });
    public void Dispose() => _f.Dispose();

    [Theory] [InlineData("GET", "/api/recordings")] [InlineData("POST", "/api/recordings")] [InlineData("DELETE", "/api/recordings/1")] [InlineData("POST", "/api/recordings/1/stop")]
    [InlineData("GET", "/api/live/lists")] [InlineData("POST", "/api/live/lists")] [InlineData("PUT", "/api/live/lists/1/channels/1")]
    public async Task NeedSignIn(string method, string url)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) })).StatusCode);

    [Fact] public async Task ValidationErrors_AreFriendly()
    {
        var c = await _f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/recordings", new { channelId = 1 })).StatusCode);                  // nothing says what to record
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/recordings", new { channelId = 999, mode = "now" })).StatusCode);   // no such channel
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/recordings/42")).StatusCode);
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/recordings")).GetProperty("recordings").GetArrayLength());
    }

    [Fact] public async Task ChannelLists_WorkThroughTheApi_AndAreInvisibleToOthers()
    {
        var c = await _f.SignedIn("alex");
        var made = await c.PostAsJsonAsync("/api/live/lists", new { name = "Sports" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/live/lists", new { name = "Sports" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsync($"/api/live/lists/{id}/channels/999", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PatchAsJsonAsync($"/api/live/lists/{id}", new { name = "Games" })).StatusCode);

        await c.PostAsJsonAsync("/api/users", new { username = "sam", password = "sams-long-password" });
        var sam = _f.Client(); await sam.PostAsJsonAsync("/api/auth/login", new { username = "sam", password = "sams-long-password" });
        Assert.Equal(0, (await sam.GetFromJsonAsync<JsonElement>("/api/live/lists")).GetProperty("lists").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync($"/api/live/channels?list={id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.DeleteAsync($"/api/live/lists/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/live/lists/{id}")).StatusCode);
    }
}
