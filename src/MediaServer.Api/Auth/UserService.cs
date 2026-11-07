using System.Globalization;
using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;

namespace MediaServer.Api.Auth;

public sealed record UserInfo(long Id, string Username, string? DisplayName, string Role, string CreatedAt, bool TwoFactor, int Sessions);

/// <summary>Managing the people who can sign in. Only administrators reach this (see AuthMiddleware).</summary>
public sealed class UserService(Database db, IConfiguration config, TimeProvider time)
{
    public static readonly string[] Roles = ["admin", "user"];
    private int Iterations => Math.Clamp(config.GetValue("Auth:Pbkdf2Iterations", PasswordHasher.DefaultIterations), 1_000, 5_000_000);

    public IReadOnlyList<UserInfo> List()
    {
        using var c = db.Open();
        return Database.Query(c, """
            SELECT u.id, u.username, u.display_name, u.role, u.created_at, COALESCE(u.totp_enabled,0),
                   (SELECT COUNT(*) FROM sessions s WHERE s.user_id=u.id AND s.expires_at > @now)
            FROM users u ORDER BY u.id
            """, r => new UserInfo(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) == 1, r.GetInt32(6)),
            ("@now", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
    }

    public UserInfo Create(string? username, string? password, string? displayName, string? role)
    {
        AuthService.ValidateCredentials(username, password, AuthService.MinPasswordFrom(config));
        var r = NormalizeRole(role);
        var display = AuthService.CleanDisplayName(displayName);
        using var c = db.Open();
        try
        {
            Database.Execute(c, null, "INSERT INTO users(username, password_hash, created_at, display_name, role) VALUES(@u,@h,@t,@d,@r)",
                ("@u", username), ("@h", PasswordHasher.Hash(password!, Iterations)), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("@d", display), ("@r", r));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new AuthException("That username is already taken.", 409); }
        return List().First(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(long id, string? role, string? displayName, bool setDisplayName)
    {
        using var c = db.Open();
        var current = Get(c, id) ?? throw new AuthException("No such user.", 404);
        if (role is not null)
        {
            var r = NormalizeRole(role);
            if (current.Role == "admin" && r != "admin" && AdminCount(c) <= 1) throw new AuthException("There must be at least one administrator.", 409);
            Database.Execute(c, null, "UPDATE users SET role=@r WHERE id=@i", ("@r", r), ("@i", id));
        }
        if (setDisplayName) Database.Execute(c, null, "UPDATE users SET display_name=@d WHERE id=@i", ("@d", AuthService.CleanDisplayName(displayName)), ("@i", id));
    }

    /// <summary>An administrator sets a new password for someone (and signs them out everywhere).</summary>
    public void ResetPassword(long id, string? newPassword)
    {
        using var c = db.Open();
        var u = Get(c, id) ?? throw new AuthException("No such user.", 404);
        AuthService.ValidatePassword(newPassword, u.Username, AuthService.MinPasswordFrom(config));
        Database.Execute(c, null, "UPDATE users SET password_hash=@h WHERE id=@i", ("@h", PasswordHasher.Hash(newPassword!, Iterations)), ("@i", id));
        Database.Execute(c, null, "DELETE FROM sessions WHERE user_id=@i", ("@i", id));
    }

    public void ResetTwoFactor(long id)
    {
        using var c = db.Open();
        if (Get(c, id) is null) throw new AuthException("No such user.", 404);
        Database.Execute(c, null, "UPDATE users SET totp_enabled=0, totp_secret=NULL, totp_last_step=NULL, recovery_codes=NULL WHERE id=@i", ("@i", id));
    }

    public void Delete(long id, long actingUserId)
    {
        using var c = db.Open();
        var u = Get(c, id) ?? throw new AuthException("No such user.", 404);
        if (id == actingUserId) throw new AuthException("You can't delete the account you're signed in with.", 409);
        if (u.Role == "admin" && AdminCount(c) <= 1) throw new AuthException("There must be at least one administrator.", 409);
        using var tx = c.BeginTransaction();
        Database.Execute(c, tx, "DELETE FROM media_progress WHERE user_id=@i", ("@i", id));
        Database.Execute(c, tx, "DELETE FROM watchlist WHERE user_id=@i", ("@i", id));
        Database.Execute(c, tx, "DELETE FROM sessions WHERE user_id=@i", ("@i", id));
        Database.Execute(c, tx, "DELETE FROM users WHERE id=@i", ("@i", id));
        tx.Commit();
    }

    private static (string Username, string Role)? Get(SqliteConnection c, long id)
    {
        var rows = Database.Query(c, "SELECT username, role FROM users WHERE id=@i", r => (r.GetString(0), r.GetString(1)), ("@i", id));
        return rows.Count == 0 ? null : rows[0];
    }

    private static int AdminCount(SqliteConnection c) => Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM users WHERE role='admin'"), CultureInfo.InvariantCulture);

    private static string NormalizeRole(string? role)
    {
        var r = (role ?? "user").Trim().ToLowerInvariant();
        return Roles.Contains(r) ? r : throw new AuthException("Role must be admin or user.");
    }
}
