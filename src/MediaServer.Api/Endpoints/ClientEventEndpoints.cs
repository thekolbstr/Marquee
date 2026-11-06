using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace MediaServer.Api.Endpoints;

public sealed record ClientCapsBody(string? V, string? A, string? C);
public sealed record ClientEventBody(string? Kind, long? MediaId, string? Mode, string? Detail, string? MediaError, bool NativeHls, bool Mse, ClientCapsBody? Caps);

/// <summary>Lets the web client say why playback fell back (e.g. a browser's native HLS rejecting a stream), so it shows up in the server log.</summary>
public static partial class ClientEventEndpoints
{
    [GeneratedRegex(@"[\p{C}]")] private static partial Regex Control();

    private static readonly object Gate = new();
    private static DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private static int _count;
    public const int MaxPerMinute = 60;
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Strips control characters (log injection) and truncates.</summary>
    internal static string Clean(string? s, int max) => s is null ? "" : Control().Replace(s.Length > max ? s[..max] : s, " ");

    internal static bool Allow(DateTimeOffset now)
    {
        lock (Gate)
        {
            if (now - _windowStart > TimeSpan.FromMinutes(1)) { _windowStart = now; _count = 0; }
            return ++_count <= MaxPerMinute;
        }
    }

    public static void MapClientEventEndpoints(this WebApplication app)
    {
        app.MapPost("/api/client-events", ([FromBody] ClientEventBody e, HttpRequest req, ILoggerFactory logs, TimeProvider time) =>
        {
            if (req.ContentLength is > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!Allow(time.GetUtcNow())) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            logs.CreateLogger("ClientEvent").LogWarning(
                "Client playback issue [{Kind}] media={Media} mode={Mode} detail={Detail} mediaError={MediaError} nativeHls={Native} mse={Mse} caps(v={V} a={A} c={C}) agent={Agent}",
                Clean(e.Kind, 40), e.MediaId, Clean(e.Mode, 20), Clean(e.Detail, 300), Clean(e.MediaError, 160), e.NativeHls, e.Mse,
                Clean(e.Caps?.V, 80), Clean(e.Caps?.A, 80), Clean(e.Caps?.C, 40), Clean(req.Headers.UserAgent.ToString(), 200));
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes)); // chunked bodies are stopped by Kestrel; sized ones are refused above
    }
}
