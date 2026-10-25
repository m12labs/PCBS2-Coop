using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>
/// Host and guest <see cref="WorldSync"/> joined by an in-memory link that the test controls, so messages can be
/// delayed, dropped, duplicated, reordered or forged.
/// </summary>
internal sealed class FakeLink
{
    public readonly WorldSync Host, Guest;
    public readonly MarkerHandler HostMarkers = new(), GuestMarkers = new();
    public readonly List<byte[]> ToHost = new(), ToGuest = new();
    public readonly List<(OperationId Op, ulong Rev)> HostAccepted = new(), GuestAccepted = new();
    public readonly List<(OperationId Op, RejectReason Reason, string Text)> HostRejected = new(), GuestRejected = new();
    public readonly List<string> Divergences = new();
    public readonly List<string> Replaced = new();

    public FakeLink(bool withCascade = false)
    {
        Host = new WorldSync(ActorSlot.Host, w => ToGuest.Add(w.CopyData()));
        Guest = new WorldSync(ActorSlot.Guest, w => ToHost.Add(w.CopyData()));
        Host.Register(CommandTypes.DiagnosticMarker, HostMarkers);
        Guest.Register(CommandTypes.DiagnosticMarker, GuestMarkers);
        Host.RegisterState(HostMarkers);
        Guest.RegisterState(GuestMarkers);
        Guest.WorldReplaced += r => Replaced.Add(r);
        if (withCascade)
        {
            Host.Register(2, new CascadeHandler { World = Host });
            Guest.Register(2, new CascadeHandler { World = Guest });
        }
        Host.Accepted += (op, rev) => HostAccepted.Add((op, rev));
        Guest.Accepted += (op, rev) => GuestAccepted.Add((op, rev));
        Host.Rejected += (op, r, t) => HostRejected.Add((op, r, t));
        Guest.Rejected += (op, r, t) => GuestRejected.Add((op, r, t));
        Guest.Diverged += d => Divergences.Add(d);
        Host.Linked = Guest.Linked = true;
    }

    /// <summary>
    /// What a join does: everything already on the wire reaches the guest first (before the snapshot mark), then the
    /// guest loads a snapshot of the host's current world and enters it.
    /// </summary>
    public void Join()
    {
        DeliverToGuest();
        MarkSnapshot();
        if (!Guest.Adopt(out var error)) throw new InvalidOperationException(error);
    }

    /// <summary>The host marks a snapshot; the guest starts holding world changes (it is "loading").</summary>
    public SnapshotPosition MarkSnapshot()
    {
        DeliverToGuest();
        var p = Host.CapturePosition();
        Guest.BeginHold(p);
        return p;
    }

    public void DeliverToGuest()
    {
        while (ToGuest.Count > 0) { var m = ToGuest[0]; ToGuest.RemoveAt(0); Deliver(Guest, m); }
    }

    public static void Deliver(WorldSync to, byte[] msg)
    {
        var r = new NetDataReader(msg);
        to.Handle((MessageType)r.GetByte(), r);
    }

    /// <summary>Deliver everything queued, in order, until both directions are empty.</summary>
    public void Flush()
    {
        while (ToHost.Count > 0 || ToGuest.Count > 0)
        {
            while (ToHost.Count > 0) { var m = ToHost[0]; ToHost.RemoveAt(0); Deliver(Host, m); }
            while (ToGuest.Count > 0) { var m = ToGuest[0]; ToGuest.RemoveAt(0); Deliver(Guest, m); }
        }
    }

    public OperationId Mark(WorldSync side, string text, ushort type = CommandTypes.DiagnosticMarker)
    {
        if (!side.Submit(type, new DiagnosticMarker(text).ToBytes(), out var op)) throw new InvalidOperationException("Submit refused");
        return op;
    }

    public static byte[] RawCommand(uint gen, uint seq, ushort type, byte[] payload)
    {
        var w = new NetDataWriter();
        w.Put((byte)MessageType.Command);
        w.Put(gen); w.Put(seq); w.Put(type);
        Protocol.WriteBlob(w, payload);
        return w.CopyData();
    }

    public bool InStep => Host.Revision == Guest.Revision && Host.ChainHash == Guest.ChainHash && Host.Generation == Guest.Generation
                          && HostMarkers.Texts.SequenceEqual(GuestMarkers.Texts);
}

internal static class WorldSyncTests
{
    public static void Run(Checker t)
    {
        t.Section("World changes: ordering and acceptance");
        {
            var l = new FakeLink();
            t.Check(!l.Guest.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("early").ToBytes(), out _), "guest can't submit before it knows the host's world");

            l.Host.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("no-world").ToBytes(), out _);
            t.Check(l.HostRejected.Count == 1 && l.HostRejected[0].Reason == RejectReason.NoWorld, "host request with no world loaded is refused (NoWorld)");

            l.Host.BeginGeneration("test world");
            l.Flush();
            l.Join();
            t.Check(l.Guest.Generation == 1 && l.Guest.Synchronized, "guest adopts host world generation on join");

            var a = l.Mark(l.Host, "A");
            l.Flush();
            t.Check(l.HostAccepted.Any(x => x.Op == a && x.Rev == 1), "host request accepted at revision 1");
            var b = l.Mark(l.Guest, "B");
            t.Check(l.GuestMarkers.Applied.Count == 1, "guest change is not applied before the host confirms it");
            l.Flush();
            t.Check(l.GuestAccepted.Any(x => x.Op == b && x.Rev == 2), "guest request confirmed with revision 2");

            // Concurrent: guest C is in flight while the host submits D. The host's order wins on both peers.
            var c = l.Mark(l.Guest, "C");
            var d = l.Mark(l.Host, "D");
            l.Flush();
            t.Check(l.HostMarkers.Texts.SequenceEqual(new[] { "A", "B", "D", "C" }), "host applied A,B,D,C (its own order)");
            t.Check(l.InStep, "guest applied the same changes in the same order with the same hash");
            t.Check(l.GuestAccepted.Any(x => x.Op == c && x.Rev == 4) && l.HostAccepted.Any(x => x.Op == d && x.Rev == 3), "revisions reflect host order");
        }

        t.Section("World changes: invalid requests");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w"); l.Join();

            var bad = l.Mark(l.Guest, "bad");
            l.Flush();
            t.Check(l.GuestRejected.Any(x => x.Op == bad && x.Reason == RejectReason.Invalid && x.Text.Contains("refused by test")), "validator refusal reaches the guest with its reason");
            t.Check(l.Host.Revision == 0 && l.HostMarkers.Applied.Count == 0 && l.GuestMarkers.Applied.Count == 0, "refused request changes nothing");

            var tooLong = l.Mark(l.Guest, new string('x', 65));
            l.Flush();
            t.Check(l.GuestRejected.Any(x => x.Op == tooLong && x.Reason == RejectReason.Invalid), "over-long marker text refused by validation");

            l.Guest.Submit(77, new byte[] { 1, 2, 3 }, out var unknown);
            l.Flush();
            t.Check(l.GuestRejected.Any(x => x.Op == unknown && x.Reason == RejectReason.UnknownCommand), "unknown command type refused");

            l.Guest.Submit(CommandTypes.DiagnosticMarker, new byte[] { 0xFF, 0xFF, 0x00 }, out var garbage);
            l.Flush();
            t.Check(l.GuestRejected.Any(x => x.Op == garbage && x.Reason == RejectReason.Malformed), "unreadable payload refused as malformed (no crash)");

            // Forged oversize command straight onto the wire (a normal guest never sends this).
            var before = l.Host.Stats.Malformed;
            FakeLink.Deliver(l.Host, FakeLink.RawCommand(1, 100, CommandTypes.DiagnosticMarker, new byte[Protocol.MaxPayloadBytes + 1]));
            t.Check(l.Host.Stats.Malformed == before + 1 && l.Host.Revision == 0, "oversize command refused before validation");

            // Truncated message: header only.
            var w = new NetDataWriter(); w.Put((byte)MessageType.Command); w.Put(1u);
            FakeLink.Deliver(l.Host, w.CopyData());
            t.Check(l.Host.Revision == 0, "truncated command dropped without effect");

            // Host-only messages sent to the host, and guest-only to the guest, are ignored.
            var m = new NetDataWriter(); m.Put((byte)MessageType.Mutation); m.Put(1u);
            var hb = l.Host.Stats.Malformed;
            FakeLink.Deliver(l.Host, m.CopyData());
            t.Check(l.Host.Stats.Malformed == hb + 1, "a Mutation sent to the host is ignored");
            l.ToGuest.Clear();
        }

        t.Section("World changes: duplicates and reordering");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w"); l.Join();
            var e = l.Mark(l.Guest, "E");
            var copy = l.ToHost[0];
            l.Flush();
            FakeLink.Deliver(l.Host, copy); // same request again
            l.Flush();
            t.Check(l.HostMarkers.Texts.Count(x => x == "E") == 1 && l.GuestMarkers.Texts.Count(x => x == "E") == 1, "duplicate request applied once on both peers");
            t.Check(l.Host.Stats.Duplicates == 1 && l.GuestAccepted.Count(x => x.Op == e) == 1, "duplicate counted; guest confirmed once");

            var f = l.Mark(l.Guest, "F");
            var g = l.Mark(l.Guest, "G");
            var msgF = l.ToHost[0]; var msgG = l.ToHost[1]; l.ToHost.Clear();
            FakeLink.Deliver(l.Host, msgG);
            FakeLink.Deliver(l.Host, msgF); // older sequence after a newer one
            l.Flush();
            t.Check(l.GuestRejected.Any(x => x.Op == f && x.Reason == RejectReason.OutOfOrder), "older request arriving after a newer one is refused (OutOfOrder)");
            t.Check(l.GuestAccepted.Any(x => x.Op == g) && l.InStep, "newer request still applied; peers in step");

            var bad = l.Mark(l.Guest, "bad");
            var badCopy = l.ToHost[0];
            l.Flush();
            var rejectsBefore = l.GuestRejected.Count;
            var dupsBefore = l.Host.Stats.Duplicates;
            var unexpectedBefore = l.Guest.Stats.UnexpectedResults;
            FakeLink.Deliver(l.Host, badCopy);
            l.Flush();
            t.Check(l.GuestRejected.Count == rejectsBefore && l.Host.Stats.Duplicates == dupsBefore + 1 && l.Guest.Stats.UnexpectedResults == unexpectedBefore + 1,
                "duplicate of a refused request repeats its outcome without a second refusal event");
        }

        t.Section("World changes: world generation boundaries");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("first"); l.Join();
            l.Mark(l.Host, "old-1"); l.Flush();

            var stale = l.Mark(l.Guest, "made-for-old-world"); // in flight...
            l.Host.BeginGeneration("load");                     // ...while the host reloads
            var inFlight = l.ToHost.ToList(); l.ToHost.Clear();
            var resetMsgs = l.ToGuest.ToList(); l.ToGuest.Clear();
            foreach (var msg in inFlight) FakeLink.Deliver(l.Host, msg);
            t.Check(l.HostMarkers.Texts.All(x => x != "made-for-old-world") && l.Host.Revision == 0, "host refuses a request made for the previous world");
            var refusal = l.ToGuest.ToList(); l.ToGuest.Clear();
            foreach (var msg in resetMsgs.Concat(refusal)) FakeLink.Deliver(l.Guest, msg);
            t.Check(l.GuestRejected.Count(x => x.Op == stale) == 1 && l.GuestRejected.First(x => x.Op == stale).Reason == RejectReason.Abandoned,
                "guest learns once that its old-world request failed");
            t.Check(l.Guest.Generation == 2 && l.Guest.Revision == 0 && l.Host.Revision == 0, "guest moved to generation 2 at revision 0");
            t.Check(!l.Guest.Admitted && l.Replaced.Count == 1, "after the host's reset the guest is out of the world (WorldReplaced) until it loads a new snapshot");
            t.Check(!l.Guest.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("x").ToBytes(), out _), "guest can't submit while out of the world");
            l.Join();

            // A change from the old world arriving after the reset is dropped.
            l.Mark(l.Host, "gen2-a");
            var gen2Mutation = l.ToGuest.ToList(); l.ToGuest.Clear();
            var oldWorld = new FakeLink();
            oldWorld.Host.BeginGeneration("x"); oldWorld.Join();
            oldWorld.Mark(oldWorld.Host, "gen1-late");
            var gen1Mutation = oldWorld.ToGuest[0];
            var droppedBefore = l.Guest.Stats.StaleDropped;
            FakeLink.Deliver(l.Guest, gen1Mutation);
            t.Check(l.Guest.Stats.StaleDropped == droppedBefore + 1, "change stamped with an older generation is dropped");
            foreach (var msg in gen2Mutation) FakeLink.Deliver(l.Guest, msg);
            t.Check(l.InStep && l.GuestMarkers.Texts.Last() == "gen2-a", "current-generation change still applies after the stale one");

            // Requests queued on the host during a reset don't leak into the new world.
            var reset2 = new NetDataWriter(); reset2.Put((byte)MessageType.WorldReset); reset2.Put(1u); reset2.Put("old", 256);
            var sd = l.Guest.Stats.StaleDropped;
            FakeLink.Deliver(l.Guest, reset2.CopyData());
            t.Check(l.Guest.Generation == 2 && l.Guest.Stats.StaleDropped == sd + 1, "reset to an older generation is ignored");
        }

        t.Section("World changes: lost or corrupted changes are detected");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w"); l.Join(); l.Flush();
            l.Mark(l.Host, "1"); l.Mark(l.Host, "2"); l.Mark(l.Host, "3");
            l.ToGuest.RemoveAt(1); // lose revision 2
            l.Flush();
            t.Check(l.Divergences.Count == 1 && l.Divergences[0].Contains("expected revision 2"), "a missing change is detected (gap)");
            t.Check(l.GuestMarkers.Texts.SequenceEqual(new[] { "1" }), "guest stops applying after a gap");
            l.Host.BeginGeneration("resync");
            l.Flush();
            t.Check(l.Guest.Divergence == null && l.Guest.Generation == 2, "a new generation clears the divergence flag");
            l.Join();

            l.Mark(l.Host, "4");
            var msg = l.ToGuest[0];
            msg[^1] ^= 0x5A; // corrupt the host hash
            l.Flush();
            t.Check(l.Divergences.Count == 2 && l.Divergences[1].Contains("hash mismatch"), "a change that hashes differently is detected");
        }

        t.Section("World changes: reentrancy and disconnects");
        {
            var l = new FakeLink(withCascade: true);
            l.Host.BeginGeneration("w"); l.Join();
            l.Host.Submit(2, Array.Empty<byte>(), out _);
            l.Flush();
            t.Check(l.HostMarkers.Applied.Count == 1 && l.HostMarkers.Applied[0].Revision == 2 && l.InStep,
                "a request made inside Apply runs after the current change (revision 2), not nested");

            var threw = false;
            try { l.Guest.BeginGeneration("guest"); } catch (InvalidOperationException) { threw = true; }
            t.Check(threw, "only the host can start a world generation");

            l.Mark(l.Guest, "pending");
            l.ToHost.Clear();
            l.Guest.OnLinkLost();
            t.Check(l.GuestRejected.Any(x => x.Reason == RejectReason.Abandoned), "pending guest request is abandoned when the link drops");

            l.Host.OnLinkLost();
            var rejoin = new WorldSync(ActorSlot.Guest, w => l.ToHost.Add(w.CopyData()));
            var rejoinMarkers = new MarkerHandler();
            rejoin.Register(CommandTypes.DiagnosticMarker, rejoinMarkers);
            rejoin.RegisterState(rejoinMarkers);
            l.Host.Linked = rejoin.Linked = true;
            rejoin.BeginHold(l.Host.CapturePosition());
            rejoin.Adopt(out _);
            rejoin.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("after-rejoin").ToBytes(), out var op);
            t.Check(op.Sequence == 1, "a rejoined guest starts numbering at 1 again");
            foreach (var m in l.ToHost.ToList()) FakeLink.Deliver(l.Host, m);
            l.ToHost.Clear();
            t.Check(l.HostMarkers.Texts.Last() == "after-rejoin", "host accepts the rejoined guest's sequence 1 (not a duplicate)");
        }
    }
}
