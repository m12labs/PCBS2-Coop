using System.Diagnostics;
using PCBS2Coop.Network;

// Network acceptance tests. No game needed.
//  - Identity/compatibility and the world-change pipeline run in memory with injected faults.
//  - Loopback runs real host + guest sessions over UDP on 127.0.0.1.
var t = new Checker();
IdentityTests.Run(t);
ObjectWorldTests.Run(t);
ActivityTests.Run(t);
AssemblyResourceTests.Run(t);
PcLifecycleTests.Run(t);
PcTransferTests.Run(t);
WorkstationTests.Run(t);
MechanismTests.Run(t);
PanelTests.Run(t);
ComponentEditTests.Run(t);
WorldSyncTests.Run(t);
SnapshotTests.Run(t);
TransportTests.Run(t);
SyncTests.Run(t);
AcceptanceTests.Run(t);
LoopbackTests.Run(t);
return t.Finish();

internal static class LoopbackTests
{
    private const int Port = 47777;

    public static void Run(Checker t)
    {
        t.Section("Loopback: join, handshake and positions");
        using var host = new CoopSession();
        using var guest = new CoopSession();
        var hostMarkers = new MarkerHandler();
        var guestMarkers = new MarkerHandler();
        host.WorldCreated += w => { w.Register(CommandTypes.DiagnosticMarker, hostMarkers); w.RegisterState(hostMarkers); };
        guest.WorldCreated += w => { w.Register(CommandTypes.DiagnosticMarker, guestMarkers); w.RegisterState(guestMarkers); };
        string? hostDrop = null;
        host.PeerDisconnected += r => hostDrop = r;

        PlayerState? atHost = null, atGuest = null;
        string? guestDrop = null;
        var hostJoined = 0; var guestJoined = 0;
        host.PeerJoined += () => hostJoined++;
        guest.PeerJoined += () => guestJoined++;
        host.RemoteState += s => atHost = s;
        guest.RemoteState += s => atGuest = s;
        guest.PeerDisconnected += r => guestDrop = r;

        var hostId = Ids.Make();
        t.Check(host.Host(Port, hostId, out _), "host binds port");
        t.Check(!new CoopSession().Host(Port, Ids.Make(), out var busy) && busy.Contains("port"), "second host on same port reports an error");
        t.Check(!guest.Join("not-an-ip", Port, Ids.Make(), out _), "invalid address rejected before connecting");
        t.Check(guest.Join("127.0.0.1", Port, Ids.Make(dlcs: new[] { "DLCPack_PCBS1DecoratorTheme" }), out _), "guest starts connecting");
        t.Check(Checker.PumpUntil(() => host.IsJoined && guest.IsJoined, 3000, host, guest), "both sides complete the handshake");
        t.Check(hostJoined == 1 && guestJoined == 1, "PeerJoined raised once on each side");
        t.Check(guest.RemoteIdentity?.Player == hostId.Player && host.RemoteIdentity?.Player == guest.LocalIdentity!.Player, "each side knows the other's player ID");
        t.Check(host.Warnings.Any(w => w.Contains("DLC")) && guest.Warnings.Any(w => w.Contains("DLC")), "DLC difference: join allowed, both sides get the warning");

        guest.SendState(new PlayerState(1, 2, 3, 90, true));
        host.SendState(new PlayerState(-4, 0, 5, 180, true));
        t.Check(Checker.PumpUntil(() => atHost != null && atGuest != null, 3000, host, guest), "positions exchanged");
        t.Check(atHost == new PlayerState(1, 2, 3, 90, true) && atGuest == new PlayerState(-4, 0, 5, 180, true), "positions arrive intact");

        guest.SendState(new PlayerState(float.NaN, 0, 0, 0, true));
        guest.SendState(new PlayerState(5, 5, 5, 5, true));
        t.Check(Checker.PumpUntil(() => host.Stats.InvalidPlayerStates == 1 && atHost?.X == 5, 3000, host, guest), "NaN position dropped and counted; later valid one used");

        t.Section("Loopback: world changes over UDP");
        t.Check(!guest.World!.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("before-world").ToBytes(), out _), "guest can't submit before it is in the host's world");
        t.Check(!host.GuestNeedsWorld, "host without a world doesn't offer one");

        host.World!.BeginGeneration("loopback world");
        t.Check(Checker.PumpUntil(() => guest.World.Generation == 1, 3000, host, guest), "guest follows the host to generation 1");
        t.Check(host.GuestNeedsWorld && guest.GuestState == GuestState.Connected, "host sees a guest that needs the world");

        t.Section("Loopback: snapshot and admission");
        host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("pre-snapshot").ToBytes(), out _);
        var file = new byte[3_000_000];
        new Random(42).NextBytes(file);
        Snapshot? got = null;
        guest.SnapshotReceived += s => got = s;
        var snapId = host.BeginSnapshot(SnapshotPurpose.Load);
        t.Check(!host.GuestNeedsWorld, "a marked snapshot stops further offers");
        host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("during-load-1").ToBytes(), out _);
        host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("during-load-2").ToBytes(), out _);
        host.SetSnapshotFile(snapId, "coopworld.binary.zip", file);
        t.Check(Checker.PumpUntil(() => got != null, 15000, host, guest), "3 MB snapshot arrives over UDP");
        t.Check(got != null && got.File.AsSpan().SequenceEqual(file) && got.Purpose == SnapshotPurpose.Load && got.Position!.Revision == 1, "snapshot intact, positioned after the first change");
        t.Check(Checker.PumpUntil(() => host.GuestState == GuestState.Loading, 3000, host, guest) && guest.GuestState == GuestState.Loading, "both sides see the guest loading");
        t.Check(guestMarkers.Applied.Count == 0 && guest.World.Holding, "changes made during the load are held");
        t.Check(guest.CompleteAdmission(out _), "guest enters the world");
        t.Check(guestMarkers.Texts.SequenceEqual(new[] { "pre-snapshot", "during-load-1", "during-load-2" }) && guest.World.ChainHash == host.World.ChainHash,
            "guest has the change from the snapshot plus the two held ones, once each, same hash");
        t.Check(Checker.PumpUntil(() => host.GuestState == GuestState.InWorld, 3000, host, guest) && !host.GuestNeedsWorld, "host sees the guest in the world");
        for (var i = 0; i < 20; i++)
        {
            guest.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker($"g{i}").ToBytes(), out _);
            host.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker($"h{i}").ToBytes(), out _);
        }
        t.Check(Checker.PumpUntil(() => guestMarkers.Applied.Count == 43 && guest.World.PendingCount == 0, 5000, host, guest), "40 interleaved changes applied on the guest");
        t.Check(hostMarkers.Texts.SequenceEqual(guestMarkers.Texts) && host.World.ChainHash == guest.World.ChainHash && host.World.Revision == 43,
            "both peers applied the same 40 changes in the same order (same hash)");
        t.Check(guest.World.Divergence == null && host.Stats.MalformedMessages == 0 && guest.Stats.MalformedMessages == 0, "no divergence or malformed traffic");

        Snapshot? copy = null;
        guest.SnapshotReceived += s => { if (s.Purpose == SnapshotPurpose.Copy) copy = s; };
        var copyId = host.BeginSnapshot(SnapshotPurpose.Copy);
        host.SetSnapshotFile(copyId, "freebuild.binary.zip", new byte[] { 1, 2, 3 });
        t.Check(Checker.PumpUntil(() => copy != null, 3000, host, guest) && copy!.FileName == "freebuild.binary.zip" && guest.GuestState == GuestState.InWorld,
            "copy snapshot (host saved) reaches the guest without affecting admission");

        t.Section("Loopback: refusals and endings");
        using var third = new CoopSession();
        string? thirdDrop = null;
        third.PeerDisconnected += r => thirdDrop = r;
        third.Join("127.0.0.1", Port, Ids.Make(), out _);
        t.Check(Checker.PumpUntil(() => thirdDrop != null, 3000, host, guest, third) && thirdDrop!.Contains("two-player"), $"third player refused with a reason ({thirdDrop})");

        // Guest leaves and comes back: the host keeps listening and its world.
        guest.Stop();
        t.Check(Checker.PumpUntil(() => !host.HasPeer, 3000, host), "host notices the guest left");
        t.Check(guest.Join("127.0.0.1", Port, Ids.Make(), out _) && Checker.PumpUntil(() => host.IsJoined && guest.IsJoined, 3000, host, guest), "guest rejoins");
        t.Check(!guest.World!.Admitted && host.GuestNeedsWorld, "rejoined guest needs a fresh snapshot");
        t.Check(Admit(host, guest, file), "rejoined guest is admitted from a new snapshot");
        t.Check(guest.World.Generation == 1 && guest.World.Revision == host.World.Revision && guest.World.ChainHash == host.World.ChainHash && guestMarkers.Texts.SequenceEqual(hostMarkers.Texts),
            "rejoined guest starts from the host's current world, including earlier changes");
        var before = guestMarkers.Applied.Count;
        guest.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("after-rejoin").ToBytes(), out _);
        t.Check(Checker.PumpUntil(() => guestMarkers.Applied.Count == before + 1, 3000, host, guest), "rejoined guest's request is accepted");

        // A guest that can't load the world leaves with a reason the host sees; the host keeps its world.
        guest.Stop();
        Checker.PumpUntil(() => !host.HasPeer, 3000, host);
        guest.Join("127.0.0.1", Port, Ids.Make(), out _);
        Checker.PumpUntil(() => host.IsJoined && guest.IsJoined, 3000, host, guest);
        hostDrop = null;
        string? failDrop = null;
        guest.PeerDisconnected += r => failDrop ??= r;
        var fid = host.BeginSnapshot(SnapshotPurpose.Load);
        host.SetSnapshotFile(fid, "coopworld.binary.zip", new byte[] { 9 });
        Checker.PumpUntil(() => guest.GuestState == GuestState.Loading, 3000, host, guest);
        guest.FailAdmission("test load failure");
        t.Check(Checker.PumpUntil(() => hostDrop != null, 3000, host, guest) && hostDrop!.Contains("test load failure"), $"host sees why the guest left ({hostDrop})");
        t.Check(failDrop != null && failDrop.Contains("test load failure"), $"guest reports its own failure reason ({failDrop})");
        t.Check(host.World.Admitted && host.GuestState == GuestState.Connected, "host keeps its world and is ready for another guest");

        // Incompatible game build is refused with the reason shown to the guest.
        guest.Stop();
        Checker.PumpUntil(() => !host.HasPeer, 3000, host);
        string? buildDrop = null;
        guest.PeerDisconnected += r => buildDrop = r;
        guest.Join("127.0.0.1", Port, Ids.Make(build: "different"), out _);
        t.Check(Checker.PumpUntil(() => buildDrop != null, 3000, host, guest) && buildDrop!.Contains("game builds"), $"different game build refused with a readable reason ({buildDrop})");
        t.Check(host.Stats.RefusedJoins == 1 && !host.HasPeer, "host counted the refusal and stays free for another guest");

        guest.Stop();
        guestDrop = null;
        guest.Join("127.0.0.1", Port, Ids.Make(), out _);
        Checker.PumpUntil(() => guest.IsJoined, 3000, host, guest);
        host.Stop("The host ended the session.");
        t.Check(Checker.PumpUntil(() => guestDrop != null, 3000, guest) && guestDrop!.Contains("ended the session"), $"guest is told why the host stopped ({guestDrop})");

        using var lonely = new CoopSession();
        string? noHost = null;
        lonely.PeerDisconnected += r => noHost = r;
        lonely.Join("127.0.0.1", Port + 1, Ids.Make(), out _);
        var sw = Stopwatch.StartNew();
        while (noHost == null && sw.ElapsedMilliseconds < 10000) { lonely.Poll(); Thread.Sleep(10); }
        t.Check(noHost != null, $"join with no host fails with a reason ({noHost})");
    }

    private static bool Admit(CoopSession host, CoopSession guest, byte[] file)
    {
        Snapshot? got = null;
        void OnSnap(Snapshot s) { if (s.Purpose == SnapshotPurpose.Load) got = s; }
        guest.SnapshotReceived += OnSnap;
        try
        {
            var id = host.BeginSnapshot(SnapshotPurpose.Load);
            host.SetSnapshotFile(id, "coopworld.binary.zip", file);
            return Checker.PumpUntil(() => got != null, 15000, host, guest) && guest.CompleteAdmission(out _)
                   && Checker.PumpUntil(() => host.GuestState == GuestState.InWorld, 3000, host, guest);
        }
        finally { guest.SnapshotReceived -= OnSnap; }
    }
}
