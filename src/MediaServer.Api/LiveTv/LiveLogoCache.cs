using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.LiveTv;

/// <summary>
/// Fetches channel logos server-side and serves them from disk, so the browser never talks to the IPTV source and the source URL
/// never reaches a client. Only small raster images are accepted (SVG can carry script).
/// </summary>
public sealed class LiveLogoCache(HttpClient http, IConfiguration config, TimeProvider time)
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Dictionary<string, (string Ext, string Type)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = (".png", "image/png"), ["image/jpeg"] = (".jpg", "image/jpeg"), ["image/jpg"] = (".jpg", "image/jpeg"),
        ["image/webp"] = (".webp", "image/webp"), ["image/gif"] = (".gif", "image/gif"),
    };
    private readonly string _dir = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "logos");

    /// <returns>Cached file path and content type, or null if the logo is unavailable.</returns>
    public async Task<(string Path, string ContentType)?> GetAsync(long channelId, string url, CancellationToken ct)
    {
        if (!M3uParser.IsAllowedUrl(url)) return null;
        var stem = Path.Combine(_dir, $"{channelId}-{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant()}");
        foreach (var (_, (ext, type)) in Types.DistinctBy(t => t.Value.Ext))
            if (File.Exists(stem + ext)) return (stem + ext, type);
        if (File.Exists(stem + ".fail") && time.GetUtcNow() - File.GetLastWriteTimeUtc(stem + ".fail") < TimeSpan.FromHours(1)) return null;

        Directory.CreateDirectory(_dir);
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var ctype = resp.Content.Headers.ContentType?.MediaType;
            if (!resp.IsSuccessStatusCode || ctype is null || !Types.TryGetValue(ctype, out var info) || resp.Content.Headers.ContentLength > MaxBytes)
                return Fail(stem);
            using var ms = new MemoryStream();
            await using (var s = new LimitedReadStream(await resp.Content.ReadAsStreamAsync(ct), MaxBytes)) await s.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Fail(stem);
            var tmp = stem + info.Ext + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, ms.ToArray(), ct);
            File.Move(tmp, stem + info.Ext, overwrite: true);
            return (stem + info.Ext, info.Type);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Fail(stem);
        }
    }

    private static (string, string)? Fail(string stem) { try { File.WriteAllText(stem + ".fail", ""); } catch (IOException) { } return null; }
}
