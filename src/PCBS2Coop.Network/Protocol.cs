using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

public enum Role : byte { None = 0, Host = 1, Client = 2 }

public enum MessageType : byte
{
    /// <summary>Position and facing of the sender's player, sent unreliably at a fixed rate.</summary>
    PlayerState = 1,
}

/// <summary>World-space snapshot of one player. Position is the feet (bottom of the character controller).</summary>
public readonly record struct PlayerState(float X, float Y, float Z, float Yaw, bool InWorkshop)
{
    public void Write(NetDataWriter w)
    {
        w.Put((byte)MessageType.PlayerState);
        w.Put(X); w.Put(Y); w.Put(Z); w.Put(Yaw); w.Put(InWorkshop);
    }

    public static PlayerState Read(NetDataReader r) =>
        new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetBool());
}

public static class Protocol
{
    public const int DefaultPort = 7777;

    /// <summary>Connection key. Mismatched mod versions are rejected during the handshake.</summary>
    public static string ConnectionKey => "PCBS2Coop/" + ModVersion.Value;
}
