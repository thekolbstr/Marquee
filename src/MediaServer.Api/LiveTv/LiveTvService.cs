using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.Probing;
using Microsoft.Data.Sqlite;

namespace MediaServer.Api.LiveTv;

public sealed record LiveProgram(long Start, long Stop, string Title, string? Subtitle, string? Description, string? Category);
/// <param name="LogoUrl">Our own proxy URL, never the source's (which may carry credentials).</param>
public sealed record LiveChannel(long Id, int? Number, string Name, string? Group, string? LogoUrl, bool Favorite, LiveProgram? Now, LiveProgram? Next);
public sealed record LiveChannelList(int Total, IReadOnlyList<LiveChannel> Items);
public sealed record GuideChannel(long Id, int? Number, string Name, string? Group, string? LogoUrl, bool Favorite, IReadOnlyList<LiveProgram> Programs);
public sealed record GuideResult(long From, long To, int Total, IReadOnlyList<GuideChannel> Channels);
public sealed record LiveGroup(string Name, int Count);
public sealed record LiveChannelDetail(LiveChannel Channel, IReadOnlyList<LiveProgram> Upcoming);
public sealed record LiveRefreshResult(int Channels, int Programs, string? EpgError);
public sealed record LiveStatus(bool Configured, int Channels, int Programs, DateTimeOffset? LastRefresh, string? LastError, bool Refreshing);
/// <summary>Internal: the playable URL for a channel. Deliberately not serialisable to clients.</summary>
public sealed record LiveStream(long ChannelId, string Url, MediaInfo? CachedInfo);

/// <summary>
/// Live TV from an M3U playlist + XMLTV guide (e.g. Dispatcharr). Clients refer to channels by database ID only; stream URLs stay
/// on the server.
/// </summary>
public sealed class LiveTvService(Database db, HttpClient http, IConfiguration config, TimeProvider time, ILogger<LiveTvService> log)
{
    private const long MaxPlaylistBytes = 20L * 1024 * 1024;
    private const long MaxGuideBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan ProbeFreshness = TimeSpan.FromHours(12);

    private int _refreshing;
    public DateTimeOffset? LastRefresh { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["LiveTv:M3uUrl"]);

    // ------------------------------------------------------------------ refresh

    /// <returns>null if a refresh is already running.</returns>
    public async Task<LiveRefreshResult?> RefreshAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Live TV is not configured (LiveTv:M3uUrl)");
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return null;
        try
        {
            LastError = null;
            var m3u = config["LiveTv:M3uUrl"]!;
            if (!M3uParser.IsAllowedUrl(m3u)) throw new InvalidOperationException("LiveTv:M3uUrl must be an http(s) URL");

            await using var plStream = await FetchAsync(m3u, MaxPlaylistBytes, ct);
            using var reader = new StreamReader(plStream);
            var playlist = M3uParser.Parse(await reader.ReadToEndAsync(ct));
            if (playlist.Channels.Count == 0) throw new InvalidOperationException("The playlist contained no playable channels");

            var origin = config["LiveTv:StreamOrigin"];
            var channels = playlist.Channels.Select(c => c with { Url = Rewrite(c.Url, origin) ?? c.Url, Logo = Rewrite(c.Logo, origin) }).ToList();
            var now = time.GetUtcNow();
            UpsertChannels(channels, now);

            string? epgError = null;
            var programs = 0;
            var epgUrl = config["LiveTv:EpgUrl"] ?? Rewrite(playlist.EpgUrl, origin);
            if (!string.IsNullOrWhiteSpace(epgUrl) && M3uParser.IsAllowedUrl(epgUrl))
            {
                try { programs = await RefreshGuideAsync(epgUrl, channels.Where(c => c.TvgId is not null).Select(c => c.TvgId!).ToHashSet(), now, ct); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Xml.XmlException or IOException or TaskCanceledException)
                {
                    epgError = "The programme guide could not be loaded";
                    log.LogWarning("Guide refresh failed: {Error}", ex.Message);
                }
            }
            LastRefresh = now;
            if (epgError is not null) LastError = epgError;
            log.LogInformation("Live TV refreshed: {Channels} channels, {Programs} programmes", channels.Count, programs);
            return new LiveRefreshResult(channels.Count, programs, epgError);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            LastError = ex is HttpRequestException ? "The playlist could not be downloaded" : ex.Message; // no URLs in messages
            log.LogWarning("Live TV refresh failed: {Error}", ex.Message);
            throw;
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    /// <summary>Swaps the origin (scheme/host/port) of a URL, e.g. to reach the source through a stable in-cluster service name.</summary>
    internal static string? Rewrite(string? url, string? origin)
    {
        if (url is null || string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("http" or "https")) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        return new UriBuilder(u) { Scheme = o.Scheme, Host = o.Host, Port = o.Port }.Uri.ToString();
    }

    private async Task<Stream> FetchAsync(string url, long maxBytes, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The download is larger than allowed");
        Stream s = new LimitedReadStream(await resp.Content.ReadAsStreamAsync(ct), maxBytes, resp);
        // Guides are often gzip-compressed (sometimes without a Content-Encoding header).
        var buffered = new BufferedStream(s);
        var head = new byte[2];
        var n = await buffered.ReadAsync(head, ct);
        var rewound = new PrependStream(head.AsSpan(0, n).ToArray(), buffered);
        return n == 2 && head[0] == 0x1f && head[1] == 0x8b ? new GZipStream(rewound, CompressionMode.Decompress) : rewound;
    }

    private void UpsertChannels(IReadOnlyList<M3uChannel> channels, DateTimeOffset now)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Execute(c, tx, "UPDATE live_channels SET active=0");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in channels)
        {
            var key = ch.TvgId ?? "name:" + new string(ch.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!seen.Add(key)) continue; // duplicate entry in the playlist: first one wins
            Database.Execute(c, tx, """
                INSERT INTO live_channels(source_key,tvg_id,name,number,group_name,logo_url,stream_url,active,updated_at)
                VALUES(@k,@t,@n,@no,@g,@l,@u,1,@now)
                ON CONFLICT(source_key) DO UPDATE SET tvg_id=excluded.tvg_id, name=excluded.name, number=excluded.number, group_name=excluded.group_name,
                    logo_url=excluded.logo_url, active=1, updated_at=excluded.updated_at,
                    probe_json=CASE WHEN stream_url<>excluded.stream_url THEN NULL ELSE probe_json END,
                    stream_url=excluded.stream_url
                """, ("@k", key), ("@t", ch.TvgId), ("@n", ch.Name), ("@no", ch.Number), ("@g", ch.Group), ("@l", ch.Logo), ("@u", ch.Url),
                ("@now", now.ToString("O", CultureInfo.InvariantCulture)));
        }
        tx.Commit();
    }

    private async Task<int> RefreshGuideAsync(string url, HashSet<string> knownChannels, DateTimeOffset now, CancellationToken ct)
    {
        await using var stream = await FetchAsync(url, MaxGuideBytes, ct);
        var from = now.AddHours(-2).ToUnixTimeSeconds();
        var to = now.AddHours(36).ToUnixTimeSeconds();
        var list = new List<XmltvProgramme>();
        try { foreach (var p in XmltvParser.ReadProgrammes(stream, from, to)) if (knownChannels.Contains(p.ChannelId)) list.Add(p); }
        catch (System.Xml.XmlException) when (list.Count > 0) { /* a truncated guide: keep what was read */ }

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Database.Execute(c, tx, "DELETE FROM live_programs");
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO live_programs(channel_tvg_id,start_ts,stop_ts,title,subtitle,description,category) VALUES($c,$s,$e,$t,$st,$d,$cat)";
        var pc = new[] { "$c", "$s", "$e", "$t", "$st", "$d", "$cat" }.Select(n => { var p = cmd.CreateParameter(); p.ParameterName = n; cmd.Parameters.Add(p); return p; }).ToArray();
        foreach (var p in list)
        {
            pc[0].Value = p.ChannelId; pc[1].Value = p.StartTs; pc[2].Value = p.StopTs; pc[3].Value = p.Title;
            pc[4].Value = (object?)p.SubTitle ?? DBNull.Value; pc[5].Value = (object?)p.Description ?? DBNull.Value; pc[6].Value = (object?)p.Category ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public LiveStatus Status()
    {
        using var c = db.Open();
        return new LiveStatus(IsConfigured, Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM live_channels WHERE active=1")),
            Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM live_programs")), LastRefresh, LastError, Volatile.Read(ref _refreshing) != 0);
    }

    // ------------------------------------------------------------------ browsing

    private sealed record Row(long Id, string? TvgId, int? Number, string Name, string? Group, bool HasLogo, bool Favorite);

    private (List<Row> Rows, int Total) QueryChannels(string? group, string? search, bool favoritesOnly, int limit, int offset)
    {
        var where = new List<string> { "c.active=1" };
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(group)) { where.Add("COALESCE(c.group_name,'Other')=@g"); ps.Add(("@g", group)); }
        // instr() rather than LIKE: user text can't act as a wildcard.
        if (!string.IsNullOrWhiteSpace(search)) { where.Add("instr(lower(c.name), lower(@q)) > 0"); ps.Add(("@q", search.Trim())); }
        if (favoritesOnly) where.Add("f.channel_id IS NOT NULL");
        var from = "FROM live_channels c LEFT JOIN live_favorites f ON f.channel_id=c.id WHERE " + string.Join(" AND ", where);

        using var conn = db.Open();
        var total = Database.Query(conn, "SELECT COUNT(*) " + from, r => r.GetInt32(0), [.. ps]).First();
        var rows = Database.Query(conn, $"SELECT c.id, c.tvg_id, c.number, c.name, c.group_name, c.logo_url IS NOT NULL, f.channel_id IS NOT NULL {from} ORDER BY COALESCE(c.number, 1000000), lower(c.name) LIMIT @lim OFFSET @off",
            r => new Row(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetBoolean(6)),
            [.. ps, ("@lim", Math.Clamp(limit, 1, 500)), ("@off", Math.Max(0, offset))]);
        return (rows, total);
    }

    private static string? Logo(Row r) => r.HasLogo ? $"/api/live/channels/{r.Id}/logo" : null;

    private List<LiveProgram> Programs(IEnumerable<string> tvgIds, long from, long to, out Dictionary<string, List<LiveProgram>> byChannel)
    {
        byChannel = [];
        var ids = tvgIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        using var c = db.Open();
        var names = ids.Select((_, i) => "@t" + i).ToList();
        var ps = ids.Select((id, i) => ("@t" + i, (object?)id)).Concat([("@from", (object?)from), ("@to", (object?)to)]).ToArray();
        var rows = Database.Query(c, $"SELECT channel_tvg_id,start_ts,stop_ts,title,subtitle,description,category FROM live_programs WHERE channel_tvg_id IN ({string.Join(',', names)}) AND stop_ts>@from AND start_ts<@to ORDER BY channel_tvg_id,start_ts",
            r => (Channel: r.GetString(0), P: new LiveProgram(r.GetInt64(1), r.GetInt64(2), r.GetString(3), N(r, 4), N(r, 5), N(r, 6))), ps);
        foreach (var (ch, p) in rows) { if (!byChannel.TryGetValue(ch, out var l)) byChannel[ch] = l = []; l.Add(p); }
        return rows.Select(r => r.P).ToList();

        static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    }

    /// <summary>The channel list with what is on now and next.</summary>
    public LiveChannelList Channels(string? group, string? search, bool favoritesOnly, int limit, int offset)
    {
        var (rows, total) = QueryChannels(group, search, favoritesOnly, limit, offset);
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        Programs(rows.Where(r => r.TvgId is not null).Select(r => r.TvgId!), now, now + 6 * 3600, out var by);
        var items = rows.Select(r =>
        {
            var list = r.TvgId is not null && by.TryGetValue(r.TvgId, out var l) ? l : [];
            var current = list.FirstOrDefault(p => p.Start <= now && p.Stop > now);
            var next = list.FirstOrDefault(p => p.Start >= (current?.Stop ?? now) && p != current);
            return new LiveChannel(r.Id, r.Number, r.Name, r.Group, Logo(r), r.Favorite, current, next);
        }).ToList();
        return new LiveChannelList(total, items);
    }

    /// <summary>Channels with their programmes for [from, from+hours): the timeline guide.</summary>
    public GuideResult Guide(string? group, string? search, bool favoritesOnly, long from, int hours, int limit, int offset)
    {
        hours = Math.Clamp(hours, 1, 12);
        var to = from + hours * 3600L;
        var (rows, total) = QueryChannels(group, search, favoritesOnly, limit, offset);
        Programs(rows.Where(r => r.TvgId is not null).Select(r => r.TvgId!), from, to, out var by);
        var channels = rows.Select(r => new GuideChannel(r.Id, r.Number, r.Name, r.Group, Logo(r), r.Favorite,
            r.TvgId is not null && by.TryGetValue(r.TvgId, out var l) ? l : [])).ToList();
        return new GuideResult(from, to, total, channels);
    }

    public IReadOnlyList<LiveGroup> Groups()
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT COALESCE(group_name,'Other'), COUNT(*) FROM live_channels WHERE active=1 GROUP BY 1 ORDER BY 1 COLLATE NOCASE", r => new LiveGroup(r.GetString(0), r.GetInt32(1)));
    }

    public LiveChannelDetail? Channel(long id)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT c.id, c.tvg_id, c.number, c.name, c.group_name, c.logo_url IS NOT NULL, c.id IN (SELECT channel_id FROM live_favorites) FROM live_channels c WHERE c.id=@i AND c.active=1",
            r => new Row(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetBoolean(6)), ("@i", id));
        if (rows.Count == 0) return null;
        var row = rows[0];
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var list = row.TvgId is null ? [] : Programs([row.TvgId], now, now + 12 * 3600, out _);
        var current = list.FirstOrDefault(p => p.Start <= now && p.Stop > now);
        var upcoming = list.Where(p => p != current && p.Start >= (current?.Stop ?? now)).Take(12).ToList();
        return new LiveChannelDetail(new LiveChannel(row.Id, row.Number, row.Name, row.Group, Logo(row), row.Favorite, current, upcoming.FirstOrDefault()), upcoming);
    }

    public bool SetFavorite(long id, bool on)
    {
        using var c = db.Open();
        if (Database.Scalar(c, $"SELECT 1 FROM live_channels WHERE id={id.ToString(CultureInfo.InvariantCulture)} AND active=1") is null) return false;
        if (on) Database.Execute(c, null, "INSERT OR IGNORE INTO live_favorites(channel_id, added_at) VALUES(@i,@t)", ("@i", id), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        else Database.Execute(c, null, "DELETE FROM live_favorites WHERE channel_id=@i", ("@i", id));
        return true;
    }

    // ------------------------------------------------------------------ playback support

    public LiveStream? GetStream(long id)
    {
        using var c = db.Open();
        var rows = Database.Query(c, "SELECT stream_url, probe_json, probed_at FROM live_channels WHERE id=@i AND active=1",
            r => (Url: r.GetString(0), Json: r.IsDBNull(1) ? null : r.GetString(1), At: r.IsDBNull(2) ? null : r.GetString(2)), ("@i", id));
        if (rows.Count == 0) return null;
        MediaInfo? cached = null;
        if (rows[0].Json is { } json && DateTimeOffset.TryParse(rows[0].At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && time.GetUtcNow() - at < ProbeFreshness)
            try { cached = JsonSerializer.Deserialize<MediaInfo>(json); } catch (JsonException) { }
        return new LiveStream(id, rows[0].Url, cached);
    }

    public void SaveProbe(long id, MediaInfo info)
    {
        using var c = db.Open();
        Database.Execute(c, null, "UPDATE live_channels SET probe_json=@j, probed_at=@t WHERE id=@i",
            ("@j", JsonSerializer.Serialize(info)), ("@t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("@i", id));
    }

    public string? LogoSource(long id)
    {
        using var c = db.Open();
        return Database.Query(c, "SELECT logo_url FROM live_channels WHERE id=@i AND active=1 AND logo_url IS NOT NULL", r => r.GetString(0), ("@i", id)).FirstOrDefault();
    }
}

/// <summary>Fails a read once more than <c>max</c> bytes have been consumed (defends against an endless or oversized download).</summary>
internal sealed class LimitedReadStream(Stream inner, long max, IDisposable? owner = null) : Stream
{
    private long _read;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Check(await inner.ReadAsync(buffer, ct));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    private int Check(int n) { _read += n; if (_read > max) throw new InvalidDataException("The download is larger than allowed"); return n; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner?.Dispose(); } base.Dispose(disposing); }
}

/// <summary>A stream that replays bytes already consumed (to sniff a header) before continuing with the rest.</summary>
internal sealed class PrependStream(byte[] prefix, Stream rest) : Stream
{
    private int _pos;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos < prefix.Length) { var n = Math.Min(count, prefix.Length - _pos); Array.Copy(prefix, _pos, buffer, offset, n); _pos += n; return n; }
        return rest.Read(buffer, offset, count);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos < prefix.Length) { var n = Math.Min(buffer.Length, prefix.Length - _pos); prefix.AsMemory(_pos, n).CopyTo(buffer); _pos += n; return n; }
        return await rest.ReadAsync(buffer, ct);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) rest.Dispose(); base.Dispose(disposing); }
}
