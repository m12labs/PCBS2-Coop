using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Chat lines and the players' /sync (task 3h): the guest leaves the world while staying connected, the
/// host starts a new generation from its reloaded save, and the guest is admitted again from a new snapshot.</summary>
internal static class SyncTests
{
    public static void Run(Checker t)
    {
        Chat(t);
        Requests(t);
        Resync(t);
        ResyncWithoutReload(t);
    }

    private sealed class Pair : IDisposable
    {
        public readonly CoopSession Host = new(), Guest = new();
        public readonly TransportTests.FramedLink HostLink = new(), GuestLink = new();
        public readonly MarkerHandler HostMarkers = new(), GuestMarkers = new();
        public readonly List<ChatLine> AtHost = new(), AtGuest = new();
        public readonly List<string> Replaced = new(), Started = new(), Rejected = new();
        public int Requests, Declines;

        public Pair()
        {
            HostLink.Peer = GuestLink; GuestLink.Peer = HostLink;
            Host.WorldCreated += w => { w.Register(CommandTypes.DiagnosticMarker, HostMarkers); w.RegisterState(HostMarkers); };
            Guest.WorldCreated += w =>
            {
                w.Register(CommandTypes.DiagnosticMarker, GuestMarkers); w.RegisterState(GuestMarkers);
                w.WorldReplaced += Replaced.Add;
                w.Rejected += (_, reason, _) => Rejected.Add(reason.ToString());
            };
            Host.ChatReceived += AtHost.Add; Guest.ChatReceived += AtGuest.Add;
            Host.SyncRequested += () => Requests++;
            Guest.SyncDeclined += () => Declines++;
            Guest.SyncStarted += Started.Add;
            Host.Host(HostLink, Ids.Make(), out _);
            Host.World!.BeginGeneration("w");
            Guest.Join(GuestLink, Ids.Make(), out _);
            Checker.PumpUntil(() => Host.IsJoined && Guest.IsJoined, 1000, Host, Guest);
        }

        public bool Admit()
        {
            Snapshot? got = null;
            void OnSnap(Snapshot s) { if (s.Purpose == SnapshotPurpose.Load) got = s; }
            Guest.SnapshotReceived += OnSnap;
            try
            {
                var id = Host.BeginSnapshot(SnapshotPurpose.Load);
                Host.SetSnapshotFile(id, "coopworld.binary.zip", new byte[] { 1, 2, 3 });
                return Checker.PumpUntil(() => got != null, 1000, Host, Guest) && Guest.CompleteAdmission(out _)
                       && Checker.PumpUntil(() => Host.GuestState == GuestState.InWorld, 1000, Host, Guest);
            }
            finally { Guest.SnapshotReceived -= OnSnap; }
        }

        public void Mark(CoopSession s, string text) => s.World!.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker(text).ToBytes(), out _);
        public bool Pump(Func<bool> done) => Checker.PumpUntil(done, 1000, Host, Guest);
        public bool InStep => Host.World!.ChainHash == Guest.World!.ChainHash && Host.World.Revision == Guest.World.Revision &&
                              HostMarkers.Texts.SequenceEqual(GuestMarkers.Texts);
        public void Dispose() { Guest.Dispose(); Host.Dispose(); }
    }

    private static void Chat(Checker t)
    {
        t.Section("Chat: lines between players");
        using var p = new Pair();
        t.Check(p.Guest.SendChat(ChatKind.Player, "  hello host ") && p.Host.SendChat(ChatKind.System, "Host is out of sync."),
            "both players can send once joined, before the guest is in the world");
        t.Check(p.Pump(() => p.AtHost.Count == 1 && p.AtGuest.Count == 1) && p.AtHost[0] == new ChatLine(ChatKind.Player, "hello host") &&
                p.AtGuest[0] == new ChatLine(ChatKind.System, "Host is out of sync."), "lines arrive trimmed, with their kind");
        t.Check(!p.Guest.SendChat(ChatKind.Player, " \t ") && ChatLine.Clean("a\nb\u0007c") == "a b c" &&
                ChatLine.Clean(new string('x', 300)).Length == ChatLine.MaxLength, "blank text is not sent; control characters and overlong text are cleaned");

        var malformed = p.Host.Stats.MalformedMessages;
        var w = new NetDataWriter();
        w.Put((byte)MessageType.Chat); w.Put((byte)ChatKind.Player); w.Put("bad\u0001line", ChatLine.MaxLength + 1);
        p.GuestLink.Send(w.CopyData(), SessionDelivery.ReliableOrdered);
        w.Reset(); w.Put((byte)MessageType.Chat); w.Put((byte)9); w.Put("unknown kind", ChatLine.MaxLength + 1);
        p.GuestLink.Send(w.CopyData(), SessionDelivery.ReliableOrdered);
        w.Reset(); w.Put((byte)MessageType.Chat); w.Put((byte)ChatKind.Player); w.Put("trailing", ChatLine.MaxLength + 1); w.Put(1);
        p.GuestLink.Send(w.CopyData(), SessionDelivery.ReliableOrdered);
        t.Check(p.Pump(() => p.Host.Stats.MalformedMessages == malformed + 3) && p.AtHost.Count == 1,
            "a line with control characters, an unknown kind or trailing bytes is dropped and counted");

        var stranger = new CoopSession();
        t.Check(!stranger.SendChat(ChatKind.Player, "nobody"), "no chat without a session");
    }

    private static void Requests(Checker t)
    {
        t.Section("Sync: request and decline");
        using var p = new Pair();
        t.Check(!p.Guest.RequestSync(), "a guest that isn't in the world can't ask for a sync");
        t.Check(p.Admit(), "guest enters the world");
        t.Check(p.Guest.RequestSync() && p.Pump(() => p.Requests == 1), "guest request reaches the host");
        t.Check(!p.Host.RequestSync() && !p.Guest.DeclineSync() && !p.Guest.BeginResync("x") && !p.Host.ReadyForWorld(),
            "each role can only use its own sync actions");
        t.Check(p.Host.DeclineSync() && p.Pump(() => p.Declines == 1) && p.Guest.GuestState == GuestState.InWorld && p.Guest.World!.Admitted,
            "a declined request leaves the guest in the world");

        var malformed = p.Guest.Stats.MalformedMessages;
        var w = new NetDataWriter();
        w.Put((byte)MessageType.Sync); w.Put((byte)SyncAction.Request); w.Put("", Protocol.MaxShortString);
        p.HostLink.Send(w.CopyData(), SessionDelivery.ReliableOrdered); // a request sent to the guest
        w.Reset(); w.Put((byte)MessageType.GuestStatus); w.Put((byte)GuestState.Connected); w.Put(1u);
        var hostMalformed = p.Host.Stats.MalformedMessages;
        p.GuestLink.Send(w.CopyData(), SessionDelivery.ReliableOrdered); // "back on the menu" without a sync
        t.Check(p.Pump(() => p.Guest.Stats.MalformedMessages == malformed + 1 && p.Host.Stats.MalformedMessages == hostMalformed + 1) &&
                p.Host.GuestState == GuestState.InWorld, "a wrong-direction sync message or an unasked 'back on the menu' is dropped and counted");
    }

    private static void Resync(Checker t)
    {
        t.Section("Sync: guest leaves, host reloads, guest is admitted again");
        using var p = new Pair();
        p.Mark(p.Host, "before"); p.Pump(() => true);
        t.Check(p.Admit() && p.InStep, "guest in the world");

        // A guest request in flight when the sync starts is abandoned on the guest; the host answers it normally.
        p.Mark(p.Guest, "in-flight");
        t.Check(p.Host.BeginResync("Client asked") && p.Host.GuestState == GuestState.Leaving && !p.Host.GuestNeedsWorld,
            "host starts the sync; it offers no world while the guest leaves");
        t.Check(!p.Host.BeginResync("again"), "a second start while the guest is leaving is refused");
        t.Check(p.Pump(() => p.Started.Count == 1) && p.Started[0] == "Client asked" && p.Guest.GuestState == GuestState.Leaving &&
                !p.Guest.World!.Admitted && p.Rejected.Contains(nameof(RejectReason.Abandoned)),
            "guest leaves the world with the reason and abandons its waiting request");
        t.Check(!p.Guest.World!.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("x").ToBytes(), out _), "a leaving guest can't submit");

        // Host reloads its save: a new generation. The guest is not "replaced" out of the session.
        p.Host.World!.BeginGeneration("co-op world after /sync");
        p.Mark(p.Host, "after-reload");
        t.Check(p.Pump(() => p.Guest.World.Generation == 2) && p.Replaced.Count == 0 && p.Guest.IsJoined && p.Guest.GuestState == GuestState.Leaving,
            "the new generation reaches the guest without a 'world replaced' exit");
        t.Check(!p.Host.GuestNeedsWorld, "still no snapshot until the guest is back on its menu");

        t.Check(p.Guest.ReadyForWorld() && p.Pump(() => p.Host.GuestState == GuestState.Connected) && p.Host.GuestNeedsWorld,
            "guest back on its menu asks for the world; the host offers it");
        t.Check(!p.Guest.ReadyForWorld(), "asking twice does nothing");
        t.Check(p.Admit() && p.Guest.World.Generation == 2 && p.InStep && p.GuestMarkers.Texts.SequenceEqual(new[] { "after-reload" }),
            "guest is admitted into the reloaded world, in step with the host");
        p.Mark(p.Guest, "after-sync");
        t.Check(p.Pump(() => p.GuestMarkers.Texts.Contains("after-sync")) && p.InStep && p.Guest.World.Divergence == null,
            "guest requests work normally after the sync");
    }

    private static void ResyncWithoutReload(Checker t)
    {
        t.Section("Sync: the host's save fails; the guest reloads the current world");
        using var p = new Pair();
        t.Check(p.Admit(), "guest in the world");
        var id = p.Host.BeginSnapshot(SnapshotPurpose.Copy);
        p.Host.SetSnapshotFile(id, "freebuild.binary.zip", new byte[] { 7 });
        t.Check(p.Host.BeginResync("Host") && p.Pump(() => p.Guest.GuestState == GuestState.Leaving), "sync starts (a queued copy is dropped)");
        p.Mark(p.Host, "while-leaving");
        t.Check(p.Guest.ReadyForWorld() && p.Pump(() => p.Host.GuestNeedsWorld), "guest back on its menu; host still in the same generation");
        t.Check(p.Admit() && p.Guest.World!.Generation == 1 && p.InStep && p.GuestMarkers.Texts.SequenceEqual(new[] { "while-leaving" }),
            "guest re-enters the current world from a fresh snapshot");

        var loading = new Pair();
        t.Check(loading.Admit(), "second pair: guest in the world");
        loading.Host.BeginResync("Host"); loading.Pump(() => loading.Guest.GuestState == GuestState.Leaving);
        loading.Guest.ReadyForWorld(); loading.Pump(() => loading.Host.GuestNeedsWorld);
        var sid = loading.Host.BeginSnapshot(SnapshotPurpose.Load);
        t.Check(!loading.Host.BeginResync("Host"), "no sync while the guest isn't in the world (loading a snapshot)");
        loading.Host.AbortSnapshot(sid, "test");
        loading.Dispose();
    }
}
