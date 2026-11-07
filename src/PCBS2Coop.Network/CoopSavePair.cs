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
    /// <summary>Up to 0.20: the world state is stored as written.</summary>
    private const ulong Magic = 0x3152494150324350; // PC2PAIR1
    /// <summary>From 0.21: the world state is compressed (<see cref="Protocol.PackState"/>).</summary>
    private const ulong PackedMagic = 0x3252494150324350; // PC2PAIR2
    private const ulong BundleMagic = 0x31454C444E423250;
    private const int MaxMetadataBytes = Protocol.MaxPackedStateBytes + 2 * MaxCarrierBytes + 4096;
    /// <summary>Earlier versions whose co-op saves this build reads: 0.18's BIOS changes live in the native PC record,
    /// and 0.19's software transfer state is optional. 0.17 changed edit progress and host save timing only; 0.20 added
    /// simulation messages (nothing saved). 0.21 compresses the state (both layouts are read); 0.22 adds tool-table and
    /// thermal paste messages (nothing saved); 0.23 adds case-modding, lighting and purchase messages (nothing new saved). 0.24
    /// adds the optional workshop state (lights, seasonal decoration, shared pictures): older saves load without it.
    /// 0.25 fixes admission snapshot timing, 0.26 shares the walking power shortcut, 0.27 fixes cold co-op boot and
    /// 0.28 restores the Windows wallpaper preview when its registry path is stale, 0.29 waits for local cable replays,
    /// 0.30 resolves external cable endpoints and discards unaccepted pipe routes, 0.31 keeps active orientation edits alive,
    /// 0.32 reports recovery after an interrupted native write, 0.33 lets sync recover failed assembly rebinds,
    /// 0.34 corrects the work-mode player pose and guards connected panel removal, 0.35 keeps refits cable-free,
    /// 0.36 resolves the case prefab behind a catalog color variant, 0.37 matches its remote carrier defaults,
    /// 0.38 keeps local cupboard files separate until pickup is accepted, and 0.39 closes a refused Decorator after its window opens.
    /// 0.40 refreshes workshop decorations and replays desktop icon clicks during work mode. 0.41 keeps Lighting's HUD local.
    /// 0.42 restores peripheral lighting from the native save once the peripheral has loaded.
    /// 0.43 sends the first effect clock after the guest enters the world.
    /// 0.44 shares the walking power shortcut when aiming at the monitor.
    /// 0.45 shares the native startup heat failure.
    /// 0.46 shares a matching startup and simulation failure once per boot.
    /// 0.47 removes a piped cooler as a whole when the editor selects its proxy.
    /// 0.48 normalizes generated motherboard standoff bindings on load.
    /// The save layout is the same across those builds.</summary>
    private static readonly string[] SameFormat = { "0.48", "0.47", "0.46", "0.45", "0.44", "0.43", "0.42", "0.41", "0.40", "0.39", "0.38", "0.37", "0.36", "0.35", "0.34", "0.33", "0.32", "0.31", "0.30", "0.29", "0.28", "0.27", "0.26", "0.25", "0.24", "0.23", "0.22", "0.21", "0.20", "0.19", "0.18", "0.17", "0.16" };

    /// <summary>Whether this build loads a co-op save written by mod version <paramref name="version"/>.</summary>
    public static bool Readable(string version) => version == ModVersion.Value || SameFormat.Contains(version);

    /// <summary>Why a save from mod version <paramref name="version"/> can't be loaded, or null if it can.</summary>
    public static string? VersionProblem(string version)
    {
        if (Readable(version)) return null;
        var oldest = SameFormat.Length > 0 ? SameFormat[^1] : ModVersion.Value;
        return Compare(version, ModVersion.Value) > 0
            ? $"This co-op world was saved by a newer version of the mod ({Shown(version)}). Update the mod to {Shown(version)} or later to load it."
            : $"This co-op world was saved by mod version {Shown(version)}. This version ({ModVersion.Value}) loads co-op worlds from {oldest} onwards only.";
    }

    private static string Shown(string version) => version.Length is > 0 and <= 16 && version.All(c => char.IsDigit(c) || c == '.') ? version : "unknown";

    /// <summary>Orders "major.minor" versions; anything unreadable counts as older.</summary>
    private static int Compare(string a, string b)
    {
        static (int, int) Parse(string v)
        {
            var parts = v.Split('.');
            return parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) ? (major, minor) : (-1, -1);
        }
        return Parse(a).CompareTo(Parse(b));
    }

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
        var w = new NetDataWriter(); w.Put(PackedMagic); w.Put(ModVersion.Value);
        w.Put(metadata.NativeLength); w.Put(metadata.NativeHash);
        w.Put(metadata.Position.Generation); w.Put(metadata.Position.Revision); w.Put(metadata.Position.ChainHash);
        Protocol.WriteBlob(w, Protocol.PackState(metadata.Position.State));
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
            var magic = r.GetULong();
            // Every layout starts "PC2PAIR" + a digit, then the writer's version: name the version before the layout.
            if ((magic & 0x00FF_FFFF_FFFF_FFFFUL) != (Magic & 0x00FF_FFFF_FFFF_FFFFUL)) throw new InvalidDataException("This file isn't a co-op save.");
            var version = r.GetString(128);
            // Older object/activity codecs are incompatible and are refused (owner decision, phase 8): never implicit
            // ID minting.
            if (VersionProblem(version) is { } problem) throw new InvalidDataException(problem);
            if (magic != Magic && magic != PackedMagic) throw new InvalidDataException("Unsupported co-op save layout.");
            var size = r.GetInt(); var hash = new byte[32]; r.GetBytes(hash, hash.Length);
            var generation = r.GetUInt(); var revision = r.GetULong(); var chain = r.GetULong();
            var stored = Protocol.ReadBlob(r, Protocol.MaxPackedStateBytes, out _) ?? throw new InvalidDataException("Invalid world state length.");
            var state = magic == PackedMagic ? Protocol.UnpackState(stored) : stored;
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
