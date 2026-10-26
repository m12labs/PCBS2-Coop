using LiteNetLib.Utils;
using PCBS2Coop.Network;

internal static class ActivityTests
{
    public static void Run(Checker t)
    {
        t.Section("Activity: shared work, resource ownership and lifecycle");
        var l = new FakeLink(); var objects = new ObjectWorld(); var guestObjects = new ObjectWorld();
        var h = new ActivityWorld(objects); var g = new ActivityWorld(guestObjects);
        l.Host.RegisterState(objects); l.Guest.RegisterState(guestObjects);
        l.Host.Register(CommandTypes.ActorActivity, h); l.Guest.Register(CommandTypes.ActorActivity, g);
        l.Host.RegisterState(h); l.Guest.RegisterState(g);
        l.Host.BeginGeneration("activity");
        var host = PlayerId.New(); var guest = PlayerId.New();
        var origin = new InstanceProvenance(InstanceOrigin.Baseline, host, default);
        objects.Replace(Guid.NewGuid(), 10, new[] { new LocationFact(new(1), "A"), new LocationFact(new(2), "B") },
            new[] { new PcFact(new(3), origin, PcLocation.Bench("A"), new[] { new PartFact(new(4), "case", "PartInstance"), new PartFact(new(5), "ram", "PartInstance") },
                new[] { new PartLink("caseID", new(4)), new PartLink("ram[0]", new(5)) }),
                new PcFact(new(6), origin, PcLocation.Bench("B"), new[] { new PartFact(new(7), "case", "PartInstance") }, new[] { new PartLink("caseID", new(7)) }) },
            new[] { new NetObjectId(8), new NetObjectId(9) });
        OperationId Send(WorldSync w, byte[] bytes) { w.Submit(CommandTypes.ActorActivity, bytes, out var op); l.Flush(); return op; }
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host)); Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest)); l.Join();
        var baseline = objects.ToBytes();
        Send(l.Host, ActivityWorld.Work(h.Get(ActorSlot.Host)!, new(3)));
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(6)));
        t.Check(h.Get(ActorSlot.Host)!.WorkingPc == new NetObjectId(3) && h.Get(ActorSlot.Guest)!.WorkingPc == new NetObjectId(6) && l.InStep, "actors work on different benches independently");
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(3)));
        t.Check(h.Actors.All(x => x.WorkingPc == new NetObjectId(3)), "both actors may work on the same PC without a whole-PC edit lock");
        var hOp = Send(l.Host, ActivityWorld.Reserve(h.Get(ActorSlot.Host)!, new(3), ActivityOperation.Edit, new NetObjectId(4)));
        var gOp = Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(5)));
        t.Check(h.Actors.All(x => x.Pending != null) && Bytes(h).SequenceEqual(Bytes(g)), "non-conflicting part claims on one PC both acquire and replicate");
        t.Check(h.Owns(ActorSlot.Guest, h.Get(ActorSlot.Guest)!.Epoch, gOp, new(3), ActivityOperation.Edit, new NetObjectId(5)), "commit seam validates actor epoch, acquisition token and exact resources");
        t.Check(!h.Owns(ActorSlot.Guest, h.Get(ActorSlot.Guest)!.Epoch, hOp, new(3), ActivityOperation.Edit, new NetObjectId(5)), "another actor's operation cannot authorize a commit");
        Send(l.Guest, ActivityWorld.Release(g.Get(ActorSlot.Guest)!, gOp));
        var before = l.Host.Revision;
        Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(4)));
        t.Check(l.Host.Revision == before && h.Get(ActorSlot.Guest)!.Pending == null, "contested part has one accepted owner; refusal leaves state unchanged");
        Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(4), new NetObjectId(5)));
        t.Check(h.Get(ActorSlot.Guest)!.Pending == null, "overlapping multi-resource claim fails atomically without partial ownership");
        t.Check(h.MovementBlock(new(3)) != null && h.MovementBlock(new(6)) == null, "active PC cannot move/store/delete/sell while independent PC stays available");
        Send(l.Host, ActivityWorld.Release(h.Get(ActorSlot.Host)!, hOp));
        Send(l.Host, ActivityWorld.Reserve(h.Get(ActorSlot.Host)!, new(3), ActivityOperation.Move));
        t.Check(h.Get(ActorSlot.Host)!.Pending == null, "movement also refuses the requester's own active work");
        var staleWork = ActivityWorld.Work(g.Get(ActorSlot.Guest)!, default);
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(6)));
        Send(l.Guest, staleWork);
        t.Check(h.Get(ActorSlot.Guest)!.WorkingPc == new NetObjectId(6), "delayed cancellation cannot clear newer work activity");
        Send(l.Host, ActivityWorld.Work(h.Get(ActorSlot.Host)!, default));
        var move = Send(l.Host, ActivityWorld.Reserve(h.Get(ActorSlot.Host)!, new(3), ActivityOperation.Move));
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(3)));
        t.Check(h.Get(ActorSlot.Host)!.Pending?.Token == move && h.Get(ActorSlot.Guest)!.WorkingPc == new NetObjectId(6), "pending movement prevents later work entry");
        Send(l.Host, ActivityWorld.Release(h.Get(ActorSlot.Host)!, move));
        t.Check(h.MovementBlock(new(3)) == null && objects.ToBytes().SequenceEqual(baseline), "cancel releases ownership without changing committed object facts");

        var oldActor = g.Get(ActorSlot.Guest)!; var oldEpoch = oldActor.Epoch;
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default));
        t.Check(h.Get(ActorSlot.Guest) == null && h.MovementBlock(new(6)) == null, "host-ordered departure releases all guest work and reservations");
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, guest));
        Send(l.Guest, ActivityWorld.Work(oldActor, new(3)));
        t.Check(h.Get(ActorSlot.Guest)!.Epoch != oldEpoch && h.Get(ActorSlot.Guest)!.WorkingPc.IsNone, "same player rejoining gets a fresh epoch; old actions are refused");
        var rev = l.Host.Revision;
        Send(l.Guest, ActivityWorld.Presence(ActorSlot.Host, guest));
        t.Check(l.Host.Revision == rev && h.Get(ActorSlot.Host)!.Player == host, "guest cannot forge host presence or release another actor");
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(999)));
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(8)));
        t.Check(l.Host.Revision == rev, "unknown and retired PCs cannot acquire activity");
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, new(3)));
        Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(7)));
        Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(5), new NetObjectId(5)));
        t.Check(h.Get(ActorSlot.Guest)!.Pending == null, "cross-PC and duplicate resource IDs are refused");
        var malformedBefore = l.GuestRejected.Count;
        Send(l.Guest, new byte[] { 255 });
        var malformed = new NetDataWriter(); malformed.Put((byte)3); malformed.Put(g.Get(ActorSlot.Guest)!.Epoch); malformed.Put(g.Get(ActorSlot.Guest)!.Version);
        malformed.Put(3UL); malformed.Put((byte)1); malformed.Put(-1);
        Send(l.Guest, malformed.CopyData());
        Send(l.Guest, ActivityWorld.Work(g.Get(ActorSlot.Guest)!, default).Concat(new byte[] { 9 }).ToArray());
        t.Check(l.GuestRejected.Count == malformedBefore + 3 && l.GuestRejected.TakeLast(3).All(x => x.Reason == RejectReason.Malformed), "unknown actions, negative resource count and trailing request bytes receive malformed outcomes");
        var release = Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(5)));
        var duplicateCancel = ActivityWorld.Release(g.Get(ActorSlot.Guest)!, release);
        Send(l.Guest, duplicateCancel);
        var replacement = Send(l.Guest, ActivityWorld.Reserve(g.Get(ActorSlot.Guest)!, new(3), ActivityOperation.Edit, new NetObjectId(5)));
        Send(l.Guest, duplicateCancel);
        t.Check(h.Get(ActorSlot.Guest)!.Pending?.Token == replacement, "old release cannot clear a replacement operation");
        var saved = Bytes(h); var roundtrip = new ActivityWorld(objects); roundtrip.Read(new(saved));
        t.Check(saved.SequenceEqual(Bytes(roundtrip)), "snapshot/save codec retains both actors and exact operation tokens");
        t.Check(Throws(() => roundtrip.Read(new(saved[..^1]))) && Bytes(roundtrip).SequenceEqual(saved), "truncated activity read does not publish a partial candidate");
        t.Check(Throws(() => roundtrip.Read(new(saved.Concat(new byte[] { 1 }).ToArray()))), "trailing activity state is refused");
        l.MarkSnapshot();
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default));
        t.Check(l.Guest.Adopt(out _) && Bytes(h).SequenceEqual(Bytes(g)) && l.InStep, "departure after snapshot mark replays before admission without stale claims");
        var position = l.Host.CapturePosition(); l.Host.BeginGeneration("resume"); l.Host.RestoreState(position.State);
        Send(l.Host, ActivityWorld.Presence(ActorSlot.Host, host)); Send(l.Host, ActivityWorld.Presence(ActorSlot.Guest, default));
        t.Check(h.Get(ActorSlot.Host)!.WorkingPc.IsNone && h.Actors.All(a => a.Pending == null) && baseline.SequenceEqual(objects.ToBytes()), "host resume clears transient activity and keeps committed objects unchanged");
        l.Host.BeginGeneration("replace");
        t.Check(h.Actors.Count == 0 && h.NextEpoch == 1, "generation replacement drops every actor reservation");
    }
    private static byte[] Bytes(ActivityWorld state) { var w = new NetDataWriter(); state.Write(w); return w.CopyData(); }
    private static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
}
