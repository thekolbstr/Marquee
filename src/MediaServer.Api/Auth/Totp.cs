using System.Security.Cryptography;
using System.Text;

namespace MediaServer.Api.Auth;

/// <summary>Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), as used by every authenticator app.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int StepSeconds = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in s.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(ch);
            if (v < 0) throw new FormatException("Not base32");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }

    public static string Code(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static long StepOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>True if <paramref name="code"/> is valid for the current step or one either side (clock drift), and that step is newer than <paramref name="lastUsedStep"/>.</summary>
    public static bool Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        var c = (code ?? "").Replace(" ", "").Replace("-", "");
        if (c.Length != 6 || !c.All(char.IsAsciiDigit)) return false;
        var current = StepOf(now);
        for (var s = current - 1; s <= current + 1; s++)
        {
            if (lastUsedStep is { } last && s <= last) continue;                  // a code works once
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(c))) { matchedStep = s; return true; }
        }
        return false;
    }

    public static string Uri(string issuer, string account, string base32Secret)
        => $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}
