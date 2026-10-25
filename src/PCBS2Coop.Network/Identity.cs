using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>Durable identity of one game install. Survives reconnects; used later for rejoin.</summary>
public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;

    public static bool TryParse(string? text, out PlayerId id)
    {
        var ok = Guid.TryParse(text, out var g) && g != Guid.Empty;
        id = ok ? new PlayerId(g) : default;
        return ok;
    }

    public void Write(NetDataWriter w) => w.Put(Value.ToByteArray());
    public static PlayerId Read(NetDataReader r)
    {
        var bytes = new byte[16];
        r.GetBytes(bytes, 16);
        return new PlayerId(new Guid(bytes));
    }

    /// <summary>Short form for logs.</summary>
    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>A participant's seat in the current session. Not durable: a reconnecting guest gets the guest seat again.</summary>
public enum ActorSlot : byte { None = 0, Host = 1, Guest = 2 }

/// <summary>
/// Identifies one requested world change: who asked, and their per-session sequence number.
/// The host deduplicates on this pair.
/// </summary>
public readonly record struct OperationId(ActorSlot Actor, uint Sequence)
{
    public override string ToString() => $"{(Actor == ActorSlot.Host ? "H" : Actor == ActorSlot.Guest ? "G" : "?")}#{Sequence}";
}

/// <summary>
/// Network identity of a shared world object (PC, part, package…). Allocated only by the host and never reused
/// within a world, so a command naming a deleted object can't hit a newer object.
/// </summary>
public readonly record struct NetObjectId(ulong Value)
{
    public static readonly NetObjectId None = default;
    public bool IsNone => Value == 0;
    public void Write(NetDataWriter w) => w.Put(Value);
    public static NetObjectId Read(NetDataReader r) => new(r.GetULong());
    public override string ToString() => IsNone ? "obj:none" : $"obj:{Value}";
}
