using MediaServer.Api.Streaming;

namespace MediaServer.Tests;

public class RangeParserTests
{
    private const long Len = 1000;

    [Theory]
    [InlineData("bytes=0-499", 0, 499)]
    [InlineData("bytes=500-999", 500, 999)]
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=-200", 800, 999)]
    [InlineData("bytes=0-0", 0, 0)]
    [InlineData("bytes=999-999", 999, 999)]
    [InlineData("bytes=900-5000", 900, 999)]   // end clamped at EOF
    [InlineData("bytes=-5000", 0, 999)]        // suffix larger than file => whole file
    [InlineData("BYTES=10-20", 10, 20)]        // unit is case-insensitive
    [InlineData("  bytes=10-20  ", 10, 20)]
    [InlineData("bytes=0-", 0, 999)]
    public void Satisfiable(string header, long start, long end)
    {
        var r = RangeParser.Parse(header, Len);
        Assert.Equal(RangeOutcome.Partial, r.Outcome);
        Assert.Equal(start, r.Start);
        Assert.Equal(end, r.End);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=1000-1500")]
    [InlineData("bytes=5000-")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=99999999999999999999-")] // overflow saturates, still unsatisfiable
    public void Unsatisfiable(string header)
        => Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse(header, Len).Outcome);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-5")]
    [InlineData("bytes=")]
    [InlineData("bytes=-")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=5-2")]          // end < start: invalid spec, ignored
    [InlineData("bytes=0-5,10-15")]    // multi-range unsupported => full body
    [InlineData("bytes=+5-10")]
    [InlineData("bytes=1_0-20")]
    [InlineData("bytes=0")]
    public void IgnoredFallsBackToFull(string? header)
        => Assert.Equal(RangeOutcome.Ignore, RangeParser.Parse(header, Len).Outcome);

    [Fact]
    public void EmptyFile_AnyRangeIsUnsatisfiable()
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=0-", 0).Outcome);
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=-5", 0).Outcome);
    }

    [Fact]
    public void LargeOffsetsBeyond4GiB()
    {
        const long big = 10L * 1024 * 1024 * 1024;
        var r = RangeParser.Parse($"bytes={big - 100}-", big);
        Assert.Equal(big - 100, r.Start);
        Assert.Equal(big - 1, r.End);
        Assert.Equal(100, r.Length);
    }
}
