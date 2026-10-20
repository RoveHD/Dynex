using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Dynex.Shaper;

/// <summary>
/// RFC 9001/9369 client Initial only, using the public version salt and DCID.
/// Does not derive session keys, inspect 1-RTT content, or alter a packet.
/// Authentication failures, unsupported versions/frames and bounds fail closed.
/// </summary>
internal static class QuicClientHello
{
    internal readonly record struct CryptoFragment(int Offset, byte[] Bytes);
    private static readonly byte[] SaltV1 = Convert.FromHexString("38762cf7f55934b34d179ae6a4c80cadccbb7f0a");
    private static readonly byte[] SaltV2 = Convert.FromHexString("0dede3def700a6db819381be6e269dcbf9bd2ed9");

    internal static bool IsInitial(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || (data[0] & 0xc0) != 0xc0) return false;
        var version = BinaryPrimitives.ReadUInt32BigEndian(data[1..]);
        return version == 1 && (data[0] & 0x30) == 0 ||
            version == 0x6b3343cf && (data[0] & 0x30) == 0x10;
    }

    internal static bool TryReadCrypto(ReadOnlySpan<byte> data, out byte[] connectionId,
        out List<CryptoFragment> fragments)
    {
        connectionId = []; fragments = [];
        if (!IsInitial(data) || data.Length is < 1200 or > 32768) return false;
        var v2 = BinaryPrimitives.ReadUInt32BigEndian(data[1..]) == 0x6b3343cf;
        var dcidLength = data[5];
        if (dcidLength is < 1 or > 20 || data.Length < 7 + dcidLength) return false;
        var dcid = data.Slice(6, dcidLength);
        var p = 6 + dcidLength;
        var scidLength = data[p++];
        if (scidLength > 20 || scidLength > data.Length - p) return false;
        p += scidLength;
        if (!VarInt(data, ref p, out var tokenLength) || tokenLength > 4096 || tokenLength > (ulong)(data.Length - p)) return false;
        p += (int)tokenLength;
        if (!VarInt(data, ref p, out var packetLength) || packetLength < 20 || packetLength > (ulong)(data.Length - p)) return false;
        var pnOffset = p;
        var packetEnd = p + (int)packetLength;
        if (pnOffset > 4096 || pnOffset + 20 > packetEnd) return false;
        try
        {
            var initial = HKDF.Extract(HashAlgorithmName.SHA256, dcid.ToArray(), v2 ? SaltV2 : SaltV1);
            var client = ExpandLabel(initial, "client in", 32);
            var prefix = v2 ? "quicv2 " : "quic ";
            var key = ExpandLabel(client, prefix + "key", 16);
            var iv = ExpandLabel(client, prefix + "iv", 12);
            var hp = ExpandLabel(client, prefix + "hp", 16);
            using var aes = Aes.Create();
            aes.Key = hp;
            var mask = aes.EncryptEcb(data.Slice(pnOffset + 4, 16), PaddingMode.None);
            var first = (byte)(data[0] ^ (mask[0] & 0x0f));
            if ((first & 0x0c) != 0) return false;
            var pnLength = (first & 3) + 1;
            var header = data[..(pnOffset + pnLength)].ToArray();
            header[0] = first;
            uint packetNumber = 0;
            for (var i = 0; i < pnLength; i++)
            {
                header[pnOffset + i] ^= mask[i + 1];
                packetNumber = packetNumber * 256 + header[pnOffset + i];
            }
            // Initial packet numbers near connection start need no epoch guess.
            // Unsupported truncated-number epochs simply fail authentication.
            for (var i = 0; i < 4; i++) iv[^(i + 1)] ^= (byte)(packetNumber >> (i * 8));
            var ciphertextLength = packetEnd - header.Length - 16;
            if (ciphertextLength <= 0) return false;
            var plaintext = new byte[ciphertextLength];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(iv, data.Slice(header.Length, ciphertextLength), data.Slice(packetEnd - 16, 16), plaintext, header);
            if (!ReadFrames(plaintext, fragments)) { fragments.Clear(); return false; }
            connectionId = dcid.ToArray();
            return fragments.Count != 0;
        }
        catch (CryptographicException) { fragments.Clear(); return false; }
    }

    private static bool ReadFrames(ReadOnlySpan<byte> data, List<CryptoFragment> fragments)
    {
        var p = 0;
        while (p < data.Length)
        {
            if (!VarInt(data, ref p, out var type)) return false;
            if (type is 0 or 1) continue; // PADDING, PING
            if (type is 2 or 3) // ACK / ACK_ECN
            {
                if (!VarInt(data, ref p, out _) || !VarInt(data, ref p, out _) ||
                    !VarInt(data, ref p, out var ranges) || ranges > 64 || !VarInt(data, ref p, out _)) return false;
                for (ulong i = 0; i < ranges; i++)
                    if (!VarInt(data, ref p, out _) || !VarInt(data, ref p, out _)) return false;
                if (type == 3)
                    for (var i = 0; i < 3; i++) if (!VarInt(data, ref p, out _)) return false;
                continue;
            }
            if (type != 6 || fragments.Count >= 32 || !VarInt(data, ref p, out var offset) ||
                !VarInt(data, ref p, out var size) || offset > 32768 || size > 32768 - offset ||
                size > (ulong)(data.Length - p)) return false;
            fragments.Add(new((int)offset, data.Slice(p, (int)size).ToArray()));
            p += (int)size;
        }
        return true;
    }

    private static bool VarInt(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset >= data.Length) return false;
        var length = 1 << (data[offset] >> 6);
        if (length > data.Length - offset) return false;
        value = (ulong)(data[offset++] & 63);
        for (var i = 1; i < length; i++) value = value * 256 + data[offset++];
        return true;
    }

    private static byte[] ExpandLabel(byte[] secret, string label, int length)
    {
        var name = Encoding.ASCII.GetBytes("tls13 " + label);
        var info = new byte[4 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)length);
        info[2] = (byte)name.Length;
        name.CopyTo(info, 3); // trailing context length is zero
        return HKDF.Expand(HashAlgorithmName.SHA256, secret, length, info);
    }
}
