using System.Globalization;
using System.Text.RegularExpressions;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using MediaServer.Api.Processes;

namespace MediaServer.Api.Library;

public sealed record SkipScanStatus(bool Running, int Done, int Total, string? Current, DateTimeOffset? LastFinished, int Intros, int Credits, int Titles, string? Error);

/// <summary>
/// Finds opening titles and end credits so the player can offer "Skip intro" / "Skip credits" for files that have no chapters.
/// <list type="bullet">
/// <item>Episodes: the opening (first minutes) and closing (last minutes) audio of an episode is compared with the same stretches of its
/// neighbours in the season. Sound that appears in the same form in several episodes is the theme tune or the credits music.</item>
/// <item>Movies: a fade to black in the last minutes followed by a long tail is taken as the start of the credits.</item>
/// </list>
/// Nothing is written to your files. Results live in the database and are used only when the file's own chapters don't already say.
/// </summary>
public sealed partial class SkipDetector(Database db, IMediaResolver media, IProcessRunner runner, IConfiguration config, TimeProvider time, ILogger<SkipDetector> log)
{
    public const int Version = 1;
    public const double MinDuration = 300;                 // shorter files (clips, specials) are left alone
    private const double HeadWindow = 600, HeadShare = 0.4;  // look for an opening in the first 10 minutes (at most 40% of the episode)
    private const double TailWindow = 480, TailShare = 0.35;
    private const double MinIntro = 15, MaxIntro = 200, MinCredits = 15;
    private const int PeersPerEpisode = 4;

    private readonly string _ffmpeg = config["Tools:FfmpegPath"] ?? "ffmpeg";
    private readonly string _tmp = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "skip-tmp");
    private readonly object _gate = new();
    private int _running;
    private int _done, _total, _intros, _credits;
    private string? _current, _error;
    private DateTimeOffset? _lastFinished;

    public bool WantIntros => config.GetValue("Skip:DetectIntros", true);
    public bool WantCredits => config.GetValue("Skip:DetectCredits", true);
    public bool AutoScan => config.GetValue("Skip:AutoScan", true);
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public SkipScanStatus Status()
    {
        using var c = db.Open();
        var titles = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(DISTINCT media_id) FROM skip_markers"));
        lock (_gate) return new SkipScanStatus(IsRunning, _done, _total, _current, _lastFinished, _intros, _credits, titles, _error);
    }

    /// <summary>Forgets everything that was detected (chapters in the files are not affected).</summary>
    public int Clear()
    {
        using var c = db.Open();
        var n = Database.Execute(c, null, "DELETE FROM skip_markers");
        Database.Execute(c, null, "DELETE FROM skip_scans");
        Database.Execute(c, null, "DELETE FROM skip_prints");
        return n;
    }

    private sealed record Row(long Id, string Kind, string Title, int? Season, int? Episode, double Duration, long Mtime);
    private sealed record Prints(uint[] Head, double HeadOffset, uint[] Tail, double TailOffset);

    /// <summary>One pass over the library: anything new or changed since the last pass is analysed, a title at a time.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            lock (_gate) { _done = 0; _total = 0; _intros = 0; _credits = 0; _current = null; _error = null; }
            var rows = LoadRows();
            var scans = LoadScans();
            var groups = rows.Where(r => r.Kind == "Episode" && r.Season is not null).GroupBy(r => (TmdbMatcher.Key(r.Title), r.Season)).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Episode ?? 0).ToList());
            int GroupSize(Row r) => r.Kind == "Episode" && r.Season is not null ? groups[(TmdbMatcher.Key(r.Title), r.Season)].Count : 1;

            var todo = rows.Where(r =>
            {
                if (!scans.TryGetValue(r.Id, out var s) || s.Mtime != r.Mtime || s.Version != Version) return true;
                var size = GroupSize(r);
                return size > s.GroupSize && s.GroupSize < 4;      // more episodes have arrived: there is now something to compare with
            }).ToList();
            lock (_gate) _total = todo.Count;
            if (todo.Count > 0) log.LogInformation("Looking for intros and credits in {Count} titles", todo.Count);

            foreach (var row in todo)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) _current = row.Kind == "Episode" ? $"{row.Title} S{row.Season}E{row.Episode}" : row.Title;
                try
                {
                    var found = row.Kind == "Episode" && row.Season is not null
                        ? await AnalyseEpisodeAsync(row, groups[(TmdbMatcher.Key(row.Title), row.Season)], ct)
                        : await AnalyseMovieAsync(row, ct);
                    Save(row, found, GroupSize(row));
                    lock (_gate) { _intros += found.Count(m => m.Kind == "intro"); _credits += found.Count(m => m.Kind == "credits"); }
                }
                catch (OperationCanceledException) { throw; }
                catch (System.ComponentModel.Win32Exception) { lock (_gate) _error = "ffmpeg could not be started."; break; }
                catch (Exception ex)
                {
                    log.LogWarning("Intro/credits detection failed for media {Id}: {Error}", row.Id, ex.Message);
                    Save(row, [], GroupSize(row));            // don't retry a file that fails every time on each pass
                }
                finally { lock (_gate) _done++; }
            }
            if (todo.Count > 0) log.LogInformation("Intro/credits scan finished: {Intros} intros and {Credits} credits found", _intros, _credits);
        }
        catch (OperationCanceledException) { /* shutting down or cancelled */ }
        catch (Exception ex) { lock (_gate) _error = ex.Message; log.LogError(ex, "Intro/credits scan failed"); }
        finally
        {
            lock (_gate) { _current = null; _lastFinished = time.GetUtcNow(); }
            Volatile.Write(ref _running, 0);
        }
    }

    // ---------------------------------------------------------------- episodes: compare with the neighbours
    private async Task<List<SkipMarker>> AnalyseEpisodeAsync(Row row, List<Row> season, CancellationToken ct)
    {
        var found = new List<SkipMarker>();
        var peers = season.Where(p => p.Id != row.Id).OrderBy(p => Math.Abs((p.Episode ?? 0) - (row.Episode ?? 0))).Take(PeersPerEpisode).ToList();
        if (peers.Count == 0) return found;
        var mine = await EnsurePrintsAsync(row, ct);
        if (mine is null) return found;
        var theirs = new List<Prints>();
        foreach (var p in peers) { ct.ThrowIfCancellationRequested(); if (await EnsurePrintsAsync(p, ct) is { } pr) theirs.Add(pr); }
        if (theirs.Count == 0) return found;

        if (WantIntros)
        {
            var spans = theirs.Select(t => AudioFingerprint.BestMatch(mine.Head, t.Head, (int)(MinIntro / AudioFingerprint.FrameSeconds)))
                .Where(m => m is not null).Select(m => (Start: mine.HeadOffset + m!.StartA * AudioFingerprint.FrameSeconds, End: mine.HeadOffset + (m.StartA + m.Length) * AudioFingerprint.FrameSeconds)).ToList();
            if (Consensus(spans, theirs.Count) is { } s && s.End - s.Start is >= MinIntro and <= MaxIntro) found.Add(new SkipMarker("intro", Math.Round(s.Start, 1), Math.Round(s.End, 1), "audio"));
        }
        if (WantCredits)
        {
            var spans = theirs.Select(t => AudioFingerprint.BestMatch(mine.Tail, t.Tail, (int)(MinCredits / AudioFingerprint.FrameSeconds)))
                .Where(m => m is not null).Select(m => (Start: mine.TailOffset + m!.StartA * AudioFingerprint.FrameSeconds, End: mine.TailOffset + (m.StartA + m.Length) * AudioFingerprint.FrameSeconds)).ToList();
            // Credits run to the end of the file (whatever follows is a short scene we'd rather not offer to skip into).
            if (Consensus(spans, theirs.Count) is { } s && s.Start >= row.Duration * 0.5 && row.Duration - s.Start >= MinCredits) found.Add(new SkipMarker("credits", Math.Round(s.Start, 1), Math.Round(row.Duration, 1), "audio"));
        }
        return found;
    }

    /// <summary>
    /// Several neighbours should agree. With two or more matches, take the middle of what they report; a lone match is enough only when it's
    /// the only neighbour we could compare with (a season of two).
    /// </summary>
    internal static (double Start, double End)? Consensus(List<(double Start, double End)> spans, int compared)
    {
        if (spans.Count == 0) return null;
        if (compared >= 3 && spans.Count < 2) return null;
        var starts = spans.Select(s => s.Start).OrderBy(x => x).ToList(); var ends = spans.Select(s => s.End).OrderBy(x => x).ToList();
        return (starts[starts.Count / 2], ends[ends.Count / 2]);
    }

    private async Task<Prints?> EnsurePrintsAsync(Row row, CancellationToken ct)
    {
        using (var c = db.Open())
        {
            var cached = Database.Query(c, "SELECT head, head_offset, tail, tail_offset FROM skip_prints WHERE media_id=@i AND mtime_ticks=@m",
                r => new Prints(ToFrames(r.IsDBNull(0) ? [] : (byte[])r[0]), r.GetDouble(1), ToFrames(r.IsDBNull(2) ? [] : (byte[])r[2]), r.GetDouble(3)), ("@i", row.Id), ("@m", row.Mtime));
            if (cached.Count == 1) return cached[0];
        }
        var path = media.ResolvePath(row.Id);
        if (path is null) return null;
        var headLen = Math.Min(HeadWindow, row.Duration * HeadShare);
        var tailLen = Math.Min(TailWindow, row.Duration * TailShare);
        var tailOffset = Math.Max(0, row.Duration - tailLen);
        var head = await DecodeAsync(path, 0, headLen, ct);
        var tail = await DecodeAsync(path, tailOffset, tailLen, ct);
        if (head is null && tail is null) return null;
        var p = new Prints(head ?? [], 0, tail ?? [], tailOffset);
        using var c2 = db.Open();
        Database.Execute(c2, null, """
            INSERT INTO skip_prints(media_id, mtime_ticks, head, head_offset, tail, tail_offset) VALUES(@i,@m,@h,@ho,@t,@to)
            ON CONFLICT(media_id) DO UPDATE SET mtime_ticks=excluded.mtime_ticks, head=excluded.head, head_offset=excluded.head_offset, tail=excluded.tail, tail_offset=excluded.tail_offset
            """, ("@i", row.Id), ("@m", row.Mtime), ("@h", ToBytes(p.Head)), ("@ho", p.HeadOffset), ("@t", ToBytes(p.Tail)), ("@to", p.TailOffset));
        return p;
    }

    /// <summary>Reads a stretch of the first audio track as 8 kHz mono and fingerprints it. Null when there's no usable audio.</summary>
    private async Task<uint[]?> DecodeAsync(string path, double offset, double length, CancellationToken ct)
    {
        Directory.CreateDirectory(_tmp);
        var file = Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromMinutes(5));
            var r = await runner.RunAsync(_ffmpeg, ["-nostdin", "-v", "error", "-y", "-ss", Num(offset), "-t", Num(length), "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-ac", "1", "-ar", AudioFingerprint.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", file], cts.Token);
            if (r.ExitCode != 0 || !File.Exists(file)) return null;
            var bytes = await File.ReadAllBytesAsync(file, ct);
            if (bytes.Length < AudioFingerprint.FrameSize * 4) return null;
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            return AudioFingerprint.Compute(pcm);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- movies: a fade to black before a long tail
    [GeneratedRegex(@"black_start:(?<s>[0-9.]+)\s+black_end:(?<e>[0-9.]+)")] private static partial Regex BlackRx();

    private async Task<List<SkipMarker>> AnalyseMovieAsync(Row row, CancellationToken ct)
    {
        var found = new List<SkipMarker>();
        if (!WantCredits || row.Kind == "Recording") return found;
        var path = media.ResolvePath(row.Id);
        if (path is null || row.Duration < 1800) return found;                  // a short clip has no credits worth skipping
        var window = Math.Min(900, row.Duration * 0.2);
        var from = row.Duration - window;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromMinutes(10));
        var r = await runner.RunAsync(_ffmpeg, ["-nostdin", "-hide_banner", "-v", "info", "-skip_frame", "nokey", "-ss", Num(from), "-i", path, "-an", "-sn", "-dn",
            "-vf", "scale=160:-2,blackdetect=d=0.4:pix_th=0.12", "-f", "null", "-"], cts.Token);
        if (r.ExitCode != 0) return found;
        var spans = BlackRx().Matches(r.StdErr).Select(m => (Start: from + double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), End: from + double.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture)))
            .Where(b => row.Duration - b.End is >= 90 and <= 780).ToList();      // 1.5 to 13 minutes of film left after the black
        if (BlackToCredits(spans, row.Duration) is { } start) found.Add(new SkipMarker("credits", Math.Round(start, 1), Math.Round(row.Duration, 1), "black"));
        return found;
    }

    /// <summary>The credits start where the last fade to black with enough film left ends.</summary>
    internal static double? BlackToCredits(IReadOnlyList<(double Start, double End)> blacks, double duration)
        => blacks.Where(b => duration - b.End >= 90).OrderBy(b => b.End).Select(b => (double?)b.End).LastOrDefault();

    // ---------------------------------------------------------------- storage
    private List<Row> LoadRows()
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT id, kind, title, season, episode, duration_seconds, mtime_ticks FROM media_files
            WHERE deleted_at IS NULL AND kind IN ('Episode','Movie') AND duration_seconds >= @min AND title IS NOT NULL
            """, r => new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDouble(5), r.GetInt64(6)),
            ("@min", MinDuration));
    }

    private Dictionary<long, (long Mtime, int Version, int GroupSize)> LoadScans()
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT media_id, mtime_ticks, version, group_size FROM skip_scans", r => (r.GetInt64(0), (r.GetInt64(1), r.GetInt32(2), r.GetInt32(3)))).ToDictionary(x => x.Item1, x => x.Item2);
    }

    private void Save(Row row, List<SkipMarker> found, int groupSize)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Execute(c, tx, "DELETE FROM skip_markers WHERE media_id=@i", ("@i", row.Id));
        foreach (var m in found)
            Database.Execute(c, tx, "INSERT INTO skip_markers(media_id, kind, start_s, end_s, source) VALUES(@i,@k,@s,@e,@src)", ("@i", row.Id), ("@k", m.Kind), ("@s", m.Start), ("@e", m.End), ("@src", m.Source ?? "audio"));
        Database.Execute(c, tx, """
            INSERT INTO skip_scans(media_id, mtime_ticks, version, group_size, scanned_at) VALUES(@i,@m,@v,@g,@t)
            ON CONFLICT(media_id) DO UPDATE SET mtime_ticks=excluded.mtime_ticks, version=excluded.version, group_size=excluded.group_size, scanned_at=excluded.scanned_at
            """, ("@i", row.Id), ("@m", row.Mtime), ("@v", Version), ("@g", groupSize), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        tx.Commit();
    }

    /// <summary>The detected skip points for a title (empty when none were found or it hasn't been scanned).</summary>
    public IReadOnlyList<SkipMarker> MarkersFor(long mediaId)
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT kind, start_s, end_s, source FROM skip_markers WHERE media_id=@i", r => new SkipMarker(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetString(3)), ("@i", mediaId));
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    internal static byte[] ToBytes(uint[] frames) { var b = new byte[frames.Length * 4]; Buffer.BlockCopy(frames, 0, b, 0, b.Length); return b; }
    internal static uint[] ToFrames(byte[] bytes) { var f = new uint[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4); return f; }
}
