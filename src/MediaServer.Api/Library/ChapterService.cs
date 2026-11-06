using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Processes;

namespace MediaServer.Api.Library;

public sealed record Chapter(double Start, double End, string Title);
public sealed record SkipMarker(string Kind, double Start, double End);
public sealed record ChapterInfo(IReadOnlyList<Chapter> Chapters, IReadOnlyList<SkipMarker> Markers);

/// <summary>
/// Chapter marks stored in the file, and the "skip intro" / "skip credits" buttons they make possible. Read lazily the first time a title is
/// played (one quick ffprobe) and cached. Files without chapters simply have no skip buttons.
/// </summary>
public sealed partial class ChapterService(Database db, IMediaResolver media, IProcessRunner runner, IConfiguration config, TimeProvider time)
{
    private readonly string _ffprobe = config["Tools:FfprobePath"] ?? "ffprobe";

    [GeneratedRegex(@"\b(intro|introduction|opening|op|recap|previously|title sequence|theme)\b", RegexOptions.IgnoreCase)] private static partial Regex IntroRx();
    [GeneratedRegex(@"\b(end credits|credits|ending|outro|ed|closing)\b", RegexOptions.IgnoreCase)] private static partial Regex CreditsRx();

    public async Task<ChapterInfo?> GetAsync(long mediaId, CancellationToken ct)
    {
        var path = media.ResolvePath(mediaId);
        if (path is null) return null;
        long mtime;
        try { var fi = new FileInfo(path); if (!fi.Exists) return null; mtime = fi.LastWriteTimeUtc.Ticks; } catch (IOException) { return null; }

        using (var c = db.Open())
        {
            var cached = Database.Query(c, "SELECT json FROM media_chapters WHERE media_id=@i AND mtime_ticks=@m", r => r.GetString(0), ("@i", mediaId), ("@m", mtime));
            if (cached.Count == 1) return Build(Parse(cached[0]));
        }

        List<Chapter> chapters;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
            var r = await runner.RunAsync(_ffprobe, ["-v", "error", "-print_format", "json", "-show_chapters", "-i", path], cts.Token);
            chapters = r.ExitCode == 0 ? ParseProbe(r.StdOut) : [];
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception) { return Build([]); }   // not cached: try again next time

        using var db2 = db.Open();
        Database.Execute(db2, null, "INSERT INTO media_chapters(media_id, mtime_ticks, json, fetched_at) VALUES(@i,@m,@j,@t) ON CONFLICT(media_id) DO UPDATE SET mtime_ticks=excluded.mtime_ticks, json=excluded.json, fetched_at=excluded.fetched_at",
            ("@i", mediaId), ("@m", mtime), ("@j", JsonSerializer.Serialize(chapters)), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        return Build(chapters);
    }

    internal static List<Chapter> ParseProbe(string json)
    {
        var list = new List<Chapter>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var ch in arr.EnumerateArray().Take(500))
            {
                if (!double.TryParse(ch.TryGetProperty("start_time", out var s) ? s.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) continue;
                if (!double.TryParse(ch.TryGetProperty("end_time", out var e) ? e.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) continue;
                var title = ch.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (end > start && start >= 0 && double.IsFinite(end)) list.Add(new Chapter(start, end, title.Length > 120 ? title[..120] : title));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private static List<Chapter> Parse(string json) { try { return JsonSerializer.Deserialize<List<Chapter>>(json) ?? []; } catch (JsonException) { return []; } }

    /// <summary>Which chapters are an intro or the credits, judged by their titles.</summary>
    internal static ChapterInfo Build(List<Chapter> chapters)
    {
        var markers = new List<SkipMarker>();
        foreach (var c in chapters)
        {
            var len = c.End - c.Start;
            if (IntroRx().IsMatch(c.Title) && !CreditsRx().IsMatch(c.Title) && len is >= 5 and <= 600) markers.Add(new SkipMarker("intro", c.Start, c.End));
            else if (CreditsRx().IsMatch(c.Title) && len >= 5) markers.Add(new SkipMarker("credits", c.Start, c.End));
        }
        return new ChapterInfo(chapters, markers);
    }
}
