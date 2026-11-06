using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using MediaServer.Api.Maintenance;
using MediaServer.Api.LiveTv;
using MediaServer.Api.Endpoints;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;
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
// One long-lived client for the IPTV source (playlist, guide, logos): pooled connections, transparent gzip.
builder.Services.AddKeyedSingleton("livetv", (_, _) => new HttpClient(new SocketsHttpHandler
    { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton(sp => new LiveTvService(sp.GetRequiredService<Database>(), sp.GetRequiredKeyedService<HttpClient>("livetv"),
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LiveTvService>>()));
builder.Services.AddSingleton(sp => new LiveLogoCache(sp.GetRequiredKeyedService<HttpClient>("livetv"), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<LiveTvRefreshService>();
builder.Services.AddSingleton<ProgressService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<DbSettingsProvider>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddHostedService<AutoScanService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<TmdbClient>(c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers(); // URLs may carry api_key
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

var app = builder.Build();
app.Services.GetRequiredService<Database>().Migrate();
((IConfigurationBuilder)app.Configuration).Add(app.Services.GetRequiredService<DbSettingsProvider>()); // UI-edited settings override the deployment's

app.UseDefaultFiles();
app.UseStaticFiles(); // the web client (wwwroot/): public, it contains no data
app.UseMiddleware<AuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
app.MapMetadataEndpoints();
app.MapLibraryEndpoints();
app.MapMediaEndpoints();
app.MapClientEventEndpoints();
app.MapLiveTvEndpoints();
app.MapSystemEndpoints();
app.MapSettingsEndpoints();
app.MapMaintenanceEndpoints();
app.MapFolderEndpoints();
app.MapHlsEndpoints();

app.Run();

public partial class Program; // for WebApplicationFactory in tests
