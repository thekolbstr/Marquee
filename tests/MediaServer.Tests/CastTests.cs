using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaServer.Api.Cast;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Tests;

public sealed class CastWireTests
{
    [Fact] public void AMessage_SurvivesEncodingAndDecoding()
    {
        var m = new CastMessage("sender-0", "receiver-0", "urn:x-cast:com.google.cast.receiver", """{"type":"LAUNCH","appId":"CC1AD845","requestId":7}""");
        var frame = CastWire.Encode(m);
        Assert.Equal(frame.Length - 4, (int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.Equal(m, CastWire.Decode(frame.AsSpan(4)));
    }

    [Fact] public void NonAsciiText_RoundTrips()
    {
        var m = new CastMessage("a", "b", "c", """{"title":"Café — 映画 🎬"}""");
        Assert.Equal(m, CastWire.Decode(CastWire.Encode(m).AsSpan(4)));
    }

    [Fact] public void ALongPayload_UsesAMultiByteLength()
    {
        var m = new CastMessage("a", "b", "c", new string('x', 70_000));
        Assert.Equal(70_000, CastWire.Decode(CastWire.Encode(m).AsSpan(4))!.Payload.Length);
    }

    [Fact] public void ABinaryPayload_DecodesWithAnEmptyPayload()
    {
        // field 5 (payload_type) = BINARY(1), field 7 = some bytes: device authentication, which we never use
        byte[] body = [0x08, 0, 0x12, 1, (byte)'s', 0x1A, 1, (byte)'d', 0x22, 1, (byte)'n', 0x28, 1, 0x3A, 2, 9, 9];
        Assert.Equal(new CastMessage("s", "d", "n", ""), CastWire.Decode(body));
    }

    [Theory] [InlineData(new byte[] { 0x12, 0x05, 1, 2 })] [InlineData(new byte[] { 0x12 })] [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void DamagedBytes_AreRefusedNotThrown(byte[] body) => Assert.Null(CastWire.Decode(body));

    [Fact] public async Task ReadingAFrame_StopsAtTheEndOfTheStream()
    {
        var a = CastWire.Encode(new CastMessage("a", "b", "c", "{}"));
        using var s = new MemoryStream(a.Concat(a).ToArray());
        Assert.NotNull(await CastWire.ReadAsync(s, default));
        Assert.NotNull(await CastWire.ReadAsync(s, default));
        Assert.Null(await CastWire.ReadAsync(s, default));
    }

    [Fact] public async Task ATruncatedFrame_IsTreatedAsAClosedConnection()
    {
        var a = CastWire.Encode(new CastMessage("a", "b", "c", "{}"));
        using var s = new MemoryStream(a[..^3]);
        Assert.Null(await CastWire.ReadAsync(s, default));
    }

    [Fact] public async Task AnAbsurdLength_MeansItIsNotACastDevice()
    {
        using var s = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF, 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CastWire.ReadAsync(s, default));
    }
}

public sealed class MdnsTests
{
    private sealed class Packet
    {
        private readonly MemoryStream _m = new();
        private int _records;
        public Packet() => _m.Write(new byte[12]);
        public int Offset => (int)_m.Length;
        public int Name(string name) { var at = Offset; foreach (var l in name.Split('.')) { var b = Encoding.UTF8.GetBytes(l); _m.WriteByte((byte)b.Length); _m.Write(b); } _m.WriteByte(0); return at; }
        public void Pointer(int to) { _m.WriteByte((byte)(0xC0 | (to >> 8))); _m.WriteByte((byte)to); }
        public void Record(Action name, int type, byte[] data)
        {
            name();
            Span<byte> h = stackalloc byte[10]; BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)type); BinaryPrimitives.WriteUInt16BigEndian(h[2..], 1); BinaryPrimitives.WriteUInt32BigEndian(h[4..], 120); BinaryPrimitives.WriteUInt16BigEndian(h[8..], (ushort)data.Length);
            _m.Write(h); _m.Write(data); _records++;
        }
        public byte[] Rdata(Action<Packet> w) { var inner = new Packet(); w(inner); return inner._m.ToArray()[12..]; }
        public byte[] Build() { var b = _m.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)_records); return b; }
    }

    private static byte[] Txt(params string[] kv) => kv.SelectMany(s => new byte[] { (byte)Encoding.UTF8.GetByteCount(s) }.Concat(Encoding.UTF8.GetBytes(s))).ToArray();
    private static byte[] Srv(int port, string target) { var p = new Packet(); p.Name(target); return [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. p.Build()[12..]]; }

    private static byte[] Response(string instance, string id, string friendly, string ip, string caps = "5", string model = "Chromecast Ultra")
    {
        var p = new Packet();
        var full = instance + "._googlecast._tcp.local";
        p.Record(() => p.Name("_googlecast._tcp.local"), 12, p.Rdata(r => r.Name(full)));
        var instAt = 0;
        p.Record(() => instAt = p.Name(full), 33, Srv(8009, instance + ".local"));
        p.Record(() => p.Pointer(instAt), 16, Txt($"id={id}", $"fn={friendly}", $"md={model}", $"ca={caps}"));      // names are shortened by pointing back, as real responders do
        p.Record(() => p.Name(instance + ".local"), 1, IPAddress.Parse(ip).GetAddressBytes());
        return p.Build();
    }

    [Fact] public void ADeviceIsFoundFromAResponse()
    {
        var d = Assert.Single(MdnsDecoder.Devices([Response("Chromecast-abc", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "Living Room", "192.0.2.40")]));
        Assert.Equal("Living Room", d.Name); Assert.Equal("192.0.2.40", d.Host); Assert.Equal(8009, d.Port);
        Assert.Equal("Chromecast Ultra", d.Model); Assert.Equal("0f1e2d3c4b5a69788796a5b4c3d2e1f0", d.Id);
    }

    [Fact] public void SeveralDevices_AreSortedByName_AndRepeatsAreMerged()
    {
        var found = MdnsDecoder.Devices([
            Response("b", "bb", "Bedroom", "192.0.2.41"), Response("a", "aa", "Attic", "192.0.2.42"), Response("b", "bb", "Bedroom", "192.0.2.41")]);
        Assert.Equal(["Attic", "Bedroom"], found.Select(d => d.Name));
    }

    [Fact] public void AudioOnlyDevices_AreLeftOut() =>
        Assert.Empty(MdnsDecoder.Devices([Response("mini", "cc", "Kitchen speaker", "192.0.2.43", caps: "198660", model: "Google Home Mini")]));

    [Fact] public void ARecordSplitAcrossPackets_IsStillAssembled()
    {
        var full = Response("x", "dd", "Den", "192.0.2.44");
        Assert.Single(MdnsDecoder.Devices([full, full]));
    }

    [Fact] public void GarbageOnTheNetwork_IsIgnored()
    {
        Assert.Empty(MdnsDecoder.Devices([[], [1, 2, 3], new byte[40], Enumerable.Repeat((byte)0xC0, 64).ToArray(), Encoding.ASCII.GetBytes("not dns at all, just text")]));
        // a name pointer that points at itself must not loop forever
        var loop = new byte[14]; loop[5] = 1; loop[12] = 0xC0; loop[13] = 12;
        Assert.Empty(MdnsDecoder.Devices([loop]));
    }

    [Fact] public void TheQuery_AsksForCastDevices_AndAnswersByUnicast()
    {
        var q = MdnsDecoder.BuildQuery();
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));
        Assert.Contains("_googlecast", Encoding.ASCII.GetString(q));
        Assert.Equal(0x8001, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 2)));   // class IN + unicast-response bit
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4)));       // PTR
    }
}

/// <summary>A pretend Chromecast: TLS on a local port, answering the few Cast messages the server sends.</summary>
internal sealed class FakeReceiver : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _cts = new();
    private SslStream? _ssl;
    private readonly SemaphoreSlim _write = new(1, 1);
    public ConcurrentQueue<(string Ns, JsonObject Json)> Received { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string PlayerState = "PLAYING"; public double Time; public double Level = 0.5; public bool Muted; public int[] Tracks = [];
    public bool LoadFails; public string? IdleReason;

    public FakeReceiver()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=fake-cast", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        try
        {
            using var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            _ssl = new SslStream(tcp.GetStream());
            await _ssl.AuthenticateAsServerAsync(_cert);
            while (!_cts.IsCancellationRequested)
            {
                var m = await CastWire.ReadAsync(_ssl, _cts.Token);
                if (m is null) break;
                var j = JsonNode.Parse(m.Payload)!.AsObject();
                Received.Enqueue((m.Namespace, j));
                await Reply(m, j);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { }
    }

    private Task Send(string ns, string from, JsonObject payload) => SendAsync(ns, from, payload);
    public async Task SendAsync(string ns, string from, JsonObject payload)
    {
        await _write.WaitAsync();
        try { await _ssl!.WriteAsync(CastWire.Encode(new CastMessage(from, "sender-0", ns, payload.ToJsonString()))); await _ssl.FlushAsync(); } finally { _write.Release(); }
    }

    private JsonObject ReceiverStatus(int id, bool app = true) => new()
    {
        ["type"] = "RECEIVER_STATUS", ["requestId"] = id,
        ["status"] = new JsonObject
        {
            ["applications"] = app ? new JsonArray(new JsonObject { ["appId"] = CastConnection.DefaultMediaReceiver, ["transportId"] = "tid-1", ["sessionId"] = "sess-1" }) : new JsonArray(),
            ["volume"] = new JsonObject { ["level"] = Level, ["muted"] = Muted },
        },
    };

    private JsonObject MediaStatus(int id) => new()
    {
        ["type"] = "MEDIA_STATUS", ["requestId"] = id,
        ["status"] = new JsonArray(new JsonObject
        {
            ["mediaSessionId"] = 1, ["playerState"] = PlayerState, ["idleReason"] = IdleReason, ["currentTime"] = Time, ["playbackRate"] = 1, ["media"] = new JsonObject { ["duration"] = 120.5 },
            ["activeTrackIds"] = new JsonArray(Tracks.Select(t => (JsonNode)t).ToArray()),
        }),
    };

    public Task Broadcast(JsonObject media) => SendAsync("urn:x-cast:com.google.cast.media", "tid-1", media);
    public Task BroadcastMedia() => Broadcast(MediaStatus(0));
    public Task BroadcastReceiver(bool app) => SendAsync("urn:x-cast:com.google.cast.receiver", "receiver-0", ReceiverStatus(0, app));

    private async Task Reply(CastMessage m, JsonObject j)
    {
        var type = (string?)j["type"]; var id = (int?)j["requestId"] ?? 0;
        switch (m.Namespace, type)
        {
            case ("urn:x-cast:com.google.cast.tp.heartbeat", "PING"): await Send(m.Namespace, "receiver-0", new JsonObject { ["type"] = "PONG" }); break;
            case ("urn:x-cast:com.google.cast.receiver", "LAUNCH"): await Send(m.Namespace, "receiver-0", ReceiverStatus(id)); break;
            case ("urn:x-cast:com.google.cast.receiver", "SET_VOLUME"):
                if (j["volume"]?["level"] is { } l) Level = (double)l;
                if (j["volume"]?["muted"] is { } mu) Muted = (bool)mu;
                await Send(m.Namespace, "receiver-0", ReceiverStatus(id)); break;
            case ("urn:x-cast:com.google.cast.receiver", "STOP"): await Send(m.Namespace, "receiver-0", ReceiverStatus(0, app: false)); break;
            case ("urn:x-cast:com.google.cast.media", "LOAD"):
                if (LoadFails) { await Send(m.Namespace, "tid-1", new JsonObject { ["type"] = "LOAD_FAILED", ["requestId"] = id }); break; }
                Time = (double?)j["currentTime"] ?? 0; Tracks = (j["activeTrackIds"] as JsonArray)?.Select(x => (int)x!).ToArray() ?? []; PlayerState = "PLAYING";
                await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
            case ("urn:x-cast:com.google.cast.media", "GET_STATUS"): await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
            case ("urn:x-cast:com.google.cast.media", "PAUSE"): PlayerState = "PAUSED"; await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
            case ("urn:x-cast:com.google.cast.media", "PLAY"): PlayerState = "PLAYING"; await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
            case ("urn:x-cast:com.google.cast.media", "SEEK"): Time = (double)j["currentTime"]!; await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
            case ("urn:x-cast:com.google.cast.media", "EDIT_TRACKS_INFO"): Tracks = ((JsonArray)j["activeTrackIds"]!).Select(x => (int)x!).ToArray(); await Send(m.Namespace, "tid-1", MediaStatus(id)); break;
        }
    }

    public void Dispose() { _cts.Cancel(); _listener.Stop(); _ssl?.Dispose(); _cert.Dispose(); }
}

public sealed class CastConnectionTests
{
    private static async Task<(FakeReceiver Tv, CastConnection Conn)> Ready()
    {
        var tv = new FakeReceiver();
        var c = new CastConnection("127.0.0.1", tv.Port);
        await c.ConnectAsync(default);
        await c.LaunchAsync(default);
        return (tv, c);
    }

    private static async Task Eventually(Func<bool> cond, string what)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(30);
        Assert.True(cond(), what);
    }

    [Fact] public async Task ItLaunchesTheMediaPlayer_AndLoadsAVideoWithSubtitles()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        Assert.True(c.AppRunning);
        await c.LoadAsync("http://192.0.2.2:8080/api/hls/abc/index.m3u8", "application/x-mpegURL", "Glass Orchard",
            [new CastTrack(1, "http://192.0.2.2:8080/api/media/5/captions/s0.vtt?k=1.x", "en", "English")], [1], 42.5, default);

        var load = tv.Received.Single(r => (string?)r.Json["type"] == "LOAD").Json;
        Assert.Equal("http://192.0.2.2:8080/api/hls/abc/index.m3u8", (string?)load["media"]!["contentId"]);
        Assert.Equal("application/x-mpegURL", (string?)load["media"]!["contentType"]);
        Assert.Equal("BUFFERED", (string?)load["media"]!["streamType"]);
        Assert.Equal("Glass Orchard", (string?)load["media"]!["metadata"]!["title"]);
        Assert.Equal("sess-1", (string?)load["sessionId"]);
        Assert.Equal(42.5, (double)load["currentTime"]!);
        Assert.True((bool)load["autoplay"]!);
        var track = load["media"]!["tracks"]!.AsArray().Single()!;
        Assert.Equal("text/vtt", (string?)track["trackContentType"]); Assert.Equal("SUBTITLES", (string?)track["subtype"]); Assert.Equal(1, (int)track["trackId"]!);
        Assert.Equal([1], load["activeTrackIds"]!.AsArray().Select(x => (int)x!));
        Assert.Equal("PLAYING", c.Media!.PlayerState); Assert.Equal(120.5, c.Media.Duration);
        // the media messages go to the app's transport, not to the platform
        Assert.Contains(tv.Received, r => r.Ns.EndsWith("tp.connection") && (string?)r.Json["type"] == "CONNECT");
    }

    [Fact] public async Task PauseSeekAndSubtitles_ReachTheTv()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        await c.LoadAsync("u", "application/x-mpegURL", "t", [], [], 0, default);
        await c.PauseAsync(default); Assert.Equal("PAUSED", c.Media!.PlayerState);
        await c.SeekAsync(90, default); Assert.Equal(90, c.Media!.CurrentTime);
        await c.PlayAsync(default); Assert.Equal("PLAYING", c.Media!.PlayerState);
        await c.SetActiveTracksAsync([2], default); Assert.Equal([2], c.Media!.ActiveTrackIds);
        await c.SetVolumeAsync(0.25, default); Assert.Equal(0.25, c.Volume);
        await c.SetMutedAsync(true, default); Assert.True(c.Muted);
        Assert.All(tv.Received.Where(r => (string?)r.Json["type"] is "PAUSE" or "SEEK" or "PLAY"), r => Assert.Equal(1, (int)r.Json["mediaSessionId"]!));
    }

    [Fact] public async Task ThePositionKeepsMovingBetweenReports()
    {
        var s = new CastMediaStatus(1, "PLAYING", null, 10, 100, 1, [], DateTimeOffset.UtcNow.AddSeconds(-3));
        Assert.InRange(s.PositionNow(DateTimeOffset.UtcNow), 12.9, 13.5);
        Assert.Equal(10, (s with { PlayerState = "PAUSED" }).PositionNow(DateTimeOffset.UtcNow));
        await Task.CompletedTask;
    }

    [Fact] public async Task AVideoTheTvRefuses_IsReportedPlainly()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        tv.LoadFails = true;
        var ex = await Assert.ThrowsAsync<CastException>(() => c.LoadAsync("u", "application/x-mpegURL", "t", [], [], 0, default));
        Assert.Contains("couldn't play", ex.Message);
    }

    [Fact] public async Task CommandsBeforeAnythingIsLoaded_AreRefused()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        await Assert.ThrowsAsync<CastException>(() => c.PauseAsync(default));
    }

    [Fact] public async Task ClosingThePlayerOnTheTv_IsNoticed()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        await c.LoadAsync("u", "application/x-mpegURL", "t", [], [], 0, default);
        await tv.BroadcastReceiver(app: false);
        await Eventually(() => !c.AppRunning, "the app should be reported as gone");
    }

    [Fact] public async Task StoppingTheApp_ClosesThePlayerOnTheTv()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        await c.StopAppAsync(default);
        await Eventually(() => !c.AppRunning, "the stop should come back as a status without the app");
        Assert.Contains(tv.Received, r => (string?)r.Json["type"] == "STOP" && (string?)r.Json["sessionId"] == "sess-1");
    }

    [Fact] public async Task StatusPushedByTheTv_UpdatesWithoutAskingForIt()
    {
        var (tv, c) = await Ready();
        using var _ = tv; await using var __ = c;
        await c.LoadAsync("u", "application/x-mpegURL", "t", [], [], 0, default);
        tv.PlayerState = "IDLE"; tv.Time = 119;
        var m = new JsonObject { ["type"] = "MEDIA_STATUS", ["requestId"] = 0, ["status"] = new JsonArray(new JsonObject { ["mediaSessionId"] = 1, ["playerState"] = "IDLE", ["idleReason"] = "FINISHED", ["currentTime"] = 119 }) };
        await tv.Broadcast(m);
        await Eventually(() => c.Media is { PlayerState: "IDLE", IdleReason: "FINISHED" }, "the end of the video should be seen");
        Assert.Equal(120.5, c.Media!.Duration);          // the duration reported earlier is kept
    }

    [Fact] public async Task TheTvDroppingTheConnection_CompletesClosed()
    {
        var (tv, c) = await Ready();
        await using var __ = c;
        tv.Dispose();
        await c.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact] public async Task ATvThatIsNotThere_GivesAFriendlyError()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        await using var c = new CastConnection("127.0.0.1", port);
        var ex = await Assert.ThrowsAsync<CastException>(() => c.ConnectAsync(default));
        Assert.Contains("switched on", ex.Message);
    }

    [Fact] public async Task ARepliesPendingWhenTheTvVanishes_FailRatherThanHang()
    {
        var (tv, c) = await Ready();
        await using var __ = c;
        tv.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => c.LoadAsync("u", "application/x-mpegURL", "t", [], [], 0, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

public sealed class CastApiTests : IDisposable
{
    private readonly AuthFixture _f = new(new Dictionary<string, string?> { ["Cast:Devices"] = "127.0.0.1:1" });
    public void Dispose() => _f.Dispose();

    [Theory] [InlineData("GET", "/api/cast/devices")] [InlineData("GET", "/api/cast/sessions")] [InlineData("POST", "/api/cast/play")] [InlineData("POST", "/api/cast/abc/control")] [InlineData("POST", "/api/cast/abc/stop")]
    public async Task CastingNeedsASignIn(string method, string url)
    {
        var res = await _f.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "POST" ? JsonContent.Create(new { }) : null });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact] public async Task ADeviceListedInSettings_ShowsUpEvenWhenDiscoveryFindsNothing()
    {
        var c = await _f.SignedIn();
        var j = await c.GetFromJsonAsync<JsonElement>("/api/cast/devices");
        var d = j.GetProperty("devices").EnumerateArray().Single(x => x.GetProperty("manual").GetBoolean());
        Assert.Equal("127.0.0.1", d.GetProperty("name").GetString());       // no answer on port 8008, so the address stands in for the name
        Assert.Matches("^[a-z0-9]+$", d.GetProperty("id").GetString()!);
    }

    [Fact] public async Task PlayingOnAnUnknownTv_IsNotFound_AndABadIdIsRefused()
    {
        var c = await _f.SignedIn();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/cast/play", new { deviceId = "nosuchtv", mediaId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/cast/play", new { deviceId = "../../etc", mediaId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/cast/nosuchtv/control", new { action = "pause" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/cast/nosuchtv/stop", null)).StatusCode);
        var s = await c.GetFromJsonAsync<JsonElement>("/api/cast/sessions");
        Assert.Equal(0, s.GetProperty("sessions").GetArrayLength());
    }

    [Fact] public async Task TheTvsPlayerMayFetchStreamsAndSubtitlesAcrossOrigins()
    {
        var c = _f.Client();                                                    // not signed in: the TV has no cookie
        var pre = await c.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/hls/" + new string('a', 32) + "/index.m3u8"));
        Assert.Equal(HttpStatusCode.NoContent, pre.StatusCode);
        Assert.Equal("*", pre.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var vtt = await c.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/media/5/captions/s0.vtt"));
        Assert.Equal(HttpStatusCode.NoContent, vtt.StatusCode);
        // other addresses get no such header
        var other = await c.GetAsync("/api/items");
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact] public async Task Subtitles_NeedASignedLink_ForThatOneTitle()
    {
        await _f.SignedIn();
        var c = _f.Client();                                                    // a fresh client with no cookie, like the TV
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/media/5/captions/s0.vtt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/media/5/captions/s0.vtt?k=1.nope")).StatusCode);
        var good = _f.Auth.StreamToken(5);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/media/5/captions/s0.vtt?k={good}")).StatusCode);   // passes sign-in (404: there is no such file)
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/media/6/captions/s0.vtt?k={good}")).StatusCode);       // a link for another title
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/media/5/captions?k={good}")).StatusCode);              // and only for the subtitle files
    }
}

/// <summary>The whole path: a real library file, a real HLS session, and a pretend TV that answers like a Chromecast.</summary>
public sealed class CastEndToEndTests : IDisposable
{
    private readonly FakeReceiver _tv = new();
    private readonly AuthFixture _f;
    private static bool Have(string t) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, t)));

    public CastEndToEndTests() => _f = new AuthFixture(new Dictionary<string, string?> { ["Auth:Enabled"] = "false", ["Cast:Devices"] = $"127.0.0.1:{_tv.Port}" });
    public void Dispose() { _f.Dispose(); _tv.Dispose(); }

    private async Task<(HttpClient Client, long Media, string Device)> Setup()
    {
        var clip = Path.Combine(_f.Dir, "lib", "Glass Orchard (2022).mp4");
        var r = await new MediaServer.Api.Processes.ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=d=20:s=640x360:r=25", "-f", "lavfi", "-i", "sine=d=20",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", clip], default);
        Assert.True(r.ExitCode == 0, r.StdErr);
        var c = _f.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/library/scan", null)).StatusCode);
        long media;
        using (var conn = _f.Db.Open()) media = Convert.ToInt64(MediaServer.Api.Data.Database.Scalar(conn, "SELECT id FROM media_files LIMIT 1"));
        var devices = await c.GetFromJsonAsync<JsonElement>("/api/cast/devices");
        return (c, media, devices.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("manual").GetBoolean()).GetProperty("id").GetString()!);
    }

    private static async Task Eventually(Func<bool> cond, string what, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20 && !cond(); i++) await Task.Delay(50);
        Assert.True(cond(), what);
    }

    private double? SavedPosition(long media)
    {
        using var conn = _f.Db.Open();
        return MediaServer.Api.Data.Database.Scalar(conn, $"SELECT position_seconds FROM media_progress WHERE media_id={media}") is { } v ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [Fact] public async Task PlayControlAndStop_DriveTheTv_AndKeepTheServersBooksStraight()
    {
        if (!Have("ffmpeg")) return;
        var (c, media, device) = await Setup();
        var res = await c.PostAsJsonAsync("/api/cast/play", new { deviceId = device, mediaId = media, title = "Glass Orchard", startSeconds = 7 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var info = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Glass Orchard", info.GetProperty("title").GetString());

        var load = _tv.Received.Single(r => (string?)r.Json["type"] == "LOAD").Json;
        Assert.Matches("^http://localhost/api/hls/[0-9a-f]{32}/index\\.m3u8$", (string?)load["media"]!["contentId"]);
        Assert.Equal(7, (double)load["currentTime"]!);
        var hls = _f.Factory.Services.GetRequiredService<MediaServer.Api.Hls.HlsSessionManager>();
        Assert.Single(hls.Sessions);

        var s = (await c.GetFromJsonAsync<JsonElement>("/api/cast/sessions")).GetProperty("sessions").EnumerateArray().Single();
        Assert.Equal("PLAYING", s.GetProperty("state").GetString()); Assert.Equal(media, s.GetProperty("mediaId").GetInt64());
        Assert.InRange(s.GetProperty("position").GetDouble(), 6.5, 9);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/cast/devices")).GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("manual").GetBoolean()).GetProperty("busy").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/cast/{device}/control", new { action = "pause" })).StatusCode);
        Assert.Equal("PAUSED", _tv.PlayerState);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/cast/{device}/control", new { action = "seek", value = 12 })).StatusCode);
        Assert.Equal(12, _tv.Time);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/cast/{device}/control", new { action = "volume", value = 0.3 })).StatusCode);
        Assert.Equal(0.3, _tv.Level);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/cast/{device}/control", new { action = "explode" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/cast/{device}/stop", null)).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/cast/sessions")).GetProperty("sessions").EnumerateArray());
        Assert.Contains(_tv.Received, r => (string?)r.Json["type"] == "STOP");
        await Eventually(() => hls.Sessions.Count == 0, "the stream should be stopped with the cast");
        Assert.InRange(SavedPosition(media) ?? 0, 11, 13);                                  // where you left off is remembered for next time
    }

    [Fact] public async Task WhenTheVideoEnds_ItIsMarkedWatched_AndEverythingIsCleanedUp()
    {
        if (!Have("ffmpeg")) return;
        var (c, media, device) = await Setup();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/cast/play", new { deviceId = device, mediaId = media, title = "x" })).StatusCode);
        await Task.Delay(1500);                                                              // the server sees it playing
        _tv.PlayerState = "IDLE"; _tv.IdleReason = "FINISHED";
        await Eventually(() => _f.Factory.Services.GetRequiredService<MediaServer.Api.Cast.CastService>().Sessions().Count == 0, "the cast should end by itself");
        long Watched() { using var conn = _f.Db.Open(); return Convert.ToInt64(MediaServer.Api.Data.Database.Scalar(conn, $"SELECT COALESCE((SELECT watched FROM media_progress WHERE media_id={media}), 0)")); }
        await Eventually(() => Watched() == 1, "the title should be marked watched");
        await Eventually(() => _f.Factory.Services.GetRequiredService<MediaServer.Api.Hls.HlsSessionManager>().Sessions.Count == 0, "the stream should be stopped");
    }

    [Fact] public async Task WhenSomeoneClosesThePlayerOnTheTv_TheServerLetsGo()
    {
        if (!Have("ffmpeg")) return;
        var (c, media, device) = await Setup();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/cast/play", new { deviceId = device, mediaId = media, title = "x" })).StatusCode);
        await _tv.BroadcastReceiver(app: false);
        await Eventually(() => _f.Factory.Services.GetRequiredService<MediaServer.Api.Cast.CastService>().Sessions().Count == 0, "a TV with no player means the cast is over");
    }

    [Fact] public async Task ATvThatRefusesTheVideo_LeavesNothingRunning()
    {
        if (!Have("ffmpeg")) return;
        var (c, media, device) = await Setup();
        _tv.LoadFails = true;
        var res = await c.PostAsJsonAsync("/api/cast/play", new { deviceId = device, mediaId = media, title = "x" });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("couldn't play", await res.Content.ReadAsStringAsync());
        Assert.Empty(_f.Factory.Services.GetRequiredService<MediaServer.Api.Hls.HlsSessionManager>().Sessions);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/cast/sessions")).GetProperty("sessions").EnumerateArray());
    }
}
