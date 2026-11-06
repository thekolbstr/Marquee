using System.Globalization;

namespace MediaServer.Api.Streaming;

public enum RangeOutcome
{
    /// <summary>No usable Range header: serve 200 with the full body.</summary>
    Ignore,
    /// <summary>Serve 206 with <see cref="RangeResult.Start"/>..<see cref="RangeResult.End"/> inclusive.</summary>
    Partial,
    /// <summary>Serve 416 with Content-Range: bytes */length.</summary>
    Unsatisfiable,
}

public readonly record struct RangeResult(RangeOutcome Outcome, long Start = 0, long End = 0)
{
    public long Length => End - Start + 1;
    public static readonly RangeResult Ignore = new(RangeOutcome.Ignore);
    public static readonly RangeResult Unsatisfiable = new(RangeOutcome.Unsatisfiable);
}

/// <summary>
/// RFC 9110 §14 single-range parser. Supports bytes=a-b, a-, and -n.
/// Multi-range requests and syntactically invalid headers are ignored (full 200 response),
/// which the RFC permits; only well-formed but unsatisfiable ranges yield 416.
/// </summary>
public static class RangeParser
{
    private const string Unit = "bytes=";

    public static RangeResult Parse(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header)) return RangeResult.Ignore;

        var h = header.AsSpan().Trim();
        if (!h.StartsWith(Unit, StringComparison.OrdinalIgnoreCase)) return RangeResult.Ignore;

        var spec = h[Unit.Length..];
        if (spec.Contains(',')) return RangeResult.Ignore; // multi-range: not supported

        var dash = spec.IndexOf('-');
        if (dash < 0) return RangeResult.Ignore;

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.IsEmpty)
        {
            // Suffix range: last n bytes.
            if (last.IsEmpty || !TryParseNumber(last, out var n)) return RangeResult.Ignore;
            if (n == 0 || length == 0) return RangeResult.Unsatisfiable;
            return new(RangeOutcome.Partial, Math.Max(0, length - n), length - 1);
        }

        if (!TryParseNumber(first, out var start)) return RangeResult.Ignore;

        long end;
        if (last.IsEmpty)
        {
            end = long.MaxValue;
        }
        else
        {
            if (!TryParseNumber(last, out end)) return RangeResult.Ignore;
            if (end < start) return RangeResult.Ignore; // invalid byte-range-spec
        }

        if (start >= length) return RangeResult.Unsatisfiable;
        return new(RangeOutcome.Partial, start, Math.Min(end, length - 1));
    }

    // Digits only (no sign/whitespace/separators). Overflowing values saturate so that
    // e.g. "bytes=99999999999999999999-" is unsatisfiable rather than "invalid".
    private static bool TryParseNumber(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        foreach (var c in s) if (c is < '0' or > '9') return false;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            value = long.MaxValue;
        return true;
    }
}
