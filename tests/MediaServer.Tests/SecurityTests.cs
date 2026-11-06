using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Security;
using MediaServer.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Tests;

public sealed class SecretBoxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("secretbox").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    private static SecretBox Box() => new(RandomNumberGenerator.GetBytes(32));
    private SecretBox FromDir() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = Path.Combine(_dir, "db", "t.db") }).Build());

    [Fact] public void RoundTrips_AndTheStoredFormIsNotTheText()
    {
        var b = Box(); var stored = b.Protect("super-secret-token", "Tmdb:ReadAccessToken");
        Assert.StartsWith("enc:v1:", stored); Assert.DoesNotContain("super-secret", stored);
        Assert.Equal("super-secret-token", b.Unprotect(stored, "Tmdb:ReadAccessToken"));
    }

    [Fact] public void EveryEncryptionIsDifferent_SoEqualSecretsAreNotVisible()
        => Assert.NotEqual(Box().Protect("same", "k"), Box().Protect("same", "k"));
    [Fact] public void TheSameBoxNeverReusesANonce()
    { var b = Box(); Assert.NotEqual(b.Protect("same", "k"), b.Protect("same", "k")); }

    [Fact] public void ACiphertextCannotBeMovedToAnotherField()
    { var b = Box(); var stored = b.Protect("v", "Tmdb:ApiKey"); Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(stored, "LiveTv:M3uUrl")); }

    [Fact] public void AnotherKey_CannotOpenIt() => Assert.ThrowsAny<CryptographicException>(() => Box().Unprotect(Box().Protect("v", "k"), "k"));

    [Theory] [InlineData("enc:v1:")] [InlineData("enc:v1:!!!notbase64")] [InlineData("enc:v1:AAAA")]
    public void GarbageAndTruncatedValues_FailCleanly(string stored) => Assert.ThrowsAny<CryptographicException>(() => Box().Unprotect(stored, "k"));

    [Fact] public void TamperingIsDetected()
    {
        var b = Box(); var stored = b.Protect("hello", "k");
        var blob = Convert.FromBase64String(stored["enc:v1:".Length..]); blob[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect("enc:v1:" + Convert.ToBase64String(blob), "k"));
    }

    [Fact] public void LegacyPlaintext_PassesThrough_SoOlderInstallsKeepWorking() => Assert.Equal("plain value", Box().Unprotect("plain value", "k"));
    [Fact] public void TryUnprotect_ReturnsNullInsteadOfThrowing() => Assert.Null(Box().TryUnprotect(Box().Protect("v", "k"), "k"));

    [Fact] public void Fingerprints_AreStable_KeyedAndDoNotContainTheValue()
    {
        var b = Box();
        Assert.Equal(b.Fingerprint("http://x/y"), b.Fingerprint("http://x/y"));
        Assert.NotEqual(b.Fingerprint("http://x/y"), b.Fingerprint("http://x/z"));
        Assert.NotEqual(b.Fingerprint("http://x/y"), Box().Fingerprint("http://x/y"));
        Assert.DoesNotContain("http", b.Fingerprint("http://x/y"));
    }

    [Fact] public void TheKeyFile_IsCreatedOnce_AndReused_WithOwnerOnlyPermissions()
    {
        var a = FromDir(); var stored = a.Protect("v", "k");
        var keyFile = Path.Combine(_dir, "db", "secret.key");
        Assert.True(File.Exists(keyFile));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        Assert.Equal("file", a.KeySource);
        Assert.Equal("v", FromDir().Unprotect(stored, "k"));                                        // a restart reads the same key
    }

    [Fact] public void AKeyFromTheEnvironment_IsPreferred_AndMustBeValid()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:SecretKey"] = key, ["Database:Path"] = Path.Combine(_dir, "e", "t.db") }).Build();
        var b = new SecretBox(cfg);
        Assert.Equal("environment", b.KeySource); Assert.False(File.Exists(Path.Combine(_dir, "e", "secret.key")));
        Assert.Equal("v", new SecretBox(cfg).Unprotect(b.Protect("v", "k"), "k"));
        foreach (var bad in new[] { "short", Convert.ToBase64String(new byte[16]), "!!!" })
            Assert.Throws<InvalidOperationException>(() => new SecretBox(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:SecretKey"] = bad }).Build()));
    }
}

public sealed class EncryptedSettingsTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly SecretBox _box = new(RandomNumberGenerator.GetBytes(32));
    private readonly ConfigurationManager _cfg = new();
    private readonly DbSettingsProvider _provider;
    private readonly SettingsService _svc;

    public EncryptedSettingsTests()
    {
        _provider = new DbSettingsProvider(_t.Db, _box);
        ((IConfigurationBuilder)_cfg).Add(_provider);
        _svc = new SettingsService(_t.Db, _provider, _cfg, TimeProvider.System, _box);
    }
    public void Dispose() => _t.Dispose();
    private string? Stored(string key) => _t.Scalar($"SELECT value FROM settings WHERE key='{key}'");

    [Fact] public void SecretsAreEncryptedInTheDatabase_ButTheServerReadsThemNormally()
    {
        _svc.Apply(new Dictionary<string, string?> { ["Tmdb:ReadAccessToken"] = "tok-123-abc", ["LiveTv:M3uUrl"] = "http://u:p@iptv.example/get?password=hunter2" });
        Assert.StartsWith("enc:v1:", Stored("Tmdb:ReadAccessToken")); Assert.StartsWith("enc:v1:", Stored("LiveTv:M3uUrl"));
        Assert.DoesNotContain("hunter2", Stored("LiveTv:M3uUrl"));
        Assert.Equal("tok-123-abc", _cfg["Tmdb:ReadAccessToken"]); Assert.Equal("http://u:p@iptv.example/get?password=hunter2", _cfg["LiveTv:M3uUrl"]);
    }

    [Fact] public void OrdinarySettings_StayReadableInTheDatabase()
    {
        _svc.Apply(new Dictionary<string, string?> { ["General:ServerName"] = "Home", ["Library:ScanIntervalMinutes"] = "30" });
        Assert.Equal("Home", Stored("General:ServerName")); Assert.Equal("30", Stored("Library:ScanIntervalMinutes"));
    }

    [Fact] public void ChangingASecretToTheSameValue_IsNotAChange_AndAnotherValueReplacesIt()
    {
        Assert.Single(_svc.Apply(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "key-one" }));
        Assert.Empty(_svc.Apply(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "key-one" }));
        Assert.Single(_svc.Apply(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "key-two" }));
        Assert.Equal("key-two", _cfg["Tmdb:ApiKey"]);
    }

    [Fact] public void ASecretThatCannotBeDecrypted_CountsAsNotSet_InsteadOfBreakingTheServer()
    {
        _svc.Apply(new Dictionary<string, string?> { ["Tmdb:ReadAccessToken"] = "tok" });
        var other = new DbSettingsProvider(_t.Db, new SecretBox(RandomNumberGenerator.GetBytes(32)));   // "the key was replaced"
        other.Load();
        Assert.False(other.TryGet("Tmdb:ReadAccessToken", out _));
    }

    [Fact] public void TheDatabaseFileNeverContainsThePlainSecret()
    {
        _svc.Apply(new Dictionary<string, string?> { ["Tmdb:ReadAccessToken"] = "needle-needle-needle-9000" });
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(_t.Dir, "t.db*")) Assert.DoesNotContain("needle-needle", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f)));
    }

    [Fact] public void TheMigrationEncryptsWhatAnOlderVersionLeftReadable_Once()
    {
        using (var c = _t.Db.Open())
        {
            Database.Execute(c, null, "INSERT INTO settings(key,value,updated_at) VALUES('Tmdb:ReadAccessToken','old-plain-token','x'),('General:ServerName','Home','x'),('auth.stream_key','AAAA','x')");
            Database.Execute(c, null, "INSERT INTO live_channels(source_key,name,stream_url,logo_url,updated_at) VALUES('1','One','http://iptv/stream/abc?pw=secret','http://iptv/logo.png','x')");
        }
        Assert.Equal((2, 1), SecretMigrator.Run(_t.Db, _box));
        Assert.StartsWith("enc:v1:", Stored("Tmdb:ReadAccessToken")); Assert.StartsWith("enc:v1:", Stored("auth.stream_key")); Assert.Equal("Home", Stored("General:ServerName"));
        Assert.StartsWith("enc:v1:", _t.Scalar("SELECT stream_url FROM live_channels")); Assert.StartsWith("enc:v1:", _t.Scalar("SELECT logo_url FROM live_channels"));
        Assert.False(string.IsNullOrEmpty(_t.Scalar("SELECT stream_sig FROM live_channels")));
        Assert.Equal((0, 0), SecretMigrator.Run(_t.Db, _box));                                       // nothing left to do the second time
        _provider.Reload();
        Assert.Equal("old-plain-token", _cfg["Tmdb:ReadAccessToken"]);
    }

    [Fact] public void TheMigration_LeavesNoReadableCopyOfTheOldValues_InTheFileOrTheLog()
    {
        using (var c = _t.Db.Open())
        {
            Database.Execute(c, null, "INSERT INTO settings(key,value,updated_at) VALUES('Tmdb:ReadAccessToken','old-needle-token-77','x')");
            Database.Execute(c, null, "INSERT INTO live_channels(source_key,name,stream_url,logo_url,updated_at) VALUES('1','One','http://iptv.example/stream/needle-stream-123?pw=needle-pass','http://iptv.example/needle-logo.png','x')");
        }
        Assert.Equal((1, 1), SecretMigrator.Run(_t.Db, _box));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(_t.Dir, "t.db*"))
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f));
            Assert.DoesNotContain("needle-", text);
        }
        _provider.Reload();
        Assert.Equal("old-needle-token-77", _cfg["Tmdb:ReadAccessToken"]);                                // and it still works
    }

    [Fact] public void ADatabaseAnEarlierBuildAlreadyEncrypted_StillGetsItsOldCopiesScrubbed_Once()
    {
        // an old build wrote the plain value, then replaced it: without secure_delete the old bytes stay in the file
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_t.Dir, "t.db")}"))
        {
            raw.Open();
            foreach (var sql in new[] { "PRAGMA secure_delete=OFF", "INSERT INTO settings(key,value,updated_at) VALUES('old.plain','http://iptv/needle-old-playlist-9?pw=1','x')",
                $"INSERT INTO settings(key,value,updated_at) VALUES('LiveTv:M3uUrl','{_box.Protect("http://iptv/new", "LiveTv:M3uUrl")}','x')", "DELETE FROM settings WHERE key='old.plain'", "PRAGMA wal_checkpoint(TRUNCATE)" })
            { using var cmd = raw.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Contains("needle-old-playlist", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(_t.Dir, "t.db"))));   // the residue is really there
        Assert.Equal((0, 0), SecretMigrator.Run(_t.Db, _box));                                          // nothing to encrypt...
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(_t.Dir, "t.db*")) Assert.DoesNotContain("needle-old-playlist", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f)));   // ...but it is scrubbed
        Assert.Equal("1", _t.Scalar("SELECT value FROM settings WHERE key='auth.scrubbed'"));
        _provider.Reload(); Assert.Equal("http://iptv/new", _cfg["LiveTv:M3uUrl"]);
    }

    [Fact] public async Task EncryptedChannelUrls_StillPlay_AndTheProbeCacheSurvivesARefresh()
    {
        var (svc, _, t, _) = LiveFixtures.Make(box: _box);
        using var _ = t;
        await svc.RefreshAsync(default);
        var id = svc.Channels(null, "cbs", false, 5, 0).Items.Single().Id;
        Assert.StartsWith("enc:v1:", t.Scalar($"SELECT stream_url FROM live_channels WHERE id={id}"));
        Assert.Equal("http://src.test/proxy/ts/stream/aaa-111", svc.GetStream(id)!.Url);
        Assert.StartsWith("http://src.test/logos/", svc.LogoSource(id));
        svc.SaveProbe(id, new MediaServer.Api.Probing.MediaInfo("mpegts", null, null, null, [], [], []));
        await svc.RefreshAsync(default);                                                             // same URL again, different ciphertext
        Assert.NotNull(svc.GetStream(id)!.CachedInfo);                                                     // so the cached probe was kept
    }

    [Fact] public async Task AChangedStreamUrl_ClearsTheCachedProbe()
    {
        var url = "http://src.test/proxy/ts/stream/aaa-111";
        var (svc, h, t, _) = LiveFixtures.Make(box: _box, route: r => r.RequestUri!.AbsolutePath == "/output/m3u"
            ? TmdbJson.Ok(LiveFixtures.M3u(("1", "CBS 12", 1, "US CBS", url))) : TmdbJson.Status(HttpStatusCode.NotFound));
        using var _ = t;
        await svc.RefreshAsync(default);
        var id = svc.Channels(null, null, false, 5, 0).Items.Single().Id;
        svc.SaveProbe(id, new MediaServer.Api.Probing.MediaInfo("mpegts", null, null, null, [], [], []));
        url = "http://src.test/proxy/ts/stream/NEW";
        await svc.RefreshAsync(default);
        Assert.Null(svc.GetStream(id)!.CachedInfo);
        Assert.Equal("http://src.test/proxy/ts/stream/NEW", svc.GetStream(id)!.Url);
        Assert.NotNull(h);
    }
}

public sealed class TotpTests
{
    [Fact] public void MatchesTheRfc6238TestVectors()
    {
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));
        Assert.Equal("081804", Totp.Code(secret, 1111111109 / 30));
        Assert.Equal("050471", Totp.Code(secret, 1111111111 / 30));
        Assert.Equal("005924", Totp.Code(secret, 1234567890 / 30));
        Assert.Equal("279037", Totp.Code(secret, 2000000000 / 30));
    }

    [Fact] public void Base32_RoundTrips_AndToleratesSpacesAndCase()
    {
        var s = Totp.NewSecret(); var b32 = Totp.ToBase32(s);
        Assert.Equal(32, b32.Length); Assert.Equal(s, Totp.FromBase32(b32));
        Assert.Equal(s, Totp.FromBase32(string.Join(" ", b32.ToLowerInvariant().Chunk(4).Select(c => new string(c)))));
        Assert.Throws<FormatException>(() => Totp.FromBase32("not*base32"));
        Assert.Equal("GEZDGNBVGY3TQOJQ", Totp.ToBase32(System.Text.Encoding.ASCII.GetBytes("1234567890")));
    }

    [Fact] public void AcceptsTheCurrentAndNeighbouringSteps_ButNotOthers_AndNeverTwice()
    {
        var secret = Totp.NewSecret(); var now = new DateTimeOffset(2026, 5, 1, 12, 0, 10, TimeSpan.Zero); var step = Totp.StepOf(now);
        Assert.True(Totp.Verify(secret, Totp.Code(secret, step), now, null, out var m)); Assert.Equal(step, m);
        Assert.True(Totp.Verify(secret, Totp.Code(secret, step - 1), now, null, out _));            // a little clock drift
        Assert.True(Totp.Verify(secret, Totp.Code(secret, step + 1), now, null, out _));
        Assert.False(Totp.Verify(secret, Totp.Code(secret, step - 3), now, null, out _));
        Assert.False(Totp.Verify(secret, Totp.Code(secret, step), now, step, out _));              // already used
        Assert.False(Totp.Verify(secret, Totp.Code(secret, step - 1), now, step, out _));          // older than the last accepted one
    }

    [Theory] [InlineData("")] [InlineData("12345")] [InlineData("1234567")] [InlineData("abcdef")] [InlineData("12 34 5x")]
    public void MalformedCodes_AreRejected(string code) => Assert.False(Totp.Verify(Totp.NewSecret(), code, DateTimeOffset.UtcNow, null, out _));

    [Fact] public void TheUri_NamesTheServerAndAccount()
        => Assert.Equal("otpauth://totp/Home%20Cinema:alex?secret=ABC&issuer=Home%20Cinema&algorithm=SHA1&digits=6&period=30", Totp.Uri("Home Cinema", "alex", "ABC"));
}

public sealed class TwoFactorFlowTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static string CurrentCode(string base32, DateTimeOffset now, int offset = 0) => Totp.Code(Totp.FromBase32(base32), Totp.StepOf(now) + offset);
    private async Task<(HttpClient Client, string Secret, string[] Recovery)> Enroll()
    {
        var c = await _f.SignedIn("alex");
        var begin = await (await c.PostAsync("/api/auth/2fa/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        var res = await c.PostAsJsonAsync("/api/auth/2fa/enable", new { code = CurrentCode(secret, _f.Time.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var codes = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        return (c, secret, codes);
    }
    private static Task<HttpResponseMessage> Login(HttpClient c) => c.PostAsJsonAsync("/api/auth/login", new { username = "alex", password = "a-long-password-123" });

    [Fact] public async Task Enrolment_NeedsAWorkingCode_AndGivesRecoveryCodesOnce()
    {
        var c = await _f.SignedIn("alex");
        var begin = await (await c.PostAsync("/api/auth/2fa/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("otpauth://totp/MediaServer:alex?secret=", begin.GetProperty("uri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/2fa/enable", new { code = "000000" })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/auth/2fa")).GetProperty("enabled").GetBoolean());       // not on until proven
        var (_, secret, codes) = (c, begin.GetProperty("secret").GetString()!, 0);
        var ok = await c.PostAsJsonAsync("/api/auth/2fa/enable", new { code = CurrentCode(secret, _f.Time.GetUtcNow()) });
        var rec = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes");
        Assert.Equal(8, rec.GetArrayLength()); Assert.Matches("^[0-9a-f]{5}-[0-9a-f]{5}$", rec[0].GetString());
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/auth/2fa")).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/auth/2fa/begin", null)).StatusCode);              // can't start over while on
        Assert.Equal(0, codes);
    }

    [Fact] public async Task Secret_IsEncryptedAtRest_AndRecoveryCodesAreHashed()
    {
        var (_, secret, recovery) = await Enroll();
        using var db = _f.Db.Open();
        var stored = Convert.ToString(Database.Scalar(db, "SELECT totp_secret FROM users WHERE username='alex'"));
        Assert.StartsWith("enc:v1:", stored); Assert.DoesNotContain(secret, stored!);
        var codesJson = Convert.ToString(Database.Scalar(db, "SELECT recovery_codes FROM users WHERE username='alex'"))!;
        Assert.DoesNotContain(recovery[0], codesJson);
    }

    [Fact] public async Task PasswordAlone_NoLongerSignsIn_ButTheCodeCompletesIt()
    {
        var (_, secret, _) = await Enroll();
        var other = _f.Client();
        var j = await (await Login(other)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(j.GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/items")).StatusCode);                       // not signed in yet
        var ticket = j.GetProperty("ticket").GetString();
        _f.Time.Advance(TimeSpan.FromSeconds(31));                                                                          // the enrolment code's step is used up
        var done = await other.PostAsJsonAsync("/api/auth/login/2fa", new { ticket, code = CurrentCode(secret, _f.Time.GetUtcNow()), remember = true });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/items")).StatusCode);
    }

    [Fact] public async Task ACodeWorksOnlyOnce()
    {
        var (_, secret, _) = await Enroll();
        _f.Time.Advance(TimeSpan.FromSeconds(31));
        var code = CurrentCode(secret, _f.Time.GetUtcNow());
        var t1 = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        var t2 = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        Assert.Equal(HttpStatusCode.OK, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t1, code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t2, code })).StatusCode);   // replayed
    }

    [Fact] public async Task RecoveryCodes_SignYouIn_EachOnce()
    {
        var (me, _, recovery) = await Enroll();
        var t = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        Assert.Equal(HttpStatusCode.OK, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = recovery[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = recovery[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = recovery[1].ToUpperInvariant().Replace("-", "") })).StatusCode);   // case and dash don't matter
        Assert.Equal(6, (await me.GetFromJsonAsync<JsonElement>("/api/auth/2fa")).GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact] public async Task WrongCodes_AreRefused_AndThrottled()
    {
        await Enroll();
        var t = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        var c = _f.Client();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = "000000" })).StatusCode);
    }

    [Theory] [InlineData("")] [InlineData("1.2.3")] [InlineData("1.99999999999.AAAA")] [InlineData("garbage")]
    public async Task ForgedOrExpiredTickets_AreRefused(string ticket)
    {
        await Enroll();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket, code = "123456" })).StatusCode);
    }

    [Fact] public async Task ATicket_ExpiresAfterFiveMinutes()
    {
        var (_, secret, _) = await Enroll();
        var t = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        _f.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = CurrentCode(secret, _f.Time.GetUtcNow()) })).StatusCode);
    }

    [Fact] public async Task ATicketForOneUser_CannotBeUsedWithAnotherUsersCode()
    {
        var (admin, _, _) = await Enroll();
        await admin.PostAsJsonAsync("/api/users", new { username = "sam", password = "sams-long-password", role = "user" });
        var sam = _f.Client(); await sam.PostAsJsonAsync("/api/auth/login", new { username = "sam", password = "sams-long-password" });
        Assert.Equal(HttpStatusCode.OK, (await sam.GetAsync("/api/items")).StatusCode);                                     // sam has no 2FA: signed straight in
    }

    [Fact] public async Task TurningItOff_NeedsThePasswordAndACode()
    {
        var (c, secret, recovery) = await Enroll();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "wrong-password-here", code = recovery[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "a-long-password-123", code = "000000" })).StatusCode);
        _f.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "a-long-password-123", code = CurrentCode(secret, _f.Time.GetUtcNow()) })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/auth/2fa")).GetProperty("enabled").GetBoolean());
        Assert.False((await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("twoFactorRequired", out _));
    }

    [Fact] public async Task RecoveryCodes_CanBeRegenerated_WithThePassword()
    {
        var (c, _, old) = await Enroll();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/2fa/recovery-codes", new { password = "nope-nope-nope" })).StatusCode);
        var fresh = (await (await c.PostAsJsonAsync("/api/auth/2fa/recovery-codes", new { password = "a-long-password-123" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes");
        Assert.Equal(8, fresh.GetArrayLength());
        var t = (await (await Login(_f.Client())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PostAsJsonAsync("/api/auth/login/2fa", new { ticket = t, code = old[0] })).StatusCode);     // the old set is dead
    }

    [Fact] public async Task AnAdministrator_CanResetSomeonesSecondFactor()
    {
        var (admin, _, _) = await Enroll();
        await admin.PostAsJsonAsync("/api/users", new { username = "sam", password = "sams-long-password", role = "user" });
        var users = (await admin.GetFromJsonAsync<JsonElement>("/api/users")).GetProperty("users");
        var sam = users.EnumerateArray().First(u => u.GetProperty("username").GetString() == "sam").GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/users/{sam}/two-factor/reset", null)).StatusCode);
    }

    [Fact] public async Task EverythingRequiresSignIn()
    {
        var c = _f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/2fa")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/auth/2fa/begin", null)).StatusCode);
    }
}
