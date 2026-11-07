using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Library;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Tests;

public sealed class ScanFilterTests
{
    private static ScanFilter Make(string? words = null, int minMb = 0)
        => ScanFilter.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:IgnoreWords"] = words, ["Library:MinFileSizeMb"] = minMb.ToString() }).Build());

    [Fact] public void ByDefault_EverythingIsKept() => Assert.True(Make().Allows("Movies/Film (2020)/Film.mkv", 10));

    [Theory]
    [InlineData("Movies/Film/Film-sample.mkv", false)]
    [InlineData("Movies/Film/Extras/Making of.mkv", false)]
    [InlineData("Movies/FEATURETTES/a.mkv", false)]
    [InlineData("Movies/Film/Film.mkv", true)]
    public void IgnoreWords_MatchFolderAndFileNames_IgnoringCase(string path, bool kept) => Assert.Equal(kept, Make("sample, extras;Featurettes").Allows(path, 1_000_000_000));

    [Fact] public void FilesBelowTheMinimumSizeAreLeftOut()
    {
        var f = Make(minMb: 50);
        Assert.False(f.Allows("a.mkv", 49L * 1024 * 1024)); Assert.True(f.Allows("a.mkv", 50L * 1024 * 1024));
    }

    [Fact] public void TheWordList_IsTidied()
    {
        Assert.Equal(["sample", "extras"], ScanFilter.ParseWords(" Sample , ,x, extras,SAMPLE\n"));          // blanks, one-letter entries and repeats dropped
        Assert.Empty(ScanFilter.ParseWords(null));
        Assert.Equal(30, ScanFilter.ParseWords(string.Join(",", Enumerable.Range(0, 100).Select(i => "word" + i))).Count);
    }

    [Fact] public async Task AScan_LeavesOutWhatTheFilterExcludes()
    {
        using var t = new TestDb(new Dictionary<string, string?> { ["Library:IgnoreWords"] = "extras" });
        var lib = Path.Combine(t.Dir, "lib");
        Directory.CreateDirectory(Path.Combine(lib, "Film (2020)", "Extras"));
        await File.WriteAllBytesAsync(Path.Combine(lib, "Film (2020)", "Film (2020).mkv"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(lib, "Film (2020)", "Extras", "Deleted scene.mkv"), [1, 2, 3]);
        var scanner = new LibraryScanner(t.Db, new NoProbe(), t.Config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryScanner>.Instance);
        await scanner.ScanAsync(default);
        Assert.Equal("1", t.Scalar("SELECT COUNT(*) FROM media_files WHERE deleted_at IS NULL"));
    }

    private sealed class NoProbe : MediaServer.Api.Probing.IMediaProbe
    {
        public Task<MediaServer.Api.Probing.MediaInfo> ProbeAsync(string path, CancellationToken ct) => throw new MediaServer.Api.Probing.ProbeFailedException("not needed here");
    }
}

public sealed class PasswordLengthSettingTests
{
    [Theory] [InlineData(8, "abcdefgh1", true)] [InlineData(12, "abcdefgh1", false)] [InlineData(12, "abcdefghij12", true)]
    public void TheMinimumLengthFollowsTheSetting(int min, string password, bool ok)
    {
        if (ok) AuthService.ValidatePassword(password, "someone", min);
        else Assert.Contains("at least 12", Assert.Throws<AuthException>(() => AuthService.ValidatePassword(password, "someone", min)).Message);
    }

    [Theory] [InlineData("2", 8)] [InlineData("20", 20)] [InlineData("500", 64)] [InlineData("abc", 8)]
    public void TheSettingCanRaiseTheMinimumButNeverLowerItBelowEight(string value, int expected)
        => Assert.Equal(expected, AuthService.MinPasswordFrom(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:MinPasswordLength"] = value }).Build()));
}

public sealed class SignInMessageTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task TheMessageShowsOnTheSignInPageOnceSaved_AndPasswordRulesFollowTheSetting()
    {
        var admin = await _f.SignedIn("alex");
        Assert.Equal(JsonValueKind.Null, (await _f.Client().GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("loginMessage").ValueKind);
        var save = await admin.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, string?> { ["General:LoginMessage"] = "Ask Alex for an account", ["Auth:MinPasswordLength"] = "14" } });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal("Ask Alex for an account", (await _f.Client().GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("loginMessage").GetString());
        var tooShort = await admin.PostAsJsonAsync("/api/users", new { username = "kid", password = "only-12-chars" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("at least 14", (await tooShort.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new { username = "kid", password = "a-long-enough-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, string?> { ["General:LoginMessage"] = new string('x', 301) } })).StatusCode);
    }
}
