using System.Net.Http.Json;
using System.Text;
using MediaServer.Api.Maintenance;

namespace MediaServer.Api.Notifications;

/// <summary>The things Marquee can tell you about. Each can be switched off in Settings → Notifications.</summary>
public enum NotifyEvent { Recording, NewTitles, Update, Test }

/// <summary>
/// Sends a short message to Gotify, ntfy or any webhook you choose. It never blocks or breaks what triggered it: a failure is logged
/// (without the address or token) and forgotten. Titles and counts are all that leaves the server; nothing about your files or paths.
/// </summary>
public sealed class NotificationService(HttpClient http, IConfiguration config, ILogger<NotificationService> log)
{
    public string Service => (config["Notify:Service"] ?? "off").ToLowerInvariant() is var s && s is "gotify" or "ntfy" or "webhook" ? s : "off";
    private string? Url => config["Notify:Url"] is { Length: > 0 } u ? u : null;
    private string? Token => config["Notify:Token"] is { Length: > 0 } t ? t : null;
    public bool IsConfigured => Service != "off" && Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public bool Wants(NotifyEvent e) => IsConfigured && e switch
    {
        NotifyEvent.Recording => config.GetValue("Notify:OnRecording", true),
        NotifyEvent.NewTitles => config.GetValue("Notify:OnNewTitles", true),
        NotifyEvent.Update => config.GetValue("Notify:OnUpdate", true),
        _ => true,
    };

    /// <summary>Sends if this kind of message is wanted. Never throws.</summary>
    public async Task NotifyAsync(NotifyEvent kind, string title, string message, CancellationToken ct = default)
    {
        if (!Wants(kind)) return;
        var (ok, _) = await SendAsync(kind, title, message, ct);
        _ = ok;
    }

    /// <returns>Whether it was accepted, and a short reason if not (safe to show: it never includes the address or token).</returns>
    public async Task<(bool Ok, string Message)> SendAsync(NotifyEvent kind, string title, string message, CancellationToken ct)
    {
        if (!IsConfigured) return (false, "Notifications are not set up.");
        title = Clip(title, 120); message = Clip(message, 500);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = Build(Service, new Uri(Url!), Token, kind, title, message);
            using var res = await http.SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode) return (true, "Sent.");
            log.LogInformation("Notification service answered {Status}", (int)res.StatusCode);
            return (false, $"The notification service answered with an error ({(int)res.StatusCode}). Check the address and token.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("Could not send a notification: {Reason}", ex.GetType().Name);   // the exception text can contain the address
            return (false, "Could not reach the notification service.");
        }
    }

    internal static HttpRequestMessage Build(string service, Uri url, string? token, NotifyEvent kind, string title, string message)
    {
        HttpRequestMessage req;
        switch (service)
        {
            case "gotify":   // POST {base}/message, app token in a header so it never lands in a URL or a log
                req = new HttpRequestMessage(HttpMethod.Post, new Uri(url.AbsoluteUri.TrimEnd('/') + "/message")) { Content = JsonContent.Create(new { title, message, priority = 5 }) };
                if (token is not null) req.Headers.Add("X-Gotify-Key", token);
                break;
            case "ntfy":     // POST {topic url}, the message is the body
                req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
                req.Headers.Add("Title", AsciiHeader(title));
                req.Headers.Add("Tags", kind switch { NotifyEvent.Recording => "movie_camera", NotifyEvent.NewTitles => "clapper", NotifyEvent.Update => "arrow_up", _ => "bell" });
                if (token is not null) req.Headers.Authorization = new("Bearer", token);
                break;
            default:         // a plain JSON webhook
                req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { @event = kind.ToString().ToLowerInvariant(), title, message, app = "Marquee", version = AppVersion.Current }) };
                if (token is not null) req.Headers.Authorization = new("Bearer", token);
                break;
        }
        req.Headers.UserAgent.ParseAdd("Marquee");
        return req;
    }

    /// <summary>HTTP headers are ASCII: anything else is replaced so a title with an accent cannot make the request invalid.</summary>
    internal static string AsciiHeader(string s) => new(s.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
    private static string Clip(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
}

/// <summary>Checks for a new release every half day and says so once per version (if the update check and the notification are both on).</summary>
public sealed class UpdateNotifier(UpdateService updates, NotificationService notify, MediaServer.Api.Data.Database db, IConfiguration config, ILogger<UpdateNotifier> log) : BackgroundService
{
    private const string Key = "internal.notified_update";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(config.GetValue("Updates:FirstCheckMinutes", 3)), ct);
            while (!ct.IsCancellationRequested)
            {
                try { await CheckOnceAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation("Update notification failed: {Reason}", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromHours(12), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task CheckOnceAsync(CancellationToken ct)
    {
        if (!notify.Wants(NotifyEvent.Update)) return;
        var u = await updates.CheckAsync(false, ct);
        if (!u.Newer || u.Latest is null || LastNotified() == u.Latest) return;
        await notify.NotifyAsync(NotifyEvent.Update, "Marquee update available", $"Version {u.Latest} is out. You are running {u.Current}.", ct);
        Remember(u.Latest);
    }

    private string? LastNotified()
    {
        using var c = db.Open();
        return MediaServer.Api.Data.Database.Query(c, "SELECT value FROM settings WHERE key=@k", r => r.GetString(0), ("@k", Key)).FirstOrDefault();
    }

    private void Remember(string version)
    {
        using var c = db.Open();
        MediaServer.Api.Data.Database.Execute(c, null, "INSERT INTO settings(key, value, updated_at) VALUES(@k,@v,@t) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at", ("@k", Key), ("@v", version), ("@t", DateTime.UtcNow.ToString("O")));
    }
}
