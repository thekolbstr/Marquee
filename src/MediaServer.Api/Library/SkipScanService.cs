namespace MediaServer.Api.Library;

/// <summary>
/// Runs <see cref="SkipDetector"/> in the background: a little after start-up, after each library scan that found something new, and now and then.
/// Detection is gentle (one title at a time) and never delays playback or scanning.
/// </summary>
public sealed class SkipScanService(SkipDetector detector, IConfiguration config, ILogger<SkipScanService> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Asks for a pass soon (after a library scan, or from the Maintenance page).</summary>
    public void Request() { try { _wake.Release(); } catch (SemaphoreFullException) { /* one is already pending */ } }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(config.GetValue("Skip:StartDelaySeconds", 90)), stop); } catch (OperationCanceledException) { return; }
        var first = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (first || detector.AutoScan) await detector.RunAsync(stop);
                first = false;
                // Wait for a request, or six hours.
                await _wake.WaitAsync(TimeSpan.FromHours(6), stop);
                await Task.Delay(TimeSpan.FromSeconds(20), stop);            // let a burst of imports settle
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning("Intro/credits pass failed: {Error}", ex.Message); await Task.Delay(TimeSpan.FromMinutes(5), stop).ContinueWith(_ => { }); }
        }
    }
}
