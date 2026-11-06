using System.Globalization;
using System.Text.Json;

namespace MediaServer.Api.Probing;

/// <summary>Parses `ffprobe -print_format json -show_format -show_streams` output into typed models.</summary>
public static class FfprobeParser
{
    /// <summary>Bump when the parser extracts more data, so stored probes are refreshed on the next scan.</summary>
    public const int Version = 2;

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? container = null; double? duration = null; long? bitRate = null, size = null;
        if (root.TryGetProperty("format", out var fmt))
        {
            container = Str(fmt, "format_name");
            duration = Dbl(fmt, "duration");
            bitRate = Lng(fmt, "bit_rate");
            size = Lng(fmt, "size");
        }

        var video = new List<VideoTrack>();
        var audio = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var index = (int)(Lng(s, "index") ?? 0);
                var codec = Str(s, "codec_name");
                var disp = s.TryGetProperty("disposition", out var d) ? d : default;
                bool Flag(string n) => disp.ValueKind == JsonValueKind.Object && Lng(disp, n) == 1;
                string? Tag(string n) => s.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object
                    ? t.EnumerateObject().FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null
                    : null;

                switch (Str(s, "codec_type"))
                {
                    case "video" when !Flag("attached_pic"): // skip embedded cover art
                        video.Add(new VideoTrack(index, codec, Str(s, "profile"),
                            (int?)Lng(s, "width"), (int?)Lng(s, "height"),
                            Rate(Str(s, "avg_frame_rate")) ?? Rate(Str(s, "r_frame_rate")),
                            Str(s, "pix_fmt"), Lng(s, "bit_rate"), Str(s, "codec_tag_string"), Str(s, "field_order")));
                        break;
                    case "audio":
                        audio.Add(new AudioTrack(index, codec, (int?)Lng(s, "channels"), Str(s, "channel_layout"),
                            (int?)Lng(s, "sample_rate"), Tag("language"), Tag("title"), Flag("default"), Lng(s, "bit_rate")));
                        break;
                    case "subtitle":
                        subs.Add(new SubtitleTrack(index, codec, Tag("language"), Tag("title"), Flag("default"), Flag("forced")));
                        break;
                }
            }
        }
        return new MediaInfo(container, duration, bitRate, size, video, audio, subs);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ffprobe emits many numbers as strings ("duration": "60.000000"); accept both forms.
    private static long? Lng(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return null;
    }

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static double? Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den != 0)
            return Math.Round(n / den, 3);
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x > 0 ? x : null;
    }
}
