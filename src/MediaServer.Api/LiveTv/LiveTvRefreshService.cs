namespace MediaServer.Api.LiveTv;

/// <summary>
/// Loads the channel list and guide shortly after startup, then keeps them fresh. The settings are re-read every cycle, so
/// turning Live TV on (or changing the interval) in the web UI takes effect without a restart.
/// </summary>
public sealed class LiveTvRefreshService(LiveTvService live, IConfiguration config, TimeProvider time, ILogger<LiveTvRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue("LiveTv:RefreshDelaySeconds", 8)), ct);
            DateTimeOffset? last = null;
            while (!ct.IsCancellationRequested)
            {
                var minutes = Math.Max(5, config.GetValue("LiveTv:RefreshMinutes", 360));
                var due = last is null || time.GetUtcNow() - last >= TimeSpan.FromMinutes(minutes);
                if (due && live.IsConfigured && config.GetValue("LiveTv:AutoRefresh", true))
                {
                    last = time.GetUtcNow();
                    try { await live.RefreshAsync(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Scheduled Live TV refresh failed: {Error}", ex.Message); }
                }
                await Task.Delay(TimeSpan.FromSeconds(config.GetValue("LiveTv:PollSeconds", 20)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
