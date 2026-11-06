using MediaServer.Api.Library;
using MediaServer.Api.Probing;

namespace MediaServer.Api.Hls;

public enum VideoMode { Copy, Encode }
public enum AudioMode { None, Copy, Aac }
public enum HlsSegments { MpegTs, Fmp4 }

/// <param name="VideoStream">Absolute ffprobe stream index.</param>
/// <param name="AudioStream">Absolute ffprobe stream index, or null when there is no audio.</param>
/// <param name="VideoTag">Container tag to force on copied video (hvc1 for HEVC: Safari and Chrome reject hev1 in HLS).</param>
/// <param name="Vod">
/// True for transcodes of files with a known duration: the full-length playlist exists up front, ffmpeg can start at any segment,
/// and a seek far from the encoder restarts it there. (Copied video keeps an EVENT playlist: its segment boundaries depend on the
/// source's keyframes, so they can't be predicted.)
/// </param>
public sealed record HlsPlan(int VideoStream, int? AudioStream, VideoMode Video, AudioMode Audio,
    HlsSegments Segments = HlsSegments.MpegTs, string? VideoTag = null, string Reason = "",
    bool Vod = false, double DurationSeconds = 0, int? MaxWidth = null, int? VideoKbps = null, int AudioKbps = 192,
    bool Live = false, bool Deinterlace = false, int? BurnSubtitle = null)
{
    /// <summary>"remux" when the video is untouched (cheap), otherwise "transcode".</summary>
    public string Mode => Video == VideoMode.Copy ? "remux" : "transcode";
}

/// <summary>Copies whatever the client can already decode, and re-encodes only what it cannot (or what the user asked to shrink).</summary>
public static class HlsPlanner
{
    public static HlsPlan? Decide(MediaInfo info, HlsOptions opts, ClientCaps? caps = null, string? fileExtension = null,
        bool forceTranscode = false, string? quality = null, int? audioIndex = null, int? burnSubtitle = null)
    {
        if (info.Video.Count == 0) return null;
        var v = info.Video[0];
        var d = PlaybackPolicy.Decide(info, fileExtension, caps ?? ClientCaps.Baseline);
        // Picture-based subtitles (PGS, DVD) can't be sent to a browser as text: they're drawn onto the video, which means re-encoding it.
        var burn = burnSubtitle is { } bs && info.Subtitles.Any(x => x.Index == bs) ? bs : (int?)null;

        // A chosen quality means "re-encode to this size/bitrate". It's ignored for sources smaller than the choice.
        var q = HlsQuality.Find(quality) is { } found && found.AppliesTo(v.Width) ? found : null;

        var copy = q is null && !forceTranscode && burn is null && d.VideoSupported && d.VideoKey is "h264" or "hevc" or "hevc10";

        // A chosen audio track (another language, a commentary) wins over the file's default one.
        var a = (audioIndex is { } ai ? info.Audio.FirstOrDefault(x => x.Index == ai) : null) ?? info.Audio.FirstOrDefault(x => x.IsDefault) ?? info.Audio.FirstOrDefault();
        var vod = !copy && info.DurationSeconds is > 0;
        // Segmented transcodes re-encode audio too: copied AAC can't be cut at exact segment boundaries after a seek.
        var audio = a is null ? AudioMode.None
            : !vod && string.Equals(a.Codec, "aac", StringComparison.OrdinalIgnoreCase) && (a.Channels ?? 2) <= 2 ? AudioMode.Copy
            : AudioMode.Aac;

        // HEVC only travels in fragmented MP4; H.264 keeps the MPEG-TS path that is proven everywhere.
        var hevc = copy && d.VideoKey is "hevc" or "hevc10";
        var reason = copy ? d.Reason.StartsWith("Direct stream") ? d.Reason : "Direct stream, video untouched"
            : burn is not null ? "Transcoding: subtitles are drawn onto the picture"
            : q is not null ? $"Transcoding to {q.Name} (your choice)"
            : forceTranscode ? "Transcoding: direct stream failed in this browser" : d.Reason;

        return new HlsPlan(v.Index, a?.Index, copy ? VideoMode.Copy : VideoMode.Encode, audio,
            hevc ? HlsSegments.Fmp4 : HlsSegments.MpegTs, hevc ? "hvc1" : null, reason,
            vod, vod ? info.DurationSeconds!.Value : 0, q?.MaxWidth, q?.VideoKbps, q?.AudioKbps ?? 192, BurnSubtitle: burn);
    }

    /// <summary>
    /// Live channels: copy the video when the browser can decode it and it is progressive (browsers don't deinterlace), otherwise
    /// encode to H.264 with deinterlacing. Audio is copied when already stereo AAC, else converted.
    /// </summary>
    public static HlsPlan? DecideLive(MediaInfo info, HlsOptions opts, ClientCaps? caps = null, bool forceTranscode = false)
    {
        if (info.Video.Count == 0) return null;
        var v = info.Video[0];
        var d = PlaybackPolicy.Decide(info, ".ts", caps ?? ClientCaps.Baseline);
        var interlaced = v.FieldOrder is "tt" or "bb" or "tb" or "bt";
        var copy = !forceTranscode && !interlaced && d.VideoSupported && d.VideoKey is "h264" or "hevc" or "hevc10";

        var a = info.Audio.FirstOrDefault(x => x.IsDefault) ?? info.Audio.FirstOrDefault();
        var audio = a is null ? AudioMode.None
            : string.Equals(a.Codec, "aac", StringComparison.OrdinalIgnoreCase) && (a.Channels ?? 2) <= 2 ? AudioMode.Copy
            : AudioMode.Aac;

        var hevc = copy && d.VideoKey is "hevc" or "hevc10";
        var reason = copy ? "Live · direct stream, video untouched"
            : forceTranscode ? "Live · transcoding: direct stream failed in this browser"
            : interlaced ? "Live · transcoding: the channel is interlaced" : $"Live · transcoding: {d.Reason.Replace("Transcoding: ", "")}";

        return new HlsPlan(v.Index, a?.Index, copy ? VideoMode.Copy : VideoMode.Encode, audio,
            hevc ? HlsSegments.Fmp4 : HlsSegments.MpegTs, hevc ? "hvc1" : null, reason, Live: true, Deinterlace: !copy && interlaced);
    }
}
