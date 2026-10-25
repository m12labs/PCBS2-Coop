using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Admission from a host snapshot and the snapshot transfer itself, in memory.</summary>
internal static class SnapshotTests
{
    public static void Run(Checker t)
    {
        t.Section("Admission: late join from a snapshot");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w");
            l.Mark(l.Host, "before-1"); l.Mark(l.Host, "before-2");
            l.Flush();
            t.Check(!l.Guest.Admitted && l.GuestMarkers.Applied.Count == 0, "connected guest is not in the world and dropped the earlier changes");
            l.Join();
            t.Check(l.Guest.Admitted && l.InStep, "late joiner adopts the host's position and mod state (markers made before it joined)");
            l.Mark(l.Guest, "after"); l.Flush();
            t.Check(l.InStep && l.GuestMarkers.Texts.SequenceEqual(new[] { "before-1", "before-2", "after" }), "changes after admission apply normally");
        }

        t.Section("Admission: changes while the snapshot loads");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w");
            l.Mark(l.Host, "a"); l.Flush();
            l.MarkSnapshot();                       // host takes the save here (revision 1)...
            var staleBefore = l.Guest.Stats.StaleDropped;
            l.Mark(l.Host, "b"); l.Mark(l.Host, "c"); // ...and keeps changing the world while the guest loads
            l.DeliverToGuest();
            t.Check(l.Guest.Holding && l.Guest.Stats.Held == 2 && l.GuestMarkers.Applied.Count == 0, "changes after the mark are held, not applied, while loading");
            t.Check(!l.Guest.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("x").ToBytes(), out _), "loading guest can't submit");
            t.Check(l.Guest.Adopt(out _), "guest finishes loading and adopts the snapshot");
            t.Check(l.InStep && l.GuestMarkers.Texts.SequenceEqual(new[] { "a", "b", "c" }), "held changes applied once, in order, after the snapshot");
            t.Check(l.Guest.Divergence == null && l.Guest.Stats.StaleDropped == staleBefore, "no gap, no duplicate");

            // A change that is already part of the snapshot (sent before the mark, delivered late) is dropped, not applied twice.
            var m = new FakeLink();
            m.Host.BeginGeneration("w");
            m.Mark(m.Host, "x");
            var early = m.ToGuest.ToList(); m.ToGuest.Clear();
            var p = m.Host.CapturePosition();
            m.Guest.BeginHold(p);
            m.Guest.Adopt(out _);
            foreach (var msg in early) FakeLink.Deliver(m.Guest, msg);
            t.Check(m.GuestMarkers.Texts.Count(x => x == "x") == 1 && m.Guest.Divergence == null, "a change included in the snapshot is not applied a second time");
        }

        t.Section("Admission: failures and resets");
        {
            var l = new FakeLink();
            l.Host.BeginGeneration("w"); l.Flush();
            var stranger = new WorldSync(ActorSlot.Host, _ => { });
            stranger.RegisterState(new NamedState("Other"));
            stranger.BeginGeneration("x");
            l.Guest.BeginHold(stranger.CapturePosition());
            t.Check(!l.Guest.Adopt(out var err) && err.Contains("unknown world state") && !l.Guest.Admitted, $"snapshot with state this guest doesn't know is refused ({err})");

            var bad = new SnapshotPosition(1, 0, 0, new byte[] { 1, 2 });
            l.Guest.BeginHold(bad);
            t.Check(!l.Guest.Adopt(out err) && !l.Guest.Admitted, "unreadable mod state is refused without entering the world");

            l.Join();
            l.Host.BeginGeneration("another world"); l.Flush();
            t.Check(l.Replaced.Count == 1 && !l.Guest.Admitted, "host starting another world after admission takes the guest out (WorldReplaced)");

            var h = new FakeLink();
            h.Host.BeginGeneration("w"); h.Flush();
            h.MarkSnapshot();
            for (var i = 0; i < 10_001; i++) h.Mark(h.Host, "m");
            h.DeliverToGuest();
            t.Check(!h.Guest.Holding && h.Divergences.Any(d => d.Contains("while loading")), "too many changes during a load is reported instead of buffering without limit");

            var g = new FakeLink();
            g.Host.BeginGeneration("w"); g.Flush(); g.MarkSnapshot();
            g.Guest.OnLinkLost();
            t.Check(!g.Guest.Holding && !g.Guest.Admitted, "a dropped link cancels the pending snapshot");
        }

        t.Section("Snapshot transfer: sender and receiver");
        {
            var wire = new List<byte[]>();
            var sender = new SnapshotSender(w => wire.Add(w.CopyData()));
            var receiver = new SnapshotReceiver();
            var file = new byte[100_000];
            new Random(7).NextBytes(file);
            var pos = new SnapshotPosition(3, 9, 12345, new byte[] { 0 });

            var id = sender.Mark(SnapshotPurpose.Load, pos);
            t.Check(wire.Count == 1 && sender.LoadInProgress, "load mark goes out immediately");
            sender.Pump(1 << 20);
            t.Check(wire.Count == 1, "nothing more is sent until the save file exists");
            sender.SetFile(id, "coopworld.binary.zip", file);
            sender.Pump(40_000);
            t.Check(wire.Count == 4, "file header plus chunks within the byte budget (32 KiB chunks)");
            sender.Pump(1 << 20);
            t.Check(!sender.LoadInProgress && wire.Count == 2 + (file.Length + Protocol.SnapshotChunkBytes - 1) / Protocol.SnapshotChunkBytes, "whole file sent, transfer finished");

            Snapshot? got = null;
            string? error = null;
            foreach (var msg in wire) Feed(receiver, msg, ref got, ref error);
            t.Check(error == null && got != null && got.File.AsSpan().SequenceEqual(file) && got.Position is { Generation: 3, Revision: 9, ChainHash: 12345 } && got.Position.State.SequenceEqual(pos.State) && got.FileName == "coopworld.binary.zip",
                "receiver reassembles the file, checks the hash and returns the snapshot position");

            // Damaged chunk.
            got = null; error = null;
            var damaged = wire.Select(m => (byte[])m.Clone()).ToList();
            damaged[3][^10] ^= 0xFF;
            foreach (var msg in damaged) Feed(receiver, msg, ref got, ref error);
            t.Check(got == null && error != null && error.Contains("checksum"), $"damaged data is refused ({error})");

            // Out of order chunk.
            got = null; error = null;
            var swapped = wire.ToList();
            (swapped[2], swapped[3]) = (swapped[3], swapped[2]);
            foreach (var msg in swapped) { Feed(receiver, msg, ref got, ref error); if (error != null) break; }
            t.Check(got == null && error != null && error.Contains("out of order"), $"chunk out of order is refused ({error})");

            // Unsafe names and sizes, forged on the wire.
            foreach (var name in new[] { "..\\..\\evil.binary", "a/b.binary", "x.exe", "", "freebuild.binary.zip.exe", new string('a', 49) + ".binary" })
            {
                receiver.Reset();
                got = null; error = null;
                Feed(receiver, wire[0], ref got, ref error);
                Feed(receiver, Header(1, name, 10), ref got, ref error);
                t.Check(error != null && error.Contains("file name"), $"file name '{name}' refused");
            }
            receiver.Reset(); got = null; error = null;
            Feed(receiver, wire[0], ref got, ref error);
            Feed(receiver, Header(1, "ok.binary", Protocol.MaxSnapshotBytes + 1), ref got, ref error);
            t.Check(error != null && error.Contains("limit"), "oversized snapshot refused before allocating it");

            receiver.Reset(); got = null; error = null;
            Feed(receiver, Header(99, "ok.binary", 10), ref got, ref error);
            t.Check(error != null, "file header without a mark is refused");

            t.Check(Protocol.IsValidSnapshotName("freebuild.binary") && Protocol.IsValidSnapshotName("autofreebuild.binary.zip") && Protocol.IsValidSnapshotName("coop-world_2.binary.zip"),
                "the game's own save names are accepted");
        }

        t.Section("Snapshot transfer: ordering between transfers");
        {
            var wire = new List<byte[]>();
            var sender = new SnapshotSender(w => wire.Add(w.CopyData()));
            var copy = sender.Mark(SnapshotPurpose.Copy, null);
            t.Check(wire.Count == 0, "a copy mark waits for its turn");
            sender.SetFile(copy, "freebuild.binary.zip", new byte[50_000]);
            sender.Pump(Protocol.SnapshotChunkBytes);
            var sentBefore = wire.Count;
            var load = sender.Mark(SnapshotPurpose.Load, new SnapshotPosition(1, 0, 0, Array.Empty<byte>()));
            var types = wire.Skip(sentBefore).Select(m => (MessageType)m[0]).ToList();
            t.Check(types.SequenceEqual(new[] { MessageType.SnapshotAbort, MessageType.SnapshotMark }), "a load snapshot aborts the copy in flight, then marks immediately");
            sender.SetFile(load, "coopworld.binary.zip", new byte[10]);
            sender.Pump(1 << 20);
            t.Check(sender.Queued == 0, "load transfer completes");

            var r = new SnapshotReceiver();
            Snapshot? got = null; string? error = null;
            foreach (var m in wire)
            {
                if ((MessageType)m[0] == MessageType.SnapshotAbort) { r.Reset(); continue; }
                Feed(r, m, ref got, ref error);
            }
            t.Check(error == null && got is { Purpose: SnapshotPurpose.Load, FileName: "coopworld.binary.zip" }, "guest ends up with the load snapshot only");
        }
    }

    private static void Feed(SnapshotReceiver r, byte[] msg, ref Snapshot? got, ref string? error)
    {
        var reader = new NetDataReader(msg);
        var type = (MessageType)reader.GetByte();
        switch (type)
        {
            case MessageType.SnapshotMark: r.OnMark(reader, out _, out _); break;
            case MessageType.SnapshotFile: error ??= r.OnFile(reader); break;
            case MessageType.SnapshotChunk:
                var e = r.OnChunk(reader, out var done);
                error ??= e;
                if (done != null) got = done;
                break;
        }
    }

    private static byte[] Header(uint id, string name, int size)
    {
        var w = new NetDataWriter();
        w.Put((byte)MessageType.SnapshotFile);
        w.Put(id);
        w.Put(name, Protocol.MaxShortString);
        w.Put(size);
        w.Put(new byte[32]);
        return w.CopyData();
    }

    private sealed class NamedState : IWorldState
    {
        public NamedState(string name) => Name = name;
        public string Name { get; }
        public void Reset() { }
        public void Write(NetDataWriter w) => w.Put(1);
        public void Read(NetDataReader r) => r.GetInt();
    }
}
