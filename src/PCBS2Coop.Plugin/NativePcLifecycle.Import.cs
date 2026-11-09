using System.Security.Cryptography;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Phase 12 (owner decision 6, November 2, 2026): importing a PC file from the cupboard. The cupboard lists this
/// player's own PC files (<c>Storage.Populate</c>, each a <c>LocalPcSave</c>: the PC record and its case design); they
/// aren't in the shared world, so picking one up imports it. This game sends the record and the design as content, then
/// asks for an import; the host gives the new PC its IDs and both games put it in this player's hands, the design under
/// a number of each game's own. Exporting stays the player's own file.
/// </summary>
internal sealed partial class NativePcLifecycle
{
    private const int MaxImportBytes = WorkshopWorld.MaxContentBytes;
    private readonly Dictionary<string, PcTemplate> _importTemplates = new(StringComparer.Ordinal);

    /// <summary>A cupboard entry that is one of this player's PC files, not a PC in the shared world.</summary>
    internal static bool IsFilePc(ComputerSave record)
    {
        var stored = Saves.localSaveGame?.computersInStorage;
        if (record == null || stored == null) return false;
        foreach (var r in stored) if (r != null && r.Pointer == record.Pointer) return false;
        return true;
    }

    internal void RequestImport(StoredPC ui) => RequestImport(ui?.m_computer);

    /// <summary>Import <paramref name="record"/> (a PC file's record) into this player's hands.</summary>
    internal void RequestImport(ComputerSave record)
    {
        if (!Ready("Import")) return;
        var wc = Workshop;
        if (wc.IsCaseLoading || Saves.SpawningPC) { Refuse("Import", "Objects are still loading."); return; }
        if (!wc.CarryingEmpty()) { Refuse("Import", "Your hands are not empty."); return; }
        var staged = record != null && _filePcs.ContainsKey(record.Pointer);
        byte[] content;
        CustomCaseSaveContainer design;
        try
        {
            design = staged ? _filePcs[record.Pointer].Design : NativeToolBenches.DesignOf(record);
            content = Pack(record, design);
        }
        catch (Exception e) { Refuse("Import", "That PC file can't be read: " + e.Message); return; }
        if (content.Length > MaxImportBytes) { Refuse("Import", $"That PC is too large to share ({content.Length / 1024 / 1024} MB, the limit is 8 MB)."); return; }
        var hash = SHA256.HashData(content);
        var state = GameController.Get()?.GetCurrentState()?.Pointer ?? IntPtr.Zero;
        var workshop = _coop.Workshop ?? throw new InvalidDataException("The workshop sync isn't running.");
        var length = content.Length;
        var request = new Request { What = "Import", State = state, FileRecord = staged ? record.Pointer : IntPtr.Zero };
        if (workshop.SendContent(content, "the imported PC", epoch =>
            {
                if (_coop.Activity.Get(_world.Local)?.Epoch != epoch) return;
                Submit(PcLifecycle.Import(epoch, hash, length), request);
            }))
            Plugin.Logger.LogInfo($"[objects] importing a PC file ({length / 1024} KB{(design != null ? ", with its case design" : "")}).");
    }

    /// <summary>The PC record and its case design, compressed.</summary>
    private static byte[] Pack(ComputerSave record, CustomCaseSaveContainer design)
    {
        if (record == null) throw new InvalidDataException("no PC");
        var pc = NativePcCodec.Write(record);
        var paint = design != null ? NativeToolBenches.WriteDesign(design) : Array.Empty<byte>();
        var w = new NetDataWriter();
        w.Put(pc.Length); w.Put(pc); w.Put(paint.Length); w.Put(paint);
        return NativeToolBenches.Deflate(w.Data, w.Length);
    }

    private static (ComputerSave Record, CustomCaseSaveContainer Design) Unpack(byte[] content)
    {
        var r = new NetDataReader(NativeToolBenches.Inflate(content, 32L * 1024 * 1024));
        var n = r.GetInt();
        if (n is < 1 or > CoopSavePair.MaxCarrierBytes) throw new InvalidDataException("Invalid imported PC.");
        var pc = new byte[n]; r.GetBytes(pc, n);
        var m = r.GetInt();
        if (m < 0 || m != r.AvailableBytes) throw new InvalidDataException("Invalid imported case design.");
        var design = new byte[m]; r.GetBytes(design, m);
        return (NativePcCodec.Read(pc), m == 0 ? null : NativeToolBenches.ReadDesign(design));
    }

    public PcTemplate DescribeImport(byte[] hash)
    {
        var hex = Convert.ToHexString(hash);
        if (_importTemplates.TryGetValue(hex, out var known)) return known;
        var content = NativeWorkshop.ReadContent(hash);
        if (content == null) return null;
        var template = NativeObjectState.Template(Unpack(content).Record);
        _importTemplates[hex] = template;
        return template;
    }

    public void Imported(in MutationContext ctx, PcFact pc, byte[] hash)
    {
        try
        {
            var content = NativeWorkshop.ReadContent(hash) ?? throw new InvalidDataException("the imported PC's content isn't on this game");
            var (record, design) = Unpack(content);
            NativeObjectState.CheckParts(pc, record);
            var number = 0;
            NativeProjection.Run(() =>
            {
                if (design == null) { record.isCustomised = false; return; }
                var manager = CustomCaseManager.Get() ?? throw new InvalidDataException("The case design list isn't loaded.");
                number = FreshDesignNumber(manager);
                design.CustomisationID = number;
                manager.CustomisationList.Add(design);
                record.CustomisationID = number; record.isCustomised = true;
            });
            if (ctx.FromLocalPlayer)
            {
                var request = LocalRequest(ctx);
                CreateHere(pc, record, null, request);
                if (request != null) _filePcs.Remove(request.FileRecord);
            }
            else Objects.SetRemoteCarrier(pc, record);
            Plugin.Logger.LogInfo($"[objects] imported PC {pc.Id} for {pc.Location.Actor} by {ctx.Op}{(design != null ? $", case design {number}" : "")}" +
                                  (ctx.FromLocalPlayer ? ", into this player's hands." : ", remote carrier."));
        }
        catch (Exception e) { Fault($"PC {pc.Id} was imported, but this game could not show it", e); }
    }

    // ---------------- PCs the host's game made by itself (phase 16d, owner decision November 9, 2026) ----------------

    private const float AdoptEvery = 1f;
    private float _nextAdopt;
    /// <summary>Host: game-made PCs being shared, by content hash.</summary>
    private readonly Dictionary<string, (ComputerSave Record, Case Mesh)> _adopting = new(StringComparer.Ordinal);

    /// <summary>Host: a PC its game made by itself is being shared (saves wait for it).</summary>
    internal bool Adopting => _adopting.Count > 0;

    /// <summary>Host, about once a second: give shared IDs to PCs its game made by itself (a PC Bay purchase into
    /// storage, a customer's PC arriving on a bench in the morning, an onboarding PC). Its content goes first, as for
    /// an import; then one host change names the place the game put it.</summary>
    private void AdoptNative()
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextAdopt || _adopting.Count > 0) return;
        _nextAdopt = now + AdoptEvery;
        var workshop = _coop.Workshop;
        if (workshop == null) return;
        foreach (var (record, mesh, place, bench) in Objects.UnboundHostPcs())
        {
            byte[] content;
            try { content = Pack(record, NativeToolBenches.DesignOf(record)); }
            catch (Exception e) { Plugin.Logger.LogWarning("[objects] a PC the game made can't be read for sharing: " + e.Message); continue; }
            if (content.Length > MaxImportBytes) { Plugin.Logger.LogWarning($"[objects] a PC the game made is too large to share ({content.Length / 1024} KB)."); continue; }
            var hash = SHA256.HashData(content);
            var hex = Convert.ToHexString(hash);
            if (_adopting.ContainsKey(hex)) continue;
            var location = place == PcPlace.Bench ? Objects.LocationOf(bench) : default;
            if (place == PcPlace.Bench && location.IsNone) { Plugin.Logger.LogWarning("[objects] a PC the game made is on a bench co-op doesn't know."); continue; }
            var length = content.Length;
            _adopting[hex] = (record, mesh);
            if (!workshop.SendContent(content, "a PC the game made", epoch =>
                {
                    // The host's change applies inside Submit, where Adopted still finds this entry.
                    if (_coop.Activity.Get(_world.Local)?.Epoch == epoch) _world.Submit(CommandTypes.PcLifecycle, PcLifecycle.Adopt(epoch, hash, length, place, location), out _);
                    _adopting.Remove(hex);
                }))
                _adopting.Remove(hex);
            else Plugin.Logger.LogInfo($"[objects] sharing a PC the game made ({place}, {length / 1024} KB).");
        }
    }

    public void Adopted(in MutationContext ctx, PcFact pc, byte[] hash)
    {
        try
        {
            if (_world.IsAuthority)
            {
                var hex = Convert.ToHexString(hash);
                var content = NativeWorkshop.ReadContent(hash) ?? throw new InvalidDataException("the PC's content is gone");
                (ComputerSave record, Case mesh) = _adopting.TryGetValue(hex, out var own) ? own : default;
                if (record == null)
                {
                    // Sent in an earlier frame: find the same record again by its content.
                    foreach (var (r, m, _, _) in Objects.UnboundHostPcs())
                        if (SHA256.HashData(Pack(r, NativeToolBenches.DesignOf(r))).AsSpan().SequenceEqual(hash)) { record = r; mesh = m; break; }
                }
                if (record == null) throw new InvalidDataException("the PC the game made is no longer where it was");
                Objects.BindAdopted(pc, record, mesh);
                Plugin.Logger.LogInfo($"[objects] PC {pc.Id} (made by the game) is shared at {pc.Location.Place}.");
                return;
            }
            var (copy, design) = Unpack(NativeWorkshop.ReadContent(hash) ?? throw new InvalidDataException("the PC's content isn't on this game"));
            NativeObjectState.CheckParts(pc, copy);
            NativeProjection.Run(() =>
            {
                if (design == null) { copy.isCustomised = false; return; }
                var manager = CustomCaseManager.Get() ?? throw new InvalidDataException("The case design list isn't loaded.");
                var number = FreshDesignNumber(manager);
                design.CustomisationID = number;
                manager.CustomisationList.Add(design);
                copy.CustomisationID = number; copy.isCustomised = true;
            });
            Objects.AdoptHere(pc, copy);
            Plugin.Logger.LogInfo($"[objects] PC {pc.Id} made by the host's game appears here ({pc.Location.Place}).");
        }
        catch (Exception e) { Fault($"PC {pc.Id} was made by the host's game, but this game could not show it", e); }
    }

    /// <summary>A design number no design or PC on this game uses.</summary>
    private int FreshDesignNumber(CustomCaseManager manager)
    {
        var top = manager.m_lastID;
        foreach (var c in manager.CustomisationList) if (c != null) top = Math.Max(top, c.CustomisationID);
        foreach (var p in Objects.Model.Pcs)
            if (Objects.RecordFor(p.Id) is { } r) top = Math.Max(top, r.CustomisationID);
        foreach (var s in Object.FindObjectsOfType<StoredPC>()) if (s?.m_computer != null) top = Math.Max(top, s.m_computer.CustomisationID);
        manager.m_lastID = top + 1;
        return top + 1;
    }
}
