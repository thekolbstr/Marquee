using MediaServer.Api.Processes;

namespace MediaServer.Api.Probing;

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);

    /// <summary>Probes a network stream (bounded in time and data; http(s) only). Defaults to the file probe for implementations that don't care.</summary>
    Task<MediaInfo> ProbeLiveAsync(string url, CancellationToken ct) => ProbeAsync(url, ct);
}

public sealed class ProbeFailedException(string message) : Exception(message);

public static class ErrorText
{
    /// <summary>Error text reaches clients, which never see server paths: show only the file name.</summary>
    public static string Scrub(string text, params string[] paths)
    {
        text = text.Trim();
        foreach (var p in paths.Where(p => !string.IsNullOrEmpty(p)).OrderByDescending(p => p.Length))
            text = text.Replace(p, Path.GetFileName(p.TrimEnd('/', '\\')), StringComparison.Ordinal);
        return text;
    }
}

public sealed class FfprobeMediaProbe(IProcessRunner runner, IConfiguration config) : IMediaProbe
{
    private readonly string _exe = config["Tools:FfprobePath"] ?? "ffprobe";

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        // "-i <path>" keeps a path that begins with "-" from being parsed as an option.
        string[] args = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", path];
        var r = await runner.RunAsync(_exe, args, ct);
        if (r.ExitCode != 0)
            throw new ProbeFailedException($"ffprobe exited {r.ExitCode}: {ErrorText.Scrub(r.StdErr, path)}");
        try { return FfprobeParser.Parse(r.StdOut); }
        catch (System.Text.Json.JsonException ex) { throw new ProbeFailedException($"ffprobe returned invalid JSON: {ex.Message}"); }
    }

    public async Task<MediaInfo> ProbeLiveAsync(string url, CancellationToken ct)
    {
        // Only http(s)/tcp/tls reach ffprobe, and it gives up quickly: a dead channel must not hang the request.
        string[] args = ["-v", "error", "-protocol_whitelist", "http,https,tcp,tls,crypto", "-rw_timeout", "12000000", "-analyzeduration", "3000000", "-probesize", "3000000",
            "-print_format", "json", "-show_format", "-show_streams", "-i", url];
        var r = await runner.RunAsync(_exe, args, ct);
        if (r.ExitCode != 0)
            throw new ProbeFailedException($"The stream could not be opened: {System.Text.RegularExpressions.Regex.Replace(r.StdErr.Trim(), @"https?://\S+", "<stream>")}");
        try { return FfprobeParser.Parse(r.StdOut); }
        catch (System.Text.Json.JsonException) { throw new ProbeFailedException("The stream could not be analysed"); }
    }
}
