using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MediaServer.Api.Cast;

/// <summary>A Cast device on the network. <see cref="Id"/> is safe to put in a URL.</summary>
public sealed record CastDevice(string Id, string Name, string Host, int Port, string Model, bool Manual = false);

/// <summary>Reads and writes the small part of DNS that mDNS service discovery uses. Pure functions, so they can be tested with hand-built packets.</summary>
public static class MdnsDecoder
{
    public const string Service = "_googlecast._tcp.local";

    /// <summary>A multicast query for Cast devices. The "unicast response" bit asks them to answer us directly, so we don't need to own port 5353.</summary>
    public static byte[] BuildQuery()
    {
        using var m = new MemoryStream();
        m.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });     // id 0, flags 0, one question
        foreach (var label in Service.Split('.')) { var b = Encoding.ASCII.GetBytes(label); m.WriteByte((byte)b.Length); m.Write(b); }
        m.WriteByte(0);
        m.Write(new byte[] { 0, 12, 0x80, 1 });                           // PTR, class IN with the unicast-response bit
        return m.ToArray();
    }

    /// <summary>Everything the packets say about Cast devices. Packets may carry the pointer, service, text and address records in any mix.</summary>
    public static IReadOnlyList<CastDevice> Devices(IEnumerable<byte[]> packets)
    {
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addr = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var suffix = "." + Service;

        foreach (var p in packets)
        {
            try
            {
                foreach (var r in Records(p))
                {
                    switch (r.Type)
                    {
                        case 12 when r.Name.Equals(Service, StringComparison.OrdinalIgnoreCase): { var pos = r.DataStart; instances.Add(ReadName(p, ref pos)); break; }
                        case 33 when r.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && r.DataLength >= 7:
                        {
                            var port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(r.DataStart + 4)); var pos = r.DataStart + 6;
                            srv[r.Name] = (ReadName(p, ref pos), port); instances.Add(r.Name); break;
                        }
                        case 16 when r.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase): txt[r.Name] = ReadTxt(p, r.DataStart, r.DataLength); instances.Add(r.Name); break;
                        case 1 when r.DataLength == 4: addr[r.Name] = new IPAddress(p.AsSpan(r.DataStart, 4)); break;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException) { /* a damaged packet from somewhere on the network: skip it */ }
        }

        var found = new Dictionary<string, CastDevice>();
        foreach (var inst in instances)
        {
            if (!srv.TryGetValue(inst, out var s) || !addr.TryGetValue(s.Target, out var ip)) continue;
            var t = txt.GetValueOrDefault(inst) ?? [];
            if (t.TryGetValue("ca", out var ca) && long.TryParse(ca, out var caps) && (caps & 1) == 0) continue;     // speakers and other audio-only devices can't show video
            var label = inst.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? inst[..^suffix.Length] : inst;
            var id = Sanitize(t.GetValueOrDefault("id") ?? label);
            found[id] = new CastDevice(id, t.GetValueOrDefault("fn") is { Length: > 0 } fn ? fn : label, ip.ToString(), s.Port, t.GetValueOrDefault("md") ?? "Cast device");
        }
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Sanitize(string s)
    {
        var id = new string(s.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return id.Length == 0 ? "device" : id.Length > 48 ? id[..48] : id;
    }

    private readonly record struct Rec(string Name, int Type, int DataStart, int DataLength);

    private static IEnumerable<Rec> Records(byte[] p)
    {
        if (p.Length < 12) yield break;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4)), total = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
        var pos = 12;
        for (var i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
        for (var i = 0; i < total && pos < p.Length; i++)
        {
            var name = ReadName(p, ref pos);
            if (pos + 10 > p.Length) yield break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
            var len = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
            pos += 10;
            if (pos + len > p.Length) yield break;
            yield return new Rec(name, type, pos, len);
            pos += len;
        }
    }

    /// <summary>A DNS name: length-prefixed labels, possibly ending in (or being) a pointer back into the packet.</summary>
    internal static string ReadName(byte[] p, ref int pos)
    {
        var sb = new StringBuilder();
        var cur = pos; var end = -1; var jumps = 0;
        while (true)
        {
            var len = p[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (end < 0) end = cur + 2;
                cur = ((len & 0x3F) << 8) | p[cur + 1];
                if (++jumps > 12) throw new FormatException("DNS name loops");
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p, cur + 1, len));
            cur += 1 + len;
        }
        pos = end >= 0 ? end : cur;
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadTxt(byte[] p, int start, int length)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = start;
        while (i < start + length)
        {
            var n = p[i++];
            if (i + n > start + length) break;
            var kv = Encoding.UTF8.GetString(p, i, n); i += n;
            var eq = kv.IndexOf('=');
            if (eq > 0) d[kv[..eq]] = kv[(eq + 1)..];
        }
        return d;
    }
}

/// <summary>Asks the local network which Cast devices are there (mDNS, the same way a phone does).</summary>
public sealed class MdnsBrowser(ILogger<MdnsBrowser>? log = null)
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async Task<IReadOnlyList<CastDevice>> BrowseAsync(TimeSpan window, CancellationToken ct)
    {
        var packets = new ConcurrentQueue<byte[]>();
        var sockets = new List<UdpClient>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(window);
        try
        {
            foreach (var ip in LocalAddresses())
            {
                try
                {
                    var u = new UdpClient(new IPEndPoint(ip, 0));
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    sockets.Add(u);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { log?.LogDebug("mDNS: can't use {Address}: {Error}", ip, ex.Message); }
            }
            if (sockets.Count == 0) { try { sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Any, 0))); } catch (SocketException) { return []; } }
            // Devices that ignore the unicast bit answer to the multicast group instead, so listen there too when the port can be shared.
            var listener = TryListener();
            if (listener is not null) sockets.Add(listener);

            var readers = sockets.Select(s => Task.Run(async () =>
            {
                try { while (true) packets.Enqueue((await s.ReceiveAsync(stop.Token)).Buffer); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
            })).ToList();

            var query = MdnsDecoder.BuildQuery();
            for (var round = 0; round < 3 && !stop.IsCancellationRequested; round++)
            {
                foreach (var s in sockets.Where(s => s != listener))
                {
                    try { await s.SendAsync(query, Group, stop.Token); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(window.TotalMilliseconds / 3.5), stop.Token); } catch (OperationCanceledException) { break; }
            }
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
            await Task.WhenAll(readers);
        }
        finally { foreach (var s in sockets) s.Dispose(); }
        return MdnsDecoder.Devices(packets);
    }

    private static UdpClient? TryListener()
    {
        try
        {
            var u = new UdpClient(AddressFamily.InterNetwork);
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var ip in LocalAddresses()) { try { u.JoinMulticastGroup(Group.Address, ip); } catch (SocketException) { } }
            return u;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return null; }
    }

    private static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct();
}
