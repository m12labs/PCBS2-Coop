using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib.Utils;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Main-thread adapter between durable logical facts and the native save graph. Native pointers are
/// temporary observation keys only. A PartInstance is scoped to its owning PC, including native aliases.</summary>
internal sealed class NativeObjectState : IWorldState
{
    public string Name => "objects";
    public ObjectWorld Model { get; } = new();
    private readonly Dictionary<long, NetObjectId> _cases = new(), _saves = new();
    private readonly Dictionary<NetObjectId, ComputerSave> _saveRoots = new();
    private Dictionary<NetObjectId, string> _saveBindings = new();
    private Dictionary<NetObjectId, ComputerSave> _carrierRecords = new();
    private Dictionary<NetObjectId, string> _loadBindings;
    private Dictionary<NetObjectId, ComputerSave> _loadingCarriers;
    private (NetObjectId Id, ComputerSave Record)? _guestCarry;
    private string _catalog;
    private bool _reading;
    private readonly Dictionary<long, ProjectionBindings<Case>.Ticket> _spawnTickets = new();
    /// <summary>When each spawn ticket was opened (realtime seconds); see <see cref="ExpireStaleSpawns"/>.</summary>
    private readonly Dictionary<long, float> _ticketBorn = new();
    private const float StaleTicketSeconds = WorldReadiness.SettleSeconds + 3f;
    private readonly Dictionary<NetObjectId, ComputerSave> _retrieving = new();
    private readonly Dictionary<NetObjectId, BenchSlot> _benchLoads = new();
    /// <summary>Remote drops whose load finished; unplugged once the world settles (see <see cref="SettleRemoteDrops"/>).</summary>
    private readonly Dictionary<NetObjectId, (Case Mesh, BenchSlot Bench)> _settleDrops = new();
    private readonly HashSet<(long Save, long Callback)> _completedLoads = new();
    /// <summary>A local retrieval of this stored record is about to start loading.</summary>
    internal void Retrieving(NetObjectId id, ComputerSave record) => _retrieving[id] = record;
    public ProjectionBindings<Case> Projections { get; } = new();
    public bool Initialized => Model.WorldId != Guid.Empty;
    internal NetObjectId IdOf(Case mesh) => mesh != null && _cases.TryGetValue(mesh.Pointer.ToInt64(), out var id) ? id : default;
    internal NetObjectId IdOf(ComputerSave save) => save != null && _saves.TryGetValue(save.Pointer.ToInt64(), out var id) ? id : default;

    /// <summary>Why this game no longer shows every accepted PC (a load that never finished), or null. Shown as
    /// out of sync until the next world generation or admission (task 3h: the players' /sync).</summary>
    public string ProjectionFault { get; private set; }
    /// <summary>Probe-only fault injection: the next remote bench load fails before the game starts it.</summary>
    internal bool FailNextRemoteLoad;

    public void Reset()
    {
        CleanupCompletedLoads();
        Model.Reset(); _cases.Clear(); _saves.Clear(); _saveRoots.Clear(); _saveBindings.Clear(); _carrierRecords.Clear(); _spawnTickets.Clear(); _ticketBorn.Clear(); _retrieving.Clear(); _benchLoads.Clear(); _settleDrops.Clear();
        _catalog = null; Projections.Reset(Guid.Empty); ProjectionFault = null; FailNextRemoteLoad = false;
        // _loadBindings/_guestCarry bridge a paired native load to the subsequent generation/admission reset.
    }

    private sealed class Observation
    {
        public readonly string Key;
        public readonly PcLocation Location;
        public readonly ComputerSave Save;
        public readonly Case Mesh;
        public Observation(string key, PcLocation location, ComputerSave save, Case mesh)
        { Key = key; Location = location; Save = save; Mesh = mesh; }
    }
    private static string PathOf(Transform t)
    {
        var path = $"{t.name}[{t.GetSiblingIndex()}]";
        for (var p = t.parent; p != null; p = p.parent) path = $"{p.name}[{p.GetSiblingIndex()}]/" + path;
        return path;
    }
    internal static string BenchKey(BenchSlot b) => b == null ? "" : $"{PathOf(b.transform)}|{b.m_type}|{b.m_spawnStage}|{b.m_deliverySubtype}|{b.m_workStation?.m_benchID}";
    private static SaveLoadSystem Saves => Object.FindObjectOfType<SaveLoadSystem>() ?? throw new InvalidDataException("Save system unavailable.");
    private static WorkshopController Workshop => Object.FindObjectOfType<WorkshopController>() ?? throw new InvalidDataException("Workshop unavailable.");
    private static string CatalogHash()
    {
        // A Free Build template is reusable stock, not a physical part. Identity is minted when a PC is created.
        var parts = CareerStatus.Get()?.GetInventory() ?? throw new InvalidDataException("Inventory unavailable.");
        var rows = new List<string>();
        foreach (var p in parts) rows.Add(p == null ? "null" : p.GetIl2CppType().FullName + ":" + p.GetPartId());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows.OrderBy(x => x, StringComparer.Ordinal)))));
    }
    private static List<Observation> Observe(ActorSlot local, PlayerId player)
    {
        var s = Saves; var result = new List<Observation>();
        foreach (var bench in s.benchSlots)
        {
            if (bench == null || bench.m_caseLoadingToSlot) throw new InvalidDataException("A bench binding is missing/loading.");
            // GetComputerRef returns the address of the native field; the generated binding loses its by-ref return.
            var pc = bench.GetComputer(true);
            if (pc?.caseID != null) result.Add(new("bench:" + BenchKey(bench), PcLocation.Bench(BenchKey(bench)), pc, bench.GetCaseInSlot()));
        }
        for (var i = 0; i < s.localSaveGame.computersInStorage.Count; i++)
        {
            var pc = s.localSaveGame.computersInStorage[i];
            if (pc?.caseID == null) throw new InvalidDataException("An invalid stored PC was encountered.");
            result.Add(new("storage:" + i, PcLocation.Storage, pc, null));
        }
        if (Workshop.HasOrLoadingCarriedCase())
        {
            var pc = Workshop.GetCarriedComputer();
            if (pc?.caseID == null || Workshop.IsCaseLoading) throw new InvalidDataException("Carried PC is loading.");
            result.Add(new("carry", PcLocation.Carried(local, player), pc, Workshop.GetCarriedCase()));
        }
        return result;
    }

    internal void SavedComputer(Case mesh, ComputerSave save)
    {
        if (!_reading && mesh != null && save != null && _cases.TryGetValue(mesh.Pointer.ToInt64(), out var id)) RememberSave(id, save);
    }

    private void RememberSave(NetObjectId id, ComputerSave save)
    {
        // Keep the IL2CPP wrapper alive while its pointer is an observation key. Otherwise native GC can reuse
        // an obsolete record's address for an unrelated PC. Retain only the latest record for each logical PC.
        if (_saveRoots.TryGetValue(id, out var old)) _saves.Remove(old.Pointer.ToInt64());
        _saveRoots[id] = save; _saves[save.Pointer.ToInt64()] = id;
    }
    internal UnityEngine.Events.UnityAction<bool, Case, BenchSlot> WrapSpawn(ComputerSave save, UnityEngine.Events.UnityAction<bool, Case, BenchSlot> callback)
    {
        if (save == null || !Initialized || !_saves.TryGetValue(save.Pointer.ToInt64(), out var id) || Model.Lookup(id) != ObjectLookup.Live) return callback;
        // Capture this attempt in the callback itself. A dictionary keyed only by save pointer lets an old
        // completion accidentally consume a newer attempt for the same save record.
        UnityEngine.Events.UnityAction<bool, Case, BenchSlot> wrapper = (System.Action<bool, Case, BenchSlot>)
            ((bool powered, Case mesh, BenchSlot slot) => callback?.Invoke(powered, mesh, slot));
        foreach (var old in _cases.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _cases.Remove(old);
        _spawnTickets.Add(wrapper.Pointer.ToInt64(), Projections.Begin(id));
        _ticketBorn[wrapper.Pointer.ToInt64()] = Time.realtimeSinceStartup;
        return wrapper;
    }

    internal bool AllowLoadCompletion(Case mesh, PartsDatabase.LoadComputerOperation operation)
    {
        var callback = operation.OnSpawnedAction;
        if (callback == null || !_spawnTickets.TryGetValue(callback.Pointer.ToInt64(), out var ticket)) return true;
        if (Model.Lookup(ticket.Id) == ObjectLookup.Live && Projections.IsPending(ticket)) return true;
        // Refuse before the native delegate can install an obsolete mesh into a bench now owned by another PC.
        _spawnTickets.Remove(callback.Pointer.ToInt64()); _ticketBorn.Remove(callback.Pointer.ToInt64());
        _completedLoads.Add((operation.CompSave.Pointer.ToInt64(), callback.Pointer.ToInt64()));
        if (mesh != null && mesh) Object.Destroy(mesh.gameObject);
        Plugin.Logger.LogInfo($"[objects] discarded stale native load for {ticket.Id} (attempt {ticket.Attempt}).");
        return false;
    }

    internal void CompletedLoad(Case mesh, PartsDatabase.LoadComputerOperation operation)
    {
        var callback = operation.OnSpawnedAction;
        // Some cached loads finish synchronously before the game's caller adds its operation to the queue.
        // Remember actual completion; remove only that exact record on the next main-thread update, after the
        // native call stack has returned. This also covers initial co-op loads before logical binding exists.
        if (CoopSaves.Current != CoopSaves.Mode.None && mesh != null && operation.CompSave != null)
            _completedLoads.Add((operation.CompSave.Pointer.ToInt64(), callback?.Pointer.ToInt64() ?? 0));
        if (callback == null || !_spawnTickets.Remove(callback.Pointer.ToInt64(), out var ticket)) return;
        _ticketBorn.Remove(callback.Pointer.ToInt64());
        var retrieved = _retrieving.Remove(ticket.Id, out var stored) ? stored : null;
        if (mesh != null && Model.Lookup(ticket.Id) == ObjectLookup.Live && Projections.Complete(ticket, mesh))
        {
            _cases[mesh.Pointer.ToInt64()] = ticket.Id;
            if (_benchLoads.Remove(ticket.Id, out var dropBench)) _settleDrops[ticket.Id] = (mesh, dropBench);
            if (Model.Pcs.Single(x => x.Id == ticket.Id).Location.Place == PcPlace.Bench) _carrierRecords.Remove(ticket.Id);
            // Every retrieval logged in F2-F17 (storage button and accepted requests) left the record listed after
            // loading; otherwise the same PC would exist twice. Remove exactly that record object.
            var list = Saves.localSaveGame?.computersInStorage;
            if (retrieved != null && list != null && list.Contains(retrieved))
            {
                list.Remove(retrieved); RefreshStorage();
                Plugin.Logger.LogInfo($"[objects] removed retrieved PC {ticket.Id}'s record from storage after loading.");
            }
        }
        else if (Model.Lookup(ticket.Id) != ObjectLookup.Live) Plugin.Logger.LogWarning($"[objects] ignored a late load completion for retired/unknown {ticket.Id}.");
    }

    internal void CleanupCompletedLoads()
    {
        if (_completedLoads.Count == 0) return;
        var ops = PartsDatabase.s_instance?.m_loadPCOperations;
        var removed = 0;
        if (ops != null)
            for (var i = ops.Count - 1; i >= 0; i--)
            {
                var op = ops[i];
                var key = (op.CompSave?.Pointer.ToInt64() ?? 0, op.OnSpawnedAction?.Pointer.ToInt64() ?? 0);
                if (_completedLoads.Contains(key) && !_spawnTickets.ContainsKey(key.Item2)) { ops.RemoveAt(i); removed++; }
            }
        _completedLoads.Clear();
        if (removed > 0) Plugin.Logger.LogInfo($"[objects] cleared {removed} completed native load record(s).");
    }

    /// <summary>Called while the world is ready (settled, nothing loading): a ticket that has been open for longer than
    /// the settle period belongs to a failed or abandoned load. Drop it so it can neither block saves forever nor bind
    /// a later mesh. A younger ticket may come from a load started this very frame, after readiness was computed
    /// (run H1: a guest replaying a held drop during admission), so it is left alone.</summary>
    internal void ExpireStaleSpawns()
    {
        if (_spawnTickets.Count == 0) return;
        var now = Time.realtimeSinceStartup;
        foreach (var key in _spawnTickets.Keys.ToArray())
        {
            if (_ticketBorn.TryGetValue(key, out var born) && now - born < StaleTicketSeconds) continue;
            var t = _spawnTickets[key];
            Plugin.Logger.LogWarning($"[objects] native load for {t.Id} never completed; dropping its projection ticket.");
            Projections.Remove(t.Id);
            ProjectionFault ??= $"PC {t.Id} never finished loading on this game.";
            _spawnTickets.Remove(key); _ticketBorn.Remove(key);
        }
    }

    // ---------------- task 3e: created/removed PCs and host observation ----------------

    /// <summary>A PC created by an accepted change, about to be loaded natively on this peer.</summary>
    internal void BindCreated(NetObjectId id, ComputerSave record) => RememberSave(id, record);

    /// <summary>A PC created by an accepted change for the other actor. Its full record stays with the mod (no mesh until 3f).</summary>
    internal void SetRemoteCarrier(PcFact pc, ComputerSave record)
    {
        CheckParts(pc, record);
        _carrierRecords[pc.Id] = record;
        _saveBindings[pc.Id] = "remote-carry:" + pc.Location.Actor;
    }

    /// <summary>The other actor's carried PC, held by the mod on this peer. Decided by the logical location: saved
    /// binding keys are written from the host's perspective.</summary>
    internal bool TryRemoteCarrier(PcFact pc, out ComputerSave record)
    {
        record = null;
        var local = CoopController.Instance?.Session.World?.Local ?? ActorSlot.None;
        return pc is { Location.Place: PcPlace.Carrier } && pc.Location.Actor != local && _carrierRecords.TryGetValue(pc.Id, out record);
    }

    /// <summary>Remove every local observation key/record of a retired PC so no stale pointer can resolve to it.</summary>
    internal void ForgetPc(NetObjectId id)
    {
        foreach (var k in _cases.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _cases.Remove(k);
        foreach (var k in _saves.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _saves.Remove(k);
        // Keep an in-flight callback's ticket until completion so AllowLoadCompletion can suppress its native work.
        _saveRoots.Remove(id); _carrierRecords.Remove(id); _saveBindings.Remove(id); _retrieving.Remove(id); _benchLoads.Remove(id); Projections.Remove(id);
    }

    internal ComputerSave FindStored(NetObjectId id)
    {
        var stored = Saves.localSaveGame?.computersInStorage;
        if (stored == null) return null;
        foreach (var pc in stored) if (pc != null && IdOf(pc) == id) return pc;
        return null;
    }

    // ---------------- task 3f: transfers ----------------

    internal BenchSlot BenchAt(string key)
    {
        foreach (var bench in Saves.benchSlots) if (bench != null && BenchKey(bench) == key) return bench;
        return null;
    }
    internal NetObjectId LocationOf(BenchSlot bench) => bench == null ? default : Model.Locations.SingleOrDefault(l => l.Key == BenchKey(bench))?.Id ?? default;

    /// <summary>The PC lives on as a record only: drop its mesh keys and projection, keep its record mapping.</summary>
    private void UnbindMesh(NetObjectId id)
    {
        foreach (var k in _cases.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _cases.Remove(k);
        Projections.Remove(id);
    }

    private void HoldRemote(PcFact pc, ActorSlot carrier, ComputerSave record)
    {
        // Pickup side effects (power off, external cables, USB devices) can change a PC's graph; the model keeps its
        // facts until component edits are synchronized (phase 4). Report it instead of refusing the accepted move.
        try { CheckParts(pc, record); }
        catch (InvalidDataException e) { Plugin.Logger.LogWarning($"[objects] PC {pc.Id} record differs from its facts after the move: {e.Message}"); }
        RememberSave(pc.Id, record);
        _carrierRecords[pc.Id] = record;
        _saveBindings[pc.Id] = "remote-carry:" + carrier;
    }

    /// <summary>The other player picked this PC up from a bench: the same pickup side effects as the native
    /// <c>Case.OnPickup</c>, then the bench's own removal. The mod holds the record; the remote view shows a box.</summary>
    internal void RemotePickup(PcFact pc, BenchSlot bench, ActorSlot carrier)
    {
        var mesh = bench.GetCaseInSlot();
        if (mesh == null && _benchLoads.TryGetValue(pc.Id, out var loading) && loading.Pointer == bench.Pointer &&
            _carrierRecords.TryGetValue(pc.Id, out var held))
        {
            // Drop then pickup can arrive in one network poll. Ownership moves immediately; the earlier load
            // remains only an obsolete attempt, which is suppressed before it can place its mesh.
            _benchLoads.Remove(pc.Id); _settleDrops.Remove(pc.Id); bench.m_caseLoadingToSlot = false;
            UnbindMesh(pc.Id); HoldRemote(pc, carrier, held);
            return;
        }
        if (mesh == null || IdOf(mesh) != pc.Id) throw new InvalidDataException($"bench does not hold PC {pc.Id} on this game");
        _settleDrops.Remove(pc.Id);
        LeaveWork(mesh);
        NativeProjection.Run(() => mesh.OnPickup());
        var record = bench.GetComputer(true); // the record after the pickup side effects (runs SaveComputer)
        if (record?.caseID == null) throw new InvalidDataException("the bench PC has no save record");
        NativeProjection.Run(() => bench.DestroyComputer());
        UnbindMesh(pc.Id);
        HoldRemote(pc, carrier, record);
    }

    /// <summary>The other player took this PC from the shared cupboard.</summary>
    internal void RemoteRetrieve(PcFact pc, ActorSlot carrier)
    {
        var record = FindStored(pc.Id) ?? throw new InvalidDataException($"PC {pc.Id} is not in this game's storage");
        Saves.localSaveGame.computersInStorage.Remove(record);
        RefreshStorage();
        HoldRemote(pc, carrier, record);
    }

    /// <summary>The other player put their carried PC on a bench: load its record there through the game's own
    /// bench loader, bound to this PC like any other load.</summary>
    internal void RemoteDrop(PcFact pc, BenchSlot bench)
    {
        if (!_carrierRecords.TryGetValue(pc.Id, out var record)) throw new InvalidDataException($"no carried record for PC {pc.Id}");
        if (bench.GetCaseInSlot() != null || bench.m_caseLoadingToSlot) throw new InvalidDataException("the bench is not empty on this game");
        if (FailNextRemoteLoad) { FailNextRemoteLoad = false; throw new InvalidDataException("injected projection failure (probe fault-projection)"); }
        _saveBindings.Remove(pc.Id);
        // Keep the held graph intact until completion: the native loader mutates its input aliases, and a newer
        // accepted move may need this PC before the mesh exists. A separate graph belongs to each load attempt.
        var input = NativePcCodec.Read(NativePcCodec.Write(record));
        RememberSave(pc.Id, input); _benchLoads[pc.Id] = bench; bench.m_caseLoadingToSlot = true;
        ShowBench(bench);
        // loadCables stays true: false also skips the PC's internal cables ("Missing cable(s)", run H6b). Known phase 5
        // gap: this load plugs the bench's external cables back in, while the mover's own DropCase leaves the PC
        // "Not plugged in" (runs H5c/H6b). Both games keep the same parts and location.
        NativeProjection.Run(() => Saves.LoadComputerToSlot(input, bench, true, false, null));
        if (!_spawnTickets.Values.Any(t => t.Id == pc.Id) && IdOf(bench.GetCaseInSlot()) != pc.Id)
            throw new InvalidDataException("the game did not start loading the PC onto the bench");
    }

    /// <summary>The other player stored their carried PC, or a departed player's PC was recovered to the cupboard.</summary>
    internal void RemoteStore(PcFact pc)
    {
        if (!_carrierRecords.Remove(pc.Id, out var record)) throw new InvalidDataException($"no carried record for PC {pc.Id}");
        _saveBindings.Remove(pc.Id);
        Saves.AddComputerToStorage(record);
        RememberSave(pc.Id, record);
        RefreshStorage();
    }

    /// <summary>
    /// Host, before the /sync save (its game is the reference): an accepted PC this game failed to show would otherwise
    /// be retired by the capture, losing a whole build (run H5b). If the mod still holds its record, put it in the shared
    /// cupboard instead, keeping its IDs and contents (the owner's rule for a PC without a place). Returns those PCs.
    /// </summary>
    internal List<NetObjectId> RecoverUnshown(PlayerId host)
    {
        var shown = new HashSet<NetObjectId>();
        foreach (var o in Observe(ActorSlot.Host, host))
        {
            NetObjectId id = default;
            if (o.Mesh != null) _cases.TryGetValue(o.Mesh.Pointer.ToInt64(), out id);
            if (id.IsNone) _saves.TryGetValue(o.Save.Pointer.ToInt64(), out id);
            if (!id.IsNone) shown.Add(id);
        }
        var recovered = new List<NetObjectId>();
        foreach (var pc in Model.Pcs)
        {
            // The guest's own carried PC lives only in the mod on the host; the capture keeps it as a remote carrier.
            if (shown.Contains(pc.Id) || pc.Location is { Place: PcPlace.Carrier, Actor: ActorSlot.Guest }) continue;
            var record = _carrierRecords.GetValueOrDefault(pc.Id) ?? _saveRoots.GetValueOrDefault(pc.Id);
            if (record?.caseID == null) { Plugin.Logger.LogWarning($"[objects] /sync: PC {pc.Id} isn't shown here and no record is held; the save retires it."); continue; }
            try { CheckParts(pc, record); }
            catch (InvalidDataException e) { Plugin.Logger.LogWarning($"[objects] /sync: PC {pc.Id}'s held record doesn't match its facts ({e.Message}); the save retires it."); continue; }
            _carrierRecords.Remove(pc.Id); _saveBindings.Remove(pc.Id);
            Saves.AddComputerToStorage(record);
            RememberSave(pc.Id, record);
            recovered.Add(pc.Id);
            Plugin.Logger.LogWarning($"[objects] /sync: PC {pc.Id} wasn't shown on the host's game; put its record in the shared cupboard.");
        }
        if (recovered.Count > 0) RefreshStorage();
        return recovered;
    }

    /// <summary>
    /// The mover's own game ran <c>Case.OnPickup</c> when it picked the PC up (power off, external cables removed) and
    /// <c>DropCase</c>'s <c>EnableCaseColliders(true)</c> + <c>OnPutDown</c> when it put it down, so its PC shows "Not
    /// plugged in". This game loaded the PC with <c>loadCables: true</c> (internal cables need it), which also plugs the
    /// bench's external cables back in (runs H5c/H6b). Once the load and its cable coroutine have settled, run the same
    /// three calls here so both games show the same plug and power state.
    /// </summary>
    internal void SettleRemoteDrops(bool worldReady)
    {
        if (!worldReady || _settleDrops.Count == 0) return;
        foreach (var (id, (mesh, bench)) in _settleDrops.ToArray())
        {
            _settleDrops.Remove(id);
            var pc = Model.Pcs.SingleOrDefault(x => x.Id == id);
            if (mesh == null || !mesh || bench == null || IdOf(mesh) != id || pc?.Location.Place != PcPlace.Bench || bench.GetCaseInSlot() != mesh) continue;
            try
            {
                NativeProjection.Run(() => { mesh.OnPickup(); mesh.EnableCaseColliders(true); mesh.OnPutDown(bench); });
                Plugin.Logger.LogInfo($"[objects] PC {id}: unplugged as the mover's drop leaves it.");
            }
            catch (Exception e) { CoopController.Instance?.ProjectionFailed($"PC {id}'s plug state could not be matched", e); }
        }
    }

    internal static void RefreshStorage()
    {
        // Only open storage views; a closed one repopulates when it opens.
        foreach (var storage in Object.FindObjectsOfType<Storage>())
            if (storage != null && storage.isActiveAndEnabled) storage.Populate(true, true, false);
    }

    /// <summary>A local work/monitor view of a PC that is about to leave this bench: step back out of it first.</summary>
    private static void LeaveWork(Case mesh)
    {
        var ctrl = GameController.Get();
        for (var i = 0; i < 4 && ctrl != null; i++)
        {
            var state = ctrl.GetCurrentState();
            var work = state?.TryCast<WorkingOnComputerStateBase>(); var monitor = state?.TryCast<LookingAtMonitorState>();
            if (work?.Case?.Pointer != mesh.Pointer && monitor?.m_case?.Pointer != mesh.Pointer) return;
            Plugin.Logger.LogWarning("[objects] the other player moved the PC this player was working on; leaving its work view.");
            ctrl.GoToPreviousState();
        }
    }

    internal BenchSlot FindBench(NetObjectId id)
    {
        foreach (var bench in Saves.benchSlots)
            if (bench != null && (IdOf(bench.GetCaseInSlot()) == id || IdOf(bench.GetComputer(false)) == id)) return bench;
        return null;
    }

    /// <summary>Host: native locations of bound PCs that differ from the model. Throws while a native load is running.
    /// Never reports another actor's carrier; unbound native PCs wait for the next save capture.</summary>
    internal List<PcObservation> ObserveHostMoves(PlayerId host)
    {
        // Meshes and stored records only: BenchSlot.GetComputer(true) and GetCarriedComputer re-run the native
        // SaveComputer (and toggle the PC object), which must not happen on a polling path.
        var s = Saves; var wc = Workshop;
        if (wc.IsCaseLoading || s.SpawningPC) throw new InvalidDataException("A PC is loading.");
        var seen = new List<(NetObjectId Id, PcLocation Location, NetObjectId Bench)>();
        void See(NetObjectId id, PcLocation location, NetObjectId bench) { if (!id.IsNone) seen.Add((id, location, bench)); }
        foreach (var bench in s.benchSlots)
        {
            if (bench == null || bench.m_caseLoadingToSlot) throw new InvalidDataException("A bench is loading.");
            var mesh = bench.GetCaseInSlot();
            if (mesh == null) continue;
            var key = BenchKey(bench);
            See(IdOf(mesh), PcLocation.Bench(key), Model.Locations.SingleOrDefault(l => l.Key == key)?.Id ?? default);
        }
        foreach (var record in s.localSaveGame.computersInStorage) See(IdOf(record), PcLocation.Storage, default);
        See(IdOf(wc.GetCarriedCase()), PcLocation.Carried(ActorSlot.Host, host), default);
        var result = new List<PcObservation>();
        foreach (var (id, location, bench) in seen)
        {
            var pc = Model.Pcs.SingleOrDefault(x => x.Id == id);
            if (pc == null || pc.Location == location || (pc.Location.Place == PcPlace.Carrier && pc.Location.Actor != ActorSlot.Host)) continue;
            if (location.Place == PcPlace.Bench && bench.IsNone) continue;
            result.Add(new(id, location.Place, bench));
        }
        return result;
    }

    /// <summary>Host, at a capture boundary: parts a native load added to PCs created by an accepted change (for
    /// example a case prefab's default fans). Only pure growth qualifies; any changed existing link is left to the
    /// capture (component edits are phase 4). Refreshes native records, like the capture itself.</summary>
    internal List<(NetObjectId Pc, PcGrowth Growth)> HostGrowth(PlayerId host)
    {
        var result = new List<(NetObjectId, PcGrowth)>();
        foreach (var o in Observe(ActorSlot.Host, host))
        {
            NetObjectId id = default;
            if (o.Mesh != null) _cases.TryGetValue(o.Mesh.Pointer.ToInt64(), out id);
            if (id.IsNone) _saves.TryGetValue(o.Save.Pointer.ToInt64(), out id);
            var pc = id.IsNone ? null : Model.Pcs.SingleOrDefault(x => x.Id == id);
            if (pc == null || pc.Provenance.Origin != InstanceOrigin.Catalog) continue;
            var native = Parts(o.Save);
            var known = new Dictionary<long, NetObjectId>(); var growth = true;
            foreach (var link in pc.Links)
            {
                var fact = pc.Parts.Single(x => x.Id == link.Part);
                if (!native.TryGetValue(link.Path, out var part) || part.GetPartId() != fact.Sku || part.GetIl2CppType().FullName != fact.NativeType ||
                    (known.TryGetValue(part.Pointer.ToInt64(), out var other) && other != fact.Id)) { growth = false; break; }
                known[part.Pointer.ToInt64()] = fact.Id;
            }
            if (!growth) { Plugin.Logger.LogWarning($"[objects] PC {pc.Id} changed natively beyond growth; its next capture assigns identities."); continue; }
            var index = new Dictionary<long, int>(); var parts = new List<TemplatePart>(); var links = new List<GrowthLink>();
            var linked = pc.Links.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, part) in native)
            {
                if (linked.Contains(path)) continue;
                var pointer = part.Pointer.ToInt64();
                if (known.TryGetValue(pointer, out var existing)) { links.Add(new(path, -1, existing)); continue; }
                if (!index.TryGetValue(pointer, out var i)) { i = parts.Count; index.Add(pointer, i); parts.Add(new(part.GetPartId(), part.GetIl2CppType().FullName)); }
                links.Add(new(path, i, default));
            }
            if (links.Count > 0) result.Add((pc.Id, new PcGrowth(parts, links)));
        }
        return result;
    }

    /// <summary>Topology of a fresh record, in the same alias/path convention as captured PCs.</summary>
    internal static PcTemplate Template(ComputerSave record)
    {
        var index = new Dictionary<long, int>(); var parts = new List<TemplatePart>(); var links = new List<TemplateLink>();
        foreach (var (path, part) in Parts(record))
        {
            if (!index.TryGetValue(part.Pointer.ToInt64(), out var i))
            {
                i = parts.Count; index.Add(part.Pointer.ToInt64(), i);
                parts.Add(new(part.GetPartId(), part.GetIl2CppType().FullName));
            }
            links.Add(new(path, i));
        }
        return new(parts, links);
    }

    public void CaptureHost(PlayerId host)
    {
        if (_spawnTickets.Count > 0) throw new InvalidDataException("A native PC projection has not completed.");
        // Owner decision (3g): packages and physical notes aren't shared; never save or send a world holding them.
        if (NativeWorkstations.Unsupported() is { } unsupported)
            throw new InvalidDataException($"This world contains {unsupported}, which co-op doesn't share yet.");
        var observations = Observe(ActorSlot.Host, host);
        var allocator = new NetObjectIdAllocator { Next = Model.NextId };
        var locations = Model.Locations.ToList();
        var manifest = Saves.benchSlots.Select(BenchKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (manifest.Distinct(StringComparer.Ordinal).Count() != manifest.Length) throw new InvalidDataException("Duplicate workshop location bindings.");
        if (Initialized && !manifest.SequenceEqual(locations.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidDataException("The workshop binding manifest changed.");
        if (!Initialized) foreach (var key in manifest) locations.Add(new(allocator.Allocate(), key));
        var pcs = new List<PcFact>(); var bindings = new Dictionary<NetObjectId, string>();
        var used = new HashSet<NetObjectId>();
        var records = new Dictionary<NetObjectId, ComputerSave>();
        foreach (var o in observations)
        {
            NetObjectId id;
            if (o.Mesh == null || !_cases.TryGetValue(o.Mesh.Pointer.ToInt64(), out id)) _saves.TryGetValue(o.Save.Pointer.ToInt64(), out id);
            if (id.IsNone) id = allocator.Allocate();
            if (!used.Add(id)) throw new InvalidDataException("The same logical PC was bound twice.");
            var prior = Model.Pcs.SingleOrDefault(x => x.Id == id);
            var parts = Parts(o.Save);
            var aliases = new Dictionary<long, NetObjectId>();
            var facts = new List<PartFact>(); var links = new List<PartLink>();
            foreach (var (path, part) in parts)
            {
                if (!aliases.TryGetValue(part.Pointer.ToInt64(), out var partId))
                {
                    var previous = prior?.Links.FirstOrDefault(x => x.Path == path);
                    var fact = previous == null ? null : prior.Parts.Single(x => x.Id == previous.Part);
                    // Slot context survives SaveComputer allocating a new record; SKU equality alone never identifies a PC.
                    partId = fact != null && fact.Sku == part.GetPartId() && fact.NativeType == part.GetIl2CppType().FullName
                        ? fact.Id : allocator.Allocate();
                    if (facts.Any(x => x.Id == partId)) throw new InvalidDataException("Native alias layout changed ambiguously.");
                    aliases.Add(part.Pointer.ToInt64(), partId);
                    facts.Add(new(partId, part.GetPartId(), part.GetIl2CppType().FullName));
                }
                links.Add(new(path, partId));
            }
            var provenance = prior?.Provenance ?? new InstanceProvenance(Initialized ? InstanceOrigin.NativeObserved : InstanceOrigin.Baseline, host, default);
            pcs.Add(new(id, provenance, o.Location, facts, links)); bindings.Add(id, o.Key);
            if (o.Location.Place == PcPlace.Carrier) records.Add(id, o.Save);
        }
        // Logical remote carriers have no native singleton on this peer. Their complete records are retained separately.
        foreach (var pc in Model.Pcs.Where(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor == ActorSlot.Guest))
        {
            if (!used.Add(pc.Id) || !_carrierRecords.TryGetValue(pc.Id, out var record)) throw new InvalidDataException("Remote carrier payload is missing.");
            pcs.Add(pc); bindings.Add(pc.Id, "remote-carry:" + pc.Location.Actor); records.Add(pc.Id, record);
        }
        var live = locations.Select(x => x.Id).Concat(pcs.SelectMany(AllIds)).ToHashSet();
        // Since task 3e every supported creation/removal is a host-ordered change. A PC that vanished natively
        // without one is a projection fault or an unsupported path; say so instead of retiring it silently.
        foreach (var lost in Model.Pcs.Where(x => !live.Contains(x.Id)))
            Plugin.Logger.LogWarning($"[objects] PC {lost.Id} ({lost.Location.Place}) is no longer present natively; retiring it at this capture.");
        var retired = Model.Retired.Concat(Model.Pcs.SelectMany(AllIds).Where(x => !live.Contains(x))).ToArray();
        Model.Replace(Initialized ? Model.WorldId : Guid.NewGuid(), allocator.Next, locations, pcs, retired);
        _saveBindings = bindings; _carrierRecords = records; _catalog = CatalogHash();
        BindObserved(observations, bindings, Model);
    }

    private static IEnumerable<NetObjectId> AllIds(PcFact pc)
    { yield return pc.Id; foreach (var part in pc.Parts) yield return part.Id; }

    private void BindObserved(List<Observation> observations, Dictionary<NetObjectId, string> bindings, ObjectWorld model)
    {
        var expected = model.Pcs.Where(x => !bindings[x.Id].StartsWith("remote-carry:", StringComparison.Ordinal)).ToArray();
        if (expected.Length != observations.Count || bindings.Count != model.Pcs.Count || bindings.Values.Distinct().Count() != bindings.Count)
            throw new InvalidDataException("Incomplete or duplicate native PC bindings.");
        var manifest = Saves.benchSlots.Select(BenchKey).OrderBy(x => x, StringComparer.Ordinal);
        if (!manifest.SequenceEqual(model.Locations.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidDataException("Workshop manifest mismatch.");
        var pending = new List<(PcFact Pc, Observation Native)>();
        var savePointers = new HashSet<long>(); var meshPointers = new HashSet<long>();
        foreach (var pc in expected)
        {
            var o = observations.SingleOrDefault(x => x.Key == bindings[pc.Id]) ?? throw new InvalidDataException($"Missing native binding for {pc.Id}.");
            CheckParts(pc, o.Save);
            if (!savePointers.Add(o.Save.Pointer.ToInt64()) || (o.Mesh != null && !meshPointers.Add(o.Mesh.Pointer.ToInt64())))
                throw new InvalidDataException("Duplicate native PC save/mesh binding.");
            pending.Add((pc, o));
        }
        // Publish local bindings only after validating the whole map. Never bind only the PCs that happened to match.
        _cases.Clear(); _saves.Clear(); _saveRoots.Clear(); Projections.Reset(model.WorldId);
        foreach (var (pc, o) in pending)
        {
            RememberSave(pc.Id, o.Save);
            if (o.Mesh != null)
            {
                _cases.Add(o.Mesh.Pointer.ToInt64(), pc.Id);
                Projections.Complete(Projections.Begin(pc.Id), o.Mesh);
            }
        }
    }

    public IReadOnlyList<CarrierPayload> CaptureCarriers() => _carrierRecords.OrderBy(x => x.Key.Value)
        .Select(x => new CarrierPayload(x.Key, NativePcCodec.Write(x.Value))).ToArray();

    /// <summary>Projection adapter seam for a host-confirmed carrier fact. Does not change logical ownership.</summary>
    public void BindCarrierPayload(NetObjectId id, ComputerSave record)
    {
        var pc = Model.Pcs.SingleOrDefault(x => x.Id == id && x.Location.Place == PcPlace.Carrier)
            ?? throw new InvalidDataException("No accepted carrier fact exists for this PC.");
        CheckParts(pc, record);
        _carrierRecords[id] = record;
        _saveBindings[id] = pc.Location.Actor == ActorSlot.Host ? "carry" : "remote-carry:" + pc.Location.Actor;
    }

    internal static void CheckParts(PcFact pc, ComputerSave save)
    {
        var parts = Parts(save);
        if (parts.Count != pc.Links.Count) throw new InvalidDataException("Carried PC topology mismatch.");
        var nativeAliases = new Dictionary<long, NetObjectId>(); var logicalAliases = new Dictionary<NetObjectId, long>();
        foreach (var (path, part) in parts)
        {
            var link = pc.Links.SingleOrDefault(x => x.Path == path) ?? throw new InvalidDataException("Carried PC path mismatch.");
            var fact = pc.Parts.Single(x => x.Id == link.Part); var pointer = part.Pointer.ToInt64();
            if (fact.Sku != part.GetPartId() || fact.NativeType != part.GetIl2CppType().FullName ||
                (nativeAliases.TryGetValue(pointer, out var alias) && alias != fact.Id) || (logicalAliases.TryGetValue(fact.Id, out var local) && local != pointer))
                throw new InvalidDataException($"Carried PC part identity/alias mismatch at {path} (expected {fact.Id} {fact.Sku}/{fact.NativeType}, native {part.GetPartId()}/{part.GetIl2CppType().FullName}).");
            nativeAliases[pointer] = fact.Id; logicalAliases[fact.Id] = pointer;
        }
    }

    /// <summary>Explicit diagnostic capture, never polling: compare freshly saved native graphs with accepted
    /// identities and locations without changing the model. The load input record may contain temporary aliases.</summary>
    internal int CheckProjection(ActorSlot local, PlayerId player)
    {
        var seen = new HashSet<NetObjectId>();
        foreach (var o in Observe(local, player))
        {
            var id = o.Mesh != null ? IdOf(o.Mesh) : IdOf(o.Save);
            var pc = Model.Pcs.SingleOrDefault(x => x.Id == id) ?? throw new InvalidDataException($"Unbound native PC at {o.Key}.");
            if (!seen.Add(id) || pc.Location != o.Location) throw new InvalidDataException($"Native location mismatch for {id} at {o.Key}.");
            // F11-F16: a PC placed under a hidden bench slot is invisible and saves null pipe ends, which the game's
            // own loader cannot read back. Part bindings skip null references, so check these explicitly.
            if (o.Mesh != null && !o.Mesh.gameObject.activeInHierarchy) throw new InvalidDataException($"Native PC {id} at {o.Key} is hidden.");
            CheckPipes(id, o.Save);
            CheckParts(pc, o.Save);
        }
        foreach (var pc in Model.Pcs.Where(x => x.Location.Place == PcPlace.Carrier && x.Location.Actor != local))
        {
            if (!TryRemoteCarrier(pc, out var record) || !seen.Add(pc.Id)) throw new InvalidDataException($"Missing remote carrier {pc.Id}.");
            CheckParts(pc, record);
        }
        if (seen.Count != Model.Pcs.Count) throw new InvalidDataException("Not every accepted PC has a native projection.");
        return seen.Count;
    }

    private static void CheckPipes(NetObjectId id, ComputerSave save)
    {
        var pipes = save?.pipesInstalled;
        if (pipes == null) return;
        for (var i = 0; i < pipes.Length; i++)
            if (pipes[i] == null || pipes[i].m_from.m_component == null || pipes[i].m_to.m_component == null)
                throw new InvalidDataException($"Native PC {id} saved water pipe {i} without its connected parts.");
    }

    /// <summary>What BenchSlot.DropPC does after WorkshopController.DropCase. BenchSlot.OnPickupPC hides a workbench's
    /// slot object; the game shows it again only in DropPC. A PC the mod places by DropCase or LoadComputerToSlot would
    /// otherwise sit under a hidden slot: invisible, and saved with null water-pipe ends that make the save unloadable.</summary>
    internal static void ShowBench(BenchSlot bench)
    {
        bench.updateStatus = true;
        if ((int)bench.m_type is 1 or 5 && !bench.gameObject.activeSelf) // WORKBENCH, WORKBENCH_TOOL
        {
            bench.gameObject.SetActive(true);
            Plugin.Logger.LogInfo($"[objects] showed the hidden bench slot {BenchKey(bench)} for a placed PC.");
        }
    }

    public void Write(NetDataWriter w)
    {
        var bytes = Model.ToBytes(); w.Put(bytes.Length); w.Put(bytes); w.Put(_catalog);
        w.Put(_saveBindings.Count);
        foreach (var b in _saveBindings.OrderBy(x => x.Key.Value)) { b.Key.Write(w); w.Put(b.Value); }
    }
    public void Read(NetDataReader r)
    {
        var length = r.GetInt();
        if (length is < 1 or > Protocol.MaxModStateBytes || length > r.AvailableBytes) throw new InvalidDataException("Invalid object model length.");
        var bytes = new byte[length]; r.GetBytes(bytes, length);
        var model = new ObjectWorld(); model.Read(new NetDataReader(bytes));
        var catalog = r.GetString(128);
        var count = r.GetInt();
        if (count != model.Pcs.Count) throw new InvalidDataException("Incomplete saved PC bindings.");
        var bindings = new Dictionary<NetObjectId, string>();
        for (var i = 0; i < count; i++)
            if (!bindings.TryAdd(NetObjectId.Read(r), r.GetString(2048))) throw new InvalidDataException("Duplicate saved PC binding.");
        if (r.AvailableBytes != 0 || model.Pcs.Any(x => !bindings.ContainsKey(x.Id))) throw new InvalidDataException("Invalid saved PC binding map.");
        foreach (var pc in model.Pcs)
        {
            var key = bindings[pc.Id];
            var valid = pc.Location.Place switch
            {
                PcPlace.Bench => key == "bench:" + pc.Location.Key,
                PcPlace.Storage => key.StartsWith("storage:", StringComparison.Ordinal) && int.TryParse(key[8..], out var index) && index >= 0,
                PcPlace.Carrier => key == (pc.Location.Actor == ActorSlot.Host ? "carry" : "remote-carry:" + pc.Location.Actor),
                _ => false
            };
            if (!valid) throw new InvalidDataException("Native save binding contradicts the logical PC location.");
        }
        if (_reading)
        {
            // Paired native load has already redirected remote/absent carriers before any native spawn.
            var effective = _loadBindings ?? bindings;
            var c = CoopController.Instance;
            BindObserved(Observe(c.Session.Role == Role.Host ? ActorSlot.Host : ActorSlot.Guest, c.Session.LocalIdentity.Player), effective, model);
            foreach (var pc in model.Pcs.Where(x => effective[x.Id].StartsWith("remote-carry:", StringComparison.Ordinal)))
            {
                if (_loadingCarriers == null || !_loadingCarriers.TryGetValue(pc.Id, out var record)) throw new InvalidDataException("Remote PC graph is missing.");
                CheckParts(pc, record);
            }
            _carrierRecords = _loadingCarriers == null ? new() : new(_loadingCarriers);
            if (catalog != CatalogHash()) throw new InvalidDataException("The saved Free Build catalog binding does not match.");
        }
        Model.Replace(model.WorldId, model.NextId, model.Locations, model.Pcs, model.Retired);
        _saveBindings = bindings; _catalog = catalog;
    }

    public void ReadPosition(SnapshotPosition position)
    {
        var reader = new NetDataReader(position.State); var count = reader.GetInt();
        if (count is < 1 or > 256) throw new InvalidDataException("Invalid world state count.");
        byte[] objectBytes = null; var names = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var name = reader.GetString(256); var len = reader.GetInt();
            if (len < 0 || len > Protocol.MaxModStateBytes || len > reader.AvailableBytes || !names.Add(name)) throw new InvalidDataException("Invalid paired world state.");
            var data = new byte[len]; reader.GetBytes(data, len); if (name == Name) objectBytes = data;
        }
        if (reader.AvailableBytes != 0 || objectBytes == null) throw new InvalidDataException("Missing paired object identities.");
        Read(new NetDataReader(objectBytes));
    }

    public void PrepareLoad(SaveMetadata metadata, SaveGame native, bool host, PlayerId localPlayer)
    {
        // WorldSync restores every registered state and validates the bindings again before replay/admission.
        ReadPosition(metadata.Position);
        _loadBindings = new(_saveBindings); _loadingCarriers = new(); _carrierRecords.Clear();
        var carriers = Model.Pcs.Where(x => x.Location.Place == PcPlace.Carrier).ToArray();
        if (metadata.Carriers.Count != carriers.Length || metadata.Carriers.Any(x => !carriers.Any(p => p.Id == x.Pc))) throw new InvalidDataException("Incomplete carried PC payloads.");
        var hostCarrier = carriers.SingleOrDefault(x => x.Location.Actor == ActorSlot.Host);
        var nativeCarry = native.carryingComputer;
        if ((nativeCarry != null) != (hostCarrier != null) || (hostCarrier != null && _saveBindings[hostCarrier.Id] != "carry"))
            throw new InvalidDataException("Native carrier record mismatch.");
        native.carryingComputer = null;
        foreach (var pc in carriers)
        {
            var payload = metadata.Carriers.Single(x => x.Pc == pc.Id);
            var record = NativePcCodec.Read(payload.NativePc);
            CheckParts(pc, record);
            if (pc.Location.Actor == ActorSlot.Host)
            {
                // Use the native record to preserve any aliases with the rest of the native save graph.
                record = nativeCarry; CheckParts(pc, record);
            }
            else if (_saveBindings[pc.Id] != "remote-carry:" + pc.Location.Actor) throw new InvalidDataException("Invalid remote carrier binding.");
            _loadingCarriers.Add(pc.Id, record);
            if (pc.Location.Actor == (host ? ActorSlot.Host : ActorSlot.Guest) && pc.Location.Player == localPlayer)
            {
                if (host) { native.carryingComputer = record; _loadBindings[pc.Id] = "carry"; continue; }
                // A guest's own carried PC is a record the host holds without a mesh (for example a catalog case it
                // created). Loaded into the hands during the level load, it left a native PC load pending forever
                // (run E3). Keep it with the mod and spawn it through the ordinary pickup path after admission.
                _loadBindings[pc.Id] = "remote-carry:" + pc.Location.Actor; _guestCarry = (pc.Id, record); continue;
            }
            if (!host && pc.Location.Actor == ActorSlot.Host)
            {
                // Host remains the carrier. Keep its graph outside native storage and the guest's singleton;
                // phase 3f supplies its remote mesh. It must not become an interactable cupboard duplicate.
                _loadBindings[pc.Id] = "remote-carry:" + pc.Location.Actor; continue;
            }
            _loadBindings[pc.Id] = "storage:" + native.computersInStorage.Count;
            native.computersInStorage.Add(record);
        }
        _reading = true;
    }

    public void FinishHostRestore(PlayerId host)
    {
        _reading = false;
        // A fresh host has no admitted guest. Restore absent carriers to the shared cupboard, preserving IDs and payloads.
        var pcs = Model.Pcs.Select(x => x.Location.Place == PcPlace.Carrier &&
            (x.Location.Actor != ActorSlot.Host || x.Location.Player != host) ? new PcFact(x.Id, x.Provenance, PcLocation.Storage, x.Parts, x.Links) : x).ToArray();
        Model.Replace(Model.WorldId, Model.NextId, Model.Locations, pcs, Model.Retired);
        _saveBindings = _loadBindings ?? _saveBindings; _loadBindings = null; _loadingCarriers = null;
    }

    public void FinishGuestRestore()
    {
        _reading = false; _loadBindings = null; _loadingCarriers = null;
        if (_guestCarry is not { } carry) return;
        _guestCarry = null;
        _carrierRecords.Remove(carry.Id); _saveBindings.Remove(carry.Id);
        // Held changes replayed during admission may already have retired or moved it.
        if (Model.Pcs.Any(x => x.Id == carry.Id && x.Location.Place == PcPlace.Carrier && x.Location.Actor == ActorSlot.Guest))
            NativePcLifecycle.SpawnIntoHands(this, carry.Id, carry.Record);
    }

    public static SortedDictionary<string, PartInstance> Parts(ComputerSave pc)
    {
        var result = new SortedDictionary<string, PartInstance>(StringComparer.Ordinal);
        var expanded = new HashSet<long>();
        void Add(string path, PartInstance part)
        {
            if (part == null) return;
            if (result.Count >= ObjectWorld.MaxLinksPerPc) throw new InvalidDataException("PC graph exceeds binding limits.");
            result.Add(path, part);
            if (!expanded.Add(part.Pointer.ToInt64())) return;
            Array(path + ".m_caseFanSlots", part.m_caseFanSlots);
            Add(path + ".m_proxyFor", part.m_proxyFor); Add(path + ".m_generatedCopy", part.m_generatedCopy);
            var gpu = part.TryCast<GpuPartInstance>(); if (gpu != null) Add(path + ".WaterCoolBlock", gpu.WaterCoolBlock);
            var ram = part.TryCast<RamPartInstance>(); if (ram != null) Add(path + ".WaterCoolBlock", ram.WaterCoolBlock);
            var board = part.TryCast<MotherboardPartInstance>();
            if (board != null) { Add(path + ".CPUInstance", board.CPUInstance); Add(path + ".WaterCoolBlock", board.WaterCoolBlock); }
            var radiator = part.TryCast<RadiatorPartInstance>();
            if (radiator?.FanId != null) for (var i = 0; i < radiator.FanId.Count; i++) Add($"{path}.FanId[{i}]", radiator.FanId[i]);
            var cables = part.TryCast<CableSetPartInstance>();
            if (cables != null)
            {
                Add(path + ".cable24Pin", cables.cable24Pin); Add(path + ".cable8Pin", cables.cable8Pin); Add(path + ".cable6Pin", cables.cable6Pin);
                Add(path + ".cableSATAPower", cables.cableSATAPower); Add(path + ".cableSATAData", cables.cableSATAData);
                Array(path + ".cableSetPartInstances", cables.cableSetPartInstances);
            }
        }
        void Array(string path, IEnumerable items)
        { if (items == null) return; var i = 0; foreach (var item in items) Add(path + "[" + i++ + "]", (PartInstance)item); }
        // Native fields only: computed properties such as allStorageSlots repeat the same physical fields.
        foreach (var p in typeof(ComputerSave).GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (typeof(ComputerSave).GetField("NativeFieldInfoPtr_" + p.Name, BindingFlags.Static | BindingFlags.NonPublic) == null) continue;
            if (typeof(PartInstance).IsAssignableFrom(p.PropertyType)) Add(p.Name, (PartInstance)p.GetValue(pc));
            else if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().FirstOrDefault() == typeof(PartInstance)) Array(p.Name, p.GetValue(pc) as IEnumerable);
        }
        if (pc.m_loops != null) for (var i = 0; i < pc.m_loops.Count; i++) Array($"m_loops[{i}].m_components", pc.m_loops[i]?.m_components);
        if (pc.LEDStripSaveList != null) for (var i = 0; i < pc.LEDStripSaveList.Count; i++)
        { Add($"LEDStripSaveList[{i}].Part", pc.LEDStripSaveList[i].Part); Add($"LEDStripSaveList[{i}].InvPart", pc.LEDStripSaveList[i].InvPart); }
        if (pc.pipesInstalled != null) for (var i = 0; i < pc.pipesInstalled.Length; i++)
        {
            var pipe = pc.pipesInstalled[i]; if (pipe == null) continue;
            Add($"pipesInstalled[{i}].m_from.m_component", pipe.m_from.m_component);
            Add($"pipesInstalled[{i}].m_from.m_parentComponent", pipe.m_from.m_parentComponent);
            Add($"pipesInstalled[{i}].m_to.m_component", pipe.m_to.m_component);
            Add($"pipesInstalled[{i}].m_to.m_parentComponent", pipe.m_to.m_parentComponent);
        }
        if (!result.ContainsKey("caseID")) throw new InvalidDataException("PC graph is missing its case.");
        return result;
    }
}

/// <summary>Uses the game's serializer to retain the complete native PC graph, including private fields/software/provenance.
/// These bytes are host-produced save data, never accepted as guest command input.</summary>
internal static class NativePcCodec
{
    public static byte[] Write(ComputerSave pc)
    {
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, pc);
            if (stream.Length is < 1 or > CoopSavePair.MaxCarrierBytes) throw new InvalidDataException("Carried PC save exceeds limits.");
            return stream.ToArray();
        }
        finally { stream.Dispose(); }
    }
    public static ComputerSave Read(byte[] bytes)
    {
        if (bytes.Length is < 1 or > CoopSavePair.MaxCarrierBytes) throw new InvalidDataException("Invalid carried PC payload size.");
        var stream = new Il2CppSystem.IO.MemoryStream(bytes);
        try
        {
            var pc = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream).TryCast<ComputerSave>();
            if (pc?.caseID == null || stream.Position != stream.Length) throw new InvalidDataException("Invalid carried PC save graph.");
            NativeObjectState.Parts(pc); return pc;
        }
        finally { stream.Dispose(); }
    }
}
