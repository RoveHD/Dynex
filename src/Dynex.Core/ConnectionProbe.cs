using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Dynex.Devices.Tests")]

namespace Dynex.Core;

public enum ConnectionTestState { Passed, Failed, Unknown, Cancelled }
public sealed record ConnectionTestStage(string Name, ConnectionTestState State, string Detail, bool Required = true);
public sealed record PeerConnectionTestResult(Guid DeviceId, DateTimeOffset TestedAtUtc, IReadOnlyList<ConnectionTestStage> Stages)
{
    public bool Succeeded => Stages.Where(s => s.Required).All(s => s.State == ConnectionTestState.Passed);
    public static PeerConnectionTestResult Unavailable(Guid id, string reason) => new(id, DateTimeOffset.UtcNow,
        [new("Lokaler Dienst / Ziel", ConnectionTestState.Unknown, reason),
         new("Anfrage und Antwort", ConnectionTestState.Unknown, "Nicht geprüft."),
         new("Gegenprobe zurück", ConnectionTestState.Unknown, "Nicht geprüft."),
         new("Authentifizierung", ConnectionTestState.Unknown, "Nicht geprüft."),
         new("Version", ConnectionTestState.Unknown, "Nicht geprüft.")]);
}

/// <summary>A bounded, independent four-message challenge. It never creates heartbeat routes or budget members.</summary>
internal sealed class ConnectionProbe(DynexSettings settings, string? signingKey, Func<Guid, bool> excluded)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Pending> pending = [];
    private readonly Dictionary<(Guid Sender, Guid Nonce), Incoming> incoming = [];
    private long rateTick;
    private int rateCount;
    internal static bool IsProbe(ReadOnlySpan<byte> packet) => packet.Length >= 4 && packet[..4].SequenceEqual("DYNP"u8);

    internal async Task<PeerConnectionTestResult> TestAsync(Guid target, IPEndPoint endpoint, TimeSpan timeout,
        Func<byte[], IPEndPoint, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var nonce = Guid.NewGuid();
        var attempt = new Pending(target, endpoint);
        lock (gate)
        {
            if (pending.Count >= 4) return PeerConnectionTestResult.Unavailable(target, "Bereits vier Tests aktiv.");
            pending.Add(nonce, attempt);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string? failure = null;
        var cancelled = false;
        try
        {
            var request = Encode(new(settings.DeviceId, target, nonce, null, 1, Now(), settings.AppVersion));
            await DeliverUntilAcknowledgedAsync(attempt.Response.Task, request, endpoint, send, deadline.Token).ConfigureAwait(false);
            var response = await attempt.Response.Task.ConfigureAwait(false);
            var confirmation = Encode(new(settings.DeviceId, target, nonce, response.Challenge, 3, Now(), settings.AppVersion));
            await DeliverUntilAcknowledgedAsync(attempt.Complete.Task, confirmation, endpoint, send, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { cancelled = cancellationToken.IsCancellationRequested; failure = cancelled ? "Test abgebrochen." : "Keine vollständige Probeantwort innerhalb des Zeitlimits; offline, Paketverlust oder ältere Version möglich."; }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
        { failure = "Test unterbrochen: " + error.Message; }
        finally { lock (gate) pending.Remove(nonce); }
        var remote = attempt.Response.Task.IsCompletedSuccessfully ? attempt.Response.Task.Result : null;
        var done = attempt.Complete.Task.IsCompletedSuccessfully;
        var unknown = cancelled ? ConnectionTestState.Cancelled : ConnectionTestState.Unknown;
        var versionState = remote?.Version is null || settings.AppVersion is null ? ConnectionTestState.Unknown :
            remote.Version == settings.AppVersion ? ConnectionTestState.Passed : ConnectionTestState.Failed;
        return new(target, DateTimeOffset.UtcNow,
        [new("Lokaler Dienst / Ziel", ConnectionTestState.Passed, $"UDP aktiv; Ziel {endpoint}."),
         new("Anfrage und Antwort", remote is not null ? ConnectionTestState.Passed : unknown, remote is not null ? "Frische, authentifizierte Antwort vom ausgewählten Gerät und Ziel-Endpunkt." : failure ?? "Unbekannt."),
         new("Gegenprobe zurück", done ? ConnectionTestState.Passed : unknown, done ? "Gegenstelle bestätigt den Empfang unserer Antwort auf ihre neue Challenge." : failure ?? "Nicht bestätigt."),
         new("Authentifizierung", remote is not null ? ConnectionTestState.Passed : unknown, remote is not null ? signingKey is null ? "Gruppenschlüssel und Geräte-ID bestätigt (Mitglieder derselben Gruppe teilen den Schlüssel)." : "Signatur an Geräte-ID und frische Test-Challenge gebunden." : "Keine passende authentifizierte Probeantwort."),
         new("Version", versionState, remote?.Version is { } version ? $"Lokal {settings.AppVersion ?? "unbekannt"}; Gegenstelle {version}; Testprotokoll 1." : "Gegenstellen-Version unbekannt; fehlende Probeantwort beweist keine inkompatible Version.")]);
    }

    internal async Task ReceiveAsync(byte[] packet, IPEndPoint source,
        Func<byte[], IPEndPoint, CancellationToken, Task> send, CancellationToken token)
    {
        lock (gate)
        {
            if (Stopwatch.GetElapsedTime(rateTick) >= TimeSpan.FromSeconds(1)) { rateTick = Stopwatch.GetTimestamp(); rateCount = 0; }
            if (++rateCount > 64) return;
        }
        var message = Decode(packet);
        if (message is null || message.Target != settings.DeviceId || message.Sender == settings.DeviceId || excluded(message.Sender)) return;
        Message? reply = null;
        lock (gate)
        {
            foreach (var key in incoming.Where(p => Stopwatch.GetElapsedTime(p.Value.Tick) > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) incoming.Remove(key);
            var keyId = (message.Sender, message.Nonce);
            if (message.Kind == 1)
            {
                if (message.Challenge is not null) return;
                Guid challenge;
                if (incoming.TryGetValue(keyId, out var cached))
                {
                    if (cached.Completed || !cached.Endpoint.Equals(source)) return;
                    challenge = cached.Challenge; // UDP retransmission never resets a challenge or its deadline.
                }
                else
                {
                    if (incoming.Count >= 64) return;
                    challenge = Guid.NewGuid();
                    incoming[keyId] = new(source, challenge, Stopwatch.GetTimestamp());
                }
                reply = new(settings.DeviceId, message.Sender, message.Nonce, challenge, 2, Now(), settings.AppVersion);
            }
            else if (message.Kind == 2 && message.Challenge is { } responseChallenge && pending.TryGetValue(message.Nonce, out var active) &&
                active.Target == message.Sender && active.Endpoint.Equals(source))
            {
                active.Response.TrySetResult(message);
            }
            else if (message.Kind == 3 && incoming.TryGetValue(keyId, out var request) &&
                request.Endpoint.Equals(source) && request.Challenge == message.Challenge)
            {
                incoming[keyId] = request with { Completed = true };
                reply = new(settings.DeviceId, message.Sender, message.Nonce, message.Challenge, 4, Now(), settings.AppVersion);
            }
            else if (message.Kind == 4 && pending.TryGetValue(message.Nonce, out var completing) &&
                completing.Target == message.Sender && completing.Endpoint.Equals(source) && completing.Response.Task.IsCompletedSuccessfully &&
                completing.Response.Task.Result.Challenge == message.Challenge)
                completing.Complete.TrySetResult();
        }
        if (reply is not null && !excluded(reply.Target)) await send(Encode(reply), source, token).ConfigureAwait(false);
    }

    private static async Task DeliverUntilAcknowledgedAsync(Task acknowledgement, byte[] packet, IPEndPoint endpoint,
        Func<byte[], IPEndPoint, CancellationToken, Task> send, CancellationToken token)
    {
        // UDP does not guarantee delivery. Retransmit only the same outstanding challenge within
        // the caller's deadline; duplicate handling is idempotent and never resets replay state.
        while (!acknowledgement.IsCompleted)
        {
            await send(packet, endpoint, token).ConfigureAwait(false);
            try { await acknowledgement.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        await acknowledgement.ConfigureAwait(false);
    }

    private byte[] Encode(Message message)
    {
        using var signer = signingKey is null ? null : AutoLanIdentity.ImportPrivate(signingKey);
        if (signer is not null) message = message with { PublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) };
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var packet = new byte[8 + body.Length + (signer is null ? 32 : 64)];
        "DYNP"u8.CopyTo(packet); packet[4] = 1; packet[5] = signer is null ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), checked((ushort)body.Length)); body.CopyTo(packet, 8);
        if (signer is not null) signer.SignData(packet.AsSpan(0, 8 + body.Length), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).CopyTo(packet, 8 + body.Length);
        else
        {
            var key = Convert.FromBase64String(settings.GroupKeyBase64);
            try { HMACSHA256.HashData(key, packet.AsSpan(0, 8 + body.Length)).CopyTo(packet, 8 + body.Length); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        return packet;
    }
    private Message? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 40 or > 2048 || !IsProbe(packet) || packet[4] != 1 || packet[5] != (signingKey is null ? 1 : 2)) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        var signatureSize = signingKey is null ? 32 : 64;
        if (packet.Length != 8 + length + signatureSize) return null;
        try
        {
            var message = JsonSerializer.Deserialize<Message>(packet.Slice(8, length));
            if (message is null || message.Sender == Guid.Empty || message.Target == Guid.Empty || message.Nonce == Guid.Empty || message.Challenge == Guid.Empty ||
                message.Kind is < 1 or > 4 || Math.Abs((decimal)Now() - message.SentAt) > 30000 ||
                message.Version is { } version && !AutoLanCrypto.IsVersion(version)) return null;
            if (signingKey is null)
            {
                var key = Convert.FromBase64String(settings.GroupKeyBase64);
                try { if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, packet[..(8 + length)]), packet[(8 + length)..])) return null; }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            else
            {
                if (message.PublicKey?.Length != 124) return null;
                var publicKey = Convert.FromBase64String(message.PublicKey);
                if (AutoLanIdentity.PublicId(publicKey) != message.Sender) return null;
                using var signer = ECDsa.Create(); signer.ImportSubjectPublicKeyInfo(publicKey, out var read);
                if (read != publicKey.Length || signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                    !signer.VerifyData(packet[..(8 + length)], packet[(8 + length)..], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            }
            return message;
        }
        catch (Exception error) when (error is JsonException or CryptographicException or ArgumentException or FormatException) { return null; }
    }
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private sealed record Message(Guid Sender, Guid Target, Guid Nonce, Guid? Challenge, int Kind, long SentAt, string? Version, string? PublicKey = null);
    private sealed record Incoming(IPEndPoint Endpoint, Guid Challenge, long Tick, bool Completed = false);
    private sealed class Pending(Guid target, IPEndPoint endpoint)
    {
        internal Guid Target { get; } = target;
        internal IPEndPoint Endpoint { get; } = endpoint;
        internal TaskCompletionSource<Message> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
