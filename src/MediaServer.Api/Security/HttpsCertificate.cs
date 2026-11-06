using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MediaServer.Api.Security;

/// <summary>
/// The server's own HTTPS certificate, for people running it without a reverse proxy. Either a certificate you supply
/// (<c>Https:CertificatePath</c>), or a self-signed one made on first start and kept next to the database. A self-signed certificate
/// encrypts traffic but browsers ask you to trust it once (the page offers the certificate for download).
/// </summary>
public static class HttpsCertificate
{
    public static X509Certificate2 Load(IConfiguration config)
    {
        if (config["Https:CertificatePath"] is { Length: > 0 } path)
            return X509CertificateLoader.LoadPkcs12FromFile(Path.GetFullPath(path), config["Https:CertificatePassword"], X509KeyStorageFlags.DefaultKeySet);

        var file = SelfSignedPath(config);
        var names = Hostnames(config);
        if (File.Exists(file))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30) && Covers(existing, names)) return existing;
                existing.Dispose();
            }
            catch (CryptographicException) { /* unreadable: make a new one */ }
        }
        var cert = CreateSelfSigned(names);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, cert.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);
    }

    public static string SelfSignedPath(IConfiguration config)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config["Database:Path"] ?? "data/mediaserver.db"))!, "https-cert.pfx");

    /// <summary>Names the certificate must be valid for: localhost, this machine, and anything listed in <c>Https:Hostnames</c> (comma-separated).</summary>
    public static IReadOnlyList<string> Hostnames(IConfiguration config)
    {
        var names = new List<string> { "localhost", "127.0.0.1", "::1" };
        try { names.Add(Dns.GetHostName()); } catch (SocketException) { }
        names.AddRange((config["Https:Hostnames"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => n.Length <= 253));
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static X509Certificate2 CreateSelfSigned(IReadOnlyList<string> names)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={names[0]}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) { if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(n); }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server authentication
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public static bool Covers(X509Certificate2 cert, IReadOnlyList<string> names)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return false;
        var have = san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(i => i.ToString())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(n => have.Contains(IPAddress.TryParse(n, out var ip) ? ip.ToString() : n));
    }
}

/// <summary>What the Network settings page shows about HTTPS.</summary>
public sealed record HttpsStatus(bool Enabled, int Port, bool SelfSigned, X509Certificate2? Cert);
