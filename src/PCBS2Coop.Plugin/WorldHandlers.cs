using LiteNetLib.Utils;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Diagnostic markers: a harmless shared fact that exercises the host-ordered change pipeline in the running game.
/// Not world state; nothing in the game changes. Used by the self test to compare both peers.
/// </summary>
internal sealed class DiagnosticMarkerHandler : ICommandHandler
{
    public readonly List<string> Applied = new();
    public string Name => "DiagnosticMarker";

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload) =>
        DiagnosticMarker.Read(payload).IsValid ? CommandVerdict.Accept : CommandVerdict.Reject("marker text must be 1-64 printable characters");

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var m = DiagnosticMarker.Read(payload);
        Applied.Add(m.Text);
        Plugin.Logger.LogInfo($"[world] marker '{m.Text}' {ctx.Op} gen {ctx.Generation} rev {ctx.Revision}");
    }
}
