using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;

namespace MediaServer.Api.Cast;

public sealed class CastException(string message) : Exception(message);

/// <summary>What the TV last told us about the video it is playing.</summary>
public sealed record CastMediaStatus(int MediaSessionId, string PlayerState, string? IdleReason, double CurrentTime, double? Duration, double PlaybackRate,
    IReadOnlyList<int> ActiveTrackIds, DateTimeOffset At)
{
    /// <summary>Where the video is now: the reported time plus what has played since the report (the TV only reports every so often).</summary>
    public double PositionNow(DateTimeOffset now) => PlayerState == "PLAYING" ? CurrentTime + Math.Max(0, (now - At).TotalSeconds) * PlaybackRate : CurrentTime;
}

public sealed record CastTrack(int Id, string Url, string? Language, string Name);

/// <summary>
/// One connection to one Cast device (port 8009, TLS) speaking the Cast v2 channels we need: connection, heartbeat, receiver (launch the
/// default media receiver, volume) and media (load, play, pause, seek, tracks). The device's certificate is not checked: it is signed by
/// Google's device authority, which isn't something a self-hosted server can verify, and it only ever receives a stream address.
/// </summary>
public sealed class CastConnection(string host, int port, ILogger? log = null) : IAsyncDisposable
{
    public const string DefaultMediaReceiver = "CC1AD845";
    private const string NsConnection = "urn:x-cast:com.google.cast.tp.connection", NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat",
        NsReceiver = "urn:x-cast:com.google.cast.receiver", NsMedia = "urn:x-cast:com.google.cast.media";
    private const string Sender = "sender-0", Platform = "receiver-0";

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestId;
    private volatile string? _transportId, _appSessionId;
    private volatile CastMediaStatus? _media;
    private double _volume = -1;
    private volatile bool _muted, _appSeen;

    /// <summary>Completes when the connection ends, for any reason.</summary>
    public Task Closed => _closed.Task;
    public CastMediaStatus? Media => _media;
    public double? Volume => _volume < 0 ? null : _volume;
    public bool Muted => _muted;
    /// <summary>True from the moment our receiver app is running until it goes away (someone pressed stop on the TV, or another app took over).</summary>
    public bool AppRunning => _transportId is not null && _appSeen;

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                await _tcp.ConnectAsync(host, port, timeout.Token);
                _ssl = new SslStream(_tcp.GetStream(), false, (_, _, _, _) => true);
                await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Close();
            throw new CastException("Couldn't reach the TV. Is it switched on and on the same network as the server?");
        }
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatLoopAsync);
        await SendAsync(NsConnection, Platform, new JsonObject { ["type"] = "CONNECT" }, ct);
    }

    /// <summary>Starts the default media receiver and waits for it to be ready.</summary>
    public async Task LaunchAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(NsReceiver, Platform, new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultMediaReceiver }, ct);
        if ((string?)reply["type"] is "LAUNCH_ERROR" or "INVALID_REQUEST") throw new CastException("The TV wouldn't start its media player.");
        var until = DateTime.UtcNow.AddSeconds(12);
        while (_transportId is null)
        {
            if (DateTime.UtcNow > until) throw new CastException("The TV didn't start its media player in time.");
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct);
        }
        await SendAsync(NsConnection, _transportId, new JsonObject { ["type"] = "CONNECT" }, ct);
    }

    public async Task LoadAsync(string url, string contentType, string title, IReadOnlyList<CastTrack> tracks, IReadOnlyList<int> activeTracks, double startSeconds, CancellationToken ct)
    {
        var media = new JsonObject
        {
            ["contentId"] = url, ["contentType"] = contentType, ["streamType"] = "BUFFERED",
            ["metadata"] = new JsonObject { ["metadataType"] = 0, ["title"] = title },
        };
        if (tracks.Count > 0)
            media["tracks"] = new JsonArray(tracks.Select(t => (JsonNode)new JsonObject
            {
                ["trackId"] = t.Id, ["type"] = "TEXT", ["trackType"] = "TEXT", ["trackContentId"] = t.Url, ["trackContentType"] = "text/vtt",
                ["subtype"] = "SUBTITLES", ["name"] = t.Name, ["language"] = t.Language ?? "und",
            }).ToArray());
        var payload = new JsonObject
        {
            ["type"] = "LOAD", ["sessionId"] = _appSessionId, ["media"] = media, ["autoplay"] = true, ["currentTime"] = Math.Max(0, startSeconds),
            ["activeTrackIds"] = new JsonArray(activeTracks.Select(i => (JsonNode)i).ToArray()),
        };
        var reply = await RequestAsync(NsMedia, _transportId!, payload, ct, TimeSpan.FromSeconds(20));
        var type = (string?)reply["type"];
        if (type != "MEDIA_STATUS") throw new CastException(type is "LOAD_FAILED" or "LOAD_CANCELLED" ? "The TV couldn't play this video." : $"The TV refused the video ({type}).");
    }

    public Task PollAsync(CancellationToken ct) => RequestAsync(NsMedia, _transportId!, new JsonObject { ["type"] = "GET_STATUS" }, ct, TimeSpan.FromSeconds(4));
    public Task PlayAsync(CancellationToken ct) => MediaCommandAsync("PLAY", null, ct);
    public Task PauseAsync(CancellationToken ct) => MediaCommandAsync("PAUSE", null, ct);
    public Task SeekAsync(double seconds, CancellationToken ct) => MediaCommandAsync("SEEK", new JsonObject { ["currentTime"] = Math.Max(0, seconds), ["resumeState"] = "PLAYBACK_START" }, ct);
    public Task SetActiveTracksAsync(IReadOnlyList<int> ids, CancellationToken ct) =>
        MediaCommandAsync("EDIT_TRACKS_INFO", new JsonObject { ["activeTrackIds"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) }, ct);

    public Task SetVolumeAsync(double level, CancellationToken ct) =>
        RequestAsync(NsReceiver, Platform, new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["level"] = Math.Clamp(level, 0, 1) } }, ct);
    public Task SetMutedAsync(bool muted, CancellationToken ct) =>
        RequestAsync(NsReceiver, Platform, new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["muted"] = muted } }, ct);

    /// <summary>Closes the media player on the TV (it returns to its home screen).</summary>
    public async Task StopAppAsync(CancellationToken ct)
    {
        if (_appSessionId is { } id) await SendAsync(NsReceiver, Platform, new JsonObject { ["type"] = "STOP", ["sessionId"] = id, ["requestId"] = Interlocked.Increment(ref _requestId) }, ct);
    }

    private async Task MediaCommandAsync(string type, JsonObject? extra, CancellationToken ct)
    {
        var id = _media?.MediaSessionId ?? throw new CastException("Nothing is playing on the TV.");
        var payload = new JsonObject { ["type"] = type, ["mediaSessionId"] = id };
        if (extra is not null) foreach (var (k, v) in extra) payload[k] = v?.DeepClone();
        await RequestAsync(NsMedia, _transportId!, payload, ct);
    }

    // ---------------------------------------------------------------------------------------------- plumbing

    private async Task<JsonNode> RequestAsync(string ns, string destination, JsonObject payload, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _requestId);
        payload["requestId"] = id;
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ns, destination, payload, ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
            using var reg = limit.Token.Register(() => tcs.TrySetCanceled());
            try { return await tcs.Task; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CastException("The TV stopped answering."); }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(string ns, string destination, JsonObject payload, CancellationToken ct)
    {
        if (_ssl is null || _closed.Task.IsCompleted) throw new CastException("The connection to the TV has ended.");
        var frame = CastWire.Encode(new CastMessage(Sender, destination, ns, payload.ToJsonString()));
        await _write.WaitAsync(ct);
        try { await _ssl.WriteAsync(frame, ct); await _ssl.FlushAsync(ct); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { Close(); throw new CastException("The connection to the TV has ended."); }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var m = await CastWire.ReadAsync(_ssl!, _cts.Token);
                if (m is null) break;
                try { await HandleAsync(m); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or InvalidCastException) { log?.LogDebug("Ignoring a Cast message that couldn't be read: {Error}", ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { }
        finally { Close(); }
    }

    private async Task HandleAsync(CastMessage m)
    {
        if (m.Payload.Length == 0) return;
        if (JsonNode.Parse(m.Payload) is not JsonObject json) return;
        var type = (string?)json["type"];
        if (m.Namespace == NsHeartbeat) { if (type == "PING") await SendAsync(NsHeartbeat, m.Source, new JsonObject { ["type"] = "PONG" }, _cts.Token); return; }
        if (m.Namespace == NsConnection) { if (type == "CLOSE") { if (m.Source == _transportId) _appSeen = false; else if (m.Source == Platform) Close(); } return; }
        if (type == "RECEIVER_STATUS") ApplyReceiverStatus(json["status"]);
        else if (type == "MEDIA_STATUS" && json["status"] is JsonArray { Count: > 0 } list && list[0] is JsonObject s) ApplyMediaStatus(s);
        if (json["requestId"] is { } rid && (int?)rid is int n and > 0 && _pending.TryGetValue(n, out var tcs)) tcs.TrySetResult(json);
    }

    private void ApplyReceiverStatus(JsonNode? status)
    {
        if (status is null) return;
        if (status["volume"] is JsonObject vol)
        {
            if (vol["level"] is { } l) _volume = (double)l;
            if (vol["muted"] is { } mu) _muted = (bool)mu;
        }
        var app = (status["applications"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => (string?)a["appId"] == DefaultMediaReceiver);
        if (app is not null && (string?)app["transportId"] is { Length: > 0 } tid) { _transportId = tid; _appSessionId = (string?)app["sessionId"]; _appSeen = true; }
        else if (_appSeen) _appSeen = false;   // our app is no longer in the list
    }

    private void ApplyMediaStatus(JsonObject s)
    {
        var tracks = (s["activeTrackIds"] as JsonArray)?.Select(x => (int)x!).ToArray() ?? [];
        _media = new CastMediaStatus((int?)s["mediaSessionId"] ?? 0, (string?)s["playerState"] ?? "IDLE", (string?)s["idleReason"], (double?)s["currentTime"] ?? 0,
            (double?)s["media"]?["duration"] ?? _media?.Duration, (double?)s["playbackRate"] ?? 1, tracks, DateTimeOffset.UtcNow);
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                await SendAsync(NsHeartbeat, Platform, new JsonObject { ["type"] = "PING" }, _cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or CastException) { }
    }

    private void Close()
    {
        if (!_closed.TrySetResult()) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var p in _pending.Values) p.TrySetCanceled();
        try { _ssl?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _tcp?.Dispose(); } catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
