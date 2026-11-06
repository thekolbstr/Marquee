using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Library;

public enum MediaKind { Unknown, Movie, Episode, Extra, Recording }

public sealed record ParsedName(
    MediaKind Kind, string Title, int? Year, int? Season, int? Episode, int? EpisodeEnd,
    string? EpisodeTitle, string? AirDate, string? Resolution, string? Source);

/// <summary>
/// Parses library-relative paths ("Show/Season 1/Show - S01E02 - Title.mkv") into structured names.
/// Pure and deterministic. Bump <see cref="Version"/> when behavior changes so stored rows are re-parsed.
/// </summary>
public static class FilenameParser
{
    public const int Version = 1;
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex GroupPrefix = new(@"^\s*(?:\[[^\]]*\]\s*)+", I);
    private static readonly Regex ParenYear = new(@"[\(\[]((?:19|20)\d{2})[\)\]]", I);
    private static readonly Regex YearParens = new(@"\(((?:19|20)\d{2})\)", I);
    private static readonly Regex YearToken = new(@"(?<![0-9A-Za-z])((?:19|20)\d{2})(?![0-9A-Za-z])", I);
    private static readonly Regex SxE = new(@"(?<![0-9A-Za-z])S(\d{1,3})[ ._-]?E(\d{1,4})", I);
    private static readonly Regex EpCont = new(@"\G(?:[ ._-]?E(\d{1,4})|-(\d{1,4})(?![0-9pPiI]))", I);
    private static readonly Regex SeasonWord = new(@"(?<![0-9A-Za-z])Season[ ._-]?(\d{1,2})[ ._-]*(?:Episode|Ep)[ ._-]?(\d{1,3})", I);
    private static readonly Regex NxM = new(@"(?<![0-9A-Za-z])(\d{1,2})x(\d{2,3})(?![0-9pPiIxX])", I);
    private static readonly Regex DateEp = new(@"(?<!\d)((?:19|20)\d{2})[ ._-](0[1-9]|1[0-2])[ ._-](0[1-9]|[12]\d|3[01])(?!\d)", I);
    private static readonly Regex AnimeDash = new(@"\s-\s(\d{1,4})(?:v\d+)?(?=\s|$)", I);
    private static readonly Regex TrailingNumber = new(@"^(.+?)\s+(\d{1,3})(?:v\d+)?$", I);
    private static readonly Regex FolderEp = new(@"^(?:(?:e|ep|episode)[ ._-]?(\d{1,3})(?![0-9])|(\d{1,3})(?![0-9A-Za-z]))", I);

    // Strong release-quality markers only: ambiguous words ("web", "complete", "multi") are deliberately absent
    // because they appear in real titles ("Charlotte's Web").
    private static readonly Regex Quality = new(
        @"(?<![0-9A-Za-z])(?:480p|576p|720p|1080p|1080i|2160p|4320p|4k|uhd|blu ?ray|bd ?rip|br ?rip|bd ?remux|remux|web ?dl|web ?rip|hdtv|pdtv|dvd ?rip|dvd ?scr|hd ?rip|hd ?cam|cam ?rip|x ?26[45]|h ?26[45]|hevc|xvid|divx|av1|hdr10\+?|hdr|dolby vision|dts|truehd|atmos|aac|e?ac3|flac|ddp? ?\d|dd\+|amzn|nf|dsnp|hmax|atvp|cd ?\d)(?![0-9A-Za-z])", I);
    private static readonly Regex ResolutionRx = new(@"(?<![0-9A-Za-z])(480p|576p|720p|1080p|1080i|2160p|4320p|4k)(?![0-9A-Za-z])", I);
    private static readonly Regex BluRayRx = new(@"(?<![0-9A-Za-z])(?:blu ?ray|bd ?rip|br ?rip|bd ?remux)(?![0-9A-Za-z])", I);
    private static readonly Regex RemuxRx = new(@"(?<![0-9A-Za-z])remux(?![0-9A-Za-z])", I);
    private static readonly Regex WebRx = new(@"(?<![0-9A-Za-z])(?:web ?dl|web ?rip|web)(?![0-9A-Za-z])", I);
    private static readonly Regex HdtvRx = new(@"(?<![0-9A-Za-z])(?:hdtv|pdtv)(?![0-9A-Za-z])", I);
    private static readonly Regex DvdRx = new(@"(?<![0-9A-Za-z])(?:dvd ?rip|dvd ?scr)(?![0-9A-Za-z])", I);

    private static readonly Regex SeasonFolder = new(@"^(?:(?:season|series)[ ._-]?(\d{1,2})(?!\d).*|s(\d{1,2}))$", I);
    private static readonly Regex SpecialsFolder = new(@"^specials?$", I);
    private static readonly Regex ExtraFolder = new(@"^(?:extras?|featurettes?|trailers?|samples?|behind the scenes|deleted scenes|bonus|interviews|scenes|shorts)$", I);
    private static readonly Regex ExtraName = new(@"^(?:sample|trailer)$|[ ._-](?:trailer|sample)$|^sample[ ._-]", I);

    private sealed record Ep(int Index, int End, int? Season, int? Episode, int? EpisodeEnd, string? Air);

    public static ParsedName Parse(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return new(MediaKind.Unknown, "", null, null, null, null, null, null, null, null);

        var stem = Path.GetFileNameWithoutExtension(segments[^1]);
        var parents = segments[..^1];

        if (parents.Any(p => ExtraFolder.IsMatch(p.Trim())) || ExtraName.IsMatch(stem.Trim()))
            return new(MediaKind.Extra, Clean(Normalize(stem)), null, null, null, null, null, null, null, null);

        int? folderSeason = null;
        foreach (var p in parents.Reverse())
        {
            var t = p.Trim();
            if (SpecialsFolder.IsMatch(t)) { folderSeason = 0; break; }
            var m = SeasonFolder.Match(t);
            if (m.Success) { folderSeason = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture); break; }
        }

        var c = ParseStem(stem, folderSeason);

        if (c.Ep is { } e)
        {
            var title = c.Title;
            if (title.Length == 0) title = FolderTitle(parents) ?? "";
            var season = e.Season ?? folderSeason;
            return new(MediaKind.Episode, title, c.Year, season, e.Episode, e.EpisodeEnd == e.Episode ? null : e.EpisodeEnd,
                c.EpisodeTitle, e.Air, c.Resolution, c.Source);
        }

        var year = c.Year;
        var movieTitle = c.Title;
        // Folder like "Movie (2010)/movie-1080p.mkv": trust the folder when the file carries no year.
        if (parents.Length > 0 && !SeasonFolder.IsMatch(parents[^1].Trim()) && !SpecialsFolder.IsMatch(parents[^1].Trim()))
        {
            var f = ParseStem(parents[^1], null);
            if (f.Title.Length > 0 && ((year is null && f.Year is not null) || movieTitle.Length == 0))
            {
                movieTitle = f.Title;
                year ??= f.Year;
            }
        }
        if (movieTitle.Length == 0)
            return new(MediaKind.Unknown, stem, null, null, null, null, null, null, c.Resolution, c.Source);
        return new(MediaKind.Movie, movieTitle, year, null, null, null, null, null, c.Resolution, c.Source);
    }

    private static string? FolderTitle(string[] parents)
    {
        foreach (var p in parents.Reverse())
        {
            var t = p.Trim();
            if (SeasonFolder.IsMatch(t) || SpecialsFolder.IsMatch(t)) continue;
            var title = ParseStem(t, null).Title;
            if (title.Length > 0) return title;
        }
        return null;
    }

    private sealed record Core(string Title, int? Year, Ep? Ep, string? EpisodeTitle, string? Resolution, string? Source);

    private static Core ParseStem(string stem, int? folderSeason)
    {
        var s0 = stem.Trim();
        var group = GroupPrefix.IsMatch(s0);
        if (group) s0 = GroupPrefix.Replace(s0, "").Trim();

        int? parenYear = null;
        foreach (Match m in ParenYear.Matches(s0)) parenYear = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        var s = Normalize(s0);
        var q = Quality.Match(s);
        var qIdx = q.Success ? q.Index : -1;

        var ep = FindEpisode(s, group, folderSeason, qIdx);

        // Year: prefer the explicit "(YYYY)"; otherwise the last year token with a non-empty title before it.
        int? year = null; var yearIdx = -1;
        var limit = ep?.Index ?? (qIdx >= 0 ? qIdx : s.Length);
        foreach (Match m in YearToken.Matches(s))
        {
            var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (ep?.Air is not null && m.Index >= ep.Index && m.Index < ep.End) continue; // year inside an air date
            if (parenYear is not null)
            {
                if (v == parenYear) { year = v; yearIdx = m.Index; }
            }
            else if (m.Index < limit && Clean(s[..m.Index]).Length > 0)
            {
                year = v; yearIdx = m.Index;
            }
        }

        var cut = s.Length;
        if (ep is not null) cut = ep.Index;
        if (yearIdx >= 0 && yearIdx < cut) cut = yearIdx;
        if (ep is null && yearIdx < 0 && qIdx >= 0) cut = qIdx;

        var title = Clean(s[..cut]);

        string? epTitle = null;
        if (ep is not null && ep.End < s.Length)
        {
            var rest = s[ep.End..];
            var rq = Quality.Match(rest);
            if (rq.Success) rest = rest[..rq.Index];
            rest = Clean(rest);
            if (rest.Length > 0 && !rest.All(char.IsDigit)) epTitle = rest;
        }

        var tail = s[Math.Min(cut, s.Length)..];
        return new Core(title, year, ep, epTitle, Resolution(tail), Source(tail));
    }

    private static Ep? FindEpisode(string s, bool group, int? folderSeason, int qIdx)
    {
        var m = SxE.Match(s);
        if (m.Success)
        {
            var ep = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var last = ep; var end = m.Index + m.Length;
            while (true)
            {
                var c = EpCont.Match(s, end);
                if (!c.Success || c.Index != end) break;
                last = Math.Max(last, int.Parse(c.Groups[c.Groups[1].Success ? 1 : 2].Value, CultureInfo.InvariantCulture));
                end += c.Length;
            }
            return new(m.Index, end, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), ep, last, null);
        }

        m = SeasonWord.Match(s);
        if (m.Success) return Simple(m);

        m = NxM.Match(s);
        if (m.Success) return Simple(m);

        m = DateEp.Match(s);
        if (m.Success)
            return new(m.Index, m.Index + m.Length, null, null, null, $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}");

        foreach (Match a in AnimeDash.Matches(s))
        {
            var digits = a.Groups[1].Value;
            var ok = a.Index > 0 && (group ? digits.Length <= 4 : digits.Length is 2 or 3);
            if (!ok) continue;
            var n = int.Parse(digits, CultureInfo.InvariantCulture);
            return new(a.Index, a.Index + a.Length, folderSeason ?? 1, n, n, null);
        }

        if (group)
        {
            var region = qIdx >= 0 ? s[..qIdx].TrimEnd() : s;
            var t = TrailingNumber.Match(region);
            if (t.Success)
            {
                var n = int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture);
                return new(t.Groups[2].Index, region.Length, folderSeason ?? 1, n, n, null);
            }
        }

        if (folderSeason is not null)
        {
            var f = FolderEp.Match(s);
            if (f.Success)
            {
                var n = int.Parse(f.Groups[f.Groups[1].Success ? 1 : 2].Value, CultureInfo.InvariantCulture);
                return new(0, f.Length, folderSeason, n, n, null);
            }
        }
        return null;

        static Ep Simple(Match m)
        {
            var e = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return new(m.Index, m.Index + m.Length, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), e, e, null);
        }
    }

    /// <summary>Turns scene-style separators into spaces. Dots are separators only when the name has no spaces.</summary>
    private static string Normalize(string s)
    {
        s = s.Replace('_', ' ');
        var dotted = !s.Contains(' ');
        s = YearParens.Replace(s, " $1 ");
        s = Regex.Replace(s, @"[\[\]\{\}]", " ");
        if (dotted)
        {
            s = s.Replace('.', ' ');
            if (!s.Contains(' ') && s.Count(ch => ch == '-') >= 2) s = s.Replace('-', ' ');
        }
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string Clean(string t)
    {
        t = Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '_', '–', '—', ':', ',');
        while (t.EndsWith('(') || t.StartsWith(')')) t = t.TrimEnd('(').TrimStart(')').Trim(' ', '-', '_', '–', '—', ':', ',');
        return t;
    }

    private static string? Resolution(string s)
    {
        var m = ResolutionRx.Match(s);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static string? Source(string s)
    {
        if (RemuxRx.IsMatch(s)) return "Remux";
        if (BluRayRx.IsMatch(s)) return "BluRay";
        if (WebRx.IsMatch(s)) return "Web";
        if (HdtvRx.IsMatch(s)) return "HDTV";
        if (DvdRx.IsMatch(s)) return "DVD";
        return null;
    }
}
