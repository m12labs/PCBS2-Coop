using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Marker handler that records what it applied. Refuses invalid markers and the text "bad".</summary>
internal sealed class MarkerHandler : ICommandHandler
{
    public readonly List<(string Text, ulong Revision, OperationId Op)> Applied = new();
    public string Name => "DiagnosticMarker";

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var m = DiagnosticMarker.Read(payload);
        if (!m.IsValid) return CommandVerdict.Reject("marker text must be 1-64 printable characters");
        return m.Text == "bad" ? CommandVerdict.Reject("refused by test") : CommandVerdict.Accept;
    }

    public void Apply(in MutationContext ctx, NetDataReader payload) =>
        Applied.Add((DiagnosticMarker.Read(payload).Text, ctx.Revision, ctx.Op));

    public IEnumerable<string> Texts => Applied.Select(a => a.Text);
}

/// <summary>On the host, applying a "cascade" marker submits a follow-up marker from inside Apply.</summary>
internal sealed class CascadeHandler : ICommandHandler
{
    public WorldSync? World;
    public string Name => "Cascade";
    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload) => CommandVerdict.Accept;
    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        if (World is { IsAuthority: true }) World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("after-cascade").ToBytes(), out _);
    }
}

internal static class Ids
{
    public static PeerIdentity Make(string build = "92d05e5a", string[]? dlcs = null, string catalog = "abc", int parts = 100, PlayerId? player = null) =>
        new(player ?? PlayerId.New(), ModVersion.Value, Protocol.Version, new GameIdentity("1.0.test", build),
            new ContentIdentity(dlcs ?? new[] { "DLCPack_FallGuys" }, catalog, parts));
}
