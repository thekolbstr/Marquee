namespace MediaServer.Api.Hls;

/// <summary>A user-selectable output quality: a width cap plus a video bitrate ceiling (constrained VBR, so quiet scenes stay small).</summary>
public sealed record HlsQuality(string Name, int MaxWidth, int VideoKbps, int AudioKbps)
{
    public static readonly IReadOnlyList<HlsQuality> All =
    [
        new("1080p", 1920, 8000, 192),
        new("720p", 1280, 4000, 160),
        new("480p", 854, 1800, 128),
        new("360p", 640, 900, 96),
    ];

    /// <summary>Whitelist lookup: anything that isn't a known name is ignored (never reaches ffmpeg).</summary>
    public static HlsQuality? Find(string? name) => All.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A quality applies when the source is at least that wide (a small tolerance covers cropped widths such as 1916).
    /// Picking the source's own resolution is still useful: it caps the bitrate for a slow connection.
    /// </summary>
    public bool AppliesTo(int? sourceWidth) => sourceWidth is { } w && MaxWidth <= w * 1.05;
}
