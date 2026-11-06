using MediaServer.Api.Library;

namespace MediaServer.Tests;

public class FilenameParserTests
{
    // path, kind, title, year, season, episode
    public static TheoryData<string, MediaKind, string, int?, int?, int?> Cases => new()
    {
        // ---- Movies ----
        { "The.Matrix.1999.1080p.BluRay.x264-GROUP.mkv", MediaKind.Movie, "The Matrix", 1999, null, null },
        { "The Matrix (1999).mkv", MediaKind.Movie, "The Matrix", 1999, null, null },
        { "The Matrix (1999)/The Matrix (1999) 1080p.mkv", MediaKind.Movie, "The Matrix", 1999, null, null },
        { "Inception.2010.720p.BRRip.XviD.AC3-RARBG.avi", MediaKind.Movie, "Inception", 2010, null, null },
        { "Blade.Runner.2049.2017.2160p.UHD.BluRay.x265.HDR.DTS-HD.MA.7.1-GRP.mkv", MediaKind.Movie, "Blade Runner 2049", 2017, null, null },
        { "2001.A.Space.Odyssey.1968.REMASTERED.1080p.BluRay.mkv", MediaKind.Movie, "2001 A Space Odyssey", 1968, null, null },
        { "1917.2019.1080p.WEB-DL.DD5.1.H264-EVO.mkv", MediaKind.Movie, "1917", 2019, null, null },
        { "1917 (2019).mkv", MediaKind.Movie, "1917", 2019, null, null },
        { "2012 (2009).mkv", MediaKind.Movie, "2012", 2009, null, null },
        { "Spider-Man.No.Way.Home.2021.1080p.WEBRip.x264.mp4", MediaKind.Movie, "Spider-Man No Way Home", 2021, null, null },
        { "Movies/Alien (1979)/Alien.1979.Directors.Cut.1080p.mkv", MediaKind.Movie, "Alien", 1979, null, null },
        { "Se7en (1995).mp4", MediaKind.Movie, "Se7en", 1995, null, null },
        { "WALL-E (2008) [1080p].mkv", MediaKind.Movie, "WALL-E", 2008, null, null },
        { "Mad Max Fury Road 2015 1080p BluRay.mkv", MediaKind.Movie, "Mad Max Fury Road", 2015, null, null },
        { "Amélie (2001).mkv", MediaKind.Movie, "Amélie", 2001, null, null },
        { "Charlotte's Web (2006).mkv", MediaKind.Movie, "Charlotte's Web", 2006, null, null },
        { "Charlotte's.Web.2006.1080p.WEB-DL.mkv", MediaKind.Movie, "Charlotte's Web", 2006, null, null },
        { "The_Lord_of_the_Rings_The_Fellowship_of_the_Ring_2001_EXTENDED_1080p.mkv", MediaKind.Movie, "The Lord of the Rings The Fellowship of the Ring", 2001, null, null },
        { "Inception.mkv", MediaKind.Movie, "Inception", null, null, null },
        { "Movie Title [2020] [1080p].mp4", MediaKind.Movie, "Movie Title", 2020, null, null },
        { "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR10+.H.265-FLUX.mkv", MediaKind.Movie, "Dune Part Two", 2024, null, null },
        { "The.Godfather.Part.II.1974.1080p.BluRay.x264.mkv", MediaKind.Movie, "The Godfather Part II", 1974, null, null },
        { "Ocean's Eleven (2001) - 1080p.mkv", MediaKind.Movie, "Ocean's Eleven", 2001, null, null },
        { "Dr. Strangelove or How I Learned to Stop Worrying and Love the Bomb (1964).mkv", MediaKind.Movie, "Dr. Strangelove or How I Learned to Stop Worrying and Love the Bomb", 1964, null, null },
        { "Pulp Fiction (1994) CD1.avi", MediaKind.Movie, "Pulp Fiction", 1994, null, null },
        { "Joker.2019.IMAX.1080p.BluRay.mkv", MediaKind.Movie, "Joker", 2019, null, null },
        { "Everything Everywhere All at Once (2022)/Everything.Everywhere.All.At.Once.2022.1080p.mkv", MediaKind.Movie, "Everything Everywhere All At Once", 2022, null, null },
        { "Foo Bar (2010)/movie-1080p.mkv", MediaKind.Movie, "Foo Bar", 2010, null, null },
        { "Up.2009.1080p.mkv", MediaKind.Movie, "Up", 2009, null, null },
        { "It (2017).mkv", MediaKind.Movie, "It", 2017, null, null },
        { "Lock.Stock.and.Two.Smoking.Barrels.1998.720p.HDRip.x264.mkv", MediaKind.Movie, "Lock Stock and Two Smoking Barrels", 1998, null, null },
        { "THE.SHAWSHANK.REDEMPTION.1994.MULTI.1080p.BluRay.x265.MKV", MediaKind.Movie, "THE SHAWSHANK REDEMPTION", 1994, null, null },

        // ---- Episodes: SxxExx ----
        { "Breaking.Bad.S01E01.Pilot.720p.BluRay.x264-DEMAND.mkv", MediaKind.Episode, "Breaking Bad", null, 1, 1 },
        { "Breaking Bad - S02E03 - Bit by a Dead Bee.mkv", MediaKind.Episode, "Breaking Bad", null, 2, 3 },
        { "Breaking Bad/Season 1/Breaking Bad - S01E02 - Cat's in the Bag.mkv", MediaKind.Episode, "Breaking Bad", null, 1, 2 },
        { "The Office (US)/Season 3/The Office (US) - S03E10 - Initiation.mkv", MediaKind.Episode, "The Office (US)", null, 3, 10 },
        { "Game.of.Thrones.S08E06.The.Iron.Throne.1080p.WEB.H264-MEMENTO.mkv", MediaKind.Episode, "Game of Thrones", null, 8, 6 },
        { "The.Mandalorian.S02E08.Chapter.16.The.Rescue.2160p.DSNP.WEB-DL.mkv", MediaKind.Episode, "The Mandalorian", null, 2, 8 },
        { "house.s05e12.hdtv.xvid-lol.avi", MediaKind.Episode, "house", null, 5, 12 },
        { "Parks_and_Recreation_S03E04_720p.mkv", MediaKind.Episode, "Parks and Recreation", null, 3, 4 },
        { "Marvel's Agents of S.H.I.E.L.D. - S01E01 - Pilot.mkv", MediaKind.Episode, "Marvel's Agents of S.H.I.E.L.D.", null, 1, 1 },
        { "Monk.S08E16.1080p.mkv", MediaKind.Episode, "Monk", null, 8, 16 },
        { "24.S01E01.720p.mkv", MediaKind.Episode, "24", null, 1, 1 },
        { "Shows/The Wire/Season 4/The Wire S04E13 Final Grades.mkv", MediaKind.Episode, "The Wire", null, 4, 13 },
        { "Chernobyl.S01E05.Vichnaya.Pamyat.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb.mkv", MediaKind.Episode, "Chernobyl", null, 1, 5 },
        { "Show S01E01 1080p.mkv", MediaKind.Episode, "Show", null, 1, 1 },
        { "Doctor.Who.2005.S01E01.Rose.720p.mkv", MediaKind.Episode, "Doctor Who", 2005, 1, 1 },
        { "Doctor Who (2005) - S01E01 - Rose.mkv", MediaKind.Episode, "Doctor Who", 2005, 1, 1 },
        { "Archer (2009) S01E01 Mole Hunt.mkv", MediaKind.Episode, "Archer", 2009, 1, 1 },
        { "Show/Specials/S00E01 - Behind.mkv", MediaKind.Episode, "Show", null, 0, 1 },

        // ---- Episodes: other numbering schemes ----
        { "Friends.1x05.The.One.with.the.East.German.Laundry.Detergent.avi", MediaKind.Episode, "Friends", null, 1, 5 },
        { "Seinfeld - 3x12 - The Pen.mkv", MediaKind.Episode, "Seinfeld", null, 3, 12 },
        { "Lost Season 2 Episode 05.mkv", MediaKind.Episode, "Lost", null, 2, 5 },
        { "[SubsPlease] Sousou no Frieren - 05 (1080p) [A1B2C3D4].mkv", MediaKind.Episode, "Sousou no Frieren", null, 1, 5 },
        { "[Erai-raws] One Piece - 1090 [1080p][Multiple Subtitle].mkv", MediaKind.Episode, "One Piece", null, 1, 1090 },
        { "[Group] Show Title 12 [720p].mkv", MediaKind.Episode, "Show Title", null, 1, 12 },
        { "Cowboy Bebop - 05 - Ballad of Fallen Angels.mkv", MediaKind.Episode, "Cowboy Bebop", null, 1, 5 },
        { "Show/Season 1/01 - Pilot.mkv", MediaKind.Episode, "Show", null, 1, 1 },
        { "Show/Season 1/Episode 05.mkv", MediaKind.Episode, "Show", null, 1, 5 },
        { "Show/Season 02/E07.mkv", MediaKind.Episode, "Show", null, 2, 7 },
        { "The Daily Show 2024-03-15.mkv", MediaKind.Episode, "The Daily Show", null, null, null },
        { "The.Daily.Show.2024.03.15.Guest.Name.720p.WEB.mkv", MediaKind.Episode, "The Daily Show", null, null, null },

        // ---- Non-primary content ----
        { "Movie (2010)/Extras/Making Of.mkv", MediaKind.Extra, "Making Of", null, null, null },
        { "Movie.2010.1080p-sample.mkv", MediaKind.Extra, "Movie 2010 1080p-sample", null, null, null },
        { "Trailers/foo.mp4", MediaKind.Extra, "foo", null, null, null },
        { "sample.mkv", MediaKind.Extra, "sample", null, null, null },
        { ".mkv", MediaKind.Unknown, ".mkv", null, null, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses(string path, MediaKind kind, string title, int? year, int? season, int? episode)
    {
        var p = FilenameParser.Parse(path);
        Assert.Equal(kind, p.Kind);
        if (kind != MediaKind.Extra && kind != MediaKind.Unknown) Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episode);
    }

    [Fact] public void CoversAtLeastFiftyRealWorldNames() => Assert.True(Cases.Count >= 50);

    [Theory]
    [InlineData("Stranger Things S04E01-E02.mkv", 1, 2)]
    [InlineData("Stranger.Things.S04E01E02.1080p.mkv", 1, 2)]
    [InlineData("Show.Name.S01E01-03.1080p.mkv", 1, 3)]
    [InlineData("Show.Name.S01E05.1080p.mkv", 5, null)]
    public void MultiEpisode(string path, int first, int? last)
    {
        var p = FilenameParser.Parse(path);
        Assert.Equal(first, p.Episode);
        Assert.Equal(last, p.EpisodeEnd);
    }

    [Fact]
    public void ReleaseInfoAndEpisodeTitle()
    {
        var p = FilenameParser.Parse("Breaking.Bad.S01E01.Pilot.720p.BluRay.x264-DEMAND.mkv");
        Assert.Equal("Pilot", p.EpisodeTitle);
        Assert.Equal("720p", p.Resolution);
        Assert.Equal("BluRay", p.Source);
        Assert.Equal("1080p", FilenameParser.Parse("X.2020.1080p.WEB-DL.mkv").Resolution);
        Assert.Equal("Web", FilenameParser.Parse("X.2020.1080p.WEB-DL.mkv").Source);
    }

    [Fact]
    public void DateEpisodeCarriesAirDate()
    {
        var p = FilenameParser.Parse("The Daily Show 2024-03-15.mkv");
        Assert.Equal("2024-03-15", p.AirDate);
        Assert.Null(p.Year); // the year belongs to the date
    }

    [Fact]
    public void WindowsSeparatorsAreAccepted()
        => Assert.Equal(3, FilenameParser.Parse(@"Show\Season 1\Show - S01E03.mkv").Episode);

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void EmptyIsUnknown(string path) => Assert.Equal(MediaKind.Unknown, FilenameParser.Parse(path).Kind);

    [Fact]
    public void PathologicalLongInputIsFast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FilenameParser.Parse(new string('a', 5000) + " - " + new string('1', 5000) + ".mkv");
        FilenameParser.Parse(string.Join(" ", Enumerable.Repeat("S01E01-", 800)) + ".mkv");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }
}
