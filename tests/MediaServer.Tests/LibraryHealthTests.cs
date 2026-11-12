using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Probing;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Tests;

public sealed class LibraryHealthTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    private LibraryHealthService Svc(bool tmdb = false) => new(_t.Db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = tmdb ? "k" : null }).Build());
    private long Add(string path, string kind, string title, int? year = null, int? season = null, int? episode = null, long size = 100, long? tmdb = null, string? error = null, string? codec = null)
    {
        var id = _t.Insert(path, kind, title, year);
        using var c = _t.Db.Open();
        var info = codec is null ? null : JsonSerializer.Serialize(new MediaInfo("matroska", 60, null, size, [new VideoTrack(0, codec, null, 1920, 1080, 24, "yuv420p", null)], [new AudioTrack(1, "aac", 2, null, 48000, null, null, true, null)], []));
        Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, size=@z, tmdb_id=@t, probe_error=@p, info_json=@j WHERE id=@i", ("@s", season), ("@e", episode), ("@z", size), ("@t", tmdb), ("@p", error), ("@j", info), ("@i", id));
        return id;
    }

    [Fact] public void FindsDuplicateFilms_ByTitleAndYear_AndByTmdbId_NotDifferentYears()
    {
        Add("/m/Film (2020) 1080p.mkv", "Movie", "Film", 2020, size: 5000);
        Add("/m/Film (2020) 720p.mkv", "Movie", "film", 2020, size: 2000);          // same title, other case
        Add("/m/Film (1999).mkv", "Movie", "Film", 1999, size: 900);                // a different film with the same title
        Add("/a/Other.mkv", "Movie", "Other A", 2010, tmdb: 77, size: 400); Add("/b/Other Cut.mkv", "Movie", "Other B", 2010, tmdb: 77, size: 300);   // matched to the same TMDB entry
        var h = Svc().Report();
        Assert.Equal(2, h.Duplicates.Count);
        var film = Assert.Single(h.Duplicates, g => g.Title.StartsWith("Film"));
        Assert.Equal("Film (2020)", film.Title); Assert.Equal(2, film.Files.Count); Assert.Equal(2000, film.WastedBytes);
        Assert.Equal("Film (2020) 1080p.mkv", film.Files[0].Name);                  // the biggest copy comes first and is the one to keep
        Assert.Equal(2300, h.DuplicateWastedBytes);
    }

    [Fact] public void FindsDuplicateEpisodes_ButNotDifferentEpisodes()
    {
        Add("/s/Show S01E01 a.mkv", "Episode", "Show", season: 1, episode: 1); Add("/s/Show S01E01 b.mkv", "Episode", "Show", season: 1, episode: 1);
        Add("/s/Show S01E02.mkv", "Episode", "Show", season: 1, episode: 2);
        var g = Assert.Single(Svc().Report().Duplicates);
        Assert.Equal("Show S01E01", g.Title);
    }

    [Fact] public void ReportsOnlyFileNames_NeverFolders()
    {
        Add("/very/private/folder/Film.mkv", "Movie", "Film", 2020, error: "ffprobe failed on /very/private/folder/Film.mkv");
        var h = Svc().Report();
        var json = JsonSerializer.Serialize(h);
        Assert.DoesNotContain("private", json); Assert.Equal("Film.mkv", h.Unreadable[0].Name);
        Assert.Equal(1, h.UnreadableCount);
    }

    [Fact] public void FlagsFilesABrowserCannotDecode_ButNotOnesItCan()
    {
        Add("/m/Old.mpg", "Movie", "Old", 1995, codec: "mpeg2video"); Add("/m/Fine.mp4", "Movie", "Fine", 2020, codec: "h264");
        var h = Svc().Report();
        Assert.Equal("Old.mpg", Assert.Single(h.NeedsTranscoding).Name);
        Assert.Equal(1, h.NeedsTranscodingCount);
    }

    [Fact] public void CountsUnmatchedTitles_OnlyWhenTmdbIsSetUp()
    {
        Add("/m/A.mkv", "Movie", "A", 2000, tmdb: 5); Add("/m/B.mkv", "Movie", "B", 2001);
        Assert.Equal(0, Svc(tmdb: false).Report().Unmatched);
        var h = Svc(tmdb: true).Report();
        Assert.Equal(1, h.Unmatched); Assert.True(h.TmdbConfigured); Assert.Equal(2, h.TotalFiles);
    }

    [Fact] public void AnEmptyLibrary_IsHealthy()
    {
        var h = Svc().Report();
        Assert.Equal(0, h.TotalFiles); Assert.Empty(h.Duplicates); Assert.Empty(h.Unreadable); Assert.Empty(h.NeedsTranscoding);
    }
}
