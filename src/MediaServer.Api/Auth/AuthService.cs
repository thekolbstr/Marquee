using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaServer.Api.Data;

namespace MediaServer.Api.Auth;

/// <summary>The signed-in person. <c>Username</c> is what they sign in with; <c>Name</c> is what the interface calls them.</summary>
public sealed record AuthUser(long Id, string Username, string? DisplayName = null)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

public sealed class AuthException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>PBKDF2-SHA256 with a per-password salt. The iteration count lives in the stored string so it can be raised later without locking anyone out.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 310_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iters) || iters is < 1 or > 5_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>Accounts, sign-in, sessions and brute-force protection. Cookie values are random and stored only as a SHA-256 hash.</summary>
public sealed partial class AuthService(Database db, IConfiguration config, TimeProvider time)
{
    public const string CookieName = "ms_session";
    /// <summary>How long a "keep me signed in" session lasts (Auth:SessionDays, default 30).</summary>
    public TimeSpan SessionLength => TimeSpan.FromDays(Math.Clamp(config.GetValue("Auth:SessionDays", 30), 1, 365));
    /// <summary>How long a session without "keep me signed in" survives without use (Auth:IdleHours, default 12).</summary>
    public TimeSpan IdleSessionLength => TimeSpan.FromHours(Math.Clamp(config.GetValue("Auth:IdleHours", 12), 1, 168));

    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _throttle = new();

    public bool Enabled => config.GetValue("Auth:Enabled", true);
    private int Iterations => Math.Clamp(config.GetValue("Auth:Pbkdf2Iterations", PasswordHasher.DefaultIterations), 1_000, 5_000_000);
    public bool SetupCodeRequired => !string.IsNullOrEmpty(config["Auth:SetupCode"]);

    public bool HasUsers()
    {
        using var c = db.Open();
        return Database.Scalar(c, "SELECT 1 FROM users LIMIT 1") is not null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")] private static partial Regex UsernameRx();

    private static readonly HashSet<string> Weak = new(StringComparer.OrdinalIgnoreCase)
        { "password", "password1", "12345678", "123456789", "1234567890", "qwertyui", "qwerty123", "iloveyou", "admin123", "letmein1", "11111111", "00000000" };

    internal static void ValidateCredentials(string? username, string? password)
    {
        if (username is null || !UsernameRx().IsMatch(username)) throw new AuthException("Usernames are 3–32 characters: letters, numbers, dot, dash or underscore.");
        ValidatePassword(password, username);
    }

    internal static void ValidatePassword(string? password, string? username = null)
    {
        if (password is null || password.Length < 8) throw new AuthException("Passwords need at least 8 characters.");
        if (password.Length > 200) throw new AuthException("That password is too long.");
        if (Weak.Contains(password) || (username is not null && password.Equals(username, StringComparison.OrdinalIgnoreCase))) throw new AuthException("That password is too easy to guess.");
    }

    /// <summary>Creates the first account. Only works while there are no accounts at all, and checks that atomically.</summary>
    public AuthUser CreateFirstUser(string? username, string? password, string? setupCode, string? displayName = null)
    {
        if (HasUsers()) throw new AuthException("This server already has an account. Sign in instead.", 409);
        var required = config["Auth:SetupCode"];
        if (!string.IsNullOrEmpty(required) && !FixedEquals(required, setupCode ?? ""))
            throw new AuthException("That setup code isn't right.", 403);
        ValidateCredentials(username, password);
        var display = CleanDisplayName(displayName);
        var hash = PasswordHasher.Hash(password!, Iterations);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var n = Database.Execute(c, tx, """
            INSERT INTO users(username, password_hash, created_at, display_name)
            SELECT @u, @h, @t, @d WHERE NOT EXISTS (SELECT 1 FROM users)
            """, ("@u", username), ("@h", hash), ("@t", Now()), ("@d", display));
        if (n == 0) throw new AuthException("This server already has an account. Sign in instead.", 409);
        var id = Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        tx.Commit();
        return new AuthUser(id, username!, display);
    }

    /// <summary>Checks the credentials. Unknown names cost the same time as wrong passwords, and repeated failures from one address slow down.</summary>
    public AuthUser Login(string? username, string? password, string clientKey)
    {
        var now = time.GetUtcNow();
        if (_throttle.TryGetValue(clientKey, out var t) && t.Until > now)
            throw new AuthException("Too many attempts. Wait a bit and try again.", 429) { RetryAfterSeconds = (int)Math.Ceiling((t.Until - now).TotalSeconds) };

        (long Id, string Name, string Hash, string? Display)? row = null;
        if (!string.IsNullOrEmpty(username) && username.Length <= 64)
        {
            using var c = db.Open();
            var rows = Database.Query(c, "SELECT id, username, password_hash, display_name FROM users WHERE username=@u", r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)), ("@u", username));
            if (rows.Count == 1) row = rows[0];
        }
        var ok = PasswordHasher.Verify(password ?? "", row?.Hash ?? DummyHash.Value);
        if (row is null || !ok)
        {
            var fails = (_throttle.TryGetValue(clientKey, out var prev) ? prev.Fails : 0) + 1;
            var wait = fails < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, fails - 5)));
            _throttle[clientKey] = (fails, now + wait);
            if (_throttle.Count > 5000) foreach (var k in _throttle.Where(kv => kv.Value.Until < now).Select(kv => kv.Key).Take(1000).ToList()) _throttle.TryRemove(k, out _);
            throw new AuthException("Wrong username or password.", 401);
        }
        _throttle.TryRemove(clientKey, out _);
        return new AuthUser(row.Value.Id, row.Value.Name, row.Value.Display);
    }

    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("not-a-real-password", 100_000));

    /// <returns>The cookie value.</returns>
    public string CreateSession(long userId, string? userAgent, bool persistent)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = time.GetUtcNow();
        using var c = db.Open();
        Database.Execute(c, null, "DELETE FROM sessions WHERE expires_at < @now", ("@now", Iso(now)));
        Database.Execute(c, null, "INSERT INTO sessions(token_hash,user_id,created_at,expires_at,last_seen,user_agent) VALUES(@h,@u,@t,@e,@t,@ua)",
            ("@h", Hash(token)), ("@u", userId), ("@t", Iso(now)), ("@e", Iso(now + (persistent ? SessionLength : IdleSessionLength))), ("@ua", userAgent is { Length: > 200 } ? userAgent[..200] : userAgent));
        return token;
    }

    /// <summary>The signed-in user for a cookie value, extending the session while it's in use.</summary>
    public AuthUser? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var now = time.GetUtcNow();
        using var c = db.Open();
        var rows = Database.Query(c, """
            SELECT u.id, u.username, s.expires_at, s.last_seen, s.created_at, u.display_name FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@h
            """, r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)), ("@h", Hash(token)));
        if (rows.Count == 0) return null;
        var (id, name, expires, seen, created, display) = rows[0];
        var exp = DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture);
        if (exp <= now) { Database.Execute(c, null, "DELETE FROM sessions WHERE token_hash=@h", ("@h", Hash(token))); return null; }
        // Slide the expiry at most once an hour (not on every request).
        if (now - DateTimeOffset.Parse(seen, CultureInfo.InvariantCulture) > TimeSpan.FromHours(1))
        {
            var length = exp - DateTimeOffset.Parse(created, CultureInfo.InvariantCulture) > IdleSessionLength ? SessionLength : IdleSessionLength;
            Database.Execute(c, null, "UPDATE sessions SET last_seen=@n, expires_at=@e WHERE token_hash=@h", ("@n", Iso(now)), ("@e", Iso(now + length)), ("@h", Hash(token)));
        }
        return new AuthUser(id, name, display);
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var c = db.Open();
        Database.Execute(c, null, "DELETE FROM sessions WHERE token_hash=@h", ("@h", Hash(token)));
    }

    /// <summary>Changes the password and signs out every other device.</summary>
    public void ChangePassword(long userId, string? current, string? next, string? keepToken)
    {
        ValidatePassword(next);
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT username, password_hash FROM users WHERE id=@i", r => (r.GetString(0), r.GetString(1)), ("@i", userId));
        if (rows.Count == 0) throw new AuthException("Not signed in.", 401);
        if (!PasswordHasher.Verify(current ?? "", rows[0].Item2)) throw new AuthException("Your current password isn't right.", 403);
        ValidatePassword(next, rows[0].Item1);
        Database.Execute(c, null, "UPDATE users SET password_hash=@h WHERE id=@i", ("@h", PasswordHasher.Hash(next!, Iterations)), ("@i", userId));
        if (keepToken is { Length: > 0 }) Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@i AND token_hash<>@k", ("@i", userId), ("@k", Hash(keepToken)));
        else Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@i", ("@i", userId));
    }

    // ---- capability links for media players
    // Some players (Safari's native video / HLS stack) don't reliably send cookies on media requests, so a direct-play URL carries a signed,
    // expiring token for that one file. HLS sessions already have unguessable ids that only a signed-in user can create.
    private byte[]? _streamKey;
    public bool TrustProxy => config.GetValue("Auth:TrustProxy", false);

    private byte[] StreamKey()
    {
        if (_streamKey is not null) return _streamKey;
        using var c = db.Open();
        Database.Execute(c, null, "INSERT OR IGNORE INTO settings(key,value,updated_at) VALUES('auth.stream_key',@v,@t)",
            ("@v", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), ("@t", Now()));
        var v = Convert.ToString(Database.Scalar(c, "SELECT value FROM settings WHERE key='auth.stream_key'"), CultureInfo.InvariantCulture)!;
        return _streamKey = Convert.FromBase64String(v);
    }

    public string StreamToken(long mediaId, TimeSpan? lifetime = null)
    {
        var exp = (time.GetUtcNow() + (lifetime ?? TimeSpan.FromHours(12))).ToUnixTimeSeconds();
        return $"{exp}.{Sign(mediaId, exp)}";
    }

    public bool CheckStreamToken(long mediaId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 120) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var exp) || exp < time.GetUtcNow().ToUnixTimeSeconds()) return false;
        return FixedEquals(Sign(mediaId, exp), token[(dot + 1)..]);
    }

    private string Sign(long mediaId, long exp)
        => Convert.ToBase64String(HMACSHA256.HashData(StreamKey(), Encoding.UTF8.GetBytes($"stream:{mediaId}:{exp}"))).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The friendly name shown in the interface. Empty clears it (the username is used instead).</summary>
    public string? SetDisplayName(long userId, string? name)
    {
        var clean = CleanDisplayName(name);
        using var c = db.Open();
        Database.Execute(c, null, "UPDATE users SET display_name=@d WHERE id=@i", ("@d", clean), ("@i", userId));
        return clean;
    }

    internal static string? CleanDisplayName(string? name)
    {
        var v = name?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > 40 || v.Any(char.IsControl)) throw new AuthException("Names can be up to 40 characters, without special characters.");
        return v;
    }

    // ---- devices (sessions)
    public sealed record SessionInfo(string Id, bool Current, string? UserAgent, string CreatedAt, string LastSeen, string ExpiresAt);

    /// <summary>The user's signed-in devices. The id is a short prefix of the stored hash: enough to name a session, useless as a credential.</summary>
    public IReadOnlyList<SessionInfo> ListSessions(long userId, string? currentToken)
    {
        var current = string.IsNullOrEmpty(currentToken) ? null : Hash(currentToken);
        using var c = db.Open();
        return Database.Query(c, "SELECT token_hash, user_agent, created_at, last_seen, expires_at FROM sessions WHERE user_id=@u AND expires_at > @now ORDER BY last_seen DESC",
            r => new SessionInfo(r.GetString(0)[..16], r.GetString(0) == current, r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)),
            ("@u", userId), ("@now", Iso(time.GetUtcNow())));
    }

    public bool RevokeSession(long userId, string id)
    {
        if (id.Length != 16 || !id.All(Uri.IsHexDigit)) return false;
        using var c = db.Open();
        return Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@u AND substr(token_hash,1,16)=@p", ("@u", userId), ("@p", id.ToUpperInvariant())) > 0;
    }

    public int RevokeOthers(long userId, string? currentToken)
    {
        using var c = db.Open();
        return string.IsNullOrEmpty(currentToken)
            ? Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@u", ("@u", userId))
            : Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@u AND token_hash<>@k", ("@u", userId), ("@k", Hash(currentToken)));
    }

    public string ServerName => config["General:ServerName"] is { Length: > 0 and <= 40 } n ? n : "MediaServer";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private string Now() => Iso(time.GetUtcNow());
    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
