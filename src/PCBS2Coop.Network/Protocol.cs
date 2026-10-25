using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum Role : byte { None = 0, Host = 1, Client = 2 }

/// <summary>First byte of every application message.</summary>
public enum MessageType : byte
{
    /// <summary>Position and facing of the sender's player, sent unreliably at a fixed rate (sequenced).</summary>
    PlayerState = 1,
    /// <summary>Host → guest after a compatible join: host identity, warnings and the current world position. Reliable ordered.</summary>
    Welcome = 2,
    /// <summary>Host → guest: a new world generation started. Reliable ordered.</summary>
    WorldReset = 3,
    /// <summary>Guest → host: a request for a world change. Reliable ordered.</summary>
    Command = 4,
    /// <summary>Host → guest: an accepted world change, in revision order. Reliable ordered.</summary>
    Mutation = 5,
    /// <summary>Host → guest: a refused command, or the original outcome of a duplicate. Reliable ordered.</summary>
    CommandResult = 6,
    /// <summary>
    /// Host → guest: a world snapshot starts here. For a load snapshot it carries the world position (generation,
    /// revision, change hash) and mod state at the moment the game save was taken; world changes after this message
    /// come after the snapshot. Reliable ordered.
    /// </summary>
    SnapshotMark = 7,
    /// <summary>Host → guest: the snapshot's file name, size and SHA-256. Chunks follow. Reliable ordered.</summary>
    SnapshotFile = 8,
    /// <summary>Host → guest: one piece of the snapshot file, in order. Reliable ordered.</summary>
    SnapshotChunk = 9,
    /// <summary>Host → guest: the snapshot can't be completed (for example the save failed). Reliable ordered.</summary>
    SnapshotAbort = 10,
    /// <summary>Guest → host: loading, in the world, or failed to load. Reliable ordered.</summary>
    GuestStatus = 11,
}

/// <summary>Why a snapshot is sent.</summary>
public enum SnapshotPurpose : byte
{
    /// <summary>The guest loads it and enters the world.</summary>
    Load = 1,
    /// <summary>The guest only stores it (its copy of the co-op world after a host save).</summary>
    Copy = 2,
}

public enum GuestState : byte
{
    /// <summary>Connected; no world loaded yet.</summary>
    Connected = 0,
    /// <summary>Received a snapshot and is loading it.</summary>
    Loading = 1,
    /// <summary>Loaded the host's world and applying its changes.</summary>
    InWorld = 2,
    /// <summary>Could not receive or load the world.</summary>
    Failed = 3,
}

/// <summary>Command type numbers. 1–99 are reserved for the mod's own diagnostics/system commands.</summary>
public static class CommandTypes
{
    /// <summary>A harmless shared marker used by tests to exercise ordering and duplicate handling end to end.</summary>
    public const ushort DiagnosticMarker = 1;
}

/// <summary>World-space snapshot of one player. Position is the feet (bottom of the character controller).</summary>
public readonly record struct PlayerState(float X, float Y, float Z, float Yaw, bool InWorkshop)
{
    /// <summary>Positions outside this range are treated as corrupt. The workshop is tens of metres across.</summary>
    public const float MaxCoordinate = 10_000f;

    public void Write(NetDataWriter w)
    {
        w.Put((byte)MessageType.PlayerState);
        w.Put(X); w.Put(Y); w.Put(Z); w.Put(Yaw); w.Put(InWorkshop);
    }

    public static PlayerState Read(NetDataReader r) =>
        new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetBool());

    public bool IsValid =>
        Ok(X) && Ok(Y) && Ok(Z) && float.IsFinite(Yaw) && MathF.Abs(Yaw) <= 100_000f;

    private static bool Ok(float v) => float.IsFinite(v) && MathF.Abs(v) <= MaxCoordinate;
}

/// <summary>Payload of <see cref="CommandTypes.DiagnosticMarker"/>.</summary>
public readonly record struct DiagnosticMarker(string Text)
{
    public const int MaxLength = 64;

    public byte[] ToBytes()
    {
        var w = new NetDataWriter();
        w.Put(Text ?? "", MaxLength + 1);
        return w.CopyData();
    }

    public static DiagnosticMarker Read(NetDataReader r) => new(r.GetString(MaxLength + 1));

    public bool IsValid => !string.IsNullOrEmpty(Text) && Text.Length <= MaxLength && Text.All(c => !char.IsControl(c));
}

public static class Protocol
{
    public const int DefaultPort = 7777;

    /// <summary>Largest command/mutation payload. Larger data goes through snapshots (chunked).</summary>
    public const int MaxPayloadBytes = 16 * 1024;
    /// <summary>Largest snapshot file. The game's Free Build saves are a few MB.</summary>
    public const int MaxSnapshotBytes = 64 * 1024 * 1024;
    /// <summary>Size of one snapshot chunk.</summary>
    public const int SnapshotChunkBytes = 32 * 1024;
    /// <summary>Largest mod-side world state carried with a load snapshot.</summary>
    public const int MaxModStateBytes = 1024 * 1024;
    public const int MaxShortString = 256;
    public const int MaxDlcEntries = 32;
    public const int MaxWarnings = 16;

    /// <summary>
    /// Connection key: the first string of a connect request. It carries the mod version, which is the only version
    /// (message layouts change only with it). Mismatched mod versions are refused with a readable reason.
    /// </summary>
    public static string ConnectionKey => KeyPrefix + ModVersion.Value;
    public const string KeyPrefix = "PCBS2Coop/";

    internal static string ReadString(NetDataReader r) => r.GetString(MaxShortString);

    /// <summary>
    /// Snapshot file names the guest accepts: the game's save file names, e.g. "coopworld.binary.zip". No paths.
    /// </summary>
    public static bool IsValidSnapshotName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string stem;
        if (name.EndsWith(".binary.zip", StringComparison.Ordinal)) stem = name[..^".binary.zip".Length];
        else if (name.EndsWith(".binary", StringComparison.Ordinal)) stem = name[..^".binary".Length];
        else return false;
        return stem.Length is >= 1 and <= 48 && stem.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');
    }

    /// <summary>Writes an int length followed by the raw bytes.</summary>
    internal static void WriteBlob(NetDataWriter w, byte[] data)
    {
        w.Put(data.Length);
        w.Put(data, 0, data.Length);
    }

    /// <summary>
    /// Reads a blob written by <see cref="WriteBlob"/>. Returns null if the length is negative, larger than
    /// <paramref name="max"/>, or more than the message holds.
    /// </summary>
    internal static byte[]? ReadBlob(NetDataReader r, int max, out int declaredLength)
    {
        declaredLength = r.GetInt();
        if (declaredLength < 0 || declaredLength > max || declaredLength > r.AvailableBytes) return null;
        var data = new byte[declaredLength];
        r.GetBytes(data, declaredLength);
        return data;
    }

    /// <summary>Exceptions LiteNetLib and our readers throw for short or corrupt messages.</summary>
    internal static bool IsReadError(Exception e) =>
        e is IndexOutOfRangeException or ArgumentException or FormatException or InvalidOperationException or OverflowException;
}
