using System.Text.RegularExpressions;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Downloads TMDB artwork once and serves it from local disk. Neither the size nor the path ever comes from the client
/// unvalidated: sizes are whitelisted and paths must look like "/abc123.jpg", so nothing can escape the cache directory.
/// </summary>
public sealed partial class ImageCache
{
    private static readonly HashSet<string> Sizes = ["w92", "w154", "w185", "w342", "w500", "w780", "w1280", "original"];
    [GeneratedRegex(@"^/[A-Za-z0-9_-]{5,80}\.(jpg|jpeg|png|webp)$")] private static partial Regex PathRx();

    private readonly HttpClient _http;
    private readonly string _dir;

    public ImageCache(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(config["Tmdb:ImageBaseUrl"] ?? "https://image.tmdb.org/t/p/");
        _dir = Path.GetFullPath(config["Images:CacheDir"] ?? "data/images");
    }

    public static bool IsValidSize(string size) => Sizes.Contains(size);

    /// <returns>Local file path, or null if the size/path is invalid or TMDB doesn't have the image.</returns>
    public async Task<string?> GetAsync(string size, string tmdbPath, CancellationToken ct)
    {
        if (!Sizes.Contains(size) || !PathRx().IsMatch(tmdbPath)) return null;
        var local = Path.Combine(_dir, size, tmdbPath.TrimStart('/'));
        if (File.Exists(local)) return local;

        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        using var resp = await _http.GetAsync($"{size}{tmdbPath}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var tmp = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs, ct);
            File.Move(tmp, local, overwrite: true); // atomic publish; concurrent downloaders are harmless
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return local;
    }
}
