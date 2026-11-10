using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Cast;

/// <summary>One message of the Cast v2 protocol: who sent it, who it is for, which channel (namespace) it belongs to, and a JSON payload.</summary>
public sealed record CastMessage(string Source, string Destination, string Namespace, string Payload);

/// <summary>
/// The Cast v2 wire format: each message is a 4-byte big-endian length followed by a protobuf "CastMessage". Only the handful of fields
/// Cast actually uses are written or read here, so no protobuf library is needed.
/// (1 protocol_version, 2 source_id, 3 destination_id, 4 namespace, 5 payload_type, 6 payload_utf8, 7 payload_binary.)
/// </summary>
public static class CastWire
{
    /// <summary>Real devices never send anything near this; a bigger frame means we are not talking to a Cast device.</summary>
    public const int MaxFrame = 1 << 20;

    public static byte[] Encode(CastMessage m)
    {
        using var body = new MemoryStream();
        body.WriteByte(0x08); body.WriteByte(0);              // protocol_version = CASTV2_1_0
        WriteString(body, 2, m.Source);
        WriteString(body, 3, m.Destination);
        WriteString(body, 4, m.Namespace);
        body.WriteByte(0x28); body.WriteByte(0);              // payload_type = STRING
        WriteString(body, 6, m.Payload);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>Decodes one message body (without the length prefix). Null if it is malformed. A binary payload (device authentication) comes back with an empty payload.</summary>
    public static CastMessage? Decode(ReadOnlySpan<byte> b)
    {
        string src = "", dst = "", ns = "", payload = "";
        ulong type = 0;
        try
        {
            var i = 0;
            while (i < b.Length)
            {
                var tag = ReadVarint(b, ref i);
                var field = (int)(tag >> 3);
                switch ((int)(tag & 7))
                {
                    case 0: { var v = ReadVarint(b, ref i); if (field == 5) type = v; break; }
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    case 2:
                    {
                        var len = ReadVarint(b, ref i);
                        if (len > (ulong)(b.Length - i)) return null;
                        var seg = b.Slice(i, (int)len); i += (int)len;
                        var text = field is 2 or 3 or 4 or 6 ? Encoding.UTF8.GetString(seg) : null;
                        switch (field) { case 2: src = text!; break; case 3: dst = text!; break; case 4: ns = text!; break; case 6: payload = text!; break; }
                        break;
                    }
                    default: return null;
                }
                if (i > b.Length) return null;
            }
        }
        catch (FormatException) { return null; }
        return new CastMessage(src, dst, ns, type == 0 ? payload : "");
    }

    /// <summary>Reads one framed message. Null when the other side closed the connection.</summary>
    public static async Task<CastMessage?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        if (!await ReadFullAsync(s, head, ct)) return null;
        var len = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (len > MaxFrame) throw new InvalidDataException("That doesn't look like a Cast device (oversized message).");
        var body = new byte[len];
        if (!await ReadFullAsync(s, body, ct)) return null;
        return Decode(body) ?? new CastMessage("", "", "", "");
    }

    private static async Task<bool> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var n = 0;
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) return false;
            n += r;
        }
        return true;
    }

    private static void WriteVarint(Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; } s.WriteByte((byte)v); }

    private static void WriteString(Stream s, int field, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        s.WriteByte((byte)((field << 3) | 2));
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int i)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= b.Length) throw new FormatException("truncated varint");
            var x = b[i++];
            result |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return result;
        }
        throw new FormatException("varint too long");
    }
}
