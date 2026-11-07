using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Arr;

/// <summary>
/// Cover art for the Downloads page. Radarr and Sonarr only know where artwork lives on the internet, and the page's security policy only
/// allows images from this server, so the server fetches each picture once and serves it from disk. The address comes from the client, so
/// only https pictures on the artwork sites those apps use are ever fetched.
/// </summary>
public sealed partial class ArrArt
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "image.tmdb.org", "artworks.thetvdb.com", "assets.fanart.tv" };
    private const long MaxBytes = 6 * 1024 * 1024;
    [GeneratedRegex(@"^/[A-Za-z0-9_./-]{3,200}\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase)] private static partial Regex PathRx();

    private readonly HttpClient _http;
    private readonly string _dir;

    public ArrArt(HttpClient http, IConfiguration config)
    {
        _http = http;
        _dir = Path.Combine(Path.GetFullPath(config["Images:CacheDir"] ?? "data/images"), "arr");
    }

    /// <summary>The page's address for a poster, or null if the app has none (or it isn't on an allowed site).</summary>
    public static string? PosterPath(JsonNode? images)
    {
        if (images is not JsonArray list) return null;
        var url = list.Select(i => (Type: (string?)i?["coverType"], Url: (string?)i?["remoteUrl"])).FirstOrDefault(i => string.Equals(i.Type, "poster", StringComparison.OrdinalIgnoreCase)).Url;
        return Allowed(url) is { } u ? "/api/arr/art?u=" + Uri.EscapeDataString(Smaller(u.AbsoluteUri)) : null;
    }

    /// <summary>TMDB serves the same picture in many sizes; a 342-pixel poster is plenty for these cards.</summary>
    internal static string Smaller(string url) => url.Replace("/t/p/original/", "/t/p/w342/", StringComparison.Ordinal);

    internal static Uri? Allowed(string? url) =>
        url is { Length: <= 400 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
        && string.IsNullOrEmpty(u.Query) && Hosts.Contains(u.Host) && PathRx().IsMatch(u.AbsolutePath) && !u.AbsolutePath.Contains("..") ? u : null;

    /// <returns>The cached file and its content type, or null if the address isn't allowed or the picture can't be had.</returns>
    public async Task<(string File, string ContentType)?> GetAsync(string url, CancellationToken ct)
    {
        if (Allowed(url) is not { } uri) return null;
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var local = Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant()[..40] + ext);
        var type = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
        if (File.Exists(local)) return (local, type);

        Directory.CreateDirectory(_dir);
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxBytes || resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
        var tmp = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) return null;
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            File.Move(tmp, local, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return (local, type);
    }
}
