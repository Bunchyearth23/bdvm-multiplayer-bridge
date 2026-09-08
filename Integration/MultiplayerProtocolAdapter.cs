using System;
using System.IO;
using BDVM.Domain;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;

namespace BDVM.Adapters;

public sealed class BDVMSerializablePacket : ISerializablePacket
{
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public void Serialize(BinaryWriter writer)
    {
        if (Data == null || Data.Length > CompanyProtocolLimits.MaximumEnvelopeBytes) throw new InvalidDataException("envelope-size-invalid");
        writer.Write(Data.Length);
        writer.Write(Data);
    }

    public void Deserialize(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > CompanyProtocolLimits.MaximumEnvelopeBytes) throw new InvalidDataException("envelope-size-invalid");
        Data = reader.ReadBytes(length);
        if (Data.Length != length) throw new EndOfStreamException();
    }
}

public interface IMultiplayerPeerIdentityResolver
{
    bool TryResolve(IPlayer peer, out string durablePlayerId);
}

public sealed class MultiplayerServerProtocolAdapter
{
    private readonly IServer server;
    private readonly IMultiplayerPeerIdentityResolver identities;
    private readonly CompanyProtocolHost host;
    private readonly Action<string>? log;

    public MultiplayerServerProtocolAdapter(IServer server, IMultiplayerPeerIdentityResolver identities, CompanyProtocolHost host, Action<string>? log = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.log = log;
    }

    // Matches the proven ServerPacketHandler<T> signature.
    public void Receive(BDVMSerializablePacket packet, IPlayer sender)
    {
        var durableId = "";
        var known = sender != null && server.GetPlayer(sender.PlayerId) == sender && identities.TryResolve(sender, out durableId);
        var peer = new PeerContext
        {
            PeerKey = sender == null ? "" : sender.PlayerId.ToString(),
            AuthenticatedPlayerId = known ? durableId : "",
            IsKnown = known,
            IsReady = known && sender != null && sender.IsLoaded
        };
        CompanyProtocolEnvelope envelope;
        ProtocolResult result;
        try
        {
            envelope = CompanyProtocolCodec.Decode(packet?.Data ?? Array.Empty<byte>());
            result = host.Receive(peer, envelope);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is System.Text.DecoderFallbackException)
        {
            result = ProtocolRequestJournal.Rejected("invalid-request", "codec-refused", ex.Message);
        }
        log?.Invoke("[correlation=" + result.RequestId + "] [event=multiplayer-intent-result] peer=" + peer.PeerKey + ", player=" + peer.AuthenticatedPlayerId + ", status=" + result.Status + ", code=" + result.Code + ", detail=" + result.Detail);
        if (known) server.SendSerializablePacketToPlayer(ToPacket(result, durableId), sender, true);
    }

    private static BDVMSerializablePacket ToPacket(ProtocolResult result, string playerId)
    {
        var payload = EncodeResult(result);
        return new BDVMSerializablePacket { Data = CompanyProtocolCodec.Encode(new CompanyProtocolEnvelope
        {
            MessageType = CompanyMessageType.IntentResult,
            RequestId = string.IsNullOrWhiteSpace(result.RequestId) ? "invalid-request" : result.RequestId,
            PlayerId = playerId,
            ExpectedVersion = Math.Max(0, result.AuthoritativeVersion),
            Payload = payload
        }) };
    }

    internal static byte[] EncodeResult(ProtocolResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)result.Status);
            CompanyProtocolCodec.WriteString(writer, result.Code, CompanyProtocolLimits.MaximumIdBytes);
            CompanyProtocolCodec.WriteString(writer, result.Detail, CompanyProtocolLimits.MaximumDetailBytes);
            writer.Write(result.Payload?.Length ?? 0);
            if (result.Payload != null) writer.Write(result.Payload);
        }
        if (stream.Length > CompanyProtocolLimits.MaximumPayloadBytes) throw new InvalidDataException("result-too-large");
        return stream.ToArray();
    }
}

public sealed class MultiplayerClientProtocolAdapter
{
    private readonly IClient client;
    public MultiplayerClientProtocolAdapter(IClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));
    public void SendIntent(CompanyProtocolEnvelope envelope) => client.SendSerializablePacketToServer(new BDVMSerializablePacket { Data = CompanyProtocolCodec.Encode(envelope) }, true);

    public ProtocolResult Receive(BDVMSerializablePacket packet)
    {
        var envelope = CompanyProtocolCodec.Decode(packet?.Data ?? Array.Empty<byte>());
        var invalid = CompanyProtocolCodec.Validate(envelope, CompanyMessageType.IntentResult);
        if (invalid != null) throw new InvalidDataException(invalid);
        using var stream = new MemoryStream(envelope.Payload, false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var result = new ProtocolResult
        {
            RequestId = envelope.RequestId,
            Status = (ProtocolResultStatus)reader.ReadByte(),
            Code = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Detail = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumDetailBytes),
            AuthoritativeVersion = envelope.ExpectedVersion
        };
        var length = reader.ReadInt32();
        if (length < 0 || length > CompanyProtocolLimits.MaximumPayloadBytes || length > stream.Length - stream.Position)
            throw new InvalidDataException("result-payload-size-invalid");
        result.Payload = reader.ReadBytes(length);
        if (stream.Position != stream.Length) throw new InvalidDataException("trailing-result-data");
        if (!Enum.IsDefined(typeof(ProtocolResultStatus), result.Status)) throw new InvalidDataException("invalid-result-status");
        return result;
    }
}
