using MediaServer.Api.Probing;

namespace MediaServer.Api.Library;

public enum PlayMode
{
    /// <summary>The browser plays the file as-is over range requests. No server work.</summary>
    Direct,
    /// <summary>Video is copied untouched into HLS; audio is converted to AAC only if needed. Nearly free.</summary>
    DirectStream,
    /// <summary>Video is re-encoded (GPU). Only when the browser cannot decode the video at all.</summary>
    Transcode,
}

/// <param name="VideoKey">Normalised video format ("h264", "hevc", "hevc10", ...) or null if no browser can decode it.</param>
public sealed record PlaybackDecision(PlayMode Mode, string Reason, string? VideoKey, bool VideoSupported, bool AudioSupported, bool ContainerOk)
{
    public string ModeName => Mode switch { PlayMode.Direct => "direct", PlayMode.DirectStream => "remux", _ => "transcode" };
}

/// <summary>Picks the cheapest viable way to play a file for a given client: direct play, then direct stream, then transcode.</summary>
public static class PlaybackPolicy
{
    public static PlaybackDecision Decide(MediaInfo? info, string? fileExtension, ClientCaps caps)
    {
        var v = info?.Video.FirstOrDefault();
        if (info is null || v is null)
            return new(PlayMode.Transcode, "The file could not be analysed", null, false, false, false);

        var vkey = VideoKey(v);
        var videoOk = vkey is not null && caps.Video.Contains(vkey);

        // Browsers play one audio track in direct play (the default one), so that is the one that must be decodable.
        var a = info.Audio.FirstOrDefault(x => x.IsDefault) ?? info.Audio.FirstOrDefault();
        var akey = a is null ? null : AudioKey(a.Codec);
        var audioOk = a is null || (akey is not null && caps.Audio.Contains(akey));

        var ckey = ContainerKey(fileExtension);
        var containerOk = ckey is not null && caps.Containers.Contains(ckey) && CodecsFit(ckey, vkey, akey);

        // Safari refuses HEVC tagged "hev1" inside MP4 (it needs "hvc1"). Copying into HLS re-tags it, so that path still works.
        var badTag = ckey == "mp4" && vkey is "hevc" or "hevc10" && v.CodecTag == "hev1" && !caps.Video.Contains("hev1");
        if (badTag) containerOk = false;

        if (videoOk && audioOk && containerOk)
            return new(PlayMode.Direct, "Direct play", vkey, true, true, true);

        if (videoOk && vkey is "h264" or "hevc" or "hevc10")
        {
            var why = new List<string>();
            if (badTag) why.Add("its HEVC tag (hev1) isn't accepted here, so it's re-tagged as hvc1");
            else if (!containerOk) why.Add($"the {(ckey ?? fileExtension?.TrimStart('.') ?? "file")} container isn't playable here");
            if (!audioOk) why.Add($"{a?.Codec ?? "audio"} audio isn't supported (converted to AAC)");
            return new(PlayMode.DirectStream, "Direct stream, video untouched: " + string.Join("; ", why), vkey, true, audioOk, containerOk);
        }

        var pix = string.IsNullOrEmpty(v.PixelFormat) ? "" : $" {v.PixelFormat}";
        return new(PlayMode.Transcode, $"Transcoding: this browser can't decode {v.Codec?.ToUpperInvariant() ?? "this"}{pix} video",
            vkey, false, audioOk, containerOk);
    }

    public static string? VideoKey(VideoTrack v)
    {
        var pix = v.PixelFormat?.ToLowerInvariant();
        var eightBit = pix is "yuv420p" or "yuvj420p";
        return v.Codec?.ToLowerInvariant() switch
        {
            "h264" when eightBit => "h264",
            "hevc" when eightBit => "hevc",
            "hevc" when pix == "yuv420p10le" => "hevc10",
            "vp9" when eightBit => "vp9",
            "av1" when eightBit || pix == "yuv420p10le" => "av1",
            _ => null,
        };
    }

    public static string? AudioKey(string? codec) => codec?.ToLowerInvariant() switch
    {
        "aac" => "aac", "mp3" => "mp3", "opus" => "opus", "vorbis" => "vorbis", "flac" => "flac", "ac3" => "ac3", "eac3" => "eac3",
        _ => null, // dts, truehd, ... no browser decodes these
    };

    private static string? ContainerKey(string? ext) => ext?.TrimStart('.').ToLowerInvariant() switch
    {
        "mp4" or "m4v" or "mov" => "mp4",
        "webm" => "webm",
        "mkv" => "mkv",
        _ => null, // avi, ts, wmv, ... never played natively
    };

    private static bool CodecsFit(string container, string? v, string? a) => container switch
    {
        "webm" => v is "vp9" or "av1" && (a is null or "opus" or "vorbis"),
        "mp4" => a is not "vorbis",
        _ => true,
    };
}
