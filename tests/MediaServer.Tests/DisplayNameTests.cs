using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Auth;

namespace MediaServer.Tests;

public sealed class DisplayNameTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    private static async Task<JsonElement> State(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/auth/state");

    [Fact]
    public async Task Setup_CanTakeAFriendlyName_AndTheUsernameStaysTheSignInName()
    {
        var c = _f.Client();
        var res = await c.PostAsJsonAsync("/api/auth/setup", new { username = "alex", password = "a-long-password-123", displayName = "  Alex the Great " });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("alex", "Alex the Great"), (j.GetProperty("user").GetString(), j.GetProperty("name").GetString()));
        var st = await State(c);
        Assert.Equal(("alex", "Alex the Great", "Alex the Great"), (st.GetProperty("user").GetString(), st.GetProperty("name").GetString(), st.GetProperty("displayName").GetString()));
        var login = await (await _f.Client().PostAsJsonAsync("/api/auth/login", new { username = "alex", password = "a-long-password-123" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alex the Great", login.GetProperty("name").GetString());                      // after signing in again, the name is still there
    }

    [Fact]
    public async Task WithoutAFriendlyName_TheUsernameIsUsed()
    {
        var c = await _f.SignedIn("alex");
        var st = await State(c);
        Assert.Equal("alex", st.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task TheNameCanBeChangedAndCleared()
    {
        var c = await _f.SignedIn("alex");
        var set = await c.PutAsJsonAsync("/api/auth/profile", new { displayName = "Lex" });
        Assert.Equal("Lex", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal("Lex", (await State(c)).GetProperty("name").GetString());
        var cleared = await (await c.PutAsJsonAsync("/api/auth/profile", new { displayName = "   " })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alex", cleared.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, (await State(c)).GetProperty("displayName").ValueKind);
    }

    [Theory] [InlineData("line\nbreak")] [InlineData("tab\there")] [InlineData("\u0007bell")]
    public async Task ControlCharacters_AreRefused(string name)
    {
        var c = await _f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/auth/profile", new { displayName = name })).StatusCode);
    }

    [Fact] public async Task OverlongNames_AreRefused()
    {
        var c = await _f.SignedIn("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/auth/profile", new { displayName = new string('x', 41) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/auth/profile", new { displayName = new string('x', 40) })).StatusCode);
    }

    [Fact] public async Task Setup_RefusesABadNameBeforeCreatingAnything()
    {
        var res = await _f.Client().PostAsJsonAsync("/api/auth/setup", new { username = "alex", password = "a-long-password-123", displayName = new string('y', 90) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await State(_f.Client())).GetProperty("setupRequired").GetBoolean());
    }

    [Fact] public async Task ChangingTheNameNeedsSignIn()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().PutAsJsonAsync("/api/auth/profile", new { displayName = "x" })).StatusCode);

    [Fact] public async Task HtmlInANameIsJustText_StoredAndReturnedVerbatim()
    {
        var c = await _f.SignedIn("alex");
        var j = await (await c.PutAsJsonAsync("/api/auth/profile", new { displayName = "<img src=x onerror=alert(1)>" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("<img src=x onerror=alert(1)>", j.GetProperty("name").GetString());            // the page uses textContent, so this can never run
    }

    [Fact] public void AuthUser_FallsBackToTheUsername()
    {
        Assert.Equal("alex", new AuthUser(1, "alex").Name);
        Assert.Equal("alex", new AuthUser(1, "alex", "  ").Name);
        Assert.Equal("Lex", new AuthUser(1, "alex", "Lex").Name);
    }
}
