using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.Notifications;

namespace MediaServer.Tests;

public sealed class NotificationTests : IDisposable
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();
    private static IConfiguration Cfg(params (string, string?)[] kv) => new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => x.Item2)).Build();
    private static NotificationService Svc(Handler h, params (string, string?)[] kv) => new(new HttpClient(h), Cfg(kv), NullLogger<NotificationService>.Instance);

    [Fact] public void NotConfigured_WantsNothing()
    {
        Assert.False(Svc(new Handler((_, _) => new(HttpStatusCode.OK))).Wants(NotifyEvent.Recording));
        Assert.False(Svc(new Handler((_, _) => new(HttpStatusCode.OK)), ("Notify:Service", "gotify")).IsConfigured);                       // no address
        Assert.False(Svc(new Handler((_, _) => new(HttpStatusCode.OK)), ("Notify:Service", "gotify"), ("Notify:Url", "ftp://x")).IsConfigured);
        Assert.False(Svc(new Handler((_, _) => new(HttpStatusCode.OK)), ("Notify:Service", "sms"), ("Notify:Url", "http://x")).IsConfigured);    // unknown service
    }

    [Fact] public async Task Gotify_PostsJson_WithTheTokenInAHeaderNotTheAddress()
    {
        var h = new Handler((_, _) => new(HttpStatusCode.OK));
        var r = await Svc(h, ("Notify:Service", "gotify"), ("Notify:Url", "http://gotify:80/"), ("Notify:Token", "SECRET")).SendAsync(NotifyEvent.Test, "Hi", "There", default);
        Assert.True(r.Ok);
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("http://gotify/message", req.RequestUri!.ToString());
        Assert.Equal("SECRET", req.Headers.GetValues("X-Gotify-Key").Single());
        Assert.DoesNotContain("SECRET", req.RequestUri!.ToString());
        Assert.Contains("\"title\":\"Hi\"", body); Assert.Contains("\"message\":\"There\"", body);
    }

    [Fact] public async Task Ntfy_PostsThePlainMessage_AndSurvivesNonAsciiTitles()
    {
        var h = new Handler((_, _) => new(HttpStatusCode.OK));
        var r = await Svc(h, ("Notify:Service", "ntfy"), ("Notify:Url", "https://ntfy.sh/topic"), ("Notify:Token", "tk")).SendAsync(NotifyEvent.Recording, "Café “Ünï”", "Body ✓", default);
        Assert.True(r.Ok);
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("Body ✓", body);
        Assert.Equal("Caf? ?Ünï?".Replace("Ü", "?").Replace("ï", "?"), req.Headers.GetValues("Title").Single());
        Assert.Equal("Bearer tk", req.Headers.Authorization!.ToString());
    }

    [Fact] public async Task Webhook_PostsJsonWithTheEventAndVersion()
    {
        var h = new Handler((_, _) => new(HttpStatusCode.OK));
        await Svc(h, ("Notify:Service", "webhook"), ("Notify:Url", "https://hooks.example/x")).SendAsync(NotifyEvent.NewTitles, "T", "M", default);
        var (_, body) = Assert.Single(h.Seen);
        Assert.Contains("\"event\":\"newtitles\"", body); Assert.Contains("\"app\":\"Marquee\"", body); Assert.Contains("\"version\":", body);
    }

    [Fact] public async Task Failures_NeverThrow_AndNeverRevealTheAddress()
    {
        var bad = new Handler((_, _) => new(HttpStatusCode.Unauthorized));
        var svc = Svc(bad, ("Notify:Service", "gotify"), ("Notify:Url", "http://secret-host:80"), ("Notify:Token", "t"));
        var r = await svc.SendAsync(NotifyEvent.Test, "a", "b", default);
        Assert.False(r.Ok); Assert.DoesNotContain("secret-host", r.Message); Assert.Contains("401", r.Message);
        await svc.NotifyAsync(NotifyEvent.Test, "a", "b");   // the fire-and-forget form swallows it
        var down = new Handler((_, _) => throw new HttpRequestException("boom http://secret-host"));
        var r2 = await Svc(down, ("Notify:Service", "gotify"), ("Notify:Url", "http://secret-host"), ("Notify:Token", "t")).SendAsync(NotifyEvent.Test, "a", "b", default);
        Assert.False(r2.Ok); Assert.DoesNotContain("secret-host", r2.Message);
    }

    [Fact] public async Task EachKindCanBeSwitchedOff()
    {
        var h = new Handler((_, _) => new(HttpStatusCode.OK));
        var svc = Svc(h, ("Notify:Service", "webhook"), ("Notify:Url", "https://x.example"), ("Notify:OnRecording", "false"));
        await svc.NotifyAsync(NotifyEvent.Recording, "a", "b"); Assert.Empty(h.Seen);
        await svc.NotifyAsync(NotifyEvent.NewTitles, "a", "b"); Assert.Single(h.Seen);
    }

    [Theory]
    [InlineData(5, 0, 0, 0, 0, 40, true)] [InlineData(500, 0, 0, 0, 0, 0, false)] [InlineData(0, 0, 0, 0, 0, 40, false)] [InlineData(3, 0, 1, 0, 0, 0, true)]
    public void NewFiles_AreAnnounced_ButNotTheFirstImportOfAnEmptyLibrary(int added, int updated, int moved, int restored, int removed, int unchanged, bool expected)
        => Assert.Equal(expected, ScanCoordinator.ShouldAnnounce(new ScanResult(added, updated, moved, restored, removed, unchanged, 0)));

    private const string OneRelease = """[ { "tag_name": "v999.0.0", "draft": false, "html_url": "https://github.com/x/y/releases/tag/v999.0.0", "assets": [] } ]""";

    [Fact] public async Task UpdateNotifier_SaysSoOncePerVersion()
    {
        var calls = new Handler((req, _) => req.RequestUri!.Host == "api.github.com" ? new(HttpStatusCode.OK) { Content = new StringContent(OneRelease, Encoding.UTF8, "application/json") } : new(HttpStatusCode.OK));
        var cfg = Cfg(("Notify:Service", "webhook"), ("Notify:Url", "https://hooks.example/x"));
        var http = new HttpClient(calls);
        var updates = new UpdateService(http, cfg, TimeProvider.System, NullLogger<UpdateService>.Instance);
        var notifier = new UpdateNotifier(updates, new NotificationService(http, cfg, NullLogger<NotificationService>.Instance), _t.Db, cfg, NullLogger<UpdateNotifier>.Instance);
        await notifier.CheckOnceAsync(default); await notifier.CheckOnceAsync(default);
        Assert.Single(calls.Seen, s => s.Req.RequestUri!.Host == "hooks.example");
        Assert.Contains("999.0.0", calls.Seen.Single(s => s.Req.RequestUri!.Host == "hooks.example").Body);
    }

    [Fact] public async Task UpdateNotifier_StaysQuiet_WhenNotificationsAreOff()
    {
        var calls = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(OneRelease) });
        var http = new HttpClient(calls); var cfg = Cfg();
        var notifier = new UpdateNotifier(new UpdateService(http, cfg, TimeProvider.System, NullLogger<UpdateService>.Instance), new NotificationService(http, cfg, NullLogger<NotificationService>.Instance), _t.Db, cfg, NullLogger<UpdateNotifier>.Instance);
        await notifier.CheckOnceAsync(default);
        Assert.Empty(calls.Seen);
    }
}
