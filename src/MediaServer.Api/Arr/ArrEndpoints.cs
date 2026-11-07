using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Security;

namespace MediaServer.Api.Arr;

public sealed record ArrAddBody(string? Source, int Id, int QualityProfileId, string? RootFolder);

/// <summary>
/// The address Radarr and Sonarr call when they import a file ("Connect → Webhook"). The long random token in the path is the credential,
/// like a signed stream link: whoever has it can only ask for a library scan.
/// </summary>
public sealed class ArrWebhook(Database db, TimeProvider time, SecretBox? box = null)
{
    private const string Key = "auth.arr_webhook";
    private int _scheduled;

    public string Token()
    {
        using var c = db.Open();
        var stored = Database.Scalar(c, $"SELECT value FROM settings WHERE key='{Key}'") as string;
        var plain = string.IsNullOrEmpty(stored) ? null : box is null ? stored : box.TryUnprotect(stored, Key);
        return string.IsNullOrEmpty(plain) ? Reset() : plain;
    }

    /// <summary>A new token; the old address stops working.</summary>
    public string Reset()
    {
        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var c = db.Open();
        Database.Execute(c, null, """
            INSERT INTO settings(key,value,updated_at) VALUES(@k,@v,@t)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
            """, ("@k", Key), ("@v", box?.Protect(fresh, Key) ?? fresh), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return fresh;
    }

    public bool Check(string candidate) =>
        candidate.Length == 48 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Token()));

    /// <summary>
    /// Several events arrive together (a season import). Wait a moment for the app to finish moving files, then scan once; if a scan is
    /// already running, start another when it ends so nothing that arrived mid-scan is missed.
    /// </summary>
    public void ScheduleScan(ScanCoordinator scans, CancellationToken stopping, TimeSpan? delay = null)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(8), stopping);
                Interlocked.Exchange(ref _scheduled, 0);   // events from here on schedule the next scan
                for (var i = 0; i < 200 && !stopping.IsCancellationRequested; i++)
                {
                    if (scans.TryStart() is { } running) { await running; return; }
                    await Task.Delay(3000, stopping);
                }
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _scheduled, 0); }
        }, stopping);
    }
}

public static class ArrEndpoints
{
    // Events that mean the files on disk changed.
    private static readonly HashSet<string> FileEvents = new(StringComparer.OrdinalIgnoreCase)
        { "Download", "Rename", "MovieFileDelete", "EpisodeFileDelete", "MovieDelete", "SeriesDelete", "Upgrade", "ImportComplete" };

    private static ArrKind? Kind(string? s) => Enum.TryParse<ArrKind>(s, true, out var k) ? k : null;

    public static void MapArrEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/arr");   // administrators only (AuthMiddleware)

        api.MapGet("/status", (ArrService arr, ArrWebhook hook) => Results.Ok(new { radarr = arr.IsConfigured(ArrKind.Radarr), sonarr = arr.IsConfigured(ArrKind.Sonarr), webhookPath = $"/api/arr/webhook/{hook.Token()}" }));
        api.MapPost("/webhook/reset", (ArrWebhook hook) => Results.Ok(new { webhookPath = $"/api/arr/webhook/{hook.Reset()}" }));

        api.MapGet("/queue", (ArrService arr, CancellationToken ct) => Run(async () => Results.Ok(new { items = await arr.QueueAsync(ct) })));
        api.MapGet("/upcoming", (ArrService arr, int? days, CancellationToken ct) => Run(async () => Results.Ok(new { items = await arr.UpcomingAsync(days ?? 14, ct) })));

        api.MapGet("/lookup", (ArrService arr, string? term, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(term) || term.Length > 100 ? Task.FromResult(Results.BadRequest(new { error = "Type a title to look for" }))
                : Run(async () => Results.Ok(new { items = await arr.LookupAsync(term.Trim(), ct) })));

        api.MapGet("/options/{kind}", (string kind, ArrService arr, CancellationToken ct) =>
            Kind(kind) is not { } k ? Task.FromResult(Results.NotFound()) : Run(async () => Results.Ok(await arr.OptionsAsync(k, ct))));

        api.MapPost("/add", (ArrAddBody body, ArrService arr, CancellationToken ct) =>
            Kind(body.Source) is not { } k || body.Id <= 0 || string.IsNullOrWhiteSpace(body.RootFolder) ? Task.FromResult(Results.BadRequest(new { error = "Choose a title, a quality and a folder" }))
                : Run(async () => { await arr.AddAsync(k, body.Id, body.QualityProfileId, body.RootFolder!, ct); return Results.Ok(new { added = true }); }));

        api.MapGet("/art", async (string? u, ArrArt art, HttpContext ctx, CancellationToken ct) =>
        {
            if (u is null || ArrArt.Allowed(u) is null) return Results.NotFound();
            try
            {
                if (await art.GetAsync(u, ct) is not { } hit) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "private, max-age=604800";
                return Results.File(hit.File, hit.ContentType);
            }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
        });

        // Called by Radarr/Sonarr themselves, with no cookie: the token in the address is the credential (see AuthMiddleware).
        app.MapPost("/api/arr/webhook/{token}", async (string token, HttpContext ctx, ArrWebhook hook, ScanCoordinator scans, IHostApplicationLifetime life) =>
        {
            if (!hook.Check(token)) return Results.NotFound();
            if (ctx.Request.ContentLength > 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            string? evt = null;
            try { using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); evt = doc.RootElement.TryGetProperty("eventType", out var e) ? e.GetString() : null; }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Expected JSON" }); }
            if (evt is not null && FileEvents.Contains(evt)) hook.ScheduleScan(scans, life.ApplicationStopping);
            return Results.Ok(new { received = evt ?? "unknown", scan = evt is not null && FileEvents.Contains(evt) });
        });
    }

    private static async Task<IResult> Run(Func<Task<IResult>> work)
    {
        try { return await work(); }
        catch (ArrException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status == System.Net.HttpStatusCode.Conflict ? 409 : 502); }
    }
}
