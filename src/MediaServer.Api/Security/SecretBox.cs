using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.Security;

/// <summary>
/// Encrypts the few secrets the server stores (TMDB token, playlist URLs, stream URLs, the two-factor secret) so a copy of the database
/// or a backup is useless without the key. AES-256-GCM with a random nonce; the value's name is bound in as associated data, so a
/// ciphertext can't be moved from one field to another. The key lives outside the database: in <c>Security:SecretKey</c> (base64, 32 bytes,
/// for example from a Kubernetes secret) or, if that isn't set, in a <c>secret.key</c> file created next to the database.
/// </summary>
public sealed class SecretBox
{
    public const string Prefix = "enc:v1:";
    private readonly byte[] _key;
    public string KeySource { get; }

    public SecretBox(IConfiguration config)
    {
        var fromConfig = config["Security:SecretKey"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            _key = ParseKey(fromConfig.Trim());
            KeySource = "environment";
            return;
        }
        var path = config["Security:SecretKeyFile"] is { Length: > 0 } f ? Path.GetFullPath(f)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db"))!, "secret.key");
        _key = LoadOrCreate(path);
        KeySource = "file";
    }

    /// <summary>For tests and tools: a box with a given key.</summary>
    public SecretBox(byte[] key) { if (key.Length != 32) throw new ArgumentException("The key must be 32 bytes."); _key = key; KeySource = "memory"; }

    public static bool IsProtected(string? s) => s is not null && s.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));
        var blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, 12); cipher.CopyTo(blob, 28);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>The original text. A value that was never encrypted (an older install) comes back as it is.</summary>
    /// <exception cref="CryptographicException">The value is encrypted but this key can't open it (wrong key, or it was tampered with).</exception>
    public string Unprotect(string stored, string context)
    {
        if (!IsProtected(stored)) return stored;
        byte[] blob;
        try { blob = Convert.FromBase64String(stored[Prefix.Length..]); }
        catch (FormatException) { throw new CryptographicException("Malformed secret"); }
        if (blob.Length < 28) throw new CryptographicException("Malformed secret");
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }

    public string? TryUnprotect(string? stored, string context)
    {
        if (stored is null) return null;
        try { return Unprotect(stored, context); } catch (CryptographicException) { return null; }
    }

    /// <summary>A stable fingerprint of a value (to notice that it changed) that can't be used to guess the value.</summary>
    public string Fingerprint(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("fp:" + value)))[..32];

    private static byte[] ParseKey(string s)
    {
        try { var k = Convert.FromBase64String(s); if (k.Length == 32) return k; } catch (FormatException) { }
        throw new InvalidOperationException("Security:SecretKey must be 32 random bytes, base64-encoded (for example: openssl rand -base64 32).");
    }

    private static byte[] LoadOrCreate(string path)
    {
        if (File.Exists(path)) return ParseKey(File.ReadAllText(path).Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        // create-new so two processes starting together can't overwrite each other's key
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            fs.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(key) + "\n"));
        }
        catch (IOException) when (File.Exists(path)) { return ParseKey(File.ReadAllText(path).Trim()); }
        return key;
    }
}
