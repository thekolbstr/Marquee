namespace MediaServer.Api.Hls;

public sealed class HlsOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string TempDir { get; init; } = Path.Combine(Path.GetTempPath(), "mediaserver-hls");
    /// <summary>Concurrent transcodes. Starting one more evicts the least recently used.</summary>
    public int MaxSessions { get; init; } = 2;
    /// <summary>A session nobody has touched for this long is killed.</summary>
    public int IdleSeconds { get; init; } = 120;
    public int StartTimeoutSeconds { get; init; } = 30;
    public int SegmentSeconds { get; init; } = 6;
    /// <summary>"libx264" (CPU), or a hardware encoder: "nvenc" (NVIDIA), "videotoolbox" (Apple), "qsv" (Intel) or "vaapi" (Linux: Intel/AMD).</summary>
    public string Encoder { get; init; } = "libx264";
    public static readonly string[] Encoders = ["libx264", "nvenc", "videotoolbox", "qsv", "vaapi"];
    /// <summary>The render node VAAPI uses (config only: Hls__VaapiDevice).</summary>
    public string VaapiDevice { get; init; } = "/dev/dri/renderD128";
    public int MaxHeight { get; init; } = 1080;
    /// <summary>x264 speed preset: slower is smaller and nicer at the same quality but uses far more CPU.</summary>
    public string Preset { get; init; } = "veryfast";
    /// <summary>x264 quality for transcodes of full-quality files (lower is better and bigger).</summary>
    public int Crf { get; init; } = 21;
    public static readonly string[] Presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow"];
    /// <summary>When false, anything that would need the video re-encoded is refused (direct play and direct stream still work).</summary>
    public bool AllowTranscoding { get; init; } = true;
    public int ReapIntervalSeconds { get; init; } = 10;
    /// <summary>How long a segment request waits for the encoder before giving up (a seek may first have to restart ffmpeg).</summary>
    public int SegmentWaitSeconds { get; init; } = 45;
    /// <summary>Live channels use shorter segments so a channel change starts in a few seconds.</summary>
    public int LiveSegmentSeconds { get; init; } = 2;

    public static HlsOptions From(IConfiguration c)
    {
        var d = new HlsOptions();
        return new HlsOptions
        {
            FfmpegPath = c["Tools:FfmpegPath"] ?? d.FfmpegPath,
            TempDir = Path.GetFullPath(c["Hls:TempDir"] ?? d.TempDir),
            MaxSessions = Math.Max(1, c.GetValue("Hls:MaxSessions", d.MaxSessions)),
            IdleSeconds = Math.Max(1, c.GetValue("Hls:IdleSeconds", d.IdleSeconds)),
            StartTimeoutSeconds = Math.Max(1, c.GetValue("Hls:StartTimeoutSeconds", d.StartTimeoutSeconds)),
            SegmentSeconds = Math.Clamp(c.GetValue("Hls:SegmentSeconds", d.SegmentSeconds), 2, 20),
            Encoder = Encoders.Contains((c["Hls:Encoder"] ?? "").ToLowerInvariant()) ? c["Hls:Encoder"]!.ToLowerInvariant() : d.Encoder,
            VaapiDevice = c["Hls:VaapiDevice"] is { Length: > 0 } dev ? dev : d.VaapiDevice,
            AllowTranscoding = c.GetValue("Hls:AllowTranscoding", d.AllowTranscoding),
            MaxHeight = Math.Max(240, c.GetValue("Hls:MaxHeight", d.MaxHeight)),
            Preset = Presets.Contains((c["Hls:Preset"] ?? "").ToLowerInvariant()) ? c["Hls:Preset"]!.ToLowerInvariant() : d.Preset,
            Crf = int.TryParse(c["Hls:Crf"], out var crf) ? Math.Clamp(crf, 14, 30) : d.Crf,
            ReapIntervalSeconds = Math.Max(1, c.GetValue("Hls:ReapIntervalSeconds", d.ReapIntervalSeconds)),
            SegmentWaitSeconds = Math.Clamp(c.GetValue("Hls:SegmentWaitSeconds", d.SegmentWaitSeconds), 1, 300),
            LiveSegmentSeconds = Math.Clamp(c.GetValue("Hls:LiveSegmentSeconds", d.LiveSegmentSeconds), 1, 10),
        };
    }
}
