using MediaServer.Api.Arr;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Endpoints;
using MediaServer.Api.Hosting;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probing;
using MediaServer.Api.Recording;
using MediaServer.Api.Processes;
using System.Security.Cryptography.X509Certificates;
using MediaServer.Api.Security;
using MediaServer.Api.Settings;
using MediaServer.Api.Streaming;

var builder = WebApplication.CreateBuilder(args);
var logStore = new MemoryLogStore();
builder.Logging.AddProvider(new MemoryLogProvider(logStore));
builder.Services.AddSingleton(logStore);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<IMediaResolver, DbMediaResolver>();
builder.Services.AddSingleton<IProcessRunner>(sp => new ProcessRunner(sp.GetRequiredService<IConfiguration>().GetValue("Tools:MaxConcurrency", 4)));
builder.Services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddSingleton<LibraryFolders>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<LibraryQueries>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<ChapterService>();
builder.Services.AddSingleton<RecordingService>();
builder.Services.AddSingleton<LiveListService>();
builder.Services.AddHostedService<RecordingWorker>();
builder.Services.AddSingleton<TrickplayService>();
// One long-lived client for the IPTV source (playlist, guide, logos): pooled connections, transparent gzip.
builder.Services.AddKeyedSingleton("livetv", (_, _) => new HttpClient(new SocketsHttpHandler
    { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton(sp => new LiveTvService(sp.GetRequiredService<Database>(), sp.GetRequiredKeyedService<HttpClient>("livetv"),
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LiveTvService>>(), sp.GetRequiredService<SecretBox>()));
builder.Services.AddSingleton(sp => new LiveLogoCache(sp.GetRequiredKeyedService<HttpClient>("livetv"), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<LiveTvRefreshService>();
builder.Services.AddSingleton<ProgressService>();
builder.Services.AddSingleton<ActivityService>();
builder.Services.AddSingleton<WatchlistService>();
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUserContext, HttpUserContext>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<DbSettingsProvider>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddHostedService<AutoScanService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<TmdbClient>(c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers(); // URLs may carry api_key
builder.Services.AddHttpClient<ArrService>(c => c.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers();
builder.Services.AddSingleton<ArrWebhook>();
builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<MetadataService>();
builder.Services.AddSingleton(sp => HlsOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new HlsSessionManager(sp.GetRequiredService<IMediaResolver>(), sp.GetRequiredService<IMediaProbe>(), sp.GetRequiredService<HlsOptions>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<HlsSessionManager>>(), () => HlsOptions.From(cfg));   // stream limit, encoder... follow the settings page
});
builder.Services.AddHostedService<HlsReaper>();

// Optional built-in HTTPS (for running without a reverse proxy): the HTTP port(s) keep working, and an HTTPS port is added.
X509Certificate2? httpsCert = null;
var httpsPort = builder.Configuration.GetValue("Https:Port", 8443);
if (builder.Configuration.GetValue("Https:Enabled", false))
{
    httpsCert = HttpsCertificate.Load(builder.Configuration);
    var httpPorts = (builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"] ?? "http://+:8080").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(u => Uri.TryCreate(u.Replace("+", "localhost").Replace("*", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var uri) ? uri : null)
        .Where(u => u is { Scheme: "http" }).Select(u => u!.Port).Distinct().ToList();
    builder.WebHost.ConfigureKestrel(o =>
    {
        foreach (var p in httpPorts) o.ListenAnyIP(p);
        o.ListenAnyIP(httpsPort, lo => lo.UseHttps(httpsCert));
    });
}
builder.Services.AddSingleton(new HttpsStatus(httpsCert is not null, httpsPort, httpsCert is not null && builder.Configuration["Https:CertificatePath"] is not { Length: > 0 }, httpsCert));

var app = builder.Build();
app.UseApiErrorHandling();
// An API address that matches nothing answers with a small JSON 404 rather than an empty page.
app.Use(async (ctx, next) =>
{
    await next();
    if (ctx.Response.StatusCode == StatusCodes.Status404NotFound && !ctx.Response.HasStarted && ctx.Response.ContentLength is null && string.IsNullOrEmpty(ctx.Response.ContentType) && ctx.Request.Path.StartsWithSegments("/api"))
        await ctx.Response.WriteAsJsonAsync(new { error = "Not found" });
});
app.UseMiddleware<MediaServer.Api.Hosting.SecurityHeadersMiddleware>();
app.Services.GetRequiredService<Database>().Migrate();
var sealedNow = SecretMigrator.Run(app.Services.GetRequiredService<Database>(), app.Services.GetRequiredService<SecretBox>());   // encrypt anything an older version left readable
if (sealedNow != (0, 0)) app.Logger.LogInformation("Encrypted {Settings} saved settings and {Channels} channel entries that were stored as plain text", sealedNow.Settings, sealedNow.Channels);
((IConfigurationBuilder)app.Configuration).Add(app.Services.GetRequiredService<DbSettingsProvider>()); // UI-edited settings override the deployment's

app.UseDefaultFiles();
if (httpsCert is not null && app.Configuration.GetValue("Https:Hsts", false))
    app.Use(async (ctx, next) => { if (ctx.Request.IsHttps) ctx.Response.Headers.StrictTransportSecurity = "max-age=31536000"; await next(); });
if (httpsCert is not null && app.Configuration.GetValue("Https:RedirectHttp", false))
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps && !ctx.Request.Path.StartsWithSegments("/health")) { ctx.Response.Redirect($"https://{ctx.Request.Host.Host}:{httpsPort}{ctx.Request.Path}{ctx.Request.QueryString}", permanent: false); return; }
        await next();
    });
app.UseStaticFiles(); // the web client (wwwroot/): public, it contains no data
app.UseMiddleware<AuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// Readiness: can the server actually do its job (reach its database)? Used by orchestrators; public and detail-free.
app.MapGet("/health/ready", (Database db) =>
{
    try { using var c = db.Open(); return Database.Scalar(c, "SELECT 1") is not null ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
    catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { return Results.StatusCode(503); }
});

// The web app manifest (what "install this app" uses). Public, like the page itself; the name follows the server-name setting.
app.MapGet("/manifest.webmanifest", (AuthService auth) => Results.Json(new
{
    name = auth.ServerName, short_name = auth.ServerName.Length > 12 ? auth.ServerName[..12] : auth.ServerName,
    description = "Your own media server: movies, shows and live TV.",
    start_url = "/", scope = "/", display = "standalone", orientation = "any", background_color = "#0a0c10", theme_color = "#0a0c10",
    icons = new object[]
    {
        new { src = "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
        new { src = "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
        new { src = "/icons/icon-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
    },
}, contentType: "application/manifest+json"));

// GET and HEAD (MapGet does not cover HEAD automatically for handlers that write the body themselves).
app.MapMethods("/stream/{id:long}", ["GET", "HEAD"], async (long id, HttpContext ctx, IMediaResolver media) =>
{
    var path = media.ResolvePath(id);
    if (path is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    await RangeFileStreamer.ServeAsync(ctx, path);
});

app.MapGet("/api/probe/{id:long}", async (long id, IMediaResolver media, IMediaProbe probe, CancellationToken ct) =>
{
    var path = media.ResolvePath(id);
    if (path is null) return Results.NotFound();
    try { return Results.Ok(await probe.ProbeAsync(path, ct)); }
    catch (ProbeFailedException ex) { return Results.Problem(ex.Message, statusCode: 422); }
});

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapMetadataEndpoints();
app.MapLibraryEndpoints();
app.MapMediaEndpoints();
app.MapClientEventEndpoints();
app.MapLiveTvEndpoints();
app.MapSystemEndpoints();
app.MapActivityEndpoints();
app.MapArrEndpoints();
app.MapHttpsEndpoints();
app.MapSettingsEndpoints();
app.MapRecordingEndpoints();
app.MapMaintenanceEndpoints();
app.MapFolderEndpoints();
app.MapHlsEndpoints();

app.Run();

public partial class Program; // for WebApplicationFactory in tests
