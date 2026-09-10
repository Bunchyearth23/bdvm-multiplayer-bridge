using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BDVM.Domain;

public sealed class AuthoritativeStatePage
{
    public string SnapshotToken { get; set; } = "";
    public int Offset { get; set; }
    public int TotalLength { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsFinal => checked(Offset + Data.Length) == TotalLength;
}

public static class AuthoritativeStatePageCodec
{
    private static readonly byte[] Magic = { (byte)'B', (byte)'D', (byte)'S', (byte)'P' };
    private const byte Version = 1;

    public static byte[] Encode(AuthoritativeStatePage page)
    {
        Validate(page);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            CompanyProtocolCodec.WriteString(writer, page.SnapshotToken, CompanyProtocolLimits.MaximumIdBytes);
            writer.Write(page.Offset);
            writer.Write(page.TotalLength);
            writer.Write(page.Data.Length);
            writer.Write(page.Data);
        }
        if (stream.Length > CompanyProtocolLimits.MaximumPayloadBytes - 1024) throw new InvalidDataException("state-page-too-large");
        return stream.ToArray();
    }

    public static AuthoritativeStatePage Decode(byte[] payload)
    {
        if (payload == null || payload.Length == 0 || payload.Length > CompanyProtocolLimits.MaximumPayloadBytes - 1024)
            throw new InvalidDataException("state-page-size-invalid");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("state-page-magic-invalid");
        if (reader.ReadByte() != Version) throw new InvalidDataException("state-page-version-unsupported");
        var page = new AuthoritativeStatePage
        {
            SnapshotToken = CompanyProtocolCodec.ReadString(reader, CompanyProtocolLimits.MaximumIdBytes),
            Offset = reader.ReadInt32(),
            TotalLength = reader.ReadInt32()
        };
        var length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("state-page-data-size-invalid");
        page.Data = reader.ReadBytes(length);
        if (stream.Position != stream.Length) throw new InvalidDataException("state-page-trailing-data");
        Validate(page);
        return page;
    }

    private static void Validate(AuthoritativeStatePage page)
    {
        if (page == null || string.IsNullOrWhiteSpace(page.SnapshotToken) || Encoding.UTF8.GetByteCount(page.SnapshotToken) > CompanyProtocolLimits.MaximumIdBytes)
            throw new InvalidDataException("state-page-token-invalid");
        if (page.Offset < 0 || page.TotalLength <= 0 || page.TotalLength > AuthoritativeStateSnapshotPager.MaximumSnapshotBytes || page.Offset > page.TotalLength || page.Data == null || page.Data.Length == 0 || page.Data.Length > AuthoritativeStateSnapshotPager.MaximumPageDataBytes)
            throw new InvalidDataException("state-page-range-invalid");
        if ((long)page.Offset + page.Data.Length > page.TotalLength) throw new InvalidDataException("state-page-range-invalid");
    }
}

public sealed class AuthoritativeStateSnapshotPager
{
    public const int MaximumPageDataBytes = 6 * 1024;
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    private readonly object gate = new object();
    private readonly Dictionary<string, Snapshot> snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
    private readonly int maximumSnapshots;
    private readonly TimeSpan lifetime;

    public AuthoritativeStateSnapshotPager(int maximumSnapshots = 16, TimeSpan? lifetime = null)
    {
        if (maximumSnapshots <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSnapshots));
        this.maximumSnapshots = maximumSnapshots;
        this.lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        if (this.lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    public AuthoritativeStatePage Read(string actorId, string? token, int offset, Func<byte[]> snapshotFactory, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new UnauthorizedAccessException("An authenticated actor is required for state transfer.");
        if (snapshotFactory == null) throw new ArgumentNullException(nameof(snapshotFactory));
        lock (gate)
        {
            Expire(now);
            Snapshot snapshot;
            if (string.IsNullOrWhiteSpace(token))
            {
                if (offset != 0) throw new InvalidOperationException("A new state transfer must start at offset zero.");
                var data = snapshotFactory() ?? throw new InvalidOperationException("The state snapshot factory returned no data.");
                if (data.Length == 0 || data.Length > MaximumSnapshotBytes) throw new InvalidOperationException("The authoritative state snapshot is outside transfer limits.");
                while (snapshots.Count >= maximumSnapshots)
                {
                    var oldest = snapshots.OrderBy(value => value.Value.LastAccess).ThenBy(value => value.Key, StringComparer.Ordinal).First();
                    snapshots.Remove(oldest.Key);
                }
                token = Guid.NewGuid().ToString("N");
                snapshot = new Snapshot(actorId, data.ToArray(), now);
                snapshots.Add(token, snapshot);
            }
            else
            {
                var existingToken = token!;
                if (Encoding.UTF8.GetByteCount(existingToken) > CompanyProtocolLimits.MaximumIdBytes) throw new InvalidOperationException("The state transfer token is invalid.");
                if (!snapshots.TryGetValue(existingToken, out snapshot!)) throw new InvalidOperationException("The state transfer token is unknown or expired.");
                if (!string.Equals(snapshot.ActorId, actorId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The state transfer token belongs to another actor.");
                snapshot.LastAccess = now;
            }
            if (offset < 0 || offset >= snapshot.Data.Length) throw new InvalidOperationException("The state transfer offset is invalid.");
            var length = Math.Min(MaximumPageDataBytes, snapshot.Data.Length - offset);
            return new AuthoritativeStatePage { SnapshotToken = token!, Offset = offset, TotalLength = snapshot.Data.Length, Data = snapshot.Data.Skip(offset).Take(length).ToArray() };
        }
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var token in snapshots.Where(value => now - value.Value.LastAccess > lifetime).Select(value => value.Key).ToArray()) snapshots.Remove(token);
    }

    private sealed class Snapshot
    {
        public Snapshot(string actorId, byte[] data, DateTimeOffset lastAccess) { ActorId = actorId; Data = data; LastAccess = lastAccess; }
        public string ActorId { get; }
        public byte[] Data { get; }
        public DateTimeOffset LastAccess { get; set; }
    }
}

public sealed class AuthoritativeStateSnapshotAssembler
{
    private readonly object gate = new object();
    private string? token;
    private int totalLength;
    private readonly SortedDictionary<int, byte[]> pages = new SortedDictionary<int, byte[]>();

    public int NextOffset { get { lock (gate) return pages.Sum(value => value.Value.Length); } }
    public string? SnapshotToken { get { lock (gate) return token; } }

    public byte[]? Accept(AuthoritativeStatePage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        AuthoritativeStatePageCodec.Encode(page);
        lock (gate)
        {
            if (token == null) { token = page.SnapshotToken; totalLength = page.TotalLength; }
            if (!string.Equals(token, page.SnapshotToken, StringComparison.Ordinal) || totalLength != page.TotalLength) throw new InvalidDataException("state-page-snapshot-conflict");
            var expected = pages.Sum(value => value.Value.Length);
            if (page.Offset < expected)
            {
                if (!pages.TryGetValue(page.Offset, out var prior) || !prior.SequenceEqual(page.Data)) throw new InvalidDataException("state-page-replay-conflict");
            }
            else
            {
                if (page.Offset != expected) throw new InvalidDataException("state-page-gap");
                pages.Add(page.Offset, page.Data.ToArray());
            }
            if (pages.Sum(value => value.Value.Length) != totalLength) return null;
            var completed = pages.SelectMany(value => value.Value).ToArray();
            ResetInternal();
            return completed;
        }
    }

    public void Reset() { lock (gate) ResetInternal(); }
    private void ResetInternal() { token = null; totalLength = 0; pages.Clear(); }
}
