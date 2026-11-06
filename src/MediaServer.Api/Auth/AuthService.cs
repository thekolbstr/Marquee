using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaServer.Api.Data;
using MediaServer.Api.Security;

namespace MediaServer.Api.Auth;

/// <summary>The signed-in person. <c>Username</c> is what they sign in with; <c>Name</c> is what the interface calls them.</summary>
public sealed record AuthUser(long Id, string Username, string? DisplayName = null, string Role = "admin")
{
    public bool IsAdmin => Role == "admin";
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
public sealed partial class AuthService(Database db, IConfiguration config, TimeProvider time, SecretBox? box = null)
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

    // ---- two-factor sign-in (TOTP + one-time recovery codes)
    private readonly ConcurrentDictionary<string, byte> _unused = new();

    public bool HasTwoFactor(long userId)
    {
        using var c = db.Open();
        return Database.Scalar(c, $"SELECT 1 FROM users WHERE id={userId.ToString(CultureInfo.InvariantCulture)} AND totp_enabled=1") is not null;
    }

    public (bool Enabled, int RecoveryCodesLeft) TwoFactorStatus(long userId)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT totp_enabled, recovery_codes FROM users WHERE id=@i", r => (r.GetInt32(0) == 1, r.IsDBNull(1) ? null : r.GetString(1)), ("@i", userId));
        if (rows.Count == 0 || !rows[0].Item1) return (false, 0);
        return (true, CodeHashes(rows[0].Item2).Count);
    }

    /// <summary>Starts enrolment: a fresh secret (not active until a code from the app proves it was set up).</summary>
    public (string Secret, string Uri) BeginTwoFactor(long userId, string username)
    {
        if (HasTwoFactor(userId)) throw new AuthException("Two-factor sign-in is already on.", 409);
        var secret = Totp.ToBase32(Totp.NewSecret());
        using var c = db.Open();
        Database.Execute(c, null, "UPDATE users SET totp_secret=@s, totp_enabled=0, totp_last_step=NULL WHERE id=@i", ("@s", Seal(secret, userId)), ("@i", userId));
        return (secret, Totp.Uri(ServerName, username, secret));
    }

    /// <returns>The recovery codes, shown once.</returns>
    public IReadOnlyList<string> EnableTwoFactor(long userId, string? code)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT totp_secret, totp_enabled FROM users WHERE id=@i", r => (r.IsDBNull(0) ? null : r.GetString(0), r.GetInt32(1)), ("@i", userId));
        if (rows.Count == 0 || rows[0].Item1 is null || rows[0].Item2 == 1) throw new AuthException("Start two-factor setup first.", 409);
        var secret = Open(rows[0].Item1!, userId) ?? throw new AuthException("Start two-factor setup again.", 409);
        if (!Totp.Verify(Totp.FromBase32(secret), code, time.GetUtcNow(), null, out var step)) throw new AuthException("That code isn't right. Check the app and try again.", 400);
        var codes = NewRecoveryCodes();
        Database.Execute(c, null, "UPDATE users SET totp_enabled=1, totp_last_step=@s, recovery_codes=@r WHERE id=@i",
            ("@s", step), ("@r", System.Text.Json.JsonSerializer.Serialize(codes.Select(HashCode).ToList())), ("@i", userId));
        return codes;
    }

    public void DisableTwoFactor(long userId, string? password, string? code)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT password_hash FROM users WHERE id=@i", r => r.GetString(0), ("@i", userId));
        if (rows.Count == 0 || !PasswordHasher.Verify(password ?? "", rows[0])) throw new AuthException("Your password isn't right.", 403);
        if (!VerifySecondFactor(userId, code)) throw new AuthException("That code isn't right.", 403);
        Database.Execute(c, null, "UPDATE users SET totp_enabled=0, totp_secret=NULL, totp_last_step=NULL, recovery_codes=NULL WHERE id=@i", ("@i", userId));
    }

    public IReadOnlyList<string> RegenerateRecoveryCodes(long userId, string? password)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT password_hash FROM users WHERE id=@i AND totp_enabled=1", r => r.GetString(0), ("@i", userId));
        if (rows.Count == 0) throw new AuthException("Two-factor sign-in is off.", 409);
        if (!PasswordHasher.Verify(password ?? "", rows[0])) throw new AuthException("Your password isn't right.", 403);
        var codes = NewRecoveryCodes();
        Database.Execute(c, null, "UPDATE users SET recovery_codes=@r WHERE id=@i", ("@r", System.Text.Json.JsonSerializer.Serialize(codes.Select(HashCode).ToList())), ("@i", userId));
        return codes;
    }

    /// <summary>A code from the authenticator app (each works once) or an unused recovery code (consumed on use).</summary>
    public bool VerifySecondFactor(long userId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40) return false;
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT totp_secret, totp_last_step, recovery_codes FROM users WHERE id=@i AND totp_enabled=1",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? (long?)null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)), ("@i", userId));
        if (rows.Count == 0) return false;
        var (sealedSecret, last, recovery) = rows[0];
        if (sealedSecret is not null && Open(sealedSecret, userId) is { } secret && Totp.Verify(Totp.FromBase32(secret), code, time.GetUtcNow(), last, out var step))
        {
            // only move forward, atomically: two requests with the same code can't both succeed
            return Database.Execute(c, null, "UPDATE users SET totp_last_step=@s WHERE id=@i AND (totp_last_step IS NULL OR totp_last_step < @s)", ("@s", step), ("@i", userId)) > 0;
        }
        var hashes = CodeHashes(recovery);
        var h = HashCode(code);
        if (!hashes.Contains(h)) return false;
        hashes.Remove(h);
        return Database.Execute(c, null, "UPDATE users SET recovery_codes=@r WHERE id=@i AND recovery_codes=@old", ("@r", System.Text.Json.JsonSerializer.Serialize(hashes)), ("@i", userId), ("@old", recovery)) > 0;
    }

    /// <summary>Proof that the password step was passed: lets the second step finish a sign-in without the password again. Valid for five minutes.</summary>
    public string MakeTicket(long userId)
    {
        var exp = time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds();
        return $"{userId}.{exp}.{SignTicket(userId, exp)}";
    }

    public long? ReadTicket(string? ticket)
    {
        var p = (ticket ?? "").Split('.');
        if (p.Length != 3 || !long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)) return null;
        if (exp < time.GetUtcNow().ToUnixTimeSeconds() || !FixedEquals(SignTicket(uid, exp), p[2])) return null;
        return uid;
    }

    public AuthUser? GetUser(long id)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT id, username, display_name, role FROM users WHERE id=@i", r => new AuthUser(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)), ("@i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    private string SignTicket(long userId, long exp) => Convert.ToBase64String(HMACSHA256.HashData(StreamKey(), Encoding.UTF8.GetBytes($"2fa:{userId}:{exp}"))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private string Seal(string v, long userId) => box?.Protect(v, $"totp:{userId}") ?? v;
    private string? Open(string stored, long userId) => box is null ? stored : box.TryUnprotect(stored, $"totp:{userId}");
    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().Replace("-", "").ToLowerInvariant())));
    private static List<string> CodeHashes(string? json) { try { return json is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch (System.Text.Json.JsonException) { return []; } }
    private static List<string> NewRecoveryCodes() => Enumerable.Range(0, 8).Select(_ => { var b = RandomNumberGenerator.GetBytes(5); var h = Convert.ToHexString(b).ToLowerInvariant(); return $"{h[..5]}-{h[5..]}"; }).ToList();

    /// <summary>Throttle bookkeeping shared by both sign-in steps.</summary>
    public void EnsureNotBlocked(string clientKey)
    {
        var now = time.GetUtcNow();
        if (_throttle.TryGetValue(clientKey, out var t) && t.Until > now)
            throw new AuthException("Too many attempts. Wait a bit and try again.", 429) { RetryAfterSeconds = (int)Math.Ceiling((t.Until - now).TotalSeconds) };
    }

    public void RegisterFailure(string clientKey)
    {
        var now = time.GetUtcNow();
        var fails = (_throttle.TryGetValue(clientKey, out var prev) ? prev.Fails : 0) + 1;
        var wait = fails < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, fails - 5)));
        _throttle[clientKey] = (fails, now + wait);
    }

    public void RegisterSuccess(string clientKey) => _throttle.TryRemove(clientKey, out _);

    /// <summary>Checks the credentials. Unknown names cost the same time as wrong passwords, and repeated failures from one address slow down.</summary>
    public AuthUser Login(string? username, string? password, string clientKey)
    {
        var now = time.GetUtcNow();
        if (_throttle.TryGetValue(clientKey, out var t) && t.Until > now)
            throw new AuthException("Too many attempts. Wait a bit and try again.", 429) { RetryAfterSeconds = (int)Math.Ceiling((t.Until - now).TotalSeconds) };

        (long Id, string Name, string Hash, string? Display, string Role)? row = null;
        if (!string.IsNullOrEmpty(username) && username.Length <= 64)
        {
            using var c = db.Open();
            var rows = Database.Query(c, "SELECT id, username, password_hash, display_name, role FROM users WHERE username=@u", r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)), ("@u", username));
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
        return new AuthUser(row.Value.Id, row.Value.Name, row.Value.Display, row.Value.Role);
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
            SELECT u.id, u.username, s.expires_at, s.last_seen, s.created_at, u.display_name, u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@h
            """, r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6)), ("@h", Hash(token)));
        if (rows.Count == 0) return null;
        var (id, name, expires, seen, created, display, role) = rows[0];
        var exp = DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture);
        if (exp <= now) { Database.Execute(c, null, "DELETE FROM sessions WHERE token_hash=@h", ("@h", Hash(token))); return null; }
        // Slide the expiry at most once an hour (not on every request).
        if (now - DateTimeOffset.Parse(seen, CultureInfo.InvariantCulture) > TimeSpan.FromHours(1))
        {
            var length = exp - DateTimeOffset.Parse(created, CultureInfo.InvariantCulture) > IdleSessionLength ? SessionLength : IdleSessionLength;
            Database.Execute(c, null, "UPDATE sessions SET last_seen=@n, expires_at=@e WHERE token_hash=@h", ("@n", Iso(now)), ("@e", Iso(now + length)), ("@h", Hash(token)));
        }
        return new AuthUser(id, name, display, role);
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
        var fresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Database.Execute(c, null, "INSERT OR IGNORE INTO settings(key,value,updated_at) VALUES('auth.stream_key',@v,@t)",
            ("@v", box?.Protect(fresh, "auth.stream_key") ?? fresh), ("@t", Now()));
        var v = Convert.ToString(Database.Scalar(c, "SELECT value FROM settings WHERE key='auth.stream_key'"), CultureInfo.InvariantCulture)!;
        var plain = box is null ? v : box.TryUnprotect(v, "auth.stream_key");
        if (plain is null)   // unreadable (the key was replaced): make a new one, which only invalidates links already handed out
        {
            plain = fresh;
            Database.Execute(c, null, "UPDATE settings SET value=@v WHERE key='auth.stream_key'", ("@v", box!.Protect(plain, "auth.stream_key")));
        }
        return _streamKey = Convert.FromBase64String(plain);
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
