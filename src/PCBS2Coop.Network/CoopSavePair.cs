using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Full native PC graph for a carrier that the native world's single carry field cannot represent.
/// Only the host constructs these bytes; commands from guests must never contain native serialized objects.</summary>
public sealed record CarrierPayload(NetObjectId Pc, byte[] NativePc);
public sealed record SaveMetadata(int NativeLength, byte[] NativeHash, SnapshotPosition Position, IReadOnlyList<CarrierPayload> Carriers);

/// <summary>A native save and its exact mod state. Disk uses a SHA-256-bound sidecar; transfer bundles both.
/// A crash between the game's save and sidecar publication leaves a detectable mismatch, never silently minted IDs.</summary>
public static class CoopSavePair
{
    public const string Extension = ".coop";
    public const int MaxCarrierBytes = 4 * 1024 * 1024;
    private const ulong Magic = 0x3152494150324350; // PC2PAIR1
    private const ulong BundleMagic = 0x31454C444E423250;
    private const int MaxMetadataBytes = Protocol.MaxModStateBytes + 2 * MaxCarrierBytes + 4096;
    private static readonly string[] CompatibleOlder = { "0.8", "0.9", "0.10" };

    public static SaveMetadata Create(byte[] native, SnapshotPosition position, IReadOnlyList<CarrierPayload> carriers)
    {
        var metadata = new SaveMetadata(native.Length, SHA256.HashData(native), position, carriers);
        Validate(metadata); return metadata;
    }
    public static void Verify(byte[] native, SaveMetadata metadata)
    {
        Validate(metadata);
        if (native.Length != metadata.NativeLength || !SHA256.HashData(native).AsSpan().SequenceEqual(metadata.NativeHash))
            throw new InvalidDataException("The game save and co-op identity state do not match.");
    }
    public static byte[] Encode(SaveMetadata metadata)
    {
        Validate(metadata);
        var w = new NetDataWriter(); w.Put(Magic); w.Put(ModVersion.Value);
        w.Put(metadata.NativeLength); w.Put(metadata.NativeHash);
        w.Put(metadata.Position.Generation); w.Put(metadata.Position.Revision); w.Put(metadata.Position.ChainHash);
        Protocol.WriteBlob(w, metadata.Position.State);
        w.Put(metadata.Carriers.Count);
        foreach (var carrier in metadata.Carriers.OrderBy(x => x.Pc.Value)) { carrier.Pc.Write(w); Protocol.WriteBlob(w, carrier.NativePc); }
        return w.CopyData();
    }
    public static SaveMetadata Decode(byte[] data)
    {
        if (data.Length > MaxMetadataBytes) throw new InvalidDataException("Co-op identity state exceeds limits.");
        try
        {
            var r = new NetDataReader(data);
            if (r.GetULong() != Magic) throw new InvalidDataException("Unsupported co-op save format.");
            var version = r.GetString(128);
            // 0.9 added a transport only. 0.10 adds an optional workstation state, which the host recaptures from the
            // loaded world when an older save lacks it. 0.11 changed only session messages (chat, /sync). The 0.8
            // world/identity/carrier layout is otherwise unchanged.
            // These are specific compatible readers, not permission to load other historical formats.
            if (version != ModVersion.Value && !CompatibleOlder.Contains(version)) throw new InvalidDataException("Unsupported co-op save version.");
            var size = r.GetInt(); var hash = new byte[32]; r.GetBytes(hash, hash.Length);
            var generation = r.GetUInt(); var revision = r.GetULong(); var chain = r.GetULong();
            var state = Protocol.ReadBlob(r, Protocol.MaxModStateBytes, out _) ?? throw new InvalidDataException("Invalid world state length.");
            var n = r.GetInt();
            if (n is < 0 or > 2) throw new InvalidDataException("Invalid carrier count.");
            var carriers = new CarrierPayload[n];
            for (var i = 0; i < n; i++) carriers[i] = new(NetObjectId.Read(r),
                Protocol.ReadBlob(r, MaxCarrierBytes, out _) ?? throw new InvalidDataException("Invalid carried PC payload length."));
            if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing co-op save bytes.");
            var metadata = new SaveMetadata(size, hash, new(generation, revision, chain, state), Array.AsReadOnly(carriers));
            Validate(metadata); return metadata;
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new InvalidDataException("Malformed co-op save state.", e); }
    }
    public static void WriteSidecar(string nativePath, byte[] native, SaveMetadata metadata)
    {
        Verify(native, metadata);
        var path = nativePath + Extension;
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encode(metadata); stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static SaveMetadata ReadSidecar(string nativePath)
    {
        var path = nativePath + Extension;
        if (!File.Exists(path)) throw new InvalidDataException("The co-op save is missing its identity state (.coop). Start a new world or restore its matching pair.");
        if (new FileInfo(path).Length > MaxMetadataBytes) throw new InvalidDataException("Co-op state file exceeds limits.");
        var metadata = Decode(File.ReadAllBytes(path));
        if (new FileInfo(nativePath).Length > Protocol.MaxSnapshotBytes) throw new InvalidDataException("Game save exceeds limits.");
        Verify(File.ReadAllBytes(nativePath), metadata); return metadata;
    }
    public static byte[] Pack(byte[] native, SaveMetadata metadata)
    {
        Verify(native, metadata);
        var state = Encode(metadata);
        if ((long)native.Length + state.Length + 16 > Protocol.MaxSnapshotBytes) throw new InvalidDataException("Paired save exceeds the snapshot limit.");
        var w = new NetDataWriter(); w.Put(BundleMagic); Protocol.WriteBlob(w, native); Protocol.WriteBlob(w, state); return w.CopyData();
    }
    public static (byte[] Native, SaveMetadata Metadata) Unpack(byte[] bundle)
    {
        try
        {
            if (bundle.Length > Protocol.MaxSnapshotBytes) throw new InvalidDataException("Paired save exceeds limits.");
            var r = new NetDataReader(bundle);
            if (r.GetULong() != BundleMagic) throw new InvalidDataException("Snapshot is missing its paired co-op state.");
            var native = Protocol.ReadBlob(r, Protocol.MaxSnapshotBytes, out _) ?? throw new InvalidDataException("Invalid game save size.");
            var metadata = Decode(Protocol.ReadBlob(r, MaxMetadataBytes, out _) ?? throw new InvalidDataException("Invalid co-op state size."));
            if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing paired save bytes.");
            Verify(native, metadata); return (native, metadata);
        }
        catch (Exception e) when (Protocol.IsReadError(e)) { throw new InvalidDataException("Malformed paired snapshot.", e); }
    }
    private static void Validate(SaveMetadata m)
    {
        if (m.NativeLength is < 1 or > Protocol.MaxSnapshotBytes || m.NativeHash.Length != 32 || m.Position.Generation == 0 ||
            m.Position.State.Length is < 1 or > Protocol.MaxModStateBytes || m.Carriers.Count > 2)
            throw new InvalidDataException("Invalid paired save metadata.");
        var ids = new HashSet<NetObjectId>();
        foreach (var c in m.Carriers)
            if (c.Pc.IsNone || !ids.Add(c.Pc) || c.NativePc.Length is < 1 or > MaxCarrierBytes)
                throw new InvalidDataException("Invalid/duplicate carried PC payload.");
    }
}
