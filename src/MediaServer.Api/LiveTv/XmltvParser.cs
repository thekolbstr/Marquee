using System.Globalization;
using System.Xml;

namespace MediaServer.Api.LiveTv;

public sealed record XmltvProgramme(string ChannelId, long StartTs, long StopTs, string Title, string? SubTitle, string? Description, string? Category);

/// <summary>
/// Streaming XMLTV reader for programme guides (which can be tens of megabytes). Hardened as external input: DTDs are prohibited
/// (no entity expansion or external fetches), text fields are length-capped, and only programmes inside the requested window are kept.
/// </summary>
public static class XmltvParser
{
    public static IEnumerable<XmltvProgramme> ReadProgrammes(Stream stream, long fromTs, long toTs, int maxProgrammes = 300_000)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0, MaxCharactersInDocument = 1_000_000_000,
        };
        using var r = XmlReader.Create(stream, settings);
        var kept = 0;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.Name != "programme") continue;
            var p = ReadProgramme(r);
            if (p is null || p.StopTs <= fromTs || p.StartTs >= toTs) continue;
            yield return p;
            if (++kept >= maxProgrammes) yield break;
        }
    }

    private static XmltvProgramme? ReadProgramme(XmlReader r)
    {
        var channel = r.GetAttribute("channel");
        var start = ParseTime(r.GetAttribute("start"));
        var stop = ParseTime(r.GetAttribute("stop"));
        string? title = null, sub = null, desc = null, cat = null;

        using (var inner = r.ReadSubtree())
        {
            inner.Read(); // the <programme> element itself
            inner.Read(); // its first child (or the end)
            // Each branch must consume exactly one node: ReadElementContentAsString() and Skip() already advance the reader, so a
            // blind Read() afterwards would jump over the next sibling (e.g. <title/> directly followed by <sub-title/>).
            while (!inner.EOF)
            {
                if (inner.NodeType != XmlNodeType.Element) { inner.Read(); continue; }
                switch (inner.Name)
                {
                    case "title": { var v = inner.ReadElementContentAsString(); title ??= Cap(v, 200); break; }
                    case "sub-title": { var v = inner.ReadElementContentAsString(); sub ??= Cap(v, 200); break; }
                    case "desc": { var v = inner.ReadElementContentAsString(); desc ??= Cap(v, 2000); break; }
                    case "category": { var v = inner.ReadElementContentAsString(); cat ??= Cap(v, 80); break; }
                    default: inner.Skip(); break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(channel) || start is null || stop is null || stop <= start || string.IsNullOrWhiteSpace(title)) return null;
        return new XmltvProgramme(channel.Length > 120 ? channel[..120] : channel, start.Value, stop.Value, title, Empty(sub), Empty(desc), Empty(cat));
    }

    /// <summary>XMLTV time: "20261106140000 +0000" (offset optional, seconds optional).</summary>
    public static long? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var space = s.IndexOf(' ');
        var datePart = space < 0 ? s : s[..space];
        var offPart = space < 0 ? "" : s[(space + 1)..].Trim();
        if (datePart.Length is not (12 or 14) || !datePart.All(char.IsAsciiDigit)) return null;
        var fmt = datePart.Length == 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
        if (!DateTime.TryParseExact(datePart, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;

        var offset = TimeSpan.Zero;
        if (offPart.Length is 5 && offPart[0] is '+' or '-' && offPart[1..].All(char.IsAsciiDigit))
        {
            var h = int.Parse(offPart.AsSpan(1, 2), CultureInfo.InvariantCulture);
            var m = int.Parse(offPart.AsSpan(3, 2), CultureInfo.InvariantCulture);
            if (h > 14 || m > 59) return null;
            offset = new TimeSpan(h, m, 0) * (offPart[0] == '-' ? -1 : 1);
        }
        else if (offPart.Length != 0 && offPart != "UTC" && offPart != "GMT") return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUnixTimeSeconds();
    }

    private static string Cap(string s, int max) => (s.Length > max ? s[..max] : s).Trim();
    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
