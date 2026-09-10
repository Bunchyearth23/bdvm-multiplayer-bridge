using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BDVM.Domain;

public enum CompanyMessageType : byte
{
    IntentRequest = 1,
    IntentResult = 2,
    AuthoritativeSnapshot = 3,
    ClientObservation = 4
}

public enum CompanyIntentType : byte
{
    CreateCompany = 1,
    ApplyToCompany = 2,
    InvitePlayer = 3,
    ChangePermission = 4,
    TransferFunds = 5,
    AcquireVehicle = 6,
    DecideApplication = 7,
    RespondToInvitation = 8,
    ChangeMembershipPolicy = 9,
    LeaveCompany = 10,
    TransferLeadership = 11,
    DissolveCompany = 12,
    ModuleOperation = 13
}

public enum ProtocolResultStatus : byte
{
    Succeeded = 1,
    Rejected = 2,
    TimedOut = 3
}

public static class CompanyProtocolLimits
{
    public const ushort MinimumSupportedVersion = 1;
    public const ushort CurrentVersion = 3;
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public const int MaximumPayloadBytes = 8 * 1024;
    public const int MaximumIdBytes = 96;
    public const int MaximumNameBytes = 128;
    public const int MaximumDetailBytes = 512;
    public const int MaximumSnapshotBytes = 8 * 1024;
    public const double MaximumTransferAmount = 1_000_000_000_000d;
}

public sealed class CompanyProtocolEnvelope
{
    public ushort ProtocolVersion { get; set; } = CompanyProtocolLimits.CurrentVersion;
    public CompanyMessageType MessageType { get; set; }
    public string RequestId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public long ExpectedVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public sealed class CompanyIntent
{
    public CompanyIntentType Type { get; set; }
    public string Name { get; set; } = "";
    public string TargetPlayerId { get; set; } = "";
    public string Permission { get; set; } = "";
    public bool Enabled { get; set; }
    public string SourceAccount { get; set; } = "";
    public string DestinationAccount { get; set; } = "";
    public double Amount { get; set; }
    public string OfferId { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Acquirer { get; set; } = "";
    public string Payer { get; set; } = "";
    public string MembershipRequestId { get; set; } = "";
    public string MembershipPolicy { get; set; } = "";
    public long DebtAmount { get; set; }
    public long PenaltyAmount { get; set; }
    public string ModuleAction { get; set; } = "";
    public string ModulePayloadJson { get; set; } = "";
}

public sealed class ProtocolResult
{
    public string RequestId { get; set; } = "";
    public ProtocolResultStatus Status { get; set; }
    public string Code { get; set; } = "";
    public string Detail { get; set; } = "";
    public long AuthoritativeVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public sealed class PeerContext
{
    public string PeerKey { get; set; } = "";
    public string AuthenticatedPlayerId { get; set; } = "";
    public bool IsKnown { get; set; }
    public bool IsReady { get; set; }
}

public interface ICompanyIntentExecutor
{
    ProtocolResult Execute(PeerContext peer, CompanyProtocolEnvelope envelope, CompanyIntent intent);
}

public interface IAuthoritativeModuleIntentExecutor
{
    ProtocolResult Execute(string authenticatedPlayerId, string requestId, string action, string payloadJson);
}

public interface IAuthoritativeSnapshotSource
{
    byte[] CreateSnapshotFor(string authenticatedPlayerId);
}

public interface IClientObservationSink
{
    void Observe(string authenticatedPlayerId, byte[] observation);
}

public sealed class ProtocolRequestJournal
{
    private const int MaximumTransientRequests = 4096;
    private readonly object gate = new object();
    private readonly Dictionary<string, StoredRequest> requests = new Dictionary<string, StoredRequest>(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredRequest> transientRequests = new Dictionary<string, StoredRequest>(StringComparer.Ordinal);
    private readonly Queue<string> transientOrder = new Queue<string>();

    public ProtocolResult ExecuteOnce(PeerContext peer, CompanyProtocolEnvelope envelope, CompanyIntent intent, ICompanyIntentExecutor executor)
    {
        var fingerprint = CompanyProtocolCodec.Fingerprint(envelope);
        var transient = intent.Type == CompanyIntentType.ModuleOperation && string.Equals(intent.ModuleAction, "state.get", StringComparison.Ordinal);
        lock (gate)
        {
            if (requests.TryGetValue(envelope.RequestId, out var known) || transientRequests.TryGetValue(envelope.RequestId, out known))
            {
                if (!string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return Rejected(envelope.RequestId, "request-id-reused", "The request ID is already bound to a different request.");
                return Clone(known.Result);
            }

            ProtocolResult result;
            try
            {
                result = executor.Execute(peer, envelope, intent) ?? Rejected(envelope.RequestId, "executor-no-result", "The host executor returned no terminal result.");
            }
            catch (Exception ex)
            {
                result = Rejected(envelope.RequestId, "host-execution-failed", ex.GetType().Name);
            }

            result.RequestId = envelope.RequestId;
            if (result.Status != ProtocolResultStatus.Succeeded && result.Status != ProtocolResultStatus.Rejected)
                result = Rejected(envelope.RequestId, "non-terminal-result", "The host executor must return a terminal result.");
            if (CompanyProtocolCodec.Utf8Length(result.Code) > CompanyProtocolLimits.MaximumIdBytes ||
                CompanyProtocolCodec.Utf8Length(result.Detail) > CompanyProtocolLimits.MaximumDetailBytes ||
                (result.Payload?.Length ?? 0) > CompanyProtocolLimits.MaximumPayloadBytes - 1024)
                result = Rejected(envelope.RequestId, "result-too-large", "The host result exceeds protocol limits.");
            var stored = new StoredRequest(fingerprint, Clone(result));
            if (transient)
            {
                transientRequests.Add(envelope.RequestId, stored);
                transientOrder.Enqueue(envelope.RequestId);
                while (transientOrder.Count > MaximumTransientRequests)
                    transientRequests.Remove(transientOrder.Dequeue());
            }
            else requests.Add(envelope.RequestId, stored);
            return Clone(result);
        }
    }

    private static ProtocolResult Clone(ProtocolResult value) => new ProtocolResult
    {
        RequestId = value.RequestId,
        Status = value.Status,
        Code = value.Code,
        Detail = value.Detail,
        AuthoritativeVersion = value.AuthoritativeVersion,
        Payload = (value.Payload ?? Array.Empty<byte>()).ToArray()
    };

    internal static ProtocolResult Rejected(string requestId, string code, string detail) => new ProtocolResult
    {
        RequestId = requestId,
        Status = ProtocolResultStatus.Rejected,
        Code = code,
        Detail = detail
    };

    private sealed class StoredRequest
    {
        public StoredRequest(string fingerprint, ProtocolResult result) { Fingerprint = fingerprint; Result = result; }
        public string Fingerprint { get; }
        public ProtocolResult Result { get; }
    }
}

public sealed class CompanyProtocolHost
{
    private readonly ICompanyIntentExecutor executor;
    private readonly ProtocolRequestJournal journal;

    public CompanyProtocolHost(ICompanyIntentExecutor executor, ProtocolRequestJournal? journal = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.journal = journal ?? new ProtocolRequestJournal();
    }

    public ProtocolResult Receive(PeerContext peer, CompanyProtocolEnvelope envelope)
    {
        var refusal = ValidatePeerAndEnvelope(peer, envelope);
        if (refusal != null) return refusal;

        CompanyIntent intent;
        try { intent = CompanyIntentCodec.Decode(envelope.Payload, envelope.ProtocolVersion); }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is DecoderFallbackException)
        { return ProtocolRequestJournal.Rejected(envelope.RequestId, "invalid-payload", ex.Message); }

        var invalid = CompanyIntentValidator.Validate(intent);
        if (invalid != null) return ProtocolRequestJournal.Rejected(envelope.RequestId, invalid, "The intent failed bounded host validation.");
        return journal.ExecuteOnce(peer, envelope, intent, executor);
    }

    private static ProtocolResult? ValidatePeerAndEnvelope(PeerContext peer, CompanyProtocolEnvelope envelope)
    {
        if (peer == null || !peer.IsKnown) return ProtocolRequestJournal.Rejected(envelope?.RequestId ?? "", "unknown-peer", "The sender is not a current server peer.");
        if (!peer.IsReady) return ProtocolRequestJournal.Rejected(envelope?.RequestId ?? "", "peer-not-ready", "The sender has not completed the ready handshake.");
        var invalid = CompanyProtocolCodec.Validate(envelope, CompanyMessageType.IntentRequest);
        if (invalid != null) return ProtocolRequestJournal.Rejected(envelope?.RequestId ?? "", invalid, "The protocol envelope is invalid.");
        if (!string.Equals(peer.AuthenticatedPlayerId, envelope.PlayerId, StringComparison.Ordinal))
            return ProtocolRequestJournal.Rejected(envelope.RequestId, "identity-spoofing", "The claimed player does not match the authenticated peer.");
        return null;
    }
}

public static class CompanyProtocolCodec
{
    private static readonly byte[] Magic = { (byte)'D', (byte)'V', (byte)'C', (byte)'P' };

    public static byte[] Encode(CompanyProtocolEnvelope envelope)
    {
        var invalid = Validate(envelope, null);
        if (invalid != null) throw new InvalidDataException(invalid);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(envelope.ProtocolVersion);
            writer.Write((byte)envelope.MessageType);
            WriteString(writer, envelope.RequestId, CompanyProtocolLimits.MaximumIdBytes);
            WriteString(writer, envelope.PlayerId, CompanyProtocolLimits.MaximumIdBytes);
            WriteString(writer, envelope.CompanyId, CompanyProtocolLimits.MaximumIdBytes);
            writer.Write(envelope.ExpectedVersion);
            writer.Write(envelope.Payload.Length);
            writer.Write(envelope.Payload);
        }
        if (stream.Length > CompanyProtocolLimits.MaximumEnvelopeBytes) throw new InvalidDataException("envelope-too-large");
        return stream.ToArray();
    }

    public static CompanyProtocolEnvelope Decode(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > CompanyProtocolLimits.MaximumEnvelopeBytes) throw new InvalidDataException("envelope-size-invalid");
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(4).SequenceEqual(Magic)) throw new InvalidDataException("invalid-magic");
        var envelope = new CompanyProtocolEnvelope
        {
            ProtocolVersion = reader.ReadUInt16(),
            MessageType = (CompanyMessageType)reader.ReadByte(),
            RequestId = ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            PlayerId = ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            CompanyId = ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            ExpectedVersion = reader.ReadInt64()
        };
        var length = reader.ReadInt32();
        if (length < 0 || length > CompanyProtocolLimits.MaximumPayloadBytes || length > stream.Length - stream.Position) throw new InvalidDataException("payload-size-invalid");
        envelope.Payload = reader.ReadBytes(length);
        if (stream.Position != stream.Length) throw new InvalidDataException("trailing-envelope-data");
        var invalid = Validate(envelope, null);
        if (invalid != null) throw new InvalidDataException(invalid);
        return envelope;
    }

    public static string? Validate(CompanyProtocolEnvelope? envelope, CompanyMessageType? requiredType)
    {
        if (envelope == null) return "missing-envelope";
        if (envelope.ProtocolVersion < CompanyProtocolLimits.MinimumSupportedVersion || envelope.ProtocolVersion > CompanyProtocolLimits.CurrentVersion) return "unsupported-version";
        if (!Enum.IsDefined(typeof(CompanyMessageType), envelope.MessageType)) return "unknown-message-type";
        if (requiredType.HasValue && envelope.MessageType != requiredType.Value) return "unexpected-message-type";
        if (!ValidId(envelope.RequestId) || !ValidId(envelope.PlayerId)) return "invalid-identity";
        if (Utf8Length(envelope.CompanyId) > CompanyProtocolLimits.MaximumIdBytes) return "company-id-too-long";
        if (envelope.ExpectedVersion < 0) return "invalid-expected-version";
        if (envelope.Payload == null || envelope.Payload.Length > CompanyProtocolLimits.MaximumPayloadBytes) return "payload-too-large";
        return null;
    }

    public static string Fingerprint(CompanyProtocolEnvelope envelope) => Convert.ToBase64String(Encode(envelope));

    internal static void WriteString(BinaryWriter writer, string value, int limit)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length > limit) throw new InvalidDataException("string-too-long");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadString(BinaryReader reader, int limit)
    {
        var length = reader.ReadUInt16();
        if (length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("string-size-invalid");
        return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
    }

    internal static int Utf8Length(string? value) => Encoding.UTF8.GetByteCount(value ?? "");
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && Utf8Length(value) <= CompanyProtocolLimits.MaximumIdBytes;
}

public static class CompanyIntentCodec
{
    public static byte[] Encode(CompanyIntent intent, ushort protocolVersion = CompanyProtocolLimits.CurrentVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write((byte)intent.Type);
            CompanyProtocolCodec.WriteString(writer, intent.Name, CompanyProtocolLimits.MaximumNameBytes);
            CompanyProtocolCodec.WriteString(writer, intent.TargetPlayerId, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, intent.Permission, CompanyProtocolLimits.MaximumIdBytes);
            writer.Write(intent.Enabled);
            CompanyProtocolCodec.WriteString(writer, intent.SourceAccount, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, intent.DestinationAccount, CompanyProtocolLimits.MaximumIdBytes);
            writer.Write(intent.Amount);
            CompanyProtocolCodec.WriteString(writer, intent.OfferId, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, intent.AssetId, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, intent.Acquirer, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, intent.Payer, CompanyProtocolLimits.MaximumIdBytes);
            if (protocolVersion >= 2)
            {
                CompanyProtocolCodec.WriteString(writer, intent.MembershipRequestId, CompanyProtocolLimits.MaximumIdBytes);
                CompanyProtocolCodec.WriteString(writer, intent.MembershipPolicy, CompanyProtocolLimits.MaximumIdBytes);
                writer.Write(intent.DebtAmount);
                writer.Write(intent.PenaltyAmount);
            }
            if (protocolVersion >= 3)
            {
                CompanyProtocolCodec.WriteString(writer, intent.ModuleAction, CompanyProtocolLimits.MaximumIdBytes);
                CompanyProtocolCodec.WriteString(writer, intent.ModulePayloadJson, CompanyProtocolLimits.MaximumPayloadBytes - 1024);
            }
        }
        if (stream.Length > CompanyProtocolLimits.MaximumPayloadBytes) throw new InvalidDataException("payload-too-large");
        return stream.ToArray();
    }

    public static CompanyIntent Decode(byte[] data, ushort protocolVersion = CompanyProtocolLimits.CurrentVersion)
    {
        if (data == null || data.Length == 0 || data.Length > CompanyProtocolLimits.MaximumPayloadBytes) throw new InvalidDataException("payload-size-invalid");
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var intent = new CompanyIntent
        {
            Type = (CompanyIntentType)reader.ReadByte(),
            Name = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumNameBytes),
            TargetPlayerId = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Permission = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Enabled = reader.ReadBoolean(),
            SourceAccount = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            DestinationAccount = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Amount = reader.ReadDouble(),
            OfferId = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            AssetId = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Acquirer = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Payer = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes)
        };
        if (protocolVersion >= 2)
        {
            intent.MembershipRequestId = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes);
            intent.MembershipPolicy = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes);
            intent.DebtAmount = reader.ReadInt64();
            intent.PenaltyAmount = reader.ReadInt64();
        }
        if (protocolVersion >= 3)
        {
            intent.ModuleAction = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes);
            intent.ModulePayloadJson = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumPayloadBytes - 1024);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("trailing-payload-data");
        return intent;
    }
}

public static class CompanyIntentValidator
{
    public static string? Validate(CompanyIntent intent)
    {
        if (intent == null || !Enum.IsDefined(typeof(CompanyIntentType), intent.Type)) return "unknown-intent-type";
        if (!Finite(intent.Amount)) return "non-finite-value";
        switch (intent.Type)
        {
            case CompanyIntentType.CreateCompany:
                return Required(intent.Name, CompanyProtocolLimits.MaximumNameBytes, "company-name");
            case CompanyIntentType.ApplyToCompany:
                return null;
            case CompanyIntentType.InvitePlayer:
                return Required(intent.TargetPlayerId, CompanyProtocolLimits.MaximumIdBytes, "target-player-id");
            case CompanyIntentType.ChangePermission:
                return Required(intent.TargetPlayerId, CompanyProtocolLimits.MaximumIdBytes, "target-player-id") ?? Required(intent.Permission, CompanyProtocolLimits.MaximumIdBytes, "permission");
            case CompanyIntentType.TransferFunds:
                if (intent.Amount <= 0 || intent.Amount > CompanyProtocolLimits.MaximumTransferAmount || intent.Amount != Math.Truncate(intent.Amount)) return "invalid-transfer-amount";
                return Required(intent.SourceAccount, CompanyProtocolLimits.MaximumIdBytes, "source-account") ?? Required(intent.DestinationAccount, CompanyProtocolLimits.MaximumIdBytes, "destination-account");
            case CompanyIntentType.AcquireVehicle:
                return Required(intent.OfferId, CompanyProtocolLimits.MaximumIdBytes, "offer-id") ?? Required(intent.AssetId, CompanyProtocolLimits.MaximumIdBytes, "asset-id") ?? Required(intent.Acquirer, CompanyProtocolLimits.MaximumIdBytes, "acquirer") ?? Required(intent.Payer, CompanyProtocolLimits.MaximumIdBytes, "payer");
            case CompanyIntentType.DecideApplication:
            case CompanyIntentType.RespondToInvitation:
                return Required(intent.MembershipRequestId, CompanyProtocolLimits.MaximumIdBytes, "membership-request-id");
            case CompanyIntentType.ChangeMembershipPolicy:
                return Required(intent.MembershipPolicy, CompanyProtocolLimits.MaximumIdBytes, "membership-policy");
            case CompanyIntentType.LeaveCompany:
                return null;
            case CompanyIntentType.TransferLeadership:
                return Required(intent.TargetPlayerId, CompanyProtocolLimits.MaximumIdBytes, "target-player-id");
            case CompanyIntentType.DissolveCompany:
                return intent.DebtAmount < 0 || intent.PenaltyAmount < 0 ? "invalid-liquidation-liability" : null;
            case CompanyIntentType.ModuleOperation:
                return Required(intent.ModuleAction, CompanyProtocolLimits.MaximumIdBytes, "module-action")
                    ?? Required(intent.ModulePayloadJson, CompanyProtocolLimits.MaximumPayloadBytes - 1024, "module-payload");
            default:
                return "unknown-intent-type";
        }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static string? Required(string? value, int maxBytes, string field) => string.IsNullOrWhiteSpace(value) || CompanyProtocolCodec.Utf8Length(value) > maxBytes ? "invalid-" + field : null;
}

public sealed class ClientRequestTracker
{
    private const int MaximumTrackedRequests = 256;
    private readonly object gate = new object();
    private readonly TimeSpan retryAfter;
    private readonly TimeSpan timeoutAfter;
    private readonly Dictionary<string, PendingRequest> pending = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
    private readonly Queue<string> submissionOrder = new Queue<string>();

    public ClientRequestTracker(TimeSpan retryAfter, TimeSpan timeoutAfter)
    {
        if (retryAfter <= TimeSpan.Zero || timeoutAfter <= retryAfter) throw new ArgumentOutOfRangeException(nameof(timeoutAfter));
        this.retryAfter = retryAfter;
        this.timeoutAfter = timeoutAfter;
    }

    public void Track(CompanyProtocolEnvelope envelope, DateTimeOffset now)
    {
        lock (gate)
        {
            if (pending.ContainsKey(envelope.RequestId))
            {
                var known = pending[envelope.RequestId];
                if (!string.Equals(CompanyProtocolCodec.Fingerprint(known.Envelope), CompanyProtocolCodec.Fingerprint(envelope), StringComparison.Ordinal))
                    throw new InvalidOperationException("client-request-id-reused");
                return;
            }

            while (pending.Count >= MaximumTrackedRequests && submissionOrder.Count > 0)
            {
                var oldest = submissionOrder.Peek();
                if (!pending.TryGetValue(oldest, out var candidate)) { submissionOrder.Dequeue(); continue; }
                if (candidate.Result == null) break;
                submissionOrder.Dequeue();
                pending.Remove(oldest);
            }
            if (pending.Count >= MaximumTrackedRequests)
                throw new InvalidOperationException("client-request-capacity-exhausted");
            pending.Add(envelope.RequestId, new PendingRequest(envelope, now));
            submissionOrder.Enqueue(envelope.RequestId);
        }
    }

    public IReadOnlyList<CompanyProtocolEnvelope> DueRetries(DateTimeOffset now)
    {
        lock (gate)
        {
            var due = new List<CompanyProtocolEnvelope>();
            foreach (var item in pending.Values)
            {
                if (item.Result != null || now - item.FirstSent >= timeoutAfter) continue;
                if (now - item.LastSent >= retryAfter) { item.LastSent = now; due.Add(item.Envelope); }
            }
            return due;
        }
    }

    public ProtocolResult ResultOrTimeout(string requestId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(requestId, out var item)) return ProtocolRequestJournal.Rejected(requestId, "unknown-request", "The client does not track this request.");
            if (item.Result != null) return item.Result;
            if (now - item.FirstSent < timeoutAfter) return new ProtocolResult { RequestId = requestId, Status = ProtocolResultStatus.TimedOut, Code = "pending" };
            return new ProtocolResult { RequestId = requestId, Status = ProtocolResultStatus.TimedOut, Code = "request-timeout" };
        }
    }

    public bool Accept(ProtocolResult result)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(result.RequestId, out var item)) return false;
            if (result.Status != ProtocolResultStatus.Succeeded && result.Status != ProtocolResultStatus.Rejected) return false;
            item.Result = result;
            return true;
        }
    }

    public IReadOnlyList<CompanyProtocolEnvelope> RequestsToResumeAfterReconnect()
    { lock (gate) return pending.Values.Where(x => x.Result == null).Select(x => x.Envelope).ToList(); }

    private sealed class PendingRequest
    {
        public PendingRequest(CompanyProtocolEnvelope envelope, DateTimeOffset now) { Envelope = envelope; FirstSent = now; LastSent = now; }
        public CompanyProtocolEnvelope Envelope { get; }
        public DateTimeOffset FirstSent { get; }
        public DateTimeOffset LastSent { get; set; }
        public ProtocolResult? Result { get; set; }
    }
}
