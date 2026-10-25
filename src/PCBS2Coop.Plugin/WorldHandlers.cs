using LiteNetLib.Utils;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Diagnostic markers: a harmless shared fact that exercises the host-ordered change pipeline in the running game.
/// Nothing in the game changes. The list travels with load snapshots, so a late-joining guest gets the markers made
/// before it joined. Used by the self test to compare both peers.
/// </summary>
internal sealed class DiagnosticMarkerHandler : ICommandHandler, IWorldState
{
    public readonly List<string> Applied = new();
    public string Name => "DiagnosticMarker";

    public void Reset() => Applied.Clear();
    public void Write(NetDataWriter w)
    {
        w.Put(Applied.Count);
        foreach (var a in Applied) w.Put(a, DiagnosticMarker.MaxLength + 1);
    }
    public void Read(NetDataReader r)
    {
        var n = r.GetInt();
        if (n < 0 || n > 100_000) throw new InvalidDataException($"bad marker count {n}");
        for (var i = 0; i < n; i++) Applied.Add(r.GetString(DiagnosticMarker.MaxLength + 1));
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload) =>
        DiagnosticMarker.Read(payload).IsValid ? CommandVerdict.Accept : CommandVerdict.Reject("marker text must be 1-64 printable characters");

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var m = DiagnosticMarker.Read(payload);
        Applied.Add(m.Text);
        Plugin.Logger.LogInfo($"[world] marker '{m.Text}' {ctx.Op} gen {ctx.Generation} rev {ctx.Revision}");
    }
}
