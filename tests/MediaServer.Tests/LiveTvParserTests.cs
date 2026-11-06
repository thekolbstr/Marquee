using System.Text;
using MediaServer.Api.LiveTv;

namespace MediaServer.Tests;

public class M3uParserTests
{
    private const string Dispatcharr = """
        #EXTM3U x-tvg-url="http://192.0.2.10:9191/output/epg" url-tvg="http://192.0.2.10:9191/output/epg"
        #EXTINF:-1 tvg-id="1" tvg-name="US Network One (KABC) Springfield (H)" tvg-logo="http://192.0.2.10:9191/api/channels/logos/848/cache/" tvg-chno="1" group-title="US News",US Network One (KABC) Springfield (H)
        http://192.0.2.10:9191/proxy/ts/stream/11111111-2222-3333-4444-555555555555
        #EXTINF:-1 tvg-id="3" tvg-name="Sports Channel" tvg-logo="http://192.0.2.10:9191/api/channels/logos/25/cache/" tvg-chno="3" group-title="US Sports",Sports Channel
        http://192.0.2.10:9191/proxy/ts/stream/66666666-7777-8888-9999-000000000000
        """;

    [Fact]
    public void ParsesARealDispatcharrPlaylist()
    {
        var p = M3uParser.Parse(Dispatcharr);
        Assert.Equal("http://192.0.2.10:9191/output/epg", p.EpgUrl);
        Assert.Equal(2, p.Channels.Count);
        var c = p.Channels[0];
        Assert.Equal(("1", "US Network One (KABC) Springfield (H)", 1, "US News"), (c.TvgId, c.Name, c.Number, c.Group));
        Assert.Equal("http://192.0.2.10:9191/api/channels/logos/848/cache/", c.Logo);
        Assert.EndsWith("11111111-2222-3333-4444-555555555555", c.Url);
    }

    [Fact]
    public void CommasInsideQuotedAttributesDoNotSplitTheName()
    {
        var p = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"x\" group-title=\"News, World\" tvg-name=\"A, B\",Real Name, With Comma\nhttp://h/s1\n");
        var c = Assert.Single(p.Channels);
        Assert.Equal("Real Name, With Comma", c.Name);
        Assert.Equal("News, World", c.Group);
    }

    [Fact]
    public void ToleratesCrlfBomBlankLinesCommentsAndExtgrp()
    {
        var text = "﻿#EXTM3U\r\n\r\n#EXTINF:-1,Plain\r\n#EXTGRP:Movies\r\n#EXTVLCOPT:network-caching=1000\r\nhttp://h/a\r\n# a comment\r\n#EXTINF:-1 tvg-chno=\"2.1\",Sub Channel\r\nhttp://h/b\r\n";
        var p = M3uParser.Parse(text);
        Assert.Equal(["Plain", "Sub Channel"], p.Channels.Select(c => c.Name));
        Assert.Equal("Movies", p.Channels[0].Group);                    // #EXTGRP applies to the next channel only
        Assert.Null(p.Channels[1].Group);
        Assert.Equal(2, p.Channels[1].Number);                          // "2.1" => 2
    }

    [Fact]
    public void FallsBackToTvgNameWhenTheDisplayNameIsEmpty()
        => Assert.Equal("Only Attr", Assert.Single(M3uParser.Parse("#EXTINF:-1 tvg-name=\"Only Attr\",\nhttp://h/a\n").Channels).Name);

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("rtsp://host/stream")]
    [InlineData("javascript:alert(1)")]
    [InlineData("concat:http://a|file:///etc/passwd")]
    [InlineData("http://has space/x")]
    [InlineData("ftp://host/x")]
    [InlineData("//host/x")]
    [InlineData("http://")]
    [InlineData("data:text/plain,hi")]
    public void OnlyHttpStreamUrlsAreAccepted(string url)
        => Assert.Empty(M3uParser.Parse($"#EXTINF:-1,Bad\n{url}\n").Channels);

    [Fact]
    public void RejectsOverlongUrlsAndControlCharacters()
    {
        Assert.Empty(M3uParser.Parse($"#EXTINF:-1,Long\nhttp://h/{new string('a', 3000)}\n").Channels);
        Assert.Empty(M3uParser.Parse("#EXTINF:-1,Ctl\nhttp://h/a\u0001b\n").Channels);
    }

    [Fact]
    public void BadLogoOrGuideUrlsAreDroppedNotTrusted()
    {
        var p = M3uParser.Parse("#EXTM3U x-tvg-url=\"file:///etc/x\"\n#EXTINF:-1 tvg-logo=\"javascript:1\",Chan\nhttp://h/a\n");
        Assert.Null(p.EpgUrl);
        Assert.Null(Assert.Single(p.Channels).Logo);
    }

    [Fact]
    public void ChannelCountAndNameLengthAreBounded()
    {
        var sb = new StringBuilder("#EXTM3U\n");
        for (var i = 0; i < M3uParser.MaxChannels + 50; i++) sb.Append($"#EXTINF:-1 tvg-id=\"{i}\",C{i}\nhttp://h/{i}\n");
        Assert.Equal(M3uParser.MaxChannels, M3uParser.Parse(sb.ToString()).Channels.Count);
        Assert.Equal(200, Assert.Single(M3uParser.Parse($"#EXTINF:-1,{new string('n', 900)}\nhttp://h/a\n").Channels).Name.Length);
    }

    [Fact]
    public void DanglingExtinfAndUrlWithoutExtinfAreIgnored()
    {
        Assert.Empty(M3uParser.Parse("#EXTINF:-1,NoUrl\n").Channels);
        Assert.Empty(M3uParser.Parse("http://h/orphan\n").Channels);
        Assert.Empty(M3uParser.Parse("").Channels);
        Assert.Empty(M3uParser.Parse("\0\0\u0007 garbage \n#EXTINF").Channels);
    }

    [Fact] public void MultipleGuideUrlsTakeTheFirst()
        => Assert.Equal("http://a/epg.xml", M3uParser.Parse("#EXTM3U url-tvg=\"http://a/epg.xml,http://b/epg2.xml\"\n").EpgUrl);
}

public class XmltvParserTests
{
    private static MemoryStream Xml(string body) => new(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><tv>{body}</tv>"));
    private static long T(string s) => XmltvParser.ParseTime(s)!.Value;
    private const long Min = 0, Max = long.MaxValue;

    [Theory]
    [InlineData("20261106140000 +0000", 1793973600)]
    [InlineData("20261106140000", 1793973600)]                 // no offset => UTC
    [InlineData("20261106090000 -0500", 1793973600)]
    [InlineData("20261106160000 +0200", 1793973600)]
    [InlineData("202611061400 +0000", 1793973600)]             // minutes only
    [InlineData("20261106140000 UTC", 1793973600)]
    public void ParsesXmltvTimes(string s, long expected) => Assert.Equal(expected, XmltvParser.ParseTime(s));

    [Theory]
    [InlineData("")] [InlineData(null)] [InlineData("garbage")] [InlineData("2026110614")] [InlineData("20261340140000 +0000")]
    [InlineData("20261106140000 +9900")] [InlineData("20261106140000 +00")] [InlineData("20261106140000 EST")] [InlineData("2026-11-06 14:00")]
    public void RejectsMalformedTimes(string? s) => Assert.Null(XmltvParser.ParseTime(s));

    [Fact]
    public void ReadsProgrammes_WithTitleSubtitleDescriptionAndCategory()
    {
        var p = XmltvParser.ReadProgrammes(Xml("""
            <programme start="20261106140000 +0000" stop="20261106153000 +0000" channel="1">
              <title lang="en">The Movie</title><sub-title>Part 1</sub-title><desc>A long description.</desc><category>Film</category><category>Drama</category>
              <icon src="http://x/y.png"/><episode-num system="onscreen">S1E2</episode-num>
            </programme>
            """), Min, Max).Single();
        Assert.Equal(("1", "The Movie", "Part 1", "A long description.", "Film"), (p.ChannelId, p.Title, p.SubTitle, p.Description, p.Category));
        Assert.Equal(T("20261106153000 +0000") - T("20261106140000 +0000"), p.StopTs - p.StartTs);
    }

    [Fact]
    public void KeepsOnlyProgrammesOverlappingTheWindow()
    {
        var xml = Xml("""
            <programme start="20261106100000 +0000" stop="20261106110000 +0000" channel="1"><title>Past</title></programme>
            <programme start="20261106140000 +0000" stop="20261106150000 +0000" channel="1"><title>Now</title></programme>
            <programme start="20261106143000 +0000" stop="20261106160000 +0000" channel="1"><title>Straddles Start</title></programme>
            <programme start="20261106200000 +0000" stop="20261106210000 +0000" channel="1"><title>Far</title></programme>
            """);
        var titles = XmltvParser.ReadProgrammes(xml, T("20261106141500 +0000"), T("20261106180000 +0000")).Select(p => p.Title);
        Assert.Equal(["Now", "Straddles Start"], titles);
    }

    [Fact]
    public void SkipsBrokenProgrammesWithoutAbortingTheRest()
    {
        var p = XmltvParser.ReadProgrammes(Xml("""
            <programme start="bad" stop="20261106150000 +0000" channel="1"><title>Bad start</title></programme>
            <programme start="20261106150000 +0000" stop="20261106140000 +0000" channel="1"><title>Stops before start</title></programme>
            <programme start="20261106140000 +0000" stop="20261106150000 +0000" channel="1"></programme>
            <programme start="20261106140000 +0000" stop="20261106150000 +0000"><title>No channel</title></programme>
            <programme start="20261106140000 +0000" stop="20261106150000 +0000" channel="2"><title>Good</title></programme>
            """), Min, Max).ToList();
        Assert.Equal("Good", Assert.Single(p).Title);
    }

    [Fact]
    public void TextIsLengthCapped()
    {
        var p = XmltvParser.ReadProgrammes(Xml($"<programme start=\"20261106140000 +0000\" stop=\"20261106150000 +0000\" channel=\"1\"><title>{new string('t', 5000)}</title><desc>{new string('d', 20000)}</desc></programme>"), Min, Max).Single();
        Assert.Equal(200, p.Title.Length); Assert.Equal(2000, p.Description!.Length);
    }

    [Fact]
    public void DtdsAreProhibited_SoEntityExpansionAndExternalEntitiesCannotBeUsed()
    {
        var bomb = "<?xml version=\"1.0\"?><!DOCTYPE tv [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><tv><programme start=\"20261106140000 +0000\" stop=\"20261106150000 +0000\" channel=\"1\"><title>&b;</title></programme></tv>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => XmltvParser.ReadProgrammes(new MemoryStream(Encoding.UTF8.GetBytes(bomb)), Min, Max).ToList());
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE tv [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><tv><programme start=\"20261106140000 +0000\" stop=\"20261106150000 +0000\" channel=\"1\"><title>&x;</title></programme></tv>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => XmltvParser.ReadProgrammes(new MemoryStream(Encoding.UTF8.GetBytes(xxe)), Min, Max).ToList());
    }

    [Fact]
    public void ProgrammeCountIsBounded()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) sb.Append($"<programme start=\"20261106140000 +0000\" stop=\"20261106150000 +0000\" channel=\"{i}\"><title>P{i}</title></programme>");
        Assert.Equal(10, XmltvParser.ReadProgrammes(Xml(sb.ToString()), Min, Max, maxProgrammes: 10).Count());
    }

    [Fact] public void EmptyAndMalformedDocuments()
    {
        Assert.Empty(XmltvParser.ReadProgrammes(Xml(""), Min, Max));
        Assert.ThrowsAny<System.Xml.XmlException>(() => XmltvParser.ReadProgrammes(new MemoryStream(Encoding.UTF8.GetBytes("<tv><programme")), Min, Max).ToList());
    }
}
