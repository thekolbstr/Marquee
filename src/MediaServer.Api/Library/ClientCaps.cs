namespace MediaServer.Api.Library;

/// <summary>
/// What the requesting browser says it can decode, as short whitelisted tokens (anything else is ignored).
/// Video: h264, hevc, hevc10, vp9, av1, plus "hev1" (accepts HEVC files tagged hev1; Safari does not). Audio: aac, mp3, opus, vorbis, flac, ac3, eac3. Containers: mp4, webm, mkv.
/// </summary>
public sealed record ClientCaps(IReadOnlySet<string> Video, IReadOnlySet<string> Audio, IReadOnlySet<string> Containers)
{
    private static readonly string[] VideoTokens = ["h264", "hevc", "hevc10", "hev1", "vp9", "av1"];
    private static readonly string[] AudioTokens = ["aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3"];
    private static readonly string[] ContainerTokens = ["mp4", "webm", "mkv"];

    /// <summary>What every browser can do; used when a client doesn't say.</summary>
    public static readonly ClientCaps Baseline = new(
        new HashSet<string> { "h264" }, new HashSet<string> { "aac", "mp3", "opus", "vorbis", "flac" }, new HashSet<string> { "mp4", "webm" });

    /// <summary>A dimension that is null falls back to the baseline; an explicit empty list means "supports none".</summary>
    public static ClientCaps From(string? video, string? audio, string? containers) => new(
        video is null ? Baseline.Video : Parse(video, VideoTokens),
        audio is null ? Baseline.Audio : Parse(audio, AudioTokens),
        containers is null ? Baseline.Containers : Parse(containers, ContainerTokens));

    private static HashSet<string> Parse(string csv, string[] allowed) =>
        csv.Length > 200 ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Where(allowed.Contains).Take(16).ToHashSet();
}
