using MediaServer.Api.Metadata;

namespace MediaServer.Api.Library;

/// <summary>
/// Runs library scans independently of any HTTP request (a proxy timeout or closed tab must not abort a long first scan),
/// follows each scan with TMDB enrichment when configured, and remembers the outcome for the UI.
/// </summary>
public sealed class ScanCoordinator(LibraryScanner scanner, IServiceScopeFactory scopes, IHostApplicationLifetime life, TimeProvider time, ILogger<ScanCoordinator> log)
{
    private readonly object _gate = new();
    private Task<ScanResult?>? _running;

    public bool IsScanning { get { lock (_gate) return _running is { IsCompleted: false }; } }
    public ScanResult? LastResult { get; private set; }
    public DateTimeOffset? LastFinished { get; private set; }
    public string? LastError { get; private set; }

    /// <returns>The running scan, or null if one is already in progress.</returns>
    public Task<ScanResult?>? TryStart()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return null;
            return _running = Task.Run(RunAsync);
        }
    }

    private async Task<ScanResult?> RunAsync()
    {
        try
        {
            LastError = null;
            var result = await scanner.ScanAsync(life.ApplicationStopping);
            if (result is not null) LastResult = result;
            await EnrichAsync();
            return result;
        }
        catch (OperationCanceledException) when (life.ApplicationStopping.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message; // scanner messages never contain server paths
            log.LogError(ex, "Library scan failed");
            return null;
        }
        finally { LastFinished = time.GetUtcNow(); }
    }

    private async Task EnrichAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MetadataService>().EnrichAsync(life.ApplicationStopping);
        }
        catch (TmdbNotConfiguredException) { /* metadata is optional */ }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Enrichment after scan failed: {Error}", ex.Message); }
    }
}

/// <summary>
/// Scans shortly after startup and then periodically, so new files show up without anyone running a command. Settings are
/// re-read every cycle, so changing them in the web UI takes effect without a restart.
/// </summary>
public sealed class AutoScanService(ScanCoordinator coordinator, IConfiguration config, TimeProvider time, LibraryFolders folders) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue("Library:AutoScanDelaySeconds", 5)), ct);
            DateTimeOffset? last = null;
            while (!ct.IsCancellationRequested)
            {
                var minutes = config.GetValue("Library:ScanIntervalMinutes", 360);
                var enabled = config.GetValue("Library:AutoScan", true) && folders.HasAny;
                var due = last is null || (minutes > 0 && time.GetUtcNow() - last >= TimeSpan.FromMinutes(minutes));
                if (enabled && due)
                {
                    last = time.GetUtcNow();
                    if (coordinator.TryStart() is { } t) await t.WaitAsync(ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(config.GetValue("Library:PollSeconds", 30)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
