namespace MediaServer.Api.Probing;

public sealed record VideoTrack(int Index, string? Codec, string? Profile, int? Width, int? Height,
    double? FrameRate, string? PixelFormat, long? BitRate, string? CodecTag = null, string? FieldOrder = null);

public sealed record AudioTrack(int Index, string? Codec, int? Channels, string? ChannelLayout,
    int? SampleRate, string? Language, string? Title, bool IsDefault, long? BitRate);

public sealed record SubtitleTrack(int Index, string? Codec, string? Language, string? Title,
    bool IsDefault, bool IsForced);

public sealed record MediaInfo(string? Container, double? DurationSeconds, long? BitRate, long? SizeBytes,
    IReadOnlyList<VideoTrack> Video, IReadOnlyList<AudioTrack> Audio, IReadOnlyList<SubtitleTrack> Subtitles);
