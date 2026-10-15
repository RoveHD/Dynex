using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace Dynex.Core;

internal sealed record WireAdvertisement(Guid DeviceId, Guid SessionId, long Sequence, long SentAtUnixMs,
    string DeviceName, bool IsGaming, double? DownloadMbps, double? LatencyMs,
    double? AppliedLimitMbps, string? Game, bool ShaperActive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? IdleBaselineMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LatencyTargetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LatencySampleSequence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? IdleBaselineAtUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? UploadMbps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DownloadBudgetProposal? BudgetProposal = null,
    // Relative share of the common download budget (1 = normal). Older peers omit it.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ShareWeight = null,
    // Busiest program on the sender right now (e.g. "steam"), for "who is using the line".
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TopProgram = null)
{
    internal static bool ValidWeight(double? weight) => weight is null || double.IsFinite(weight.Value) && weight >= 0.25 && weight <= 4;
    internal static bool ValidProgram(string? name) => name is null || name.Length is > 0 and <= 64 && !name.Any(char.IsControl);
}

internal sealed class PacketCodec : IDisposable
{
    internal const int MaxPacketBytes = 2048;
    private const int HeaderSize = 27;
    // This is encrypted binary JSON, never HTML. Direct UTF-8 keeps bounded 80-character
    // Unicode names inside the packet budget instead of expanding every character to \uXXXX.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly byte[] _encryptionKey, _authenticationKey, _groupTag;

    internal PacketCodec(string base64Key)
    {
        var master = Convert.FromBase64String(base64Key);
        try
        {
            _encryptionKey = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("DYNEX/v1/encryption"));
            _authenticationKey = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("DYNEX/v1/authentication"));
            _groupTag = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("DYNEX/v1/group"))[..8];
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    internal byte[] Encode(WireAdvertisement message)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (plain.Length > MaxPacketBytes - HeaderSize - 32) throw new ArgumentException("Heartbeat zu groß.");
        var packet = new byte[HeaderSize + plain.Length + 32];
        "DYNX"u8.CopyTo(packet);
        packet[4] = 1;
        _groupTag.CopyTo(packet, 5);
        RandomNumberGenerator.Fill(packet.AsSpan(13, 12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(25, 2), checked((ushort)plain.Length));
        try
        {
            using var aes = new AesGcm(_encryptionKey, 16);
            aes.Encrypt(packet.AsSpan(13, 12), plain, packet.AsSpan(HeaderSize, plain.Length),
                packet.AsSpan(HeaderSize + plain.Length, 16), packet.AsSpan(0, HeaderSize));
            var mac = HMACSHA256.HashData(_authenticationKey, packet.AsSpan(0, packet.Length - 16));
            mac.AsSpan(0, 16).CopyTo(packet.AsSpan(packet.Length - 16));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return packet;
    }

    internal WireAdvertisement? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < HeaderSize + 32 or > MaxPacketBytes || !packet[..4].SequenceEqual("DYNX"u8) ||
            packet[4] != 1 || !CryptographicOperations.FixedTimeEquals(packet.Slice(5, 8), _groupTag)) return null;
        var size = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(25, 2));
        if (size + HeaderSize + 32 != packet.Length) return null;
        var expected = HMACSHA256.HashData(_authenticationKey, packet[..^16]);
        if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 16), packet[^16..])) return null;
        var plain = new byte[size];
        try
        {
            using var aes = new AesGcm(_encryptionKey, 16);
            aes.Decrypt(packet.Slice(13, 12), packet.Slice(HeaderSize, size),
                packet.Slice(HeaderSize + size, 16), plain, packet[..HeaderSize]);
            var result = JsonSerializer.Deserialize<WireAdvertisement>(plain);
            if (result is null || result.DeviceId == Guid.Empty || result.SessionId == Guid.Empty || result.Sequence <= 0 ||
                string.IsNullOrWhiteSpace(result.DeviceName) || result.DeviceName.Length > 80 || result.DeviceName.Any(char.IsControl) ||
                result.Game?.Length > 80 || result.Game?.Any(char.IsControl) == true ||
                !ValidMetric(result.DownloadMbps, 100000) || !ValidMetric(result.LatencyMs, 10000) ||
                !ValidMetric(result.AppliedLimitMbps, 100000) || !ValidMetric(result.IdleBaselineMs, 10000) ||
                !ValidMetric(result.UploadMbps, 100000) ||
                result.LatencyTargetId?.Length > 256 || result.LatencyTargetId?.Any(char.IsControl) == true ||
                result.LatencySampleSequence is <= 0 || result.BudgetProposal is { IsValid: false } || !WireAdvertisement.ValidWeight(result.ShareWeight) ||
                !WireAdvertisement.ValidProgram(result.TopProgram)) return null;
            return result;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool ValidMetric(double? value, double max) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= max;
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_encryptionKey);
        CryptographicOperations.ZeroMemory(_authenticationKey);
    }
}
