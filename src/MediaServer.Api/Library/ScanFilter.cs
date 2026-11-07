namespace MediaServer.Api.Library;

/// <summary>
/// What a scan leaves out: files smaller than a minimum (samples, trailers) and anything with one of the "ignore" words in its folder or file name
/// (extras, featurettes...). Files that stop matching simply disappear from the library at the next scan; nothing on disk is touched.
/// </summary>
public sealed class ScanFilter(long minBytes, IReadOnlyList<string> words)
{
    public static readonly ScanFilter None = new(0, []);

    public static ScanFilter From(IConfiguration c) => new(
        (int.TryParse(c["Library:MinFileSizeMb"], out var mb) ? Math.Clamp(mb, 0, 10240) : 0) * 1024L * 1024L, ParseWords(c["Library:IgnoreWords"]));

    /// <summary>"sample, extras ,Featurettes" → ["sample", "extras", "featurettes"] (lower case, at most 30 words).</summary>
    public static IReadOnlyList<string> ParseWords(string? text) =>
        (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()).Distinct().Take(30).ToList();

    /// <param name="relativePath">Path below the library folder.</param>
    public bool Allows(string relativePath, long size)
    {
        if (size < minBytes) return false;
        if (words.Count == 0) return true;
        var lower = relativePath.ToLowerInvariant();
        return !words.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }
}
