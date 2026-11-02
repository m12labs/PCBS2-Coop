using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Case designs from the case-modding table (phase 11, owner decision 1: result only). The game keeps a case's paint,
/// stickers and theme in a world-wide list (<c>CustomCaseManager.CustomisationList</c>) under a number from a per-game
/// counter; the PC record only names that number (<c>CustomisationID</c>, <c>isCustomised</c>). Two games could hand out
/// the same number, so the host gives out the number while it accepts the result, and each game moves a different PC
/// that happens to use it to a fresh number of its own. The other game puts the design in its list and reloads that PC
/// on the table to show it.
/// </summary>
internal sealed partial class NativeToolBenches
{
    private const int MaxDesignBytes = 8 * 1024 * 1024;

    private static CustomCaseManager Designs => CustomCaseManager.Get() ?? throw new InvalidDataException("The case design list isn't loaded.");

    /// <summary>The live record of a PC: the one its bench shows, else the bound record.</summary>
    private ComputerSave LiveRecord(NetObjectId id) => _coop.Objects.FindBench(id)?.GetComputer(false) ?? _coop.Objects.RecordFor(id);

    private (NetObjectId Id, ComputerSave Record) PcOn(BenchToolSlot table)
    {
        var mesh = table?.Slot?.GetCaseInSlot();
        var id = _coop.Objects.IdOf(mesh);
        return id.IsNone ? (default, null) : (id, LiveRecord(id));
    }

    private static CustomCaseSaveContainer DesignOf(ComputerSave record)
    {
        if (record == null || !record.isCustomised) return null;
        foreach (var c in Designs.CustomisationList) if (c != null && c.CustomisationID == record.CustomisationID) return c;
        return null;
    }

    private static byte[] WriteDesign(CustomCaseSaveContainer design)
    {
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, design);
            if (stream.Length is < 1 or > MaxDesignBytes) throw new InvalidDataException("the case design is too large to share");
            return stream.ToArray();
        }
        finally { stream.Dispose(); }
    }

    private static CustomCaseSaveContainer ReadDesign(byte[] bytes)
    {
        var stream = new Il2CppSystem.IO.MemoryStream(bytes);
        try
        {
            var design = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream).TryCast<CustomCaseSaveContainer>();
            if (design == null || stream.Position != stream.Length) throw new InvalidDataException("Invalid case design.");
            return design;
        }
        finally { stream.Dispose(); }
    }

    /// <summary>The design's hash on the case now on that table ("" when it has none), or null when it can't be read.</summary>
    private string DesignHash(BenchToolSlot table)
    {
        try { return DesignOf(PcOn(table).Record) is { } d ? Convert.ToHexString(SHA256.HashData(WriteDesign(d))) : ""; }
        catch (Exception e) { Plugin.Logger.LogWarning("[tools] the case design at the start couldn't be read: " + e.Message); return null; }
    }

    /// <summary>The finished design of the case on the session's table (PC, this game's design number, the design),
    /// compressed; null when the case has no design or it didn't change.</summary>
    private byte[] DesignResult(Session s)
    {
        var (id, record) = s.Pc.IsNone ? PcOn(s.Table) : (s.Pc, LiveRecord(s.Pc));
        if (id.IsNone || record == null) throw new InvalidDataException("the PC on the case-modding table isn't in the shared world");
        var design = DesignOf(record);
        if (design == null) return null;
        // Leaving the table always saves a design, even an empty one for a case that had none: nothing to share then.
        if ((design.SavedPrims == null || design.SavedPrims.Count == 0) && s.DesignBefore == "") return null;
        var bytes = WriteDesign(design);
        if (s.DesignBefore != null && s.DesignBefore == Convert.ToHexString(SHA256.HashData(bytes))) return null;
        var w = new NetDataWriter(); id.Write(w); w.Put(record.CustomisationID); w.Put(bytes.Length); w.Put(bytes);
        var packed = Deflate(w.Data, w.Length);
        if (packed.Length > ToolBenchWorld.MaxBytes)
            throw new InvalidDataException($"the case design is too large to share ({packed.Length / 1024} KB compressed, the limit is {ToolBenchWorld.MaxBytes / 1024} KB)");
        Plugin.Logger.LogInfo($"[tools] case design of PC {id}: {design.SavedPrims?.Count ?? 0} layer(s), {bytes.Length} bytes ({packed.Length} compressed).");
        return packed;
    }

    private static (NetObjectId Pc, int Proposed, byte[] Design) ReadResult(byte[] compressed)
    {
        var r = new NetDataReader(Inflate(compressed, MaxDesignBytes + 64));
        var pc = NetObjectId.Read(r); var proposed = r.GetInt(); var length = r.GetInt();
        if (pc.IsNone || length is < 1 or > MaxDesignBytes || length != r.AvailableBytes) throw new InvalidDataException("Invalid case design result.");
        var bytes = new byte[length]; r.GetBytes(bytes, length);
        return (pc, proposed, bytes);
    }

    private List<(NetObjectId Id, ComputerSave Record)> Records()
    {
        var result = new List<(NetObjectId, ComputerSave)>();
        foreach (var pc in _coop.Objects.Model.Pcs)
            if (LiveRecord(pc.Id) is { } record) result.Add((pc.Id, record));
        return result;
    }

    /// <summary>Host: the design number that PC gets. It keeps its number if no other PC here uses it; otherwise it gets
    /// one above every number this game knows and the editor's own.</summary>
    public int DesignId(BenchUse use, byte[] result)
    {
        try
        {
            var (pc, proposed, _) = ReadResult(result);
            var records = Records();
            var mine = records.FirstOrDefault(x => x.Id == pc).Record;
            var used = records.Where(x => x.Id != pc && x.Record.isCustomised).Select(x => x.Record.CustomisationID).ToHashSet();
            if (mine != null && mine.isCustomised && mine.CustomisationID > 0 && !used.Contains(mine.CustomisationID)) return mine.CustomisationID;
            var manager = Designs;
            var top = Math.Max(manager.m_lastID, proposed);
            foreach (var c in manager.CustomisationList) if (c != null) top = Math.Max(top, c.CustomisationID);
            foreach (var x in records) top = Math.Max(top, x.Record.CustomisationID);
            return top + 1;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[tools] couldn't pick a case design number; the editor's own is kept: " + e.Message);
            return 0;
        }
    }

    /// <summary>Another PC on this game that uses <paramref name="id"/> moves to a fresh number (with its design).</summary>
    private void Free(CustomCaseManager manager, int id, ComputerSave keep)
    {
        foreach (var (other, record) in Records())
        {
            if (record.Pointer == keep.Pointer || !record.isCustomised || record.CustomisationID != id) continue;
            var fresh = manager.GetNewUID();
            foreach (var c in manager.CustomisationList)
                if (c != null && c.CustomisationID == id) { c.CustomisationID = fresh; break; }
            record.CustomisationID = fresh;
            Plugin.Logger.LogInfo($"[tools] PC {other}'s case design moves to number {fresh} here ({id} goes to the shared design).");
        }
    }

    private void DesignLeft(in MutationContext ctx, BenchUse use, byte[] result, int design)
    {
        if (result == null)
        {
            if (!ctx.FromLocalPlayer) Plugin.Logger.LogInfo($"[tools] the other player left the case-modding table {use.Bench} without changing the design.");
            return;
        }
        var local = ctx.FromLocalPlayer;
        NetObjectId pc = default;
        try
        {
            var (id, proposed, bytes) = ReadResult(result);
            pc = id;
            var number = design > 0 ? design : proposed;
            if (number <= 0) throw new InvalidDataException("the case design has no number");
            var record = LiveRecord(pc) ?? throw new InvalidDataException($"PC {pc} isn't on this game");
            var manager = Designs;
            NativeProjection.Run(() =>
            {
                if (manager.m_lastID < number) manager.m_lastID = number;
                Free(manager, number, record);
                var list = manager.CustomisationList;
                if (local)
                {
                    if (record.CustomisationID == number) return;
                    if (DesignOf(record) is { } mine) mine.CustomisationID = number;
                    Plugin.Logger.LogInfo($"[tools] PC {pc}'s case design takes the shared number {number} (was {record.CustomisationID} here).");
                    record.CustomisationID = number;
                    record.isCustomised = true;
                    return;
                }
                var incoming = ReadDesign(bytes);
                incoming.CustomisationID = number;
                var old = record.isCustomised ? record.CustomisationID : 0;
                var oldShared = old > 0 && old != number && Records().Any(x => x.Record.Pointer != record.Pointer && x.Record.isCustomised && x.Record.CustomisationID == old);
                for (var i = list.Count - 1; i >= 0; i--)
                    if (list[i] == null || list[i].CustomisationID == number || (old > 0 && !oldShared && list[i].CustomisationID == old)) list.RemoveAt(i);
                list.Add(incoming);
                record.isCustomised = true;
                record.CustomisationID = number;
            });
            if (local) return;
            Plugin.Logger.LogInfo($"[tools] the other player's case design for PC {pc} is here (number {number}, {bytes.Length} bytes).");
            var fact = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pc);
            var bench = _coop.Objects.FindBench(pc);
            if (fact == null || bench == null) { Plugin.Logger.LogInfo($"[tools] PC {pc} isn't on a bench here; it shows the design when it's next loaded."); return; }
            // A copy: the reload destroys the bench's PC (and its record) first.
            _coop.Objects.ReloadOnBench(fact, bench, NativePcCodec.Read(NativePcCodec.Write(record)), "the other player finished case modding the PC this player was looking at; leaving its view.");
        }
        catch (Exception e)
        {
            if (local) Plugin.Logger.LogWarning($"[tools] taking the shared case design number failed: {e.Message}");
            else _coop.ProjectionFailed($"The other player's case design{(pc.IsNone ? "" : $" for PC {pc}")} could not be shown on this game", e);
        }
    }
}
