using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MediaServer.Api.Auth;
using MediaServer.Api.Data;
using MediaServer.Api.Hls;
using MediaServer.Api.Library;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MediaServer.Api.Cast;

public sealed record CastPlayRequest(string DeviceId, long MediaId, string? Title = null, double StartSeconds = 0, int? AudioIndex = null, string? Caption = null);
public sealed record CastCaption(string Id, int TrackId, string Label, string? Language);
public sealed record CastSessionInfo(string DeviceId, string DeviceName, long MediaId, string Title, string State, double Position, double? Duration,
    double? Volume, bool Muted, IReadOnlyList<CastCaption> Captions, string? ActiveCaption, long OwnerId);

/// <summary>
/// Plays library items on Cast devices (Chromecast, Google TV, TVs with Chromecast built in) from the server instead of from the browser.
/// The server finds the devices, starts a stream the TV can play, tells the TV to fetch it, and then watches the TV: it keeps the stream alive,
/// saves watch progress for the person who started it, and cleans up when the video ends or someone stops it on the TV. Because the server
/// does the work, casting needs no secure page, works from any browser, and carries on if the browser tab is closed.
/// </summary>
public sealed class CastService(IConfiguration config, HlsSessionManager hls, SubtitleService subtitles, AuthService auth, Database db, TimeProvider time,
    IServer server, ILoggerFactory logs) : IAsyncDisposable
{
    internal HlsSessionManager Hls => hls;
    internal TimeProvider Clock => time;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60), Remember = TimeSpan.FromMinutes(10);
    private readonly ILogger _log = logs.CreateLogger<CastService>();
    private readonly MdnsBrowser _mdns = new(logs.CreateLogger<MdnsBrowser>());
    private readonly SemaphoreSlim _browseGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (CastDevice Device, DateTimeOffset Seen)> _found = new();
    private readonly ConcurrentDictionary<string, string> _manualNames = new();
    private readonly ConcurrentDictionary<string, ActiveCast> _active = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private DateTimeOffset _lastBrowse = DateTimeOffset.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    // ---------------------------------------------------------------------------------------------- devices

    /// <summary>The TVs the server can see. Discovery takes about three seconds, so results are reused for a minute unless <paramref name="refresh"/> is set.</summary>
    public async Task<IReadOnlyList<CastDevice>> DevicesAsync(bool refresh, CancellationToken ct)
    {
        if (refresh || time.GetUtcNow() - _lastBrowse > Fresh)
        {
            await _browseGate.WaitAsync(ct);
            try
            {
                if (refresh || time.GetUtcNow() - _lastBrowse > Fresh)
                {
                    foreach (var d in await _mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct)) _found[d.Id] = (d, time.GetUtcNow());
                    _lastBrowse = time.GetUtcNow();
                }
            }
            finally { _browseGate.Release(); }
        }
        var list = _found.Values.Where(v => time.GetUtcNow() - v.Seen < Remember).Select(v => v.Device).ToList();
        foreach (var m in await ManualAsync(ct)) if (list.All(d => d.Id != m.Id && d.Host != m.Host)) list.Add(m);
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Addresses typed into Settings → Network &amp; security, for networks where discovery can't work (a container without host networking, another subnet).</summary>
    private async Task<IReadOnlyList<CastDevice>> ManualAsync(CancellationToken ct)
    {
        var result = new List<CastDevice>();
        foreach (var entry in (config["Cast:Devices"] ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20))
        {
            var host = entry; var port = 8009;
            var colon = entry.LastIndexOf(':');
            if (colon > 0 && int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536) { host = entry[..colon]; port = p; }
            if (host.Length > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) continue;
            var name = _manualNames.TryGetValue(host, out var n) ? n : await LookUpNameAsync(host, ct);
            _manualNames[host] = name;
            result.Add(new CastDevice("m" + MdnsDecoder.Sanitize(host + port), name, host, port, "Cast device", Manual: true));
        }
        return result;
    }

    /// <summary>A Cast device answers a small unauthenticated question about its own name on port 8008.</summary>
    private static async Task<string> LookUpNameAsync(string host, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://{host}:8008/setup/eureka_info?params=name", ct));
            if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        return host;
    }

    public bool IsBusy(string deviceId) => _active.ContainsKey(deviceId);

    // ---------------------------------------------------------------------------------------------- playing

    /// <exception cref="KeyNotFoundException">Unknown device or media.</exception>
    /// <exception cref="CastException">The TV couldn't be reached or wouldn't play it.</exception>
    public async Task<CastSessionInfo> PlayAsync(CastPlayRequest r, long ownerId, string? requestBase, CancellationToken ct)
    {
        var device = (await DevicesAsync(false, ct)).FirstOrDefault(d => d.Id == r.DeviceId) ?? throw new KeyNotFoundException("That TV isn't visible to the server any more.");
        if (!double.IsFinite(r.StartSeconds) || r.StartSeconds < 0 || r.StartSeconds > 1_000_000) throw new InvalidOperationException("startSeconds is out of range");
        await _startGate.WaitAsync(ct);
        HlsSession? stream = null;
        CastConnection? conn = null;
        try
        {
            if (_active.TryRemove(device.Id, out var old)) await old.EndAsync(finished: false);
            // Prepare the stream first, so a problem with the file is reported without disturbing whatever the TV is showing.
            stream = await hls.StartAsync(r.MediaId, ct, ClientCaps.From("h264", "aac,mp3", "mp4"), false, r.StartSeconds, null, r.AudioIndex, null);
            conn = new CastConnection(device.Host, device.Port, _log);
            await conn.ConnectAsync(ct);
            await conn.LaunchAsync(ct);

            var baseUrl = BaseUrl(device, requestBase);
            var token = auth.StreamToken(r.MediaId, TimeSpan.FromHours(12));
            var captions = subtitles.List(r.MediaId).Where(t => t.Supported).Select((t, i) => new CastCaption(t.Id, i + 1, t.Label, t.Language)).ToList();
            var tracks = captions.Select(c => new CastTrack(c.TrackId, $"{baseUrl}/api/media/{r.MediaId.ToString(CultureInfo.InvariantCulture)}/captions/{Uri.EscapeDataString(c.Id)}.vtt?k={token}", c.Language, c.Label)).ToList();
            var active = captions.Where(c => c.Id == r.Caption).Select(c => c.TrackId).ToList();
            var title = string.IsNullOrWhiteSpace(r.Title) ? "Video" : r.Title.Trim()[..Math.Min(r.Title.Trim().Length, 200)];

            await conn.LoadAsync($"{baseUrl}/api/hls/{stream.Id}/index.m3u8", "application/x-mpegURL", title, tracks, active, r.StartSeconds, ct);
            var progress = new ProgressService(db, time, config, new FixedUser(ownerId));
            var cast = new ActiveCast(this, device, conn, r.MediaId, title, stream, ownerId, captions, progress);
            _active[device.Id] = cast;
            cast.Start();
            _log.LogInformation("Casting media {Media} to {Device}", r.MediaId, device.Name);
            return cast.Info();
        }
        catch
        {
            if (stream is not null) await hls.StopAsync(stream.Id);
            if (conn is not null) { try { await conn.StopAppAsync(CancellationToken.None); } catch (CastException) { } await conn.DisposeAsync(); }
            throw;
        }
        finally { _startGate.Release(); }
    }

    /// <summary>An address for this server that the TV can fetch from: the one configured, or this machine's address on the network the TV is on.</summary>
    internal string BaseUrl(CastDevice device, string? requestBase)
    {
        if (config["Cast:BaseUrl"] is { Length: > 0 } configured) return configured.TrimEnd('/');
        var port = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a.Replace("+", "localhost").Replace("*", "localhost").Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var u) ? u : null)
            .FirstOrDefault(u => u is { Scheme: "http" })?.Port;
        var ip = LocalAddressFor(device.Host);
        if (ip is not null && port is not null) return $"http://{ip}:{port}";
        if (requestBase is { Length: > 0 } && requestBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return requestBase.TrimEnd('/');
        throw new CastException("The server couldn't work out an address the TV can reach. Set “Address for casting” in Settings → Network & security.");
    }

    private static string? LocalAddressFor(string host)
    {
        try
        {
            var target = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (target is null) return null;
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, 9);                                  // no packet is sent: this only asks the OS which local address would be used
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
    }

    // ---------------------------------------------------------------------------------------------- controlling

    public IReadOnlyList<CastSessionInfo> Sessions() => _active.Values.Select(a => a.Info()).ToList();

    public async Task ControlAsync(string deviceId, string action, double? value, string? text, CancellationToken ct)
    {
        if (!_active.TryGetValue(deviceId, out var a)) throw new KeyNotFoundException("Nothing is being cast to that TV.");
        try
        {
            switch (action)
            {
                case "play": await a.Conn.PlayAsync(ct); break;
                case "pause": await a.Conn.PauseAsync(ct); break;
                case "seek": await a.Conn.SeekAsync(Math.Clamp(value ?? 0, 0, a.Stream.DurationSeconds > 0 ? a.Stream.DurationSeconds : 1_000_000), ct); break;
                case "volume": await a.Conn.SetVolumeAsync(value ?? 1, ct); break;
                case "mute": await a.Conn.SetMutedAsync(value is > 0, ct); break;
                case "captions":
                    var track = a.Captions.FirstOrDefault(c => c.Id == text);
                    await a.Conn.SetActiveTracksAsync(track is null ? [] : [track.TrackId], ct);
                    a.ActiveCaption = track?.Id;
                    break;
                default: throw new InvalidOperationException("Unknown action.");
            }
        }
        catch (CastException ex) when (ex.Message.Contains("ended", StringComparison.Ordinal)) { await a.EndAsync(finished: false); throw; }
    }

    public async Task StopAsync(string deviceId)
    {
        if (_active.TryRemove(deviceId, out var a)) await a.EndAsync(finished: false, stopApp: true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _active.Keys.ToList()) await StopAsync(id);
    }

    // ---------------------------------------------------------------------------------------------- one running cast

    private sealed class FixedUser(long id) : IUserContext { public long UserId => id; }

    private sealed class ActiveCast(CastService owner, CastDevice device, CastConnection conn, long mediaId, string title, HlsSession stream, long ownerId,
        IReadOnlyList<CastCaption> captions, ProgressService progress)
    {
        private readonly CancellationTokenSource _cts = new();
        private int _ended;
        private Task? _monitor;
        public CastConnection Conn => conn;
        public HlsSession Stream => stream;
        public IReadOnlyList<CastCaption> Captions => captions;
        public string? ActiveCaption { get; set; }

        public void Start() => _monitor = Task.Run(MonitorAsync);

        public CastSessionInfo Info()
        {
            var m = conn.Media;
            var state = m is null ? "LOADING" : m.PlayerState;
            var active = ActiveCaption ?? captions.FirstOrDefault(c => m?.ActiveTrackIds.Contains(c.TrackId) == true)?.Id;
            return new CastSessionInfo(device.Id, device.Name, mediaId, title, state, m?.PositionNow(owner.Clock.GetUtcNow()) ?? 0, m?.Duration ?? (stream.DurationSeconds > 0 ? stream.DurationSeconds : null),
                conn.Volume, conn.Muted, captions, active, ownerId);
        }

        /// <summary>Watches the TV once a second: keeps the stream alive, saves progress, and notices when the video ends or the TV is switched away.</summary>
        private async Task MonitorAsync()
        {
            var finished = false; var seenActive = false; var failures = 0; var lastSave = DateTimeOffset.MinValue;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (await Task.WhenAny(Task.Delay(1000, _cts.Token), conn.Closed) == conn.Closed) break;
                    try { await conn.PollAsync(_cts.Token); failures = 0; }
                    catch (CastException) { if (++failures >= 4) break; }
                    if (!conn.AppRunning) break;                                               // someone closed the player on the TV
                    if (owner.Hls.Get(stream.Id) is { } s) owner.Hls.Touch(s);                 // the TV fetches segments, but a paused video must not time out
                    if (conn.Media is not { } m) continue;
                    if (m.PlayerState is "PLAYING" or "PAUSED" or "BUFFERING") seenActive = true;
                    if (m.PlayerState == "IDLE" && seenActive)
                    {
                        finished = m.IdleReason == "FINISHED";
                        if (!finished) owner._log.LogInformation("Cast to {Device} ended ({Reason})", device.Name, m.IdleReason ?? "idle");
                        break;
                    }
                    var now = owner.Clock.GetUtcNow();
                    if (now - lastSave > TimeSpan.FromSeconds(10) && m.PlayerState is "PLAYING" or "PAUSED") { SaveProgress(m.PositionNow(now), m.Duration, null); lastSave = now; }
                }
            }
            catch (OperationCanceledException) { }
            finally { await EndAsync(finished); }
        }

        private void SaveProgress(double position, double? duration, bool? watched)
        {
            try { progress.Save(mediaId, position, duration ?? (stream.DurationSeconds > 0 ? stream.DurationSeconds : null), watched); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { owner._log.LogWarning("Couldn't save cast progress: {Error}", ex.Message); }
        }

        public async Task EndAsync(bool finished, bool stopApp = false)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;
            var last = conn.Media;
            _cts.Cancel();
            owner._active.TryRemove(new KeyValuePair<string, ActiveCast>(device.Id, this));
            if (finished) SaveProgress(last?.Duration ?? stream.DurationSeconds, last?.Duration ?? stream.DurationSeconds, true);
            else if (last is { PlayerState: not "IDLE" } && last.PositionNow(owner.Clock.GetUtcNow()) > 5) SaveProgress(last.PositionNow(owner.Clock.GetUtcNow()), last.Duration, null);
            if (stopApp) { try { await conn.StopAppAsync(CancellationToken.None); } catch (CastException) { } }
            await conn.DisposeAsync();
            await owner.Hls.StopAsync(stream.Id);
        }
    }
}
