using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Library;

namespace MediaServer.Tests;

internal sealed class FixedUser(long id = 1) : IUserContext { public long UserId { get; set; } = id; }

public sealed class PerPersonHistoryTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FixedUser _who = new();
    private readonly ProgressService _p;
    private readonly LibraryQueries _q;
    private readonly WatchlistService _w;
    public PerPersonHistoryTests() { _p = new ProgressService(_t.Db, TimeProvider.System, _t.Config, _who); _q = new LibraryQueries(_t.Db, _who); _w = new WatchlistService(_t.Db, TimeProvider.System, _who); }
    public void Dispose() => _t.Dispose();

    private long Movie(string title, double duration = 6000) { var id = _t.Insert($"{title}.mkv", "Movie", title, 2020); using var c = _t.Db.Open(); Database.Execute(c, null, "UPDATE media_files SET duration_seconds=@d WHERE id=@i", ("@d", duration), ("@i", id)); return id; }
    private long Ep(string show, int s, int e) { var id = _t.Insert($"{show} S{s}E{e}.mkv", "Episode", show, 2020); using var c = _t.Db.Open(); Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, duration_seconds=1500 WHERE id=@i", ("@s", s), ("@e", e), ("@i", id)); return id; }

    [Fact] public void EachPersonHasTheirOwnProgress()
    {
        var m = Movie("Shared Film");
        _who.UserId = 1; _p.Save(m, 3000, 6000, null);
        _who.UserId = 2;
        Assert.Empty(_q.Continue(10));                                                                  // person 2 hasn't started anything
        _p.Save(m, 100, 6000, null);
        _who.UserId = 1; Assert.Equal(3000, _q.Continue(10).Single().PositionSeconds);
        _who.UserId = 2; Assert.Equal(100, _q.Continue(10).Single().PositionSeconds);
    }

    [Fact] public void Watched_Resume_AndContinueAreAllPerPerson()
    {
        var m = Movie("Film");
        _who.UserId = 1; _p.Save(m, 5900, 6000, null);                                                  // finished
        _who.UserId = 2;
        Assert.False(_q.Movie(m, ClientCaps.Baseline)!.File.ResumeSeconds > 0);
        _p.Save(m, 2000, 6000, null);
        Assert.Equal(2000, _q.Movie(m, ClientCaps.Baseline)!.File.ResumeSeconds);
        _who.UserId = 1; Assert.Equal(0, _q.Movie(m, ClientCaps.Baseline)!.File.ResumeSeconds);          // person 1 finished it, so no resume point
    }

    [Fact] public void ClearingProgressOnlyClearsYours()
    {
        var m = Movie("Film");
        _who.UserId = 1; _p.Save(m, 3000, 6000, null); _who.UserId = 2; _p.Save(m, 200, 6000, null);
        _p.Clear(m);
        _who.UserId = 1; Assert.Single(_q.Continue(10));
        _who.UserId = 2; Assert.Empty(_q.Continue(10));
    }

    [Fact] public void BulkMarkWatched_AndUnwatched_AreScopedToYou()
    {
        var a = Ep("Show", 1, 1); var b = Ep("Show", 1, 2); var c = Ep("Show", 2, 1);
        _who.UserId = 1;
        Assert.Equal(2, _p.SetWatched(_q.EpisodeFiles("title:show", 1), true));
        _who.UserId = 2; Assert.Equal("0", _t.Scalar($"SELECT COUNT(*) FROM media_progress WHERE user_id=2"));
        _who.UserId = 1; Assert.Equal("2", _t.Scalar("SELECT COUNT(*) FROM media_progress WHERE user_id=1 AND watched=1"));
        _p.SetWatched(_q.EpisodeFiles("title:show", null), true);
        Assert.Equal("3", _t.Scalar("SELECT COUNT(*) FROM media_progress WHERE user_id=1 AND watched=1"));
        _p.SetWatched(_q.EpisodeFiles("title:show", 2), false);
        Assert.Equal("2", _t.Scalar("SELECT COUNT(*) FROM media_progress WHERE user_id=1"));
        Assert.NotEqual(0, a + b + c);
    }

    [Fact] public void MyList_IsPerPerson_Ordered_AndIgnoresRepeatsAndJunk()
    {
        var m1 = Movie("Alpha"); var m2 = Movie("Beta"); Ep("Gamma", 1, 1);
        _who.UserId = 1;
        Assert.True(_w.Add("movie", m1.ToString())); Assert.True(_w.Add("movie", m1.ToString())); Assert.True(_w.Add("movie", m2.ToString())); Assert.True(_w.Add("series", "title:gamma"));
        Assert.False(_w.Add("album", "x")); Assert.False(_w.Add("movie", "")); Assert.False(_w.Add("movie", new string('x', 300)));
        Assert.Equal(3, _w.Keys().Count);
        Assert.Equal(["Gamma", "Beta", "Alpha"], _q.ItemsByKeys(_w.Keys()).Select(i => i.Title));        // newest first
        _who.UserId = 2; Assert.Empty(_w.Keys());
        _who.UserId = 1; Assert.True(_w.Remove("movie", m1.ToString())); Assert.False(_w.Remove("movie", m1.ToString()));
        Assert.Equal(2, _w.Keys().Count);
    }

    [Fact] public void MyList_SkipsTitlesThatNoLongerExist()
    {
        var m = Movie("Gone"); _w.Add("movie", m.ToString()); _w.Add("movie", "99999");
        Assert.Equal(["Gone"], _q.ItemsByKeys(_w.Keys()).Select(i => i.Title));
    }

    [Fact] public void ListingEverything_IsNotCappedAtTwoHundred()
    {
        for (var i = 0; i < 230; i++) Movie($"Film {i:000}");
        Assert.Equal(230, _q.List(null, "movie", "title", 1, 0, unlimited: true).Items.Count);
        Assert.Equal(200, _q.List(null, "movie", "title", 100_000, 0).Items.Count);                    // the page API stays capped
    }

    [Fact] public void ExistingHistory_MovesToTheFirstAccount_WhenTheDatabaseUpgrades()
    {
        // the pre-multi-user layout, with a row in it, upgraded in place
        var dir = Directory.CreateTempSubdirectory("migrate").FullName;
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "old.db")}"))
        {
            c.Open();
            foreach (var sql in new[] { "CREATE TABLE users(id INTEGER PRIMARY KEY, username TEXT, password_hash TEXT, created_at TEXT, display_name TEXT)", "INSERT INTO users(id,username,password_hash,created_at) VALUES(7,'a','h','x')",
                "CREATE TABLE media_files(id INTEGER PRIMARY KEY)", "INSERT INTO media_files(id) VALUES(1)",
                "CREATE TABLE media_progress(media_id INTEGER PRIMARY KEY, position_seconds REAL NOT NULL, duration_seconds REAL, watched INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL)", "INSERT INTO media_progress VALUES(1, 50, 100, 0, 'x')" })
            { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            var migration = typeof(Database).Assembly.GetType("MediaServer.Api.Data.Migrations")!.GetField("All")!.GetValue(null) as string[];
            using var m = c.CreateCommand(); m.CommandText = migration![8]; m.ExecuteNonQuery();                       // v9
            using var q = c.CreateCommand(); q.CommandText = "SELECT user_id, media_id, position_seconds FROM media_progress";
            using var r = q.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal((7L, 1L, 50.0), (r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

public sealed class UserManagementTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    private async Task<(HttpClient Admin, HttpClient Sam, long SamId)> TwoPeople()
    {
        var admin = await _f.SignedIn("alex");
        var res = await admin.PostAsJsonAsync("/api/users", new { username = "sam", password = "sams-long-password", displayName = "Sam", role = "user" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var sam = _f.Client(); Assert.Equal(HttpStatusCode.OK, (await sam.PostAsJsonAsync("/api/auth/login", new { username = "sam", password = "sams-long-password" })).StatusCode);
        return (admin, sam, id);
    }

    [Fact] public async Task TheFirstAccountIsAnAdministrator_AndNewOnesDefaultToPeople()
    {
        var (admin, sam, _) = await TwoPeople();
        Assert.Equal("admin", (await admin.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("role").GetString());
        Assert.Equal("user", (await sam.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("role").GetString());
        var made = await admin.PostAsJsonAsync("/api/users", new { username = "kid", password = "kids-long-password" });
        Assert.Equal("user", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
    }

    [Theory] [InlineData("GET", "/api/settings")] [InlineData("PUT", "/api/settings")] [InlineData("GET", "/api/users")] [InlineData("POST", "/api/users")] [InlineData("GET", "/api/library/folders")]
    [InlineData("POST", "/api/library/folders")] [InlineData("GET", "/api/fs/browse")] [InlineData("POST", "/api/maintenance/optimize-database")] [InlineData("GET", "/api/maintenance/backup")]
    [InlineData("GET", "/api/system")] [InlineData("GET", "/api/system/logs")] [InlineData("POST", "/api/live/refresh")] [InlineData("GET", "/api/tmdb/search?type=movie&q=x")]
    [InlineData("PUT", "/api/media/1/match")] [InlineData("DELETE", "/api/media/1/match")] [InlineData("POST", "/api/settings/test/tmdb")]
    [InlineData("GET", "/api/system/activity")] [InlineData("GET", "/api/arr/status")] [InlineData("GET", "/api/arr/queue")] [InlineData("POST", "/api/arr/add")] [InlineData("POST", "/api/settings/test/radarr")]
    public async Task OrdinaryPeople_CannotReachAdministration(string method, string url)
    {
        var (_, sam, _) = await TwoPeople();
        var res = await sam.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("admin_required", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory] [InlineData("/api/items")] [InlineData("/api/continue")] [InlineData("/api/genres")] [InlineData("/api/watchlist")] [InlineData("/api/live/status")] [InlineData("/api/app")] [InlineData("/api/auth/sessions")]
    public async Task OrdinaryPeople_CanStillUseTheServer(string url)
    {
        var (_, sam, _) = await TwoPeople();
        Assert.Equal(HttpStatusCode.OK, (await sam.GetAsync(url)).StatusCode);
    }

    [Fact] public async Task Watching_IsKeptApartBetweenPeople()
    {
        var (admin, sam, _) = await TwoPeople();
        using (var db = _f.Db.Open()) Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,duration_seconds) VALUES('m.mp4',1,1,'x','x',1,'Movie','M',6000)");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/media/1/progress", new { position = 3000, duration = 6000 })).StatusCode);
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/continue")).EnumerateArray());
        Assert.Empty((await sam.GetFromJsonAsync<JsonElement>("/api/continue")).EnumerateArray());
        Assert.Equal(3000, (await admin.GetFromJsonAsync<JsonElement>("/api/media/1/play")).GetProperty("resumeSeconds").GetDouble());
        Assert.Equal(0, (await sam.GetFromJsonAsync<JsonElement>("/api/media/1/play")).GetProperty("resumeSeconds").GetDouble());
    }

    [Fact] public async Task MyList_WorksThroughTheApi_PerPerson()
    {
        var (admin, sam, _) = await TwoPeople();
        using (var db = _f.Db.Open()) Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title) VALUES('m.mp4',1,1,'x','x',1,'Movie','Mine')");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync("/api/watchlist/movie/1", null)).StatusCode);
        Assert.Equal("Mine", (await admin.GetFromJsonAsync<JsonElement>("/api/watchlist")).GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(0, (await sam.GetFromJsonAsync<JsonElement>("/api/watchlist")).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/watchlist/album/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/watchlist/movie/1")).StatusCode);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/api/watchlist/keys")).GetProperty("keys").GetArrayLength());
    }

    [Fact] public async Task MarkingASeasonWatched_ThroughTheApi()
    {
        var (admin, _, _) = await TwoPeople();
        using (var db = _f.Db.Open())
            for (var e = 1; e <= 3; e++) Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,season,episode,duration_seconds) VALUES(@p,1,1,'x','x',1,'Episode','Show',1,@e,1200)", ("@p", $"e{e}.mkv"), ("@e", e));
        var r = await admin.PostAsync("/api/items/series/title:show/watched?season=1&watched=true", null);
        Assert.Equal(3, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetInt32());
        var show = await admin.GetFromJsonAsync<JsonElement>("/api/items/series/title:show");
        Assert.All(show.GetProperty("seasons")[0].GetProperty("episodes").EnumerateArray(), e => Assert.True(e.GetProperty("watched").GetBoolean()));
        await admin.PostAsync("/api/items/series/title:show/watched?watched=false", null);
        Assert.All((await admin.GetFromJsonAsync<JsonElement>("/api/items/series/title:show")).GetProperty("seasons")[0].GetProperty("episodes").EnumerateArray(), e => Assert.False(e.GetProperty("watched").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/items/series/title:nothing/watched", null)).StatusCode);
    }

    [Fact] public async Task Usernames_AreUnique_IgnoringCase_AndValidated()
    {
        var (admin, _, _) = await TwoPeople();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users", new { username = "SAM", password = "another-long-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new { username = "x", password = "another-long-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new { username = "newbie", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new { username = "newbie", password = "a-long-password-123", role = "superuser" })).StatusCode);
    }

    [Fact] public async Task TheLastAdministrator_CannotBeDemotedOrDeleted_AndNobodyDeletesThemselves()
    {
        var (admin, _, samId) = await TwoPeople();
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/users")).GetProperty("users").EnumerateArray().First(u => u.GetProperty("username").GetString() == "alex").GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/users/{me}", new { role = "user" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{me}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/users/9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/users/{samId}", new { role = "admin" })).StatusCode);       // promote sam: now two
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/users/{me}", new { role = "user" })).StatusCode);             // so alex can step down
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/users")).StatusCode);                                                  // and immediately loses admin access
    }

    [Fact] public async Task ResettingAPassword_SignsThatPersonOut_AndTheNewPasswordWorks()
    {
        var (admin, sam, samId) = await TwoPeople();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/users/{samId}/password", new { password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/users/{samId}/password", new { password = "brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login", new { username = "sam", password = "sams-long-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Client().PostAsJsonAsync("/api/auth/login", new { username = "sam", password = "brand-new-password" })).StatusCode);
    }

    [Fact] public async Task DeletingSomeone_RemovesTheirSignInsHistoryAndList_ButNotOthers()
    {
        var (admin, sam, samId) = await TwoPeople();
        using (var db = _f.Db.Open()) Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,duration_seconds) VALUES('m.mp4',1,1,'x','x',1,'Movie','M',6000)");
        await sam.PostAsJsonAsync("/api/media/1/progress", new { position = 100, duration = 6000 }); await sam.PutAsync("/api/watchlist/movie/1", null);
        await admin.PostAsJsonAsync("/api/media/1/progress", new { position = 200, duration = 6000 });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{samId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/items")).StatusCode);
        using var c = _f.Db.Open();
        Assert.Equal(1L, Database.Scalar(c, "SELECT COUNT(*) FROM media_progress")); Assert.Equal(0L, Database.Scalar(c, "SELECT COUNT(*) FROM watchlist"));
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>("/api/continue")).GetArrayLength());
    }

    [Fact] public async Task ThePeopleList_ShowsRolesDevicesAndTwoFactor_ButNeverSecrets()
    {
        var (admin, _, _) = await TwoPeople();
        var text = await admin.GetStringAsync("/api/users");
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("pbkdf2", text); Assert.DoesNotContain("totp_secret", text);
        var users = JsonDocument.Parse(text).RootElement.GetProperty("users");
        Assert.Equal(["alex", "sam"], users.EnumerateArray().Select(u => u.GetProperty("username").GetString()));
        Assert.Equal(1, users[1].GetProperty("sessions").GetInt32());
    }

    [Fact] public async Task AnOrdinaryPerson_CanChangeTheirOwnPasswordAndName()
    {
        var (_, sam, _) = await TwoPeople();
        Assert.Equal(HttpStatusCode.NoContent, (await sam.PostAsJsonAsync("/api/auth/password", new { current = "sams-long-password", @new = "sams-new-password!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sam.PutAsJsonAsync("/api/auth/profile", new { displayName = "Sammy" })).StatusCode);
    }

    [Fact] public async Task MaintenanceClearHistory_OnlyClearsTheCallersOwn()
    {
        var (admin, sam, _) = await TwoPeople();
        using (var db = _f.Db.Open()) Database.Execute(db, null, "INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,duration_seconds) VALUES('m.mp4',1,1,'x','x',1,'Movie','M',6000)");
        await sam.PostAsJsonAsync("/api/media/1/progress", new { position = 100, duration = 6000 }); await admin.PostAsJsonAsync("/api/media/1/progress", new { position = 200, duration = 6000 });
        await admin.PostAsync("/api/maintenance/clear-watch-history?confirm=true", null);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/continue")).EnumerateArray());
        Assert.Single((await sam.GetFromJsonAsync<JsonElement>("/api/continue")).EnumerateArray());
    }
}
