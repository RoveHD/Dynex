using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dynex.Core;

internal sealed record AutoHello(Guid DeviceId, Guid SessionId, string SigningPublicKey,
    string ExchangePublicKey, string Challenge, long SentAtUnixMs,
    Guid? TargetId = null, Guid? TargetSession = null, string? TargetChallenge = null, string? TargetExchangePublicKey = null,
    // Optional and signed. Releases before 1.0.6 ignore this unknown property and do not send it.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AppVersion = null);

internal sealed class AutoLanCrypto : IDisposable
{
    internal const int MaxDatagram = 2048;
    internal const int StatusHeader = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ECDsa _signing;
    private readonly ECDiffieHellman _exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    internal AutoHello Local { get; }

    internal AutoLanCrypto(string privateKey, string? appVersion = null)
    {
        _signing = AutoLanIdentity.ImportPrivate(privateKey);
        var pub = _signing.ExportSubjectPublicKeyInfo();
        Local = new(AutoLanIdentity.PublicId(pub), Guid.NewGuid(), Convert.ToBase64String(pub),
            Convert.ToBase64String(_exchange.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 0, AppVersion: appVersion);
    }

    internal static bool IsVersion(string value) => value.Length <= 16 && value.All(c => char.IsAsciiDigit(c) || c == '.') &&
        Version.TryParse(value, out var parsed) && parsed.Major < 1000;

    internal byte[] Hello(DateTimeOffset now) => Sign(Local with { SentAtUnixMs = now.ToUnixTimeMilliseconds() }, 1);
    internal byte[] Ack(AutoHello remote, DateTimeOffset now) => Sign(Local with
    {
        SentAtUnixMs = now.ToUnixTimeMilliseconds(), TargetId = remote.DeviceId,
        TargetSession = remote.SessionId, TargetChallenge = remote.Challenge, TargetExchangePublicKey = remote.ExchangePublicKey
    }, 2);

    internal byte[] Sign(AutoHello body, byte kind)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        if (json.Length > MaxDatagram - 72) throw new ArgumentException("AutoLAN-Handshake zu groß.");
        var packet = new byte[8 + json.Length + 64];
        "DYNA"u8.CopyTo(packet); packet[4] = 1; packet[5] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)json.Length);
        json.CopyTo(packet, 8);
        var signature = _signing.SignData(packet.AsSpan(0, packet.Length - 64), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        signature.CopyTo(packet, packet.Length - 64);
        return packet;
    }

    internal static AutoHello? Verify(ReadOnlySpan<byte> packet, DateTimeOffset now)
    {
        if (packet.Length is < 72 or > MaxDatagram || !packet[..4].SequenceEqual("DYNA"u8) || packet[4] != 1 || packet[5] is not (1 or 2) ||
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) + 72 != packet.Length) return null;
        try
        {
            var hello = JsonSerializer.Deserialize<AutoHello>(packet.Slice(8, packet.Length - 72));
            if (hello is null || hello.DeviceId == Guid.Empty || hello.SessionId == Guid.Empty ||
                hello.SigningPublicKey?.Length != 124 || hello.ExchangePublicKey?.Length != 124 || hello.Challenge?.Length != 44 ||
                Math.Abs((decimal)now.ToUnixTimeMilliseconds() - hello.SentAtUnixMs) > 30000 ||
                hello.AppVersion is { } version && !IsVersion(version) ||
                packet[5] == 1 && (hello.TargetId is not null || hello.TargetSession is not null || hello.TargetChallenge is not null || hello.TargetExchangePublicKey is not null) ||
                packet[5] == 2 && (hello.TargetId is null || hello.TargetSession is null || hello.TargetChallenge?.Length != 44 || hello.TargetExchangePublicKey?.Length != 124)) return null;
            var pub = Convert.FromBase64String(hello.SigningPublicKey);
            var exchange = Convert.FromBase64String(hello.ExchangePublicKey);
            if (pub.Length != 91 || exchange.Length != 91 || Convert.FromBase64String(hello.Challenge).Length != 32 ||
                hello.TargetChallenge is { } target && Convert.FromBase64String(target).Length != 32 ||
                hello.TargetExchangePublicKey is { } targetKey && Convert.FromBase64String(targetKey).Length != 91 || AutoLanIdentity.PublicId(pub) != hello.DeviceId) return null;
            using var signer = ECDsa.Create();
            signer.ImportSubjectPublicKeyInfo(pub, out var read);
            if (read != pub.Length || signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !signer.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(pub) ||
                !signer.VerifyData(packet[..^64], packet[^64..], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            using var dh = ECDiffieHellman.Create();
            dh.ImportSubjectPublicKeyInfo(exchange, out read);
            if (read != exchange.Length || dh.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !dh.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(exchange)) return null;
            return hello;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException or JsonException) { return null; }
    }

    internal bool IsAckForLocal(AutoHello remote) => remote.TargetId == Local.DeviceId &&
        remote.TargetSession == Local.SessionId && remote.TargetChallenge == Local.Challenge && remote.TargetExchangePublicKey == Local.ExchangePublicKey;

    internal AutoChannel Derive(AutoHello remote)
    {
        using var dh = ECDiffieHellman.Create();
        dh.ImportSubjectPublicKeyInfo(Convert.FromBase64String(remote.ExchangePublicKey), out _);
        var raw = _exchange.DeriveRawSecretAgreement(dh.PublicKey);
        // Canonical transcript excludes changing wall-clock timestamps and signature randomness.
        static string Participant(AutoHello h) => $"{h.DeviceId:N}|{h.SessionId:N}|{h.SigningPublicKey}|{h.ExchangePublicKey}|{h.Challenge}";
        var first = Local.DeviceId.CompareTo(remote.DeviceId) < 0;
        var transcript = Encoding.UTF8.GetBytes("DYNEX/AutoLAN/v1|" + (first ? Participant(Local) + "|" + Participant(remote) : Participant(remote) + "|" + Participant(Local)));
        var salt = SHA256.HashData(transcript);
        try
        {
            var forward = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, salt, "DYNEX/AutoLAN/v1/first-to-second"u8.ToArray());
            var reverse = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, salt, "DYNEX/AutoLAN/v1/second-to-first"u8.ToArray());
            return new(remote, first ? forward : reverse, first ? reverse : forward);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    internal byte[] Encrypt(AutoChannel channel, WireAdvertisement wire)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(wire, JsonOptions);
        if (plain.Length > MaxDatagram - StatusHeader - 16) throw new ArgumentException("AutoLAN-Status zu groß.");
        var packet = new byte[StatusHeader + plain.Length + 16];
        "DYNA"u8.CopyTo(packet); packet[4] = 1; packet[5] = 3;
        Local.DeviceId.TryWriteBytes(packet.AsSpan(6, 16)); channel.Remote.DeviceId.TryWriteBytes(packet.AsSpan(22, 16));
        Local.SessionId.TryWriteBytes(packet.AsSpan(38, 16)); channel.Remote.SessionId.TryWriteBytes(packet.AsSpan(54, 16));
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(70, 8), wire.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(78, 8), wire.SentAtUnixMs);
        RandomNumberGenerator.Fill(packet.AsSpan(86, 12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(98, 2), (ushort)plain.Length);
        try
        {
            using var aes = new AesGcm(channel.SendKey, 16);
            aes.Encrypt(packet.AsSpan(86, 12), plain, packet.AsSpan(StatusHeader, plain.Length), packet.AsSpan(packet.Length - 16), packet.AsSpan(0, StatusHeader));
            return packet;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal WireAdvertisement? Decrypt(AutoChannel channel, ReadOnlySpan<byte> packet, DateTimeOffset now)
    {
        if (packet.Length is < StatusHeader + 16 or > MaxDatagram || !packet[..4].SequenceEqual("DYNA"u8) || packet[4] != 1 || packet[5] != 3 ||
            new Guid(packet.Slice(6, 16)) != channel.Remote.DeviceId || new Guid(packet.Slice(22, 16)) != Local.DeviceId ||
            new Guid(packet.Slice(38, 16)) != channel.Remote.SessionId || new Guid(packet.Slice(54, 16)) != Local.SessionId ||
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(98, 2)) + StatusHeader + 16 != packet.Length) return null;
        var sequence = BinaryPrimitives.ReadInt64BigEndian(packet.Slice(70, 8));
        var sent = BinaryPrimitives.ReadInt64BigEndian(packet.Slice(78, 8));
        if (sequence <= channel.LastReceivedSequence || Math.Abs((decimal)now.ToUnixTimeMilliseconds() - sent) > 30000) return null;
        var plain = new byte[packet.Length - StatusHeader - 16];
        try
        {
            using var aes = new AesGcm(channel.ReceiveKey, 16);
            aes.Decrypt(packet.Slice(86, 12), packet.Slice(StatusHeader, plain.Length), packet[^16..], plain, packet[..StatusHeader]);
            var wire = JsonSerializer.Deserialize<WireAdvertisement>(plain);
            if (wire is null || wire.DeviceId != channel.Remote.DeviceId || wire.SessionId != channel.Remote.SessionId ||
                wire.Sequence != sequence || wire.SentAtUnixMs != sent || string.IsNullOrWhiteSpace(wire.DeviceName) ||
                wire.DeviceName.Length > 80 || wire.DeviceName.Any(char.IsControl) || wire.Game?.Length > 80 || wire.Game?.Any(char.IsControl) == true ||
                !Valid(wire.DownloadMbps, 100000) || !Valid(wire.UploadMbps, 100000) || !Valid(wire.LatencyMs, 10000) || !Valid(wire.AppliedLimitMbps, 100000) ||
                !Valid(wire.IdleBaselineMs, 10000) || wire.LatencyTargetId?.Length > 256 || wire.LatencyTargetId?.Any(char.IsControl) == true ||
                wire.LatencySampleSequence is <= 0 || wire.BudgetProposal is { IsValid: false } || !WireAdvertisement.ValidWeight(wire.ShareWeight)) return null;
            channel.LastReceivedSequence = sequence;
            return wire;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool Valid(double? d, double max) => d is null || double.IsFinite(d.Value) && d >= 0 && d <= max;
    public void Dispose() { _signing.Dispose(); _exchange.Dispose(); }
}

internal sealed class AutoChannel(AutoHello remote, byte[] sendKey, byte[] receiveKey) : IDisposable
{
    internal AutoHello Remote { get; } = remote;
    internal byte[] SendKey { get; } = sendKey;
    internal byte[] ReceiveKey { get; } = receiveKey;
    internal long LastReceivedSequence { get; set; }
    public void Dispose() { CryptographicOperations.ZeroMemory(SendKey); CryptographicOperations.ZeroMemory(ReceiveKey); }
}
