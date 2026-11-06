using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaServer.Api.LiveTv;

public sealed record M3uChannel(string? TvgId, string Name, int? Number, string? Group, string? Logo, string Url);
public sealed record M3uPlaylist(string? EpgUrl, IReadOnlyList<M3uChannel> Channels);

/// <summary>
/// Parses IPTV M3U playlists. The playlist is external input, so everything is bounded and validated: only http(s) stream URLs are
/// accepted (nothing that could make ffmpeg read a local file or an exotic protocol), names are length-capped, and the channel
/// count is limited.
/// </summary>
public static partial class M3uParser
{
    public const int MaxChannels = 5000;
    public const int MaxUrlLength = 2048;

    [GeneratedRegex("([A-Za-z0-9_-]+)=\"([^\"]*)\"")] private static partial Regex AttrRx();

    public static M3uPlaylist Parse(string text)
    {
        string? epg = null;
        var channels = new List<M3uChannel>();
        (Dictionary<string, string> Attrs, string Name)? pending = null;
        string? extGroup = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var a = Attrs(line);
                var url = a.GetValueOrDefault("x-tvg-url") ?? a.GetValueOrDefault("url-tvg");
                epg = url?.Split(',')[0].Trim();
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) { pending = ParseInf(line); continue; }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase)) { extGroup = line[8..].Trim(); continue; }
            if (line[0] == '#' || pending is null) continue;

            var (attrs, name) = pending.Value;
            pending = null;
            if (!IsAllowedUrl(line)) { extGroup = null; continue; }

            var tvgName = attrs.GetValueOrDefault("tvg-name");
            var display = Cap(!string.IsNullOrWhiteSpace(name) ? name : tvgName ?? "", 200);
            if (display.Length == 0) { extGroup = null; continue; }
            channels.Add(new M3uChannel(
                NullIfEmpty(Cap(attrs.GetValueOrDefault("tvg-id"), 120)), display, ParseNumber(attrs.GetValueOrDefault("tvg-chno")),
                NullIfEmpty(Cap(attrs.GetValueOrDefault("group-title") ?? extGroup, 120)), AllowedUrlOrNull(attrs.GetValueOrDefault("tvg-logo")), line));
            extGroup = null;
            if (channels.Count >= MaxChannels) break;
        }
        return new M3uPlaylist(AllowedUrlOrNull(epg), channels);
    }

    public static bool IsAllowedUrl(string url) =>
        url.Length <= MaxUrlLength && !url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
        && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host);

    private static string? AllowedUrlOrNull(string? url) => url is not null && IsAllowedUrl(url) ? url : null;

    /// <summary>"#EXTINF:-1 key="value" key2="v,2",Display Name": the name starts at the first comma outside quotes.</summary>
    private static (Dictionary<string, string>, string) ParseInf(string line)
    {
        var body = line[8..];
        var inQuote = false;
        var comma = -1;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '"') inQuote = !inQuote;
            else if (body[i] == ',' && !inQuote) { comma = i; break; }
        }
        var attrPart = comma < 0 ? body : body[..comma];
        var name = comma < 0 ? "" : body[(comma + 1)..].Trim();
        return (Attrs(attrPart), name);
    }

    private static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx().Matches(s)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static int? ParseNumber(string? s) =>
        s is not null && int.TryParse(s.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and < 100000 ? n : null;

    private static string Cap(string? s, int max) => s is null ? "" : (s.Length > max ? s[..max] : s).Trim();
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
