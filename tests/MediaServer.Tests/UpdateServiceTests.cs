using System.Runtime.InteropServices;
using System.Text.Json;
using MediaServer.Api.Maintenance;

namespace MediaServer.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.0.4-alpha", "0.0.3-alpha", true)]
    [InlineData("v0.0.3-alpha", "0.0.3-dev", false)]       // a dev build of the same number is not out of date
    [InlineData("v0.0.3", "0.0.3", false)]
    [InlineData("0.1.0", "0.0.9", true)]
    [InlineData("v1.0", "0.9.9", true)]
    [InlineData("v0.0.2", "0.0.3", false)]                 // never suggests going backwards
    [InlineData("alpha-0.0.2", "0.0.1", true)]             // the older tag style
    [InlineData("v0.0.4", "3f2a9c1", false)]               // a git-hash build has no number to compare
    [InlineData("nightly", "0.0.3", false)]
    [InlineData(null, "0.0.3", false)]
    public void IsNewer_ComparesTheNumbers(string? latest, string? current, bool expected) => Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Fact] public void PlatformToken_MatchesThePackageNames()
    {
        Assert.Equal("windows-x64", UpdateService.PlatformToken(Architecture.X64, true, false, false));
        Assert.Equal("linux-arm64", UpdateService.PlatformToken(Architecture.Arm64, false, true, false));
        Assert.Equal("macos-arm64", UpdateService.PlatformToken(Architecture.Arm64, false, false, true));
        Assert.Equal("macos-x64", UpdateService.PlatformToken(Architecture.X64, false, false, true));
        Assert.Null(UpdateService.PlatformToken(Architecture.Arm64, true, false, false));
    }

    private const string Releases = """
        [
          { "tag_name": "v0.0.2-alpha", "draft": false, "html_url": "https://github.com/x/y/releases/tag/v0.0.2-alpha", "assets": [] },
          { "tag_name": "v0.0.9-alpha", "draft": true,  "html_url": "https://github.com/x/y/releases/tag/v0.0.9-alpha", "assets": [] },
          { "tag_name": "v0.0.4-alpha", "draft": false, "body": "What's new", "html_url": "https://github.com/x/y/releases/tag/v0.0.4-alpha",
            "assets": [
              { "name": "Marquee-linux-x64.tar.gz",  "browser_download_url": "https://github.com/x/y/releases/download/v0.0.4-alpha/Marquee-linux-x64.tar.gz" },
              { "name": "Marquee-macos-arm64.zip",   "browser_download_url": "https://github.com/x/y/releases/download/v0.0.4-alpha/Marquee-macos-arm64.zip" },
              { "name": "Marquee-windows-x64.zip",   "browser_download_url": "https://evil.example/Marquee-windows-x64.zip" }
            ] }
        ]
        """;

    [Fact] public void Parse_PicksTheHighestPublishedRelease_AndTheRightDownload()
    {
        using var doc = JsonDocument.Parse(Releases);
        var u = UpdateService.Parse(doc.RootElement, "0.0.3-dev", DateTimeOffset.UnixEpoch, "macos-arm64");
        Assert.True(u.Newer); Assert.Equal("0.0.4-alpha", u.Latest);                        // the draft 0.0.9 is ignored
        Assert.Equal("Marquee-macos-arm64.zip", u.DownloadName); Assert.EndsWith("Marquee-macos-arm64.zip", u.DownloadUrl);
        Assert.Equal("What's new", u.Notes);
    }

    [Fact] public void Parse_DropsLinksThatDoNotPointAtGitHub()
    {
        using var doc = JsonDocument.Parse(Releases);
        Assert.Null(UpdateService.Parse(doc.RootElement, "0.0.3", DateTimeOffset.UnixEpoch, "windows-x64").DownloadUrl);
    }

    [Fact] public void Parse_WhenAlreadyCurrent_IsNotNewer()
    {
        using var doc = JsonDocument.Parse(Releases);
        Assert.False(UpdateService.Parse(doc.RootElement, "0.0.4-alpha", DateTimeOffset.UnixEpoch, null).Newer);
        using var none = JsonDocument.Parse("[]");
        Assert.Null(UpdateService.Parse(none.RootElement, "0.0.4", DateTimeOffset.UnixEpoch, null).Latest);
    }
}
