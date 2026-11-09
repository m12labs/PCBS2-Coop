using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

/// <summary>Phase 8: compressed world state, save layouts and versions, hidden backups and the rejoin position.</summary>
internal static class PersistenceTests
{
    public static void Run(Checker t)
    {
        States(t);
        Layouts(t);
        Backups(t);
        Spawn(t);
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    private static void States(Checker t)
    {
        t.Section("Persistence: compressed world state");
        // Like the real objects state of about 120 built PCs: many similar binding strings.
        var w = new NetDataWriter();
        for (var i = 0; i < 120 * 165; i++) { w.Put($"m_motherboard/m_slots[{i % 24}]/screw{i % 7}/m_holes[{i % 5}]/m_target"); w.Put("ScrewSlotInstance"); w.Put((ulong)i); }
        var state = w.CopyData();
        var packed = Protocol.PackState(state);
        t.Check(state.Length > 1024 * 1024 && packed.Length < Protocol.MaxPackedStateBytes && Protocol.UnpackState(packed).SequenceEqual(state),
            $"a world state larger than the old 1 MB limit packs ({state.Length} -> {packed.Length} bytes) and comes back exactly");
        var noise = new byte[Protocol.MaxPackedStateBytes + 4096]; new Random(8).NextBytes(noise);
        t.Check(Throws(() => Protocol.PackState(noise)), "a state that stays too large after compression is refused with a reason");
        t.Check(Throws(() => Protocol.PackState(new byte[Protocol.MaxModStateBytes + 1])), "an expanded state over the limit is refused");
        var full = Protocol.PackState(new byte[Protocol.MaxModStateBytes]);
        var bigger = Deflate(new byte[Protocol.MaxModStateBytes + 64 * 1024]);
        t.Check(Protocol.UnpackState(full).Length == Protocol.MaxModStateBytes && Throws(() => Protocol.UnpackState(bigger)),
            "expansion stops at the limit (a small file can't unpack into an unbounded state)");
        t.Check(Throws(() => Protocol.UnpackState(new byte[] { 1, 2, 3, 4, 5 })), "damaged compressed state is refused");
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>The layout 0.16–0.20 wrote: the world state stored as is.</summary>
    private static byte[] OldLayout(string version, byte[] native, byte[] state)
    {
        var w = new NetDataWriter();
        w.Put(0x3152494150324350UL); w.Put(version); w.Put(native.Length); w.Put(SHA256.HashData(native));
        w.Put(1U); w.Put(5UL); w.Put(77UL); w.Put(state.Length); w.Put(state); w.Put(0);
        return w.CopyData();
    }

    private static void Layouts(Checker t)
    {
        t.Section("Persistence: save layouts and versions");
        var native = new byte[] { 3, 1, 4, 1, 5 }; var state = new byte[] { 0, 0, 0, 0 };
        var meta = CoopSavePair.Create(native, new SnapshotPosition(1, 5, 77, state), Array.Empty<CarrierPayload>());
        var encoded = CoopSavePair.Encode(meta);
        t.Check(BitConverter.ToUInt64(encoded, 0) == 0x3352494150324350UL && CoopSavePair.Decode(encoded).Position.State.SequenceEqual(state),
            "this version writes the compressed layout (with its campaign, 0.58) and reads it back");
        var old = CoopSavePair.Decode(OldLayout("0.20", native, state));
        t.Check(old.Position.State.SequenceEqual(state) && old.Position.Revision == 5, "a 0.20 save (uncompressed layout) still loads");
        string Message(string version)
        {
            try { CoopSavePair.Decode(OldLayout(version, native, state)); return ""; }
            catch (InvalidDataException e) { return e.Message; }
        }
        var older = Message("0.15"); var newer = Message("0.99"); var junk = Message("x\u0001y");
        t.Check(older.Contains("0.15") && older.Contains("0.16 onwards"), $"a 0.15 save is refused with a clear reason ({older})");
        t.Check(newer.Contains("newer") && newer.Contains("0.99"), $"a save from a newer mod version says to update ({newer})");
        t.Check(junk.Contains("unknown"), "an unreadable version string is not echoed");
        var foreign = (byte[])encoded.Clone(); foreign[0] ^= 0xFF;
        t.Check(Throws(() => CoopSavePair.Decode(foreign)), "a file that isn't a co-op save is refused");
        t.Check(CoopSavePair.VersionProblem(ModVersion.Value) == null && CoopSavePair.VersionProblem("0.16") == null, "0.16 to this version load");
    }

    private static void Backups(Checker t)
    {
        t.Section("Persistence: hidden backups of co-op saves");
        var dir = Path.Combine(Path.GetTempPath(), "pcbs2-backup-test-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(dir, "Backup");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "World--auto.binary.zip");
            var kept = Path.Combine(backups, "World--auto.binary.zip");
            void Save(byte[] native, ulong revision)
            {
                File.WriteAllBytes(path, native);
                CoopSavePair.WriteSidecar(path, native, CoopSavePair.Create(native, new SnapshotPosition(1, revision, 9, new byte[] { 0, 0, 0, 0 }), Array.Empty<CarrierPayload>()));
            }
            ulong Revision(string p) => CoopSavePair.ReadSidecar(p).Position.Revision;
            t.Check(!CoopSaveBackups.Keep(path, backups), "nothing to keep before the first save");
            Save(new byte[] { 1, 1, 1 }, 1);
            t.Check(CoopSaveBackups.Keep(path, backups) && Revision(kept) == 1, "the pair about to be overwritten is kept");
            Save(new byte[] { 2, 2, 2 }, 2);
            t.Check(!CoopSaveBackups.Restore(path, backups, out var fine) && fine == null && Revision(path) == 2, "a readable save is left alone");
            // The game wrote the next save, but the game closed before its .coop was written.
            CoopSaveBackups.Keep(path, backups);
            File.WriteAllBytes(path, new byte[] { 3, 3, 3, 3 });
            t.Check(!CoopSaveBackups.Readable(path, out var why) && why != null, "a save cut off between the game's file and its pair doesn't read");
            t.Check(!CoopSaveBackups.Keep(path, backups) && Revision(kept) == 2, "an unreadable save never replaces the good backup");
            t.Check(CoopSaveBackups.Restore(path, backups, out var problem) && problem != null && Revision(path) == 2 &&
                    File.ReadAllBytes(kept + CoopSaveBackups.UnreadableSuffix).Length == 4,
                "the backup goes back in its place; the unreadable file is kept beside the backup");
            File.Delete(path + CoopSavePair.Extension);
            t.Check(CoopSaveBackups.Restore(path, backups, out _) && CoopSaveBackups.Readable(path, out _), "a save that lost its .coop is restored too");
            var lone = Path.Combine(dir, "Other.binary.zip"); File.WriteAllBytes(lone, new byte[] { 9 });
            t.Check(!CoopSaveBackups.Restore(lone, backups, out _) && File.Exists(lone), "without a backup, an unreadable save stays as it is");
            var raw = path[..^4];
            var partial = new byte[] { 4, 5, 6 };
            File.WriteAllBytes(raw, partial);
            var aside = Path.Combine(backups, Path.GetFileName(raw) + CoopSaveBackups.UnreadableSuffix);
            t.Check(CoopSaveBackups.Restore(raw, backups, out _) && !File.Exists(raw) && File.Exists(aside) &&
                    File.ReadAllBytes(aside).SequenceEqual(partial) && Revision(path) == 2,
                "a raw write interrupted before zipping is set aside and the previous pair stays intact");
            t.Check(!CoopSaveBackups.Restore(raw, backups, out _), "an interrupted raw save is reported only once");
            var rawOnly = Path.Combine(dir, "Unpaired.binary");
            File.WriteAllBytes(rawOnly, partial);
            t.Check(!CoopSaveBackups.Restore(rawOnly, backups, out _) && File.Exists(rawOnly),
                "a raw save without a readable previous pair is left alone");
            CoopSaveBackups.Delete("World--auto.binary.zip", backups);
            t.Check(Directory.GetFiles(backups).Length == 0, "deleting a world's save deletes its backup");
            t.Check(!Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Any(f => f.Contains(".tmp-")), "no temporary files are left behind");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Spawn(Checker t)
    {
        t.Section("Persistence: a rejoining guest's position");
        using var p = new SyncTests.Pair();
        var at = new List<PlayerState>();
        p.Guest.SpawnReceived += at.Add;
        var pose = new PlayerState(1.5f, 0.25f, -3f, 90f, true);
        t.Check(!p.Host.SendSpawn(pose), "nothing is sent before the guest is in the world");
        t.Check(p.Admit(), "guest admitted");
        t.Check(!p.Guest.SendSpawn(pose), "only the host sends a position");
        t.Check(!p.Host.SendSpawn(pose with { X = float.NaN }), "an invalid position isn't sent");
        t.Check(p.Host.SendSpawn(pose) && p.Pump(() => at.Count == 1) && at[0] == pose, "the guest gets where it stood when it left");
    }
}
