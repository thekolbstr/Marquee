using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Cast;

/// <summary>Which addresses a TV fetches by itself (so they need CORS headers and, for subtitles, a signed link instead of a cookie).</summary>
public static partial class CastAccess
{
    [GeneratedRegex(@"^/api/hls/[0-9a-f]{32}/[^/]+$")] private static partial Regex HlsRx();
    [GeneratedRegex(@"^/api/media/(\d{1,18})/captions/[^/]+\.vtt$")] private static partial Regex CaptionRx();

    public static bool IsTvFetch(PathString path) => HlsRx().IsMatch(path.Value ?? "") || CaptionRx().IsMatch(path.Value ?? "");

    /// <summary>The media id in a subtitle address, or null if the path isn't one.</summary>
    public static long? CaptionMediaId(PathString path) =>
        CaptionRx().Match(path.Value ?? "") is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
