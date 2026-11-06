using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Probing;
using MediaServer.Api.Processes;

namespace MediaServer.Api.Library;

/// <summary>A caption track a viewer can switch on. <c>Id</c> is opaque: "e&lt;stream index&gt;" (inside the file) or "f&lt;n&gt;" (a .srt next to it).</summary>
public sealed record CaptionTrack(string Id, string Label, string? Language, bool Forced, bool IsDefault, bool Supported, string Source);

/// <summary>
/// Captions as WebVTT, which every browser can show. Text tracks are converted with ffmpeg and cached; picture-based tracks
/// (PGS, DVD) are listed but flagged unsupported. Clients pass a media ID and a track ID, never a path.
/// </summary>
public sealed class SubtitleService(Database db, IMediaResolver media, IProcessRunner runner, IConfiguration config)
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "ttml" };
    private static readonly string[] SidecarExtensions = [".srt", ".vtt", ".ass", ".ssa"];
    private const int MaxSidecars = 20;

    private readonly string _dir = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "subs");
    private readonly string _ffmpeg = config["Tools:FfmpegPath"] ?? "ffmpeg";
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public IReadOnlyList<CaptionTrack> List(long mediaId)
    {
        var path = media.ResolvePath(mediaId);
        if (path is null) return [];
        var tracks = new List<CaptionTrack>();
        foreach (var t in Embedded(mediaId))
        {
            var supported = t.Codec is not null && TextCodecs.Contains(t.Codec);
            tracks.Add(new CaptionTrack($"e{t.Index}", Label(t.Title, t.Language, t.Codec, tracks.Count), Lang(t.Language), t.IsForced, t.IsDefault, supported, "embedded"));
        }
        var n = 0;
        foreach (var f in Sidecars(path))
        {
            var lang = SidecarLanguage(path, f);
            tracks.Add(new CaptionTrack($"f{n++}", Label(null, lang, null, tracks.Count), Lang(lang), false, false, true, "file"));
        }
        return tracks;
    }

    /// <returns>The path of a cached .vtt file, or null if the track doesn't exist / can't be converted.</returns>
    public async Task<string?> GetVttAsync(long mediaId, string trackId, CancellationToken ct)
    {
        var path = media.ResolvePath(mediaId);
        if (path is null || trackId.Length is 0 or > 8) return null;
        string input;
        string[] map;
        if (trackId[0] == 'e' && int.TryParse(trackId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
        {
            var t = Embedded(mediaId).FirstOrDefault(x => x.Index == idx);
            if (t?.Codec is null || !TextCodecs.Contains(t.Codec)) return null;
            input = path; map = ["-map", $"0:{idx}"];
        }
        else if (trackId[0] == 'f' && int.TryParse(trackId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            var files = Sidecars(path);
            if (n >= files.Count) return null;
            input = files[n]; map = [];
        }
        else return null;

        long stamp;
        try { stamp = new FileInfo(input).LastWriteTimeUtc.Ticks; } catch (IOException) { return null; }
        var cached = Path.Combine(_dir, $"{mediaId}-{trackId}-{stamp:x}.vtt");
        if (File.Exists(cached)) return cached;

        // One conversion per track at a time; everyone asking meanwhile shares the result.
        var job = _inflight.GetOrAdd(cached, _ => new Lazy<Task<string?>>(() => ConvertAsync(input, map, cached)));
        try { return await job.Value.WaitAsync(ct); }
        finally { if (job.Value.IsCompleted) _inflight.TryRemove(cached, out _); }
    }

    private async Task<string?> ConvertAsync(string input, string[] map, string target)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = target + $".{Guid.NewGuid():N}.tmp";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)); // reading a subtitle track out of a big file can take a while
            var args = new List<string> { "-nostdin", "-v", "error", "-i", input };
            args.AddRange(map);
            args.AddRange(["-f", "webvtt", "-y", tmp]);
            ProcessResult r;
            try { r = await runner.RunAsync(_ffmpeg, args, cts.Token); }
            catch (OperationCanceledException) { TryDelete(tmp); return null; }
            if (r.ExitCode != 0 || !File.Exists(tmp)) { TryDelete(tmp); return null; }
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        finally { _gate.Release(); }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch (IOException) { } }

    private IReadOnlyList<SubtitleTrack> Embedded(long mediaId)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT info_json FROM media_files WHERE id=@id AND deleted_at IS NULL", r => r.IsDBNull(0) ? null : r.GetString(0), ("@id", mediaId));
        if (rows.Count == 0 || rows[0] is null) return [];
        try { return JsonSerializer.Deserialize<MediaInfo>(rows[0]!)?.Subtitles ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Subtitle files sitting next to the video, named like it ("Movie.en.srt"). Sorted so the numbering is stable.</summary>
    private static List<string> Sidecars(string videoPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoPath)!;
            var stem = Path.GetFileNameWithoutExtension(videoPath);
            return Directory.EnumerateFiles(dir)
                .Where(f => SidecarExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                    && Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal).Take(MaxSidecars).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? SidecarLanguage(string videoPath, string sidecar)
    {
        var name = Path.GetFileNameWithoutExtension(sidecar)[Path.GetFileNameWithoutExtension(videoPath).Length..].Trim('.');
        return name.Length is > 0 and <= 40 ? name : null;
    }

    private static string? Lang(string? l) => string.IsNullOrWhiteSpace(l) ? null : l.Length > 12 ? l[..12] : l;

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English", ["es"] = "Spanish", ["spa"] = "Spanish", ["fr"] = "French", ["fre"] = "French", ["fra"] = "French",
        ["de"] = "German", ["ger"] = "German", ["deu"] = "German", ["it"] = "Italian", ["ita"] = "Italian", ["pt"] = "Portuguese", ["por"] = "Portuguese",
        ["ja"] = "Japanese", ["jpn"] = "Japanese", ["ko"] = "Korean", ["kor"] = "Korean", ["zh"] = "Chinese", ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["ru"] = "Russian", ["rus"] = "Russian", ["nl"] = "Dutch", ["dut"] = "Dutch", ["nld"] = "Dutch", ["sv"] = "Swedish", ["swe"] = "Swedish",
        ["pl"] = "Polish", ["pol"] = "Polish", ["tr"] = "Turkish", ["tur"] = "Turkish", ["ar"] = "Arabic", ["ara"] = "Arabic", ["hi"] = "Hindi", ["hin"] = "Hindi",
    };

    internal static string Label(string? title, string? language, string? codec, int ordinal)
    {
        var lang = language is { Length: > 0 } l ? Names.GetValueOrDefault(l) ?? l : null;
        if (!string.IsNullOrWhiteSpace(title) && title.Length <= 60) return lang is not null && !title.Contains(lang, StringComparison.OrdinalIgnoreCase) ? $"{lang} – {title}" : title;
        return lang ?? $"Track {ordinal + 1}";
    }
}
