using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MediaServer.Api.Security;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Tests;

public sealed class HttpsCertificateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("https").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    private IConfiguration Cfg(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Database:Path"] = Path.Combine(_dir, "db", "t.db") };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    [Fact] public void ASelfSignedCertificate_IsAUsableServerCertificate()
    {
        using var cert = HttpsCertificate.CreateSelfSigned(["media.example", "192.0.2.5", "localhost"]);
        Assert.True(cert.HasPrivateKey);
        Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(4)); Assert.True(cert.NotBefore < DateTime.UtcNow);
        Assert.True(HttpsCertificate.Covers(cert, ["media.example", "192.0.2.5", "LOCALHOST"]));
        Assert.False(HttpsCertificate.Covers(cert, ["other.example"]));
    }

    [Fact] public async Task ARealTlsHandshake_Succeeds_WithTheGeneratedCertificate()
    {
        using var cert = HttpsCertificate.CreateSelfSigned(["localhost"]);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(tcp.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            var buf = new byte[4]; await ssl.ReadExactlyAsync(buf); await ssl.WriteAsync(buf);
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        await using var cs = new SslStream(client.GetStream(), false, (_, c, _, _) => c!.GetCertHashString() == cert.GetCertHashString());   // trust exactly this certificate
        await cs.AuthenticateAsClientAsync("localhost");
        await cs.WriteAsync("ping"u8.ToArray());
        var echo = new byte[4]; await cs.ReadExactlyAsync(echo);
        Assert.Equal("ping", System.Text.Encoding.ASCII.GetString(echo));
        await server; listener.Stop();
    }

    [Fact] public void TheCertificateIsCreatedOnce_KeptNextToTheDatabase_AndReused()
    {
        var a = HttpsCertificate.Load(Cfg()); var b = HttpsCertificate.Load(Cfg());
        Assert.Equal(a.Thumbprint, b.Thumbprint);
        var file = Path.Combine(_dir, "db", "https-cert.pfx");
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact] public void ANewHostname_RegeneratesTheCertificate_SoItCoversThatName()
    {
        var a = HttpsCertificate.Load(Cfg());
        var b = HttpsCertificate.Load(Cfg(new() { ["Https:Hostnames"] = "media.home.example, 192.0.2.9" }));
        Assert.NotEqual(a.Thumbprint, b.Thumbprint);
        Assert.True(HttpsCertificate.Covers(b, ["media.home.example", "192.0.2.9"]));
    }

    [Fact] public void ACorruptedFile_IsReplaced_NotFatal()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "db")); File.WriteAllText(Path.Combine(_dir, "db", "https-cert.pfx"), "garbage");
        Assert.True(HttpsCertificate.Load(Cfg()).HasPrivateKey);
    }

    [Fact] public void YourOwnCertificate_CanBeUsedInstead()
    {
        using var mine = HttpsCertificate.CreateSelfSigned(["mine.example"]);
        var pfx = Path.Combine(_dir, "mine.pfx"); File.WriteAllBytes(pfx, mine.Export(X509ContentType.Pfx, "pw"));
        var loaded = HttpsCertificate.Load(Cfg(new() { ["Https:CertificatePath"] = pfx, ["Https:CertificatePassword"] = "pw" }));
        Assert.Equal(mine.Thumbprint, loaded.Thumbprint);
        Assert.False(File.Exists(Path.Combine(_dir, "db", "https-cert.pfx")));                                  // nothing self-signed was made
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => HttpsCertificate.Load(Cfg(new() { ["Https:CertificatePath"] = pfx, ["Https:CertificatePassword"] = "wrong" })));
    }

    [Fact] public void TheNameList_AlwaysIncludesLocalhost_AndIgnoresJunk()
    {
        var names = HttpsCertificate.Hostnames(Cfg(new() { ["Https:Hostnames"] = " a.example ,, a.example; b.example " }));
        Assert.Contains("localhost", names); Assert.Contains("127.0.0.1", names);
        Assert.Single(names, n => n == "a.example"); Assert.Contains("b.example", names);
        Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
    }
}

public sealed class MediaLinkCorsTests : IDisposable
{
    private readonly AuthFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact] public async Task MediaLinks_CanBeFetchedFromAnotherOrigin_ForCasting()
    {
        var c = _f.Client();
        var req = new HttpRequestMessage(HttpMethod.Options, $"/api/hls/{new string('a', 32)}/index.m3u8"); req.Headers.Add("Origin", "https://cast.example"); req.Headers.Add("Access-Control-Request-Method", "GET");
        var pre = await c.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, pre.StatusCode);
        Assert.Equal("*", pre.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Range", pre.Headers.GetValues("Access-Control-Allow-Headers").Single());
        var get = await c.GetAsync($"/api/hls/{new string('a', 32)}/index.m3u8");
        Assert.Equal("*", get.Headers.GetValues("Access-Control-Allow-Origin").Single());                    // even the 404 carries it, so the browser reports the real status
        Assert.Equal("*", (await c.GetAsync("/stream/5?k=1.bad")).Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact] public async Task TheApiItself_NeverGetsTheseHeaders()
    {
        var c = await _f.SignedIn("alex");
        Assert.False((await c.GetAsync("/api/items")).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await c.GetAsync("/api/auth/state")).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await _f.Client().GetAsync("/api/items")).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact] public async Task ThePwaFiles_ArePublic_AndTheManifestFollowsTheServerName()
    {
        var c = _f.Client();
        var manifest = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/manifest.webmanifest");
        Assert.Equal("MediaServer", manifest.GetProperty("name").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        Assert.Contains(manifest.GetProperty("icons").EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var url in new[] { "/sw.js", "/icons/icon-192.png", "/icons/icon-512.png", "/icons/icon-maskable-512.png", "/icons/apple-touch-icon.png", "/vendor/qrcode.js", "/extras.js", "/certificate.cer" })
            Assert.Contains((await c.GetAsync(url)).StatusCode, new[] { System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.NotFound });
        Assert.Equal(System.Net.HttpStatusCode.OK, (await c.GetAsync("/sw.js")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/certificate.cer")).StatusCode);        // no HTTPS configured: nothing to download
        var me = await _f.SignedIn("alex");
        await me.PutAsJsonAsync("/api/settings", new { values = new Dictionary<string, object?> { ["General:ServerName"] = "Home Cinema" } });
        Assert.Equal("Home Cinema", (await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/manifest.webmanifest")).GetProperty("name").GetString());
    }

    [Fact] public async Task TheServiceWorker_NeverCachesTheApiOrStreams()
    {
        var sw = await _f.Client().GetStringAsync("/sw.js");
        Assert.Contains("startsWith('/api/')", sw); Assert.Contains("startsWith('/stream/')", sw);
    }
}
