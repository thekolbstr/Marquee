using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Probing;

namespace MediaServer.Tests;

public class PlaybackPolicyTests
{
    public static readonly ClientCaps Chrome = ClientCaps.From("h264,hevc,hevc10,vp9,av1", "aac,mp3,opus,vorbis,flac", "mp4,webm,mkv");
    public static readonly ClientCaps Safari = ClientCaps.From("h264,hevc,hevc10", "aac,mp3,flac,ac3,eac3", "mp4");
    public static readonly ClientCaps Firefox = ClientCaps.From("h264,vp9,av1", "aac,mp3,opus,vorbis,flac", "mp4,webm");

    private static MediaInfo Info(string vcodec, string pix, int h, params (string Codec, bool Default)[] audio) =>
        new("x", 60, 1, 1, [new VideoTrack(0, vcodec, null, 1920, h, 24, pix, null)],
            audio.Select((a, i) => new AudioTrack(i + 1, a.Codec, 2, null, 48000, null, null, a.Default, null)).ToList(), []);
    private static MediaInfo Info(string v, string pix, string audio) => Info(v, pix, 1080, (audio, true));

    [Theory]
    // ---- baseline client (says nothing): h264/aac in mp4 only
    [InlineData("h264", "yuv420p", "aac", ".mp4", "base", PlayMode.Direct)]
    [InlineData("h264", "yuv420p", "aac", ".mkv", "base", PlayMode.DirectStream)]   // container not playable => repackage, don't re-encode
    [InlineData("h264", "yuv420p", "ac3", ".mp4", "base", PlayMode.DirectStream)]   // only the audio needs work
    [InlineData("hevc", "yuv420p", "aac", ".mp4", "base", PlayMode.Transcode)]
    [InlineData("vp9", "yuv420p", "opus", ".webm", "base", PlayMode.Transcode)]   // baseline = h264 only; clients report the rest
    // ---- Chrome-like: HEVC + MKV natively, no AC3/EAC3/DTS
    [InlineData("hevc", "yuv420p", "aac", ".mp4", "chrome", PlayMode.Direct)]
    [InlineData("hevc", "yuv420p", "aac", ".mkv", "chrome", PlayMode.Direct)]
    [InlineData("hevc", "yuv420p10le", "aac", ".mkv", "chrome", PlayMode.Direct)]
    [InlineData("hevc", "yuv420p10le", "eac3", ".mkv", "chrome", PlayMode.DirectStream)] // the common case: copy video, AAC audio
    [InlineData("hevc", "yuv420p", "dts", ".mkv", "chrome", PlayMode.DirectStream)]
    [InlineData("h264", "yuv420p", "aac", ".avi", "chrome", PlayMode.DirectStream)]     // avi is never native
    [InlineData("vp9", "yuv420p", "opus", ".webm", "chrome", PlayMode.Direct)]
    [InlineData("av1", "yuv420p", "aac", ".mp4", "chrome", PlayMode.Direct)]
    // ---- Safari-like: HEVC + AC3/EAC3 in MP4, no MKV
    [InlineData("hevc", "yuv420p", "eac3", ".mp4", "safari", PlayMode.Direct)]
    [InlineData("hevc", "yuv420p", "eac3", ".mkv", "safari", PlayMode.DirectStream)]
    [InlineData("hevc", "yuv420p", "dts", ".mp4", "safari", PlayMode.DirectStream)]
    // ---- Firefox-like: no HEVC => the video really must be re-encoded
    [InlineData("hevc", "yuv420p", "aac", ".mp4", "firefox", PlayMode.Transcode)]
    [InlineData("hevc", "yuv420p10le", "aac", ".mkv", "firefox", PlayMode.Transcode)]
    // ---- nothing can play these
    [InlineData("vc1", "yuv420p", "ac3", ".mkv", "chrome", PlayMode.Transcode)]
    [InlineData("mpeg4", "yuv420p", "mp3", ".avi", "chrome", PlayMode.Transcode)]
    [InlineData("h264", "yuv444p", "aac", ".mp4", "chrome", PlayMode.Transcode)]       // 4:4:4
    [InlineData("h264", "yuv420p10le", "aac", ".mkv", "chrome", PlayMode.Transcode)]    // 10-bit H.264
    [InlineData("hevc", "yuv444p", "aac", ".mkv", "chrome", PlayMode.Transcode)]
    public void Matrix(string v, string pix, string audio, string ext, string client, PlayMode expected)
    {
        var caps = client switch { "chrome" => Chrome, "safari" => Safari, "firefox" => Firefox, _ => ClientCaps.Baseline };
        Assert.Equal(expected, PlaybackPolicy.Decide(Info(v, pix, audio), ext, caps).Mode);
    }

    [Fact] public void DefaultAudioTrackDecides_NotAnyOtherTrack()
    {
        var info = Info("hevc", "yuv420p", 1080, ("aac", false), ("eac3", true));
        Assert.Equal(PlayMode.DirectStream, PlaybackPolicy.Decide(info, ".mkv", Chrome).Mode);            // default = eac3
        var info2 = Info("hevc", "yuv420p", 1080, ("aac", true), ("dts", false));
        Assert.Equal(PlayMode.Direct, PlaybackPolicy.Decide(info2, ".mkv", Chrome).Mode);                  // a spare DTS track doesn't matter
    }

    [Fact] public void NoAudioAndHighResolutionAreFine()
        => Assert.Equal(PlayMode.Direct, PlaybackPolicy.Decide(Info("h264", "yuv420p", 2160), ".mp4", ClientCaps.Baseline).Mode);

    [Fact] public void WebmRequiresWebmCodecs()
        => Assert.NotEqual(PlayMode.Direct, PlaybackPolicy.Decide(Info("h264", "yuv420p", "aac"), ".webm", Chrome).Mode);

    [Fact] public void EmptyCaps_MeansNothingIsSupported()
        => Assert.Equal(PlayMode.Transcode, PlaybackPolicy.Decide(Info("h264", "yuv420p", "aac"), ".mp4", ClientCaps.From("", "", "")).Mode);

    [Fact] public void NoInfoOrNoVideo_Transcodes()
    {
        Assert.Equal(PlayMode.Transcode, PlaybackPolicy.Decide(null, ".mp4", Chrome).Mode);
        Assert.Equal(PlayMode.Transcode, PlaybackPolicy.Decide(new MediaInfo("mp3", 1, 1, 1, [], [], []), ".mp3", Chrome).Mode);
    }

    [Fact] public void ReasonsExplainTheChoice()
    {
        Assert.Equal("Direct play", PlaybackPolicy.Decide(Info("hevc", "yuv420p", "aac"), ".mp4", Chrome).Reason);
        var ds = PlaybackPolicy.Decide(Info("hevc", "yuv420p", "eac3"), ".mkv", Safari).Reason;
        Assert.Contains("video untouched", ds); Assert.Contains("mkv", ds);
        var tc = PlaybackPolicy.Decide(Info("vc1", "yuv420p", "ac3"), ".mkv", Chrome).Reason;
        Assert.Contains("VC1", tc);
    }
}

public class ClientCapsTests
{
    [Fact] public void MissingMeansBaseline() => Assert.Equal(ClientCaps.Baseline.Video, ClientCaps.From(null, null, null).Video);
    [Fact] public void ExplicitEmptyMeansNone() => Assert.Empty(ClientCaps.From("", null, null).Video);

    [Fact] public void OnlyWhitelistedTokens_CaseInsensitive_Trimmed()
    {
        var c = ClientCaps.From("H264, hevc ,<script>,../x,;drop", "AAC,flac,wma", "MKV,iso");
        Assert.Equal(new[] { "h264", "hevc" }, c.Video.OrderBy(x => x));
        Assert.Equal(new[] { "aac", "flac" }, c.Audio.OrderBy(x => x));
        Assert.Equal(new[] { "mkv" }, c.Containers);
    }

    [Fact] public void OversizedInputIsIgnored() => Assert.Empty(ClientCaps.From(new string('h', 5000), null, null).Video);
    [Fact] public void DimensionsAreIndependent()
    {
        var c = ClientCaps.From("hevc", null, null);
        Assert.Contains("hevc", c.Video); Assert.Equal(ClientCaps.Baseline.Audio, c.Audio);
    }
}

public sealed class LibraryQueriesTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly LibraryQueries _q;
    public LibraryQueriesTests() => _q = new LibraryQueries(_t.Db);
    public void Dispose() => _t.Dispose();

    private long Add(string path, string kind, string title, int? year = null, int? season = null, int? ep = null, int? epEnd = null,
        string? epTitle = null, string? firstSeen = "2026-01-01", int? tmdbId = null, string? tmdbType = null, string? mtitle = null,
        string? poster = null, MediaInfo? info = null)
    {
        var id = _t.Insert(path, kind, title, year);
        using var c = _t.Db.Open();
        Database.Execute(c, null, """
            UPDATE media_files SET season=@s, episode=@e, episode_end=@ee, episode_title=@et, first_seen=@fs, tmdb_type=@tt, tmdb_id=@ti,
                match_state=@ms, info_json=@ij, duration_seconds=@d WHERE id=@id
            """, ("@s", season), ("@e", ep), ("@ee", epEnd), ("@et", epTitle), ("@fs", firstSeen), ("@tt", tmdbType), ("@ti", tmdbId),
            ("@ms", tmdbId is null ? null : "matched"), ("@ij", info is null ? null : JsonSerializer.Serialize(info)), ("@d", info?.DurationSeconds), ("@id", id));
        if (mtitle is not null)
            Database.Execute(c, null, """
                INSERT OR REPLACE INTO metadata(tmdb_type,tmdb_id,title,year,overview,poster_path,backdrop_path,rating,genres,fetched_at)
                VALUES(@t,@i,@title,@y,'Plot',@p,@b,7.5,'Drama','x')
                """, ("@t", tmdbType), ("@i", tmdbId), ("@title", mtitle), ("@y", year), ("@p", poster), ("@b", poster is null ? null : "/bk.jpg"));
        return id;
    }

    [Fact]
    public void SeriesAreGrouped_ExtrasAndDeletedAreHidden()
    {
        for (var i = 1; i <= 3; i++) Add($"BB/S01E0{i}.mkv", "Episode", "Breaking Bad", season: 1, ep: i, tmdbId: 1396, tmdbType: "tv", mtitle: "Breaking Bad", poster: "/p.jpg");
        Add("Matrix.mkv", "Movie", "The Matrix", 1999);
        Add("extra.mkv", "Extra", "Making Of");
        var gone = Add("gone.mkv", "Movie", "Gone");
        using (var c = _t.Db.Open()) Database.Exec(c, $"UPDATE media_files SET deleted_at='x' WHERE id={gone}");

        var list = _q.List(null, null, "title", 60, 0);
        Assert.Equal(2, list.Total);
        var show = list.Items.Single(i => i.Type == "series");
        Assert.Equal(("Breaking Bad", 3, "tmdb:1396"), (show.Title, show.EpisodeCount, show.Key));
        Assert.NotNull(show.PosterUrl);
        Assert.Null(list.Items.Single(i => i.Type == "movie").PosterUrl);
    }

    [Fact]
    public void SameTmdbMovieInTwoFiles_IsOneItem_WithOtherVersion()
    {
        var a = Add("A.1080p.mkv", "Movie", "Dune", 2021, tmdbId: 438631, tmdbType: "movie", mtitle: "Dune");
        var b = Add("A.4k.mkv", "Movie", "Dune", 2021, tmdbId: 438631, tmdbType: "movie", mtitle: "Dune");
        Assert.Equal(1, _q.List(null, null, "title", 60, 0).Total);
        var d = _q.Movie(a, ClientCaps.Baseline)!;
        Assert.Equal(b, Assert.Single(d.OtherVersions).MediaId);
    }

    [Fact]
    public void UnparseableFilesStillAppear_SoNothingIsUnplayable()
    {
        Add("mystery.mkv", "Unknown", "");
        var item = Assert.Single(_q.List(null, null, "title", 60, 0).Items);
        Assert.Equal("mystery", item.Title);
    }

    [Fact]
    public void Search_IsCaseAndPunctuationInsensitive_AndMatchesFilenamesAndTokens()
    {
        Add("Spider-Man.mkv", "Movie", "Spider-Man", 2002);
        Add("Other.mkv", "Movie", "Other", 2000);
        Assert.Equal("Spider-Man", Assert.Single(_q.List("spider man", null, "title", 60, 0).Items).Title);
        Assert.Equal("Spider-Man", Assert.Single(_q.List("SPIDERMAN".Replace("MAN", "-man"), null, "title", 60, 0).Items).Title);
        Assert.Empty(_q.List("zzz", null, "title", 60, 0).Items);
    }

    [Fact]
    public void Search_MatchesEpisodeSeriesByNameAndHandlesWeirdInput()
    {
        Add("BB/S01E01.mkv", "Episode", "Breaking Bad", season: 1, ep: 1);
        Assert.Single(_q.List("breaking", "series", "title", 60, 0).Items);
        Assert.Empty(_q.List("breaking", "movie", "title", 60, 0).Items);
        Assert.Empty(_q.List("'; DROP TABLE media_files;--", null, "title", 60, 0).Items); // no results, no crash
        Assert.Empty(_q.List("%", null, "title", 60, 0).Items);                              // punctuation-only is not "match everything"
        Assert.Single(_q.List("   ", null, "title", 60, 0).Items);                           // blank search = no filter
    }

    [Fact]
    public void SortingAndPaging()
    {
        Add("1.mkv", "Movie", "The Zebra", 2001, firstSeen: "2026-01-01");
        Add("2.mkv", "Movie", "Apple", 2020, firstSeen: "2026-03-01");
        Add("3.mkv", "Movie", "Mango", 2010, firstSeen: "2026-02-01");
        Assert.Equal(["Apple", "Mango", "The Zebra"], _q.List(null, null, "title", 60, 0).Items.Select(i => i.Title));  // "The" ignored
        Assert.Equal(["Apple", "Mango", "The Zebra"], _q.List(null, null, "year", 60, 0).Items.Select(i => i.Title));
        Assert.Equal(["Apple", "Mango", "The Zebra"], _q.List(null, null, "recent", 60, 0).Items.Select(i => i.Title));
        var page = _q.List(null, null, "title", 1, 1);
        Assert.Equal(3, page.Total);
        Assert.Equal("Mango", Assert.Single(page.Items).Title);
        Assert.Equal(3, _q.List(null, null, "title", 9999, -5).Items.Count); // limit clamped, negative offset tolerated
    }

    [Fact]
    public void SeriesDetail_OrdersSeasonsAndEpisodes_AndLinksNextEpisode()
    {
        var e2 = Add("b.mkv", "Episode", "Show", season: 1, ep: 2, epTitle: "Second");
        var e1 = Add("a.mkv", "Episode", "Show", season: 1, ep: 1, epTitle: "First");
        var s2 = Add("c.mkv", "Episode", "Show", season: 2, ep: 1);
        var sp = Add("d.mkv", "Episode", "Show", season: 0, ep: 1, epTitle: "Special");
        var multi = Add("m.mkv", "Episode", "Show", season: 1, ep: 3, epEnd: 4);

        var key = _q.List(null, "series", "title", 10, 0).Items.Single().Key;
        var d = _q.Series(key)!;
        Assert.Equal([0, 1, 2], d.Seasons.Select(s => s.Season!.Value));
        Assert.Equal([e1, e2, multi], d.Seasons[1].Episodes.Select(e => e.MediaId));
        Assert.Equal("First", d.Seasons[1].Episodes[0].Title);
        Assert.Equal("Episode 3", d.Seasons[1].Episodes[2].Title);

        Assert.Equal(e2, _q.Play(e1, ClientCaps.Baseline)!.NextMediaId);
        Assert.Equal(s2, _q.Play(multi, ClientCaps.Baseline)!.NextMediaId);          // season 1 -> season 2 (specials come first, skipped past)
        Assert.Null(_q.Play(s2, ClientCaps.Baseline)!.NextMediaId);
        Assert.Equal(e1, _q.Play(sp, ClientCaps.Baseline)!.NextMediaId);
        Assert.Null(_q.Series("title:nope"));
    }

    [Fact]
    public void PlayInfo_ReflectsProbeAndPolicy()
    {
        var info = new MediaInfo("mov,mp4,m4a,3gp,3g2,mj2", 120, 1, 1, [new VideoTrack(0, "h264", null, 1280, 720, 24, "yuv420p", null)],
            [new AudioTrack(1, "aac", 2, "stereo", 48000, "eng", null, true, null)], [new SubtitleTrack(2, "mov_text", "eng", null, false, false)]);
        var id = Add("x.mp4", "Movie", "X", 2000, info: info);
        var p = _q.Play(id, ClientCaps.Baseline)!;
        Assert.True(p.DirectPlay);
        Assert.Equal($"/stream/{id}", p.StreamUrl);
        Assert.Equal((1280, 720, "h264"), (p.Width, p.Height, p.VideoCodec));
        Assert.Equal("eng", Assert.Single(p.Subtitles).Language);
        Assert.Null(_q.Play(424242, ClientCaps.Baseline));
    }

    [Fact]
    public void PlayInfo_ModeFollowsTheClientsCapabilities()
    {
        var info = new MediaInfo("matroska,webm", 120, 1, 1, [new VideoTrack(0, "hevc", null, 3840, 2160, 24, "yuv420p10le", null)],
            [new AudioTrack(1, "eac3", 6, null, 48000, "eng", null, true, null)], []);
        var id = Add("Film.2160p.mkv", "Movie", "Film", 2020, info: info);
        var baseline = _q.Play(id, ClientCaps.Baseline)!;
        Assert.Equal(("transcode", false), (baseline.Mode, baseline.DirectPlay));
        var chrome = _q.Play(id, PlaybackPolicyTests.Chrome)!;
        Assert.Equal(("remux", false), (chrome.Mode, chrome.DirectPlay));           // HEVC ok, EAC3 not
        var safariMp4Id = Add("Film2.mp4", "Movie", "Film2", 2021, info: info);
        var safari = _q.Play(safariMp4Id, PlaybackPolicyTests.Safari)!;
        Assert.Equal(("direct", true), (safari.Mode, safari.DirectPlay));
        Assert.Contains("untouched", chrome.Reason);
    }

    [Fact]
    public void Movie_RejectsEpisodeIds()
    {
        var e = Add("e.mkv", "Episode", "Show", season: 1, ep: 1);
        Assert.Null(_q.Movie(e, ClientCaps.Baseline));
    }
}
