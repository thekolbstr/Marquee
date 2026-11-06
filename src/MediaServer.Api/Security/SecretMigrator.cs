using MediaServer.Api.Data;
using MediaServer.Api.Settings;

namespace MediaServer.Api.Security;

/// <summary>On start-up, encrypts secrets that an earlier version stored as plain text. Safe to run every time: it only touches what isn't encrypted yet.</summary>
public static class SecretMigrator
{
    public static (int Settings, int Channels) Run(Database db, SecretBox box)
    {
        int settings = 0, channels = 0;
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var secretKeys = SettingsService.Defs.Where(d => d.Kind is SettingKind.Secret or SettingKind.SecretUrl).Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        secretKeys.Add("auth.stream_key");
        foreach (var (key, value) in Database.Query(c, "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))))
        {
            if (!secretKeys.Contains(key) || SecretBox.IsProtected(value)) continue;
            Database.Execute(c, tx, "UPDATE settings SET value=@v WHERE key=@k", ("@v", box.Protect(value, key)), ("@k", key));
            settings++;
        }

        var rows = Database.Query(c, "SELECT id, stream_url, logo_url, stream_sig FROM live_channels",
            r => (Id: r.GetInt64(0), Url: r.GetString(1), Logo: r.IsDBNull(2) ? null : r.GetString(2), Sig: r.IsDBNull(3) ? null : r.GetString(3)));
        foreach (var r in rows)
        {
            var urlPlain = box.TryUnprotect(r.Url, "live.stream");
            if (urlPlain is null) continue;                                   // sealed with a key we don't have; the next refresh replaces it
            var needsUrl = !SecretBox.IsProtected(r.Url);
            var needsLogo = r.Logo is not null && !SecretBox.IsProtected(r.Logo);
            if (!needsUrl && !needsLogo && r.Sig is not null) continue;
            Database.Execute(c, tx, "UPDATE live_channels SET stream_url=@u, logo_url=@l, stream_sig=@s WHERE id=@i",
                ("@u", needsUrl ? box.Protect(r.Url, "live.stream") : r.Url), ("@l", needsLogo ? box.Protect(r.Logo!, "live.logo") : r.Logo),
                ("@s", box.Fingerprint(urlPlain)), ("@i", r.Id));
            channels++;
        }
        tx.Commit();
        // Encrypting a value leaves its old plain-text copy behind in freed database pages and in the write-ahead log. Rewrite the file so the
        // readable copies are really gone, not just unreferenced.
        // (This also runs once for databases an earlier build already encrypted, which still carry the old copies.)
        var done = Database.Scalar(c, "SELECT 1 FROM settings WHERE key='auth.scrubbed'") is not null;
        if (settings > 0 || channels > 0 || !done)
        {
            Database.Execute(c, null, "INSERT OR REPLACE INTO settings(key,value,updated_at) VALUES('auth.scrubbed','1','x')");
            Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            Database.Exec(c, "VACUUM;");
            Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        return (settings, channels);
    }
}
