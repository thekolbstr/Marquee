using Microsoft.Data.Sqlite;

namespace MediaServer.Api.Data;

/// <summary>SQLite access: WAL, foreign_keys=ON on every connection, forward-only migrations via user_version.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(IConfiguration config)
    {
        var path = Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON;");   // deleted rows (old sessions, removed secrets) are overwritten, not just unlinked
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        using (var j = c.CreateCommand())
        {
            j.CommandText = "PRAGMA journal_mode=WAL;";
            j.ExecuteScalar();
        }
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"));
        for (var i = version; i < Migrations.All.Length; i++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, Migrations.All[i], tx);
            Exec(c, $"PRAGMA user_version={i + 1};", tx);
            tx.Commit();
        }
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}

public static class Migrations
{
    /// <summary>Append only. Index+1 is the schema version.</summary>
    public static readonly string[] All =
    [
        // v1: library files. path is relative to Library:Root.
        """
        CREATE TABLE media_files (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            path             TEXT    NOT NULL UNIQUE,
            size             INTEGER NOT NULL,
            mtime_ticks      INTEGER NOT NULL,
            duration_seconds REAL,
            info_json        TEXT,
            probe_error      TEXT,
            first_seen       TEXT    NOT NULL,
            last_scanned     TEXT    NOT NULL,
            deleted_at       TEXT
        );
        CREATE INDEX ix_media_files_identity ON media_files(size, mtime_ticks);
        CREATE INDEX ix_media_files_deleted ON media_files(deleted_at);
        """,
        // v2: parsed filename fields. parser_version=0 means "needs (re)parsing".
        """
        ALTER TABLE media_files ADD COLUMN parser_version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE media_files ADD COLUMN kind TEXT;
        ALTER TABLE media_files ADD COLUMN title TEXT;
        ALTER TABLE media_files ADD COLUMN year INTEGER;
        ALTER TABLE media_files ADD COLUMN season INTEGER;
        ALTER TABLE media_files ADD COLUMN episode INTEGER;
        ALTER TABLE media_files ADD COLUMN episode_end INTEGER;
        ALTER TABLE media_files ADD COLUMN episode_title TEXT;
        ALTER TABLE media_files ADD COLUMN air_date TEXT;
        CREATE INDEX ix_media_files_title ON media_files(title);
        """,
        // v3: TMDB cache, metadata, match state, manual overrides.
        """
        ALTER TABLE media_files ADD COLUMN tmdb_type TEXT;
        ALTER TABLE media_files ADD COLUMN tmdb_id INTEGER;
        ALTER TABLE media_files ADD COLUMN match_state TEXT;   -- NULL (pending) | matched | unmatched | manual
        ALTER TABLE media_files ADD COLUMN matched_at TEXT;
        CREATE INDEX ix_media_files_tmdb ON media_files(tmdb_type, tmdb_id);

        CREATE TABLE tmdb_cache (
            key        TEXT PRIMARY KEY,   -- request path+query, never contains credentials
            status     INTEGER NOT NULL,   -- 200 or 404 (negative cache)
            body       TEXT,
            fetched_at TEXT NOT NULL
        );

        CREATE TABLE metadata (
            tmdb_type      TEXT NOT NULL CHECK (tmdb_type IN ('movie','tv')),
            tmdb_id        INTEGER NOT NULL,
            title          TEXT NOT NULL,
            original_title TEXT,
            year           INTEGER,
            release_date   TEXT,
            overview       TEXT,
            poster_path    TEXT,
            backdrop_path  TEXT,
            rating         REAL,
            genres         TEXT,
            fetched_at     TEXT NOT NULL,
            PRIMARY KEY (tmdb_type, tmdb_id)
        );

        CREATE TABLE match_overrides (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            media_id   INTEGER REFERENCES media_files(id) ON DELETE CASCADE,  -- file scope
            kind       TEXT,                                                  -- title scope
            title_key  TEXT,                                                  -- title scope
            tmdb_type  TEXT NOT NULL CHECK (tmdb_type IN ('movie','tv')),
            tmdb_id    INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            CHECK ((media_id IS NOT NULL) <> (title_key IS NOT NULL))
        );
        CREATE UNIQUE INDEX ux_override_media ON match_overrides(media_id) WHERE media_id IS NOT NULL;
        CREATE UNIQUE INDEX ux_override_title ON match_overrides(kind, title_key) WHERE title_key IS NOT NULL;
        """,
        // v4: probe versioning (re-probe when the parser learns new fields) and server-side watch progress (syncs across devices).
        """
        ALTER TABLE media_files ADD COLUMN probe_version INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE media_progress (
            media_id         INTEGER PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
            position_seconds REAL    NOT NULL,
            duration_seconds REAL,
            watched          INTEGER NOT NULL DEFAULT 0,
            updated_at       TEXT    NOT NULL
        );
        CREATE INDEX ix_progress_updated ON media_progress(updated_at);
        """,
        // v5: Live TV. stream_url is never returned by the API (IPTV URLs often embed credentials).
        """
        CREATE TABLE live_channels (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source_key  TEXT    NOT NULL UNIQUE,   -- tvg-id when the playlist has one, else the normalised name
            tvg_id      TEXT,                      -- joins to live_programs.channel_tvg_id
            name        TEXT    NOT NULL,
            number      INTEGER,
            group_name  TEXT,
            logo_url    TEXT,
            stream_url  TEXT    NOT NULL,
            active      INTEGER NOT NULL DEFAULT 1,
            probe_json  TEXT,
            probed_at   TEXT,
            updated_at  TEXT    NOT NULL
        );
        CREATE INDEX ix_live_channels_group ON live_channels(group_name);
        CREATE TABLE live_programs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_tvg_id  TEXT    NOT NULL,
            start_ts        INTEGER NOT NULL,      -- unix seconds
            stop_ts         INTEGER NOT NULL,
            title           TEXT    NOT NULL,
            subtitle        TEXT,
            description     TEXT,
            category        TEXT
        );
        CREATE INDEX ix_live_programs_channel_time ON live_programs(channel_tvg_id, start_ts);
        CREATE INDEX ix_live_programs_stop ON live_programs(stop_ts);
        CREATE TABLE live_favorites (
            channel_id INTEGER PRIMARY KEY REFERENCES live_channels(id) ON DELETE CASCADE,
            added_at   TEXT NOT NULL
        );
        """,
        // v6: settings edited in the web UI. They sit on top of appsettings / environment variables, and removing a row
        // falls back to whatever the deployment configured.
        """
        CREATE TABLE settings (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        // v7: sign-in and extra library folders. Sessions store only a hash of the cookie value, so a copy of the database can't be replayed.
        """
        CREATE TABLE users (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            created_at    TEXT NOT NULL
        );
        CREATE TABLE sessions (
            token_hash TEXT PRIMARY KEY,
            user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            last_seen  TEXT NOT NULL,
            user_agent TEXT
        );
        CREATE INDEX ix_sessions_expires ON sessions(expires_at);
        CREATE TABLE library_folders (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            path     TEXT NOT NULL UNIQUE,
            name     TEXT NOT NULL,
            enabled  INTEGER NOT NULL DEFAULT 1,
            added_at TEXT NOT NULL
        );
        """,
        // v8: the name shown in the interface (the username stays the sign-in name).
        "ALTER TABLE users ADD COLUMN display_name TEXT;",
        // v9: several people can share a server. Watch history becomes per person (existing history goes to the first account), and each
        // person has their own list of things to watch later.
        """
        ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'admin';
        CREATE TABLE media_progress_v2 (
            user_id          INTEGER NOT NULL DEFAULT 1,
            media_id         INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
            position_seconds REAL    NOT NULL,
            duration_seconds REAL,
            watched          INTEGER NOT NULL DEFAULT 0,
            updated_at       TEXT    NOT NULL,
            PRIMARY KEY (user_id, media_id)
        );
        INSERT INTO media_progress_v2(user_id, media_id, position_seconds, duration_seconds, watched, updated_at)
            SELECT COALESCE((SELECT MIN(id) FROM users), 1), media_id, position_seconds, duration_seconds, watched, updated_at FROM media_progress;
        DROP TABLE media_progress;
        ALTER TABLE media_progress_v2 RENAME TO media_progress;
        CREATE INDEX ix_progress_updated ON media_progress(updated_at);
        CREATE INDEX ix_progress_media ON media_progress(media_id);
        CREATE TABLE watchlist (
            user_id  INTEGER NOT NULL,
            kind     TEXT    NOT NULL,          -- 'movie' or 'series'
            item_key TEXT    NOT NULL,          -- the media id for a movie, the series key for a show
            added_at TEXT    NOT NULL,
            PRIMARY KEY (user_id, kind, item_key)
        );
        """,
        // v10: two-factor sign-in (TOTP) with one-time recovery codes. The secret is stored encrypted.
        """
        ALTER TABLE users ADD COLUMN totp_enabled INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE users ADD COLUMN totp_secret TEXT;
        ALTER TABLE users ADD COLUMN totp_last_step INTEGER;
        ALTER TABLE users ADD COLUMN recovery_codes TEXT;
        """,
        // v11: stream URLs are stored encrypted, so "did the URL change" is answered with a keyed fingerprint.
        "ALTER TABLE live_channels ADD COLUMN stream_sig TEXT;",
        // v12: chapter marks (read from the file the first time it's played) drive the skip-intro and skip-credits buttons.
        """
        CREATE TABLE media_chapters (
            media_id    INTEGER PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
            mtime_ticks INTEGER NOT NULL,
            json        TEXT    NOT NULL,
            fetched_at  TEXT    NOT NULL
        );
        """,
        // v13: recording live TV, and your own named groups of channels.
        """
        CREATE TABLE recordings (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id      INTEGER NOT NULL DEFAULT 1,
            channel_id   INTEGER NOT NULL,
            channel_name TEXT    NOT NULL,
            title        TEXT    NOT NULL,
            description  TEXT,
            start_ts     INTEGER NOT NULL,
            stop_ts      INTEGER NOT NULL,
            status       TEXT    NOT NULL,          -- scheduled | recording | done | failed | cancelled
            error        TEXT,
            file_name    TEXT,                      -- inside the recordings folder
            created_at   TEXT    NOT NULL,
            started_at   TEXT,
            finished_at  TEXT
        );
        CREATE INDEX ix_recordings_status ON recordings(status, start_ts);
        CREATE TABLE live_lists (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id    INTEGER NOT NULL,
            name       TEXT    NOT NULL COLLATE NOCASE,
            created_at TEXT    NOT NULL,
            UNIQUE (user_id, name)
        );
        CREATE TABLE live_list_channels (
            list_id    INTEGER NOT NULL REFERENCES live_lists(id) ON DELETE CASCADE,
            channel_id INTEGER NOT NULL,
            PRIMARY KEY (list_id, channel_id)
        );
        """,
        // v14: how much each person actually watched, per title per day (filled in from the progress heartbeats). Powers Settings -> Watch activity.
        """
        CREATE TABLE watch_activity (
            user_id  INTEGER NOT NULL,
            media_id INTEGER NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
            day      TEXT    NOT NULL,          -- server-local date, yyyy-MM-dd
            seconds  REAL    NOT NULL DEFAULT 0,
            plays    INTEGER NOT NULL DEFAULT 0,
            last_at  TEXT    NOT NULL,
            PRIMARY KEY (user_id, media_id, day)
        );
        CREATE INDEX ix_activity_day ON watch_activity(day);
        CREATE INDEX ix_activity_last ON watch_activity(last_at);
        """,
    ];
}
