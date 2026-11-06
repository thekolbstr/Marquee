using System.Globalization;
using System.Text;

namespace MediaServer.Api.Metadata;

public static class TmdbMatcher
{
    /// <summary>Case/diacritic/punctuation-insensitive comparison key.</summary>
    public static string Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '\'' || ch == '’') continue;            // "Ocean's" == "Oceans"
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Picks the best hit, or null when nothing is convincingly similar (better unmatched than confidently wrong;
    /// the override table exists for the rest). Hits arrive in TMDB relevance order, which breaks ties.
    /// </summary>
    public static TmdbHit? Pick(IReadOnlyList<TmdbHit> hits, string title, int? year)
    {
        var key = Key(title);
        if (key.Length == 0 || hits.Count == 0) return null;

        bool TitleEq(TmdbHit h) => Key(h.Title) == key || Key(h.OriginalTitle) == key;
        bool YearWithin(TmdbHit h, int tol) => year is null || h.Year is null || Math.Abs(h.Year.Value - year.Value) <= tol;
        bool Similar(TmdbHit h)
        {
            foreach (var hk in new[] { Key(h.Title), Key(h.OriginalTitle) })
            {
                if (hk.Length < 3 || key.Length < 3) continue;
                if (hk.Contains(key, StringComparison.Ordinal) || key.Contains(hk, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        return (year is null ? null : hits.FirstOrDefault(h => TitleEq(h) && h.Year == year))
            ?? hits.FirstOrDefault(h => TitleEq(h) && YearWithin(h, 1))
            ?? (year is null ? null : hits.FirstOrDefault(h => Similar(h) && h.Year == year))
            ?? hits.FirstOrDefault(h => Similar(h) && YearWithin(h, 1));
    }
}
