namespace MediaServer.Api.Library;

/// <summary>Finds and downloads subtitles for a title from the configured provider, with a cap so one browser cannot use up the day's allowance.</summary>
public sealed class SubtitleDownloads(SubtitleService subs, ISubtitleProvider provider, TimeProvider time)
{
    public const int MaxPerHour = 20;
    private readonly Queue<DateTimeOffset> _recent = new();

    public bool IsConfigured => provider.IsConfigured;

    public async Task<IReadOnlyList<SubtitleResult>> SearchAsync(long mediaId, string language, CancellationToken ct)
    {
        var q = subs.QueryFor(mediaId) ?? throw new SubtitleProviderException("That title is not in the library.");
        return await provider.SearchAsync(q, language, ct);
    }

    /// <returns>The id of the new caption track.</returns>
    public async Task<string> DownloadAsync(long mediaId, string fileId, string language, CancellationToken ct)
    {
        if (subs.QueryFor(mediaId) is null) throw new SubtitleProviderException("That title is not in the library.");
        if (!TryTake()) throw new SubtitleProviderException("That is a lot of downloads in a short time. Try again in a few minutes.");
        var bytes = await provider.DownloadAsync(fileId, ct);
        return subs.SaveDownloaded(mediaId, language, fileId, bytes) ?? throw new SubtitleProviderException("That did not look like a subtitle file, or this title already has the most it can keep. Remove one first.");
    }

    private bool TryTake()
    {
        lock (_recent)
        {
            var now = time.GetUtcNow();
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromHours(1)) _recent.Dequeue();
            if (_recent.Count >= MaxPerHour) return false;
            _recent.Enqueue(now); return true;
        }
    }
}
