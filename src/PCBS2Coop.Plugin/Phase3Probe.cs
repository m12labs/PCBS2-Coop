using System.Reflection;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Opt-in, observational phase 3 evidence. Requires the self test's disposable host/join setup.
/// Hooks never suppress calls or change arguments/results. Pointers are local diagnostic identities, never net IDs.
/// A fixed command file supports captures and camera positioning; gameplay actions still use ordinary input.
/// </summary>
internal sealed class Phase3Probe
{
    public static bool Enabled => AutoTest.IsEnabled && Environment.GetEnvironmentVariable("PCBS2COOP_PROBE") is "3" or "4";
    internal static bool ComponentsEnabled => Enabled && Environment.GetEnvironmentVariable("PCBS2COOP_PROBE") == "4";
    internal static string Phase => ComponentsEnabled ? "phase4" : "phase3";
    private static readonly Lazy<string> ProbeRunId = new(ReadRunId);
    public static string RunId => ProbeRunId.Value;
    private static Phase3Probe _current;
    private readonly string _dir;
    private readonly int _mainThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, string> _interactions = new();
    private readonly Dictionary<long, float> _calls = new();
    private long _sequence;
    private float _nextPoll;
    private int _commandsRead, _capture;
    private string _lastState, _lastScene;
    private bool _wasReady;
    private bool _sampling;
    private int _reloadStep;
    private float _reloadDeadline;
    private float _reloadMenuAt = -1;
    private readonly WorldReadiness _reloadReadiness = new();
    private bool _reloadCopyFixture;
    private byte[] _identityExpected;
    private readonly Phase4Probe _components;
    internal string EvidenceDir => _dir;

    public Phase3Probe()
    {
        _current = this;
        var role = Environment.GetEnvironmentVariable("PCBS2COOP_AUTOTEST")?.Trim().ToLowerInvariant();
        _dir = Path.Combine(Diagnostics.Dir, Phase, RunId, role);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "commands.txt"), "");
        InstallHooks();
        if (ComponentsEnabled) _components = new Phase4Probe(this);
        Event("enabled", new { run = RunId, role, version = Network.ModVersion.Value });
    }

    private static string ReadRunId()
    {
        var value = Environment.GetEnvironmentVariable("PCBS2COOP_PROBE_RUN") ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        if (value.Length is < 1 or > 48 || value.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("PCBS2COOP_PROBE_RUN must be 1-48 ASCII letters/digits/hyphens.");
        return value;
    }

    internal bool Safe => AutoTest.IsolatedSaveDir != null &&
        Path.GetFullPath(AutoTest.IsolatedSaveDir).StartsWith(Path.GetFullPath(Path.Combine(Diagnostics.Dir, Phase, RunId)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        IsWithin(SaveLoadSystem.s_saveDir ?? ".", AutoTest.IsolatedSaveDir);

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void Update(CoopRunner runner)
    {
        try
        {
            if (!Safe || Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.1f;
            _components?.Update();
            var state = State();
            if (state != _lastState) { Event("state", state); _lastState = state; }
            if (runner.Scene != _lastScene)
            {
                _lastScene = runner.Scene;
                _interactions.Clear();
                Event("scene", runner.Scene);
            }
            if (runner.Coop.WorldReady && !_wasReady) Capture("ready");
            _wasReady = runner.Coop.WorldReady;
            var lines = File.ReadAllLines(Path.Combine(_dir, "commands.txt"));
            while (_commandsRead < lines.Length)
            {
                var command = lines[_commandsRead].Trim();
                if (ComponentsEnabled && command.StartsWith("p4-", StringComparison.Ordinal) && !runner.Coop.WorldReady) break;
                _commandsRead++;
                Command(command, runner);
            }
            UpdateReloadCheck(runner);
        }
        catch (Exception e) { Plugin.Logger.LogError("PHASE3 probe: " + e); }
    }

    private void Command(string command, CoopRunner runner)
    {
        if (command.Length == 0) return;
        Event("operator-command", command);
        if (_components?.Command(command, runner) == true) return;
        if (command == "capture") Capture("operator");
        else if (command == "package-fixture-save")
        {
            // G7 setup only: native package creation/save outside a session, followed by a hash-matched
            // sidecar retaining the pre-fixture PC facts. Continue must refuse before admitting this world.
            if (!Safe || !runner.Coop.WorldReady || runner.Coop.Session.Role != Network.Role.Host || runner.Coop.Session.HasPeer)
                throw new InvalidOperationException("Package fixture requires an isolated ready host without a guest.");
            runner.StartCoroutine(PackageFixture(runner).WrapToIl2Cpp());
        }
        else if (command.StartsWith("activity-reserve "))
        {
            var coop = runner.Coop; var actor = coop.Activity.Get(coop.Session.World.Local);
            var pc = coop.Objects.Model.Pcs.Single(x => x.Id == actor.WorkingPc);
            var target = pc.Parts[int.Parse(command[17..])].Id;
            coop.Session.World.Submit(Network.CommandTypes.ActorActivity,
                Network.ActivityWorld.Reserve(actor, pc.Id, Network.ActivityOperation.Edit, target), out var op);
            Event("direct-request", new { purpose = "resource ownership seam, no component commit", op = op.ToString(), pc = pc.Id.Value, target = target.Value });
        }
        else if (command == "activity-release")
        {
            var coop = runner.Coop; var actor = coop.Activity.Get(coop.Session.World.Local);
            if (actor.Pending != null) coop.Session.World.Submit(Network.CommandTypes.ActorActivity, Network.ActivityWorld.Release(actor, actor.Pending.Token), out _);
        }
        else if (command == "activity-report")
        {
            var coop = runner.Coop; var state = new LiteNetLib.Utils.NetDataWriter(); coop.Activity.Write(state);
            var result = new { generation = coop.Session.World.Generation, revision = coop.Session.World.Revision,
                hash = Convert.ToHexString(SHA256.HashData(state.CopyData())), actors = coop.Activity.Actors.Select(a => new
                { actor = a.Actor.ToString(), player = a.Player.ToString(), a.Epoch, a.Version, work = a.WorkingPc.Value,
                    carried = coop.Activity.Carried(a.Actor).Value, pending = a.Pending?.Token.ToString(), targets = a.Pending?.Targets.Select(t => t.Value).ToArray() }),
                nativeState = GameController.Get()?.GetCurrentState()?.GetIl2CppType().Name,
                activeBench = NativeObjectState.BenchKey(Object.FindObjectOfType<WorkshopController>().m_activeBenchSlot),
                camera = Camera.main?.transform.position.ToString(),
                blockers = coop.Objects.Model.Pcs.Select(p => new { pc = p.Id.Value, reason = coop.Activity.MovementBlock(p.Id) }),
                remoteColliders = Object.FindObjectsOfType<Collider>().Count(c => c.transform.root.name.StartsWith("PCBS2Coop Remote")) };
            File.WriteAllText(Path.Combine(_dir, "activity-report.json"), JsonSerializer.Serialize(result)); Event("activity-report", result);
        }
        else if (command == "objects-report")
        {
            // Task 3e: logical model hash/IDs plus this peer's native counts. Compare the hash across peers.
            var coop = runner.Coop; var m = coop.Objects.Model;
            var wc = Object.FindObjectOfType<WorkshopController>(); var sl = Object.FindObjectOfType<SaveLoadSystem>();
            var result = new { generation = coop.Session.World.Generation, revision = coop.Session.World.Revision,
                hash = Convert.ToHexString(SHA256.HashData(m.ToBytes())), world = m.WorldId.ToString(), next = m.NextId, retired = m.Retired.Count,
                skipped = coop.Lifecycle?.Handler.SkippedIds,
                pcs = m.Pcs.Select(p => new { pc = p.Id.Value, origin = p.Provenance.Origin.ToString(), op = p.Provenance.Operation.ToString(),
                    place = p.Location.Place.ToString(), key = p.Location.Key, actor = p.Location.Actor.ToString(), sku = p.Parts.FirstOrDefault(x => p.Links.Any(l => l.Path == "caseID" && l.Part == x.Id))?.Sku,
                    parts = p.Parts.Select(x => x.Id.Value).ToArray() }),
                native = new { benchPcs = sl.benchSlots.Count(b => b?.GetCaseInSlot() != null), stored = sl.localSaveGame.computersInStorage.Count,
                    carried = coop.Objects.IdOf(wc.GetCarriedCase()).Value, carrying = wc.GetCarriedCase() != null, loading = wc.IsCaseLoading,
                    // Task 3f: where this game shows each PC, to compare visible placement across peers.
                    benches = sl.benchSlots.Where(b => b != null && (b.GetCaseInSlot() != null || b.m_caseLoadingToSlot))
                        .Select(b => new { bench = coop.Objects.LocationOf(b).Value, pc = coop.Objects.IdOf(b.GetCaseInSlot()).Value, loading = b.m_caseLoadingToSlot }).OrderBy(x => x.bench),
                    storedIds = sl.localSaveGame.computersInStorage.ToArray().Select(r => coop.Objects.IdOf(r).Value).OrderBy(x => x) },
                remote = new { carries = coop.RemoteCarries, box = coop.RemoteBoxShown, look = coop.RemoteBoxLook },
                catalog = CareerStatus.Get()?.GetInventory()?.Count, loadOps = LoadOps() };
            File.WriteAllText(Path.Combine(_dir, "objects-report.json"), JsonSerializer.Serialize(result)); Event("objects-report", result);
        }
        else if (command.StartsWith("transfer-request:", StringComparison.Ordinal))
        {
            // Pinned stale intents for two-peer race checks. These use the production host validation and native
            // projection, but bypass gesture capture deliberately; record them separately from ordinary input.
            var coop = runner.Coop; var world = coop.Session.World;
            var actor = coop.Activity.Get(world.Local) ?? throw new InvalidOperationException("Actor is not admitted.");
            var bits = command.Split(':');
            var id = new Network.NetObjectId(ulong.Parse(bits[2], System.Globalization.CultureInfo.InvariantCulture));
            byte[] payload = bits[1] switch
            {
                "pickup" when bits.Length == 4 => Network.PcTransfers.Pickup(actor.Epoch, id, new Network.NetObjectId(ulong.Parse(bits[3]))),
                "drop" when bits.Length == 5 => Network.PcTransfers.Drop(actor.Epoch, id, new Network.NetObjectId(ulong.Parse(bits[3])), new Network.NetObjectId(ulong.Parse(bits[4]))),
                "store" when bits.Length == 3 => Network.PcTransfers.Store(actor.Epoch, id),
                "retrieve" when bits.Length == 3 => Network.PcTransfers.Retrieve(actor.Epoch, id),
                _ => throw new ArgumentException("transfer-request:<pickup|drop|store|retrieve>:<pc>[:bench][:occupant]")
            };
            if (!world.Submit(Network.CommandTypes.PcTransfer, payload, out var op)) throw new InvalidOperationException("Transfer request was not submitted.");
            Event("direct-request", new { purpose = "pinned transfer intent for race validation, not gesture evidence", command, op = op.ToString() });
        }
        else if (command == "objects-check")
        {
            var coop = runner.Coop;
            var before = coop.Objects.Model.ToBytes();
            var count = 0; string error = null;
            try { count = coop.Objects.CheckProjection(coop.Session.World.Local, coop.Session.LocalIdentity.Player); }
            catch (Exception e) { error = e.Message; }
            var unchanged = before.SequenceEqual(coop.Objects.Model.ToBytes());
            var result = new { passed = error == null && unchanged, count, unchanged, error, revision = coop.Session.World.Revision };
            File.WriteAllText(Path.Combine(_dir, "objects-check.json"), JsonSerializer.Serialize(result));
            Event("objects-check", result);
        }
        else if (command == "peripherals-report")
        {
            // Task 3g: shared slot facts (hash to compare across peers) and what this game shows in each slot.
            var coop = runner.Coop; var ws = coop.Workstations; var state = new LiteNetLib.Utils.NetDataWriter(); ws.Model.Write(state);
            string check = null;
            try { ws.CheckNative(); } catch (Exception e) { check = e.Message; }
            var result = new { revision = coop.Session.World.Revision, hash = Convert.ToHexString(SHA256.HashData(state.CopyData())),
                slots = ws.Model.Slots.Select(s => $"{s.Bench}/{s.Slot}={s.Path}"), native = ws.Report(), settled = ws.Settled, check,
                unsupported = NativeWorkstations.Unsupported(), inventory = CareerStatus.Get()?.GetPeripheralInventory()?.Count };
            File.WriteAllText(Path.Combine(_dir, "peripherals-report.json"), JsonSerializer.Serialize(result)); Event("peripherals-report", result);
        }
        else if (command.StartsWith("chat:", StringComparison.Ordinal))
        {
            // Task 3h: the same handler the chat box's Enter calls; typed input is certified separately.
            Event("direct-call", "CoopController.SubmitChat (the chat box's submit handler), not typed-input evidence");
            runner.Coop.SubmitChat(command[5..]);
        }
        else if (command == "chat-report")
        {
            var coop = runner.Coop;
            var result = new { role = coop.Session.Role.ToString(), sync = coop.SyncState, requestWaiting = coop.SyncRequestWaiting, outOfSync = coop.OutOfSync,
                guestState = coop.Session.GuestState.ToString(), marker = runner.Chat.MarkerShown, typing = runner.Chat.Typing,
                lines = coop.ChatLines.Select(l => l.Text) };
            File.WriteAllText(Path.Combine(_dir, "chat-report.json"), JsonSerializer.Serialize(result)); Event("chat-report", result);
        }
        else if (command == "fault-projection")
        {
            // Task 3h: injected fault. The next accepted remote bench drop fails on this game before its native load.
            runner.Coop.Objects.FailNextRemoteLoad = true;
            Event("injected-fault", "the next remote bench load on this game fails (repair check), not a native failure");
        }
        else if (command == "summary-report")
        {
            // Task 3h: one file per game with every shared-state hash, to compare both games at a checkpoint.
            var coop = runner.Coop; var world = coop.Session.World;
            string Hash(Action<LiteNetLib.Utils.NetDataWriter> write) { var w = new LiteNetLib.Utils.NetDataWriter(); write(w); return Convert.ToHexString(SHA256.HashData(w.CopyData())); }
            string objectsCheck = null, perifCheck = null;
            try { coop.Objects.CheckProjection(world.Local, coop.Session.LocalIdentity.Player); } catch (Exception e) { objectsCheck = e.Message; }
            try { coop.Workstations.CheckNative(); } catch (Exception e) { perifCheck = e.Message; }
            var m = coop.Objects.Model;
            var result = new { role = coop.Session.Role.ToString(), generation = world.Generation, revision = world.Revision, chain = world.ChainHash.ToString("X16"),
                divergence = world.Divergence, projectionFault = coop.Objects.ProjectionFault, sync = coop.SyncState,
                objects = Convert.ToHexString(SHA256.HashData(m.ToBytes())), activity = Hash(coop.Activity.Write), peripherals = Hash(coop.Workstations.Model.Write),
                pcs = m.Pcs.Count, next = m.NextId, retired = m.Retired.Count, objectsCheck, perifCheck,
                placement = m.Pcs.Select(p => $"{p.Id.Value}@{p.Location.Place}:{p.Location.Key}{p.Location.Actor}").OrderBy(x => x) };
            File.WriteAllText(Path.Combine(_dir, "summary-report.json"), JsonSerializer.Serialize(result)); Event("summary-report", result);
        }
        else if (command.StartsWith("perif-request:", StringComparison.Ordinal))
        {
            // Pinned intent (optionally naming a stale "from" path) for race checks; bypasses the gesture deliberately.
            var coop = runner.Coop; var world = coop.Session.World; var bits = command.Split(':', 5);
            if (bits.Length < 4) throw new ArgumentException("perif-request:<bench>:<slot>:<part>[:from path]");
            int bench = int.Parse(bits[1]), slot = int.Parse(bits[2]);
            var from = bits.Length == 5 ? bits[4] : coop.Workstations.Model.Get(bench, slot)?.Path ?? "";
            world.Submit(Network.CommandTypes.PeripheralSwap, Network.WorkstationWorld.Swap(coop.Activity.Get(world.Local)!.Epoch, bench, slot, from, bits[3]), out var op);
            Event("direct-request", new { purpose = "pinned peripheral intent for race validation, not gesture evidence", command, op = op.ToString() });
        }
        else if (command.StartsWith("perif-swap:", StringComparison.Ordinal))
        {
            // The native SwapPerif call the inventory's install button makes, so the production intercept handles it.
            var bits = command.Split(':');
            if (bits.Length != 4) throw new ArgumentException("perif-swap:<bench>:<slot>:<part>");
            var table = Object.FindObjectOfType<SaveLoadSystem>().GetBenchPerifSlots();
            PeripheralSlot target = null;
            foreach (var entry in table) if ((int)entry.Key == int.Parse(bits[1])) target = entry.Value[int.Parse(bits[2])];
            PartInstance part = null;
            foreach (var p in CareerStatus.Get().GetPeripheralInventory()) if (p != null && p.GetPartId() == bits[3]) { part = p; break; }
            if (target == null || part == null) throw new ArgumentException("unknown slot or peripheral part");
            target.SwapPerif(part);
            Event("direct-call", new { purpose = "native SwapPerif through the co-op intercept, not UI evidence", command });
        }
        else if (command.StartsWith("diag-template-store:", StringComparison.Ordinal))
        {
            // Native diagnostic, not a lifecycle command: put a catalog-style record (as the native case switch builds
            // it) straight into this peer's storage, optionally after the paired-save codec, without a logical ID.
            var codec = command.EndsWith(":1", StringComparison.Ordinal);
            var item = CareerStatus.Get().GetInventory().ToArray().First(p => p != null && p.GetPartId() == "Case_Kolink_4");
            var record = NativePcLifecycle.NewFromCatalog(item);
            if (codec) record = NativePcCodec.Read(NativePcCodec.Write(record));
            Object.FindObjectOfType<SaveLoadSystem>().localSaveGame.computersInStorage.Add(record);
            Event("direct-setup", new { fixture = "catalog-style record in native storage", codec, sku = record.caseID.GetPartId(), fans = record.caseFanSlots?.Length });
        }
        else if (command == "load-ops") Event("load-ops", LoadOps());
        else if (command.StartsWith("native-bench:", StringComparison.Ordinal))
        {
            // Vanilla comparison only: direct native pickup/drop by bench index, outside any co-op session.
            if (runner.Coop.Session.IsRunning) throw new InvalidOperationException("native-bench is for a solo comparison world.");
            var bits = command.Split(':'); var bench = Object.FindObjectOfType<SaveLoadSystem>().benchSlots[int.Parse(bits[2])];
            if (bits[1] == "pickup") bench.OnPickupPC();
            else if (bits[1] == "drop") Object.FindObjectOfType<WorkshopController>().DropCase(bench);
            else throw new ArgumentException("native-bench:<pickup|drop>:<bench index>");
            Event("direct-call", new { purpose = "solo vanilla comparison", command });
        }
        else if (command.StartsWith("solo-load:", StringComparison.Ordinal))
        {
            // Load a disposable save from the isolated root as an ordinary (non-co-op) Free Build.
            if (runner.Coop.Session.IsRunning || runner.Scene != CoopRunner.MenuScene) throw new InvalidOperationException("solo-load needs the main menu without a session.");
            var level = Object.FindObjectOfType<LevelLoadPersistency>();
            level.gameModeToLoad = GameMode.FREEBUILD;
            level.LoadGameFromDir(command["solo-load:".Length..], CoopRunner.WorkshopScene, false);
            Event("direct-call", new { purpose = "solo vanilla comparison load", command, saveDir = SaveLoadSystem.s_saveDir });
        }
        else if (command == "pipes-report")
        {
            // Explicit capture: the native load's FixForVersion dereferences every installed pipe's source component.
            var coop = runner.Coop; var sl = Object.FindObjectOfType<SaveLoadSystem>(); var wc = Object.FindObjectOfType<WorkshopController>();
            var rows = new List<object>();
            foreach (var b in sl.benchSlots) { var pc = b?.GetCaseInSlot() != null ? b.GetComputer(true) : null; if (pc?.caseID != null) rows.Add(Pipes("bench:" + coop.Objects.LocationOf(b).Value, coop.Objects.IdOf(b.GetCaseInSlot()).Value, pc)); }
            var stored = sl.localSaveGame.computersInStorage;
            for (var i = 0; i < stored.Count; i++) rows.Add(Pipes("storage:" + i, coop.Objects.IdOf(stored[i]).Value, stored[i]));
            if (wc.GetCarriedCase() != null && !wc.IsCaseLoading) rows.Add(Pipes("carry", coop.Objects.IdOf(wc.GetCarriedCase()).Value, wc.GetCarriedComputer()));
            // Live pipe objects: why WaterPipe.Save's connector -> ComponentPC lookup succeeds or fails.
            static string Connector(WaterConnector c)
            {
                if (c == null) return c is null ? "null" : "destroyed";
                var active = c.GetComponentInParent<ComponentPC>(); var any = c.GetComponentInParent<ComponentPC>(true);
                return $"{c.name} active={c.gameObject.activeInHierarchy} comp={(active == null ? "none" : active.name)} compInactive={(any == null ? "none" : any.name)} path={PathOf(c.transform)}";
            }
            foreach (var pipe in Object.FindObjectsOfType<WaterPipe>(true))
            {
                var mesh = pipe.GetComponentInParent<Case>(true);
                rows.Add(new { where = "mesh", pc = coop.Objects.IdOf(mesh).Value, path = PathOf(pipe.transform), active = pipe.gameObject.activeInHierarchy, from = Connector(pipe.m_from), to = Connector(pipe.m_to) });
            }
            File.WriteAllText(Path.Combine(_dir, "pipes-report.json"), JsonSerializer.Serialize(rows)); Event("pipes-report", rows);
        }
        else if (command == "identity-save")
        {
            Event("direct-call", "identity regression: native CreateSaveGame(auto Free Build), not input-path evidence");
            Object.FindObjectOfType<SaveLoadSystem>().CreateSaveGame(SaveLoadSystem.s_autoFreeBuildSaveName, false, "");
            Capture("identity-save");
        }
        else if (command == "identity-roundtrip")
        {
            var pcs = new List<ComputerSave>();
            foreach (var b in Object.FindObjectOfType<SaveLoadSystem>().benchSlots)
            { var pc = b.GetComputer(true); if (pc?.caseID != null) pcs.Add(pc); }
            foreach (var pc in Object.FindObjectOfType<SaveLoadSystem>().localSaveGame.computersInStorage) pcs.Add(pc);
            var carried = Object.FindObjectOfType<WorkshopController>().GetCarriedComputer(); if (carried?.caseID != null) pcs.Add(carried);
            var rows = pcs.Select(pc =>
            {
                var bytes = NativePcCodec.Write(pc); var restored = NativePcCodec.Read(bytes); var again = NativePcCodec.Write(restored);
                return new { sku = pc.caseID.GetPartId(), bytes = bytes.Length, identical = bytes.AsSpan().SequenceEqual(again),
                    bindings = NativeObjectState.Parts(pc).Count, restoredBindings = NativeObjectState.Parts(restored).Count };
            }).ToArray();
            var result = new { passed = rows.Length > 0 && rows.All(x => x.identical && x.bindings == x.restoredBindings), rows };
            File.WriteAllText(Path.Combine(_dir, "identity-roundtrip.json"), JsonSerializer.Serialize(result)); Event("identity-roundtrip", result);
        }
        else if (command is "identity-resume" or "identity-fixture-resume")
        {
            if (_reloadStep != 0) throw new InvalidOperationException("A reload check is already running.");
            var before = runner.Coop.Objects.Model.ToBytes();
            if (command == "identity-fixture-resume")
            {
                var fixture = Path.Combine(AutoTest.IsolatedSaveDir, "ReloadFixture", "autofreebuild.binary.zip");
                var saved = new NativeObjectState(); saved.ReadPosition(Network.CoopSavePair.ReadSidecar(fixture).Position);
                before = saved.Model.ToBytes();
            }
            File.WriteAllBytes(Path.Combine(_dir, "identity-before.bin"), before);
            var expected = new Network.ObjectWorld(); expected.Read(new LiteNetLib.Utils.NetDataReader(before));
            var host = runner.Coop.Session.LocalIdentity.Player;
            var pcs = expected.Pcs.Select(x => x.Location.Place == Network.PcPlace.Carrier &&
                (x.Location.Actor != Network.ActorSlot.Host || x.Location.Player != host)
                ? x with { Location = Network.PcLocation.Storage } : x).ToArray();
            expected.Replace(expected.WorldId, expected.NextId, expected.Locations, pcs, expected.Retired);
            _identityExpected = expected.ToBytes(); _reloadCopyFixture = command == "identity-fixture-resume";
            File.WriteAllBytes(Path.Combine(_dir, "identity-expected.bin"), _identityExpected);
            _reloadStep = 1; _reloadMenuAt = -1; _reloadDeadline = Time.realtimeSinceStartup + 45;
            Event("direct-call", "identity regression: return to menu, Host Continue, compare exact logical facts");
            Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
        }
        else if (command == "identity-remote-fixture")
        {
            // Supplemental persistence fixture, not a certified guest-input/transfer path (3d-3f).
            var coop = runner.Coop;
            if (coop.Session.Role != Network.Role.Host || coop.Session.HasPeer) throw new InvalidOperationException("Remote fixture requires an isolated host with no peer.");
            coop.Objects.CaptureHost(coop.Session.LocalIdentity.Player);
            var model = coop.Objects.Model;
            var pc = model.Pcs.Last(x => x.Location.Place == Network.PcPlace.Bench);
            var bench = Object.FindObjectOfType<SaveLoadSystem>().benchSlots.Single(x => NativeObjectState.BenchKey(x) == pc.Location.Key);
            var save = bench.GetComputer(true); bench.DestroyComputer();
            var fixturePc = pc with { Location = Network.PcLocation.Carried(Network.ActorSlot.Guest, Network.PlayerId.New()) };
            model.Replace(model.WorldId, model.NextId, model.Locations, model.Pcs.Select(x => x.Id == pc.Id ? fixturePc : x), model.Retired);
            coop.Objects.BindCarrierPayload(pc.Id, save);
            Event("direct-setup", new { fixture = "remote actor carrier persistence", pc = pc.Id.Value, sku = save.caseID.GetPartId() });
        }
        else if (command == "identity-duplicate-fixture")
        {
            PartInstance template = null;
            foreach (var part in CareerStatus.Get().GetInventory()) if (part.GetPartId() == "Case_Kolink_4") { template = part; break; }
            if (template == null) throw new InvalidOperationException("Fixture case missing from this catalog.");
            var saves = Object.FindObjectOfType<SaveLoadSystem>();
            saves.AddComputerToStorage(new ComputerSave { caseID = template });
            saves.AddComputerToStorage(new ComputerSave { caseID = template });
            Event("direct-setup", new { fixture = "two stored PCs sharing the same native case template pointer", sku = template.GetPartId(), pointer = template.Pointer.ToInt64() });
        }
        else if (command == "identity-menu") Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
        else if (command == "identity-join")
        { if (!runner.Coop.Join("127.0.0.1", 7777, out var error)) throw new InvalidOperationException(error); }
        else if (command == "native-save")
        {
            Event("direct-call", "SaveCurrentGame: supplemental check, not input-path evidence");
            Object.FindObjectOfType<SaveLoadSystem>()?.SaveCurrentGame();
        }
        else if (command == "skip-import-tutorial")
        {
            // Setup only, before opening storage. This avoids importing a file from the owner's PC folder.
            // Retrieval/deletion are still exercised through their ordinary UI, with the setup recorded separately.
            foreach (var onboarding in JobTutorialHintUi.freebuildOnboardingClasses)
                if (onboarding.TryCast<ImportPCOnboarding>() != null)
                {
                    onboarding.SetOnboardingComplete();
                    Event("direct-setup", "ImportPCOnboarding.SetOnboardingComplete before opening storage");
                }
        }
        else if (command == "repro-reload")
        {
            if (_reloadStep != 0) throw new InvalidOperationException("A reload check is already running.");
            var fixture = Path.Combine(AutoTest.IsolatedSaveDir, "ReloadFixture", "autofreebuild.binary.zip");
            if (!File.Exists(fixture)) throw new FileNotFoundException("Install the disposable reload fixture first.", fixture);
            var resultFile = Path.Combine(_dir, "reload-result.json");
            if (File.Exists(resultFile)) File.Delete(resultFile);
            Event("reload-check-start", new { fixture = Path.GetFileName(fixture), hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))) });
            _reloadStep = 1;
            _reloadCopyFixture = true; _identityExpected = null;
            _reloadMenuAt = -1;
            _reloadDeadline = Time.realtimeSinceStartup + 35;
            Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
        }
        else if (command.StartsWith("aim:", StringComparison.Ordinal)) Aim(command);
        else Event("unknown-command", command);
    }

    private void UpdateReloadCheck(CoopRunner runner)
    {
        if (_reloadStep == 0) return;
        if (_reloadStep == 1 && runner.Scene == CoopRunner.MenuScene && runner.Menu != null)
        {
            // Match the real caller after the menu has initialized, rather than calling a not-yet-ready menu
            // directly on its first frame. Copy the immutable fixture only after leaving the previous world.
            if (_reloadMenuAt < 0) _reloadMenuAt = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _reloadMenuAt < 3) return;
            var coopDir = Path.Combine(AutoTest.IsolatedSaveDir, "Coop");
            if (_reloadCopyFixture)
            {
                var fixture = Path.Combine(AutoTest.IsolatedSaveDir, "ReloadFixture", "autofreebuild.binary.zip");
                File.Copy(fixture, Path.Combine(coopDir, "autofreebuild.binary.zip"), true);
                if (File.Exists(fixture + ".coop")) File.Copy(fixture + ".coop", Path.Combine(coopDir, "autofreebuild.binary.zip.coop"), true);
            }
            _reloadReadiness.Reset();
            _reloadStep = 2;
            Event("direct-call", "reload check: CoopController.HostWorld(7777, continueWorld:true)");
            if (!runner.Coop.HostWorld(7777, true, out var error)) throw new InvalidOperationException(error);
        }
        else if (_reloadStep == 2)
        {
            _reloadReadiness.Update(runner.Scene == CoopRunner.WorkshopScene);
            if (_reloadReadiness.Ready || Time.realtimeSinceStartup > _reloadDeadline)
            {
                var cases = Object.FindObjectsOfType<Case>().Length;
                var stored = Object.FindObjectOfType<SaveLoadSystem>()?.localSaveGame?.computersInStorage?.Count ?? -1;
                // Recorded B2 fixture: populated MSI and be quiet PCs, one benched Kolink and one stored Kolink.
                var identity = _identityExpected != null && runner.Coop.Objects.Initialized && _identityExpected.AsSpan().SequenceEqual(runner.Coop.Objects.Model.ToBytes());
                var result = new { passed = _reloadReadiness.Ready && (_identityExpected != null ? identity : cases == 3 && stored == 1), identity,
                    cases, stored, waiting = _reloadReadiness.Waiting, failure = runner.Coop.LastFailure };
                if (_identityExpected != null && runner.Coop.Objects.Initialized) File.WriteAllBytes(Path.Combine(_dir, "identity-after.bin"), runner.Coop.Objects.Model.ToBytes());
                Capture("reload-check");
                File.WriteAllText(Path.Combine(_dir, "reload-result.json"), JsonSerializer.Serialize(result));
                Event("reload-check-result", result);
                _reloadStep = 0;
            }
        }
        else if (Time.realtimeSinceStartup > _reloadDeadline)
        {
            File.WriteAllText(Path.Combine(_dir, "reload-result.json"), "{\"passed\":false,\"error\":\"main-menu timeout\"}");
            _reloadStep = 0;
        }
    }

    private void InstallHooks()
    {
        var harmony = new Harmony(Plugin.Guid + ".phase3probe");
        Observe(typeof(WorkshopController), "PickUpCase", "DropCase", "StoreCarriedCase", "DestroyCarriedCase", "SpawnCase", "SetCaseLoading", "PickupPackage", "PickUpPostIt", "DropPackage", "DropPostIt");
        Observe(typeof(BenchSlot), "OnPickupPC", "DropPC", "SetCaseInSlot", "SetComputer", "DestroyComputer", "SetPackageInSlot");
        Observe(typeof(SaveLoadSystem), "PickupComputerFromStorage", "AddComputerToStorage", "RemoveComputer", "SaveComputer", "SaveComputersData", "SavePlayerData", "FreebuildSave", "SaveCurrentGame", "CreateSaveGame", "LoadComputerToSlot", "LoadComputerToSlotAsync", "LoadComputerToHandsAsync", "LoadPlayerData", "OnSpawn");
        Observe(typeof(StoredPC), "PickUp", "DeletePC", "RenamePC", "Init");
        Observe(typeof(ItemDisplay), "InstallButtonClicked", "ButtonPressedPackage");
        Observe(typeof(CareerStatus), "AddPartToInventory", "RemovePartFromInventory", "AddCaseToInventory", "RemoveItemFromInventory");
        Observe(typeof(Inventory), "ShowInventory", "HideInventory");
        Observe(typeof(Case), "OnPickup");
        Observe(typeof(PeripheralSlot), "Swap", "LoadPeripheralAsync");
        Observe(typeof(PostItSlot), "OnBenchSwap");
        Observe(typeof(NewMainMenu), "ContinueFreeBuild");
        Observe(typeof(LevelLoadPersistency), "LoadGame", "LoadGameFromDir", "StartNewGame", "ResetFreebuild");
        Observe(typeof(SaveLoadSystem), "LoadGame");
        // Native load steps inside SaveLoadSystem.LoadGame's own catch. An enter without an exit (and the interop
        // trampoline's logged IL2CPP stack) locates a step that throws while loading a save.
        Observe(typeof(SaveLoadSystem), "LoadComputersData", "LoadPostItsData", "LoadPeripheralsData", "LoadPC", "LoadPackageToSlot", "RefreshSlots", "BlockForPendingSaves");
        Observe(typeof(ComputerSave), "FixForVersion");
        Observe(typeof(PartsDatabase), "InitComputerLoading", "RestoreCustomCables");
        Observe(typeof(CustomCaseManager), "RestoreFromSaveList", "FixForVersion");
        Observe(typeof(CareerStatus), "UpdateInventory", "SetState");
        Observe(typeof(GameController), "LoadSafePosStack");
        foreach (var type in new[] { typeof(BenchSlot), typeof(InWorldInventory), typeof(Cupboard), typeof(PeripheralSlot), typeof(PostItSlot) })
        {
            var method = AccessTools.DeclaredMethod(type, "GetInteractions");
            if (method == null) { Event("missing-interaction-method", type.Name); continue; }
            try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(Phase3Probe), nameof(InteractionsAfter))); }
            catch (Exception e) { Event("hook-error", new { method = type.Name + ".GetInteractions", error = e.Message }); }
        }

        void Observe(Type type, params string[] names)
        {
            foreach (var name in names)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.Name == name).ToArray();
                if (methods.Length == 0) Event("missing-method", type.Name + "." + name);
                foreach (var method in methods)
                {
                    try
                    {
                        // A second hook on code another hooked method already runs crashes the game (GamePatches.NativeCode).
                        if (GamePatches.FoldedWith(method) is { } folded) { Event("hook-skipped", new { method = method.ToString(), sharesCodeWith = folded.DeclaringType?.Name + "." + folded.Name }); continue; }
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(Phase3Probe), nameof(Before)),
                            postfix: new HarmonyMethod(typeof(Phase3Probe), method.ReturnType == typeof(void) ? nameof(AfterVoid) : nameof(AfterValue)));
                        Event("hook", method.ToString());
                    }
                    catch (Exception e) { Event("hook-error", new { method = type.Name + "." + name, error = e.Message }); }
                }
            }
        }
    }

    private static void Before(MethodBase __originalMethod, object __instance, object[] __args, out long __state)
    {
        __state = 0;
        var p = _current;
        if (p == null || p._sampling || Environment.CurrentManagedThreadId != p._mainThread) return;
        try
        {
            if (!p.Safe) return;
            __state = ++p._sequence;
            p._calls[__state] = Time.realtimeSinceStartup;
            p.Event("enter", new { call = __state, method = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name, instance = Describe(__instance), args = __args.Select(Describe).ToArray(), state = p.State() });
        }
        catch (Exception e) { Plugin.Logger.LogWarning("PHASE3 observer entry: " + e.Message); }
    }

    private static void AfterVoid(MethodBase __originalMethod, long __state) => After(__originalMethod, __state, null);
    private static void AfterValue(MethodBase __originalMethod, long __state, object __result) => After(__originalMethod, __state, __result);
    private static void After(MethodBase method, long call, object result)
    {
        if (call == 0) return;
        var p = _current;
        try
        {
            p._calls.Remove(call, out var start);
            p.Event("exit", new { call, method = method.DeclaringType?.Name + "." + method.Name, elapsed = Time.realtimeSinceStartup - start, result = Describe(result), state = p.State() });
            if (result is ComputerSave save) p.Event("saved-pc", Pc(save));
        }
        catch (Exception e) { Plugin.Logger.LogWarning("PHASE3 observer exit: " + e.Message); }
    }

    private static void InteractionsAfter(object __instance, Il2CppSystem.Collections.Generic.List<WorldInteraction> __result)
    {
        var p = _current;
        if (p == null || Environment.CurrentManagedThreadId != p._mainThread || p._sampling) return;
        try
        {
            if (!p.Safe || __result == null) return;
            var key = Describe(__instance);
            var actions = new List<string>();
            foreach (var x in __result) actions.Add($"{x.m_control}:{x.m_name}");
            var text = string.Join("; ", actions);
            if (p._interactions.TryGetValue(key, out var old) && text == old) return;
            p._interactions[key] = text;
            p.Event("interactions", new { target = key, actions = text });
        }
        catch (Exception e) { Plugin.Logger.LogWarning("PHASE3 interactions: " + e.Message); }
    }

    private string State()
    {
        var samplingWas = _sampling;
        _sampling = true;
        try
        {
            var wc = Object.FindObjectOfType<WorkshopController>();
            var sl = Object.FindObjectOfType<SaveLoadSystem>();
            var db = PartsDatabase.s_instance;
            var inv = CareerStatus.Get()?.GetInventory();
            var stored = sl?.localSaveGame?.computersInStorage;
            var benches = sl?.benchSlots == null ? "none" : string.Join(";", sl.benchSlots.Select((b, i) => $"{i}:{Describe(b?.GetCaseInSlot())}:{b?.m_caseLoadingToSlot}"));
            return $"scene={SceneManager.GetActiveScene().name} carry={Describe(wc?.GetCarriedCase())} package={Describe(wc?.GetCarriedPackage())} note={Describe(wc?.GetCarriedPostIt())} " +
                $"caseLoading={wc?.IsCaseLoading} active={Describe(wc?.m_activeBenchSlot)} cases={Object.FindObjectsOfType<Case>().Length} inventory={inv?.Count} storage={stored?.Count} " +
                $"pending={db?.m_loadPCOperations?.Count}/{db?.m_loadPartOperations?.Count}/{db?.m_loadPlugOperations?.Count}/{db?.m_loadCableOperations?.Count}/{db?.m_loadPrefabOperations?.Count} saveThread={SaveLoadSystem.WaitingOnThread()} benches=[{benches}]";
        }
        finally { _sampling = samplingWas; }
    }

    private void Capture(string reason)
    {
        _sampling = true;
        try
        {
            var sb = new StringBuilder();
            var sl = Object.FindObjectOfType<SaveLoadSystem>();
            var wc = Object.FindObjectOfType<WorkshopController>();
            sb.AppendLine($"phase3 {RunId} capture {++_capture} {reason} scene={SceneManager.GetActiveScene().name}");
            sb.AppendLine($"game={GameInfo.ReadGame()} content={GameInfo.ReadContent()} unity={Application.unityVersion}");
            sb.AppendLine(State());
            var objects = CoopController.Instance?.Objects;
            if (objects?.Initialized == true)
            {
                var data = objects.Model.ToBytes();
                File.WriteAllBytes(Path.Combine(_dir, $"capture-{_capture:D3}-objects.bin"), data);
                sb.AppendLine($"OBJECTS world={objects.Model.WorldId} next={objects.Model.NextId} bytes={data.Length} hash={Convert.ToHexString(SHA256.HashData(data))} retired={objects.Model.Retired.Count}");
                foreach (var pc in objects.Model.Pcs)
                    sb.AppendLine($"OBJECT {pc.Id} place={pc.Location.Place}:{pc.Location.Key}:{pc.Location.Actor}:{pc.Location.Player} parts={pc.Parts.Count} links={pc.Links.Count}");
            }
            var activity = CoopController.Instance?.Activity;
            if (activity != null)
                foreach (var actor in activity.Actors)
                    sb.AppendLine($"ACTOR {actor.Actor} player={actor.Player} epoch={actor.Epoch} version={actor.Version} work={actor.WorkingPc} carry={activity.Carried(actor.Actor)} pending={actor.Pending?.Token} targets={string.Join(",", actor.Pending?.Targets ?? Array.Empty<Network.NetObjectId>())}");
            _sampling = true;
            if (sl?.benchSlots != null)
                for (var i = 0; i < sl.benchSlots.Length; i++)
                {
                    var b = sl.benchSlots[i];
                    sb.AppendLine($"SAVE-BENCH {i} {Describe(b)} type={b.m_type} spawn={b.m_spawnStage} delivery={b.m_deliverySubtype} workstation={Describe(b.m_workStation)} perifBench={b.m_workStation?.m_benchID}");
                    sb.AppendLine($"  reset={Describe(b.PlayerResetPosition)} caseCollider={Collider(b.m_caseCol)} packageCollider={Collider(b.m_packageCol)}");
                    var pc = b.GetComputer(false);
                    if (pc != null) sb.AppendLine("  " + JsonSerializer.Serialize(Pc(pc)));
                }
            sb.AppendLine($"CARRIER {Describe(wc?.GetCarriedCase())} transform={Describe(wc?.GetCarriedTransform())} player={Describe(wc?.playerTransform)} camera={Describe(wc?.mainCamera)}");
            if (wc?.GetCarriedCase() != null) sb.AppendLine(JsonSerializer.Serialize(Pc(wc.GetCarriedComputer())));
            if (sl?.localSaveGame?.computersInStorage != null)
                for (var i = 0; i < sl.localSaveGame.computersInStorage.Count; i++) sb.AppendLine($"STORED {i} {JsonSerializer.Serialize(Pc(sl.localSaveGame.computersInStorage[i]))}");
            var inventory = CareerStatus.Get()?.GetInventory();
            if (inventory != null)
                foreach (var p in inventory) sb.AppendLine($"INVENTORY {Describe(p)}");
            var peripherals = CareerStatus.Get()?.GetPeripheralInventory();
            if (peripherals != null)
                foreach (var p in peripherals) sb.AppendLine($"PERIPHERAL-INVENTORY {Describe(p)}");
            DumpTargets<InWorldInventory>("inventory");
            DumpTargets<Cupboard>("cupboard");
            DumpTargets<PeripheralSlot>("peripheral");
            DumpTargets<PostItSlot>("note");
            DumpTargets<Package>("package");
            foreach (var text in Object.FindObjectsOfType<TMPro.TMP_Text>())
                if (!string.IsNullOrWhiteSpace(text.text)) sb.AppendLine($"TEXT {PathOf(text.transform)} = {text.text.Replace('\n', ' ')}");
            foreach (var button in Object.FindObjectsOfType<UnityEngine.UI.Button>(true))
            {
                var callbacks = Enumerable.Range(0, button.onClick.GetPersistentEventCount()).Select(i => button.onClick.GetPersistentMethodName(i));
                sb.AppendLine($"BUTTON {PathOf(button.transform)} active={button.gameObject.activeInHierarchy} interactable={button.interactable} persistent=[{string.Join(",", callbacks)}]");
            }
            var path = Path.Combine(_dir, $"capture-{_capture:000}.txt");
            File.WriteAllText(path, sb.ToString());
            CoopRunner.Instance.StartCoroutine(Screenshot(Path.ChangeExtension(path, ".png")).WrapToIl2Cpp());
            Event("capture", new { reason, file = Path.GetFileName(path) });

            void DumpTargets<T>(string label) where T : Component
            {
                var targets = Object.FindObjectsOfType<T>(true).OrderBy(x => PathOf(x.transform)).ToArray();
                for (var i = 0; i < targets.Length; i++)
                {
                    var t = targets[i];
                    sb.AppendLine($"TARGET {label}:{i} {Describe(t)} active={t.gameObject.activeInHierarchy} pos={t.transform.position} forward={t.transform.forward}");
                    if (t is PostItSlot note) sb.AppendLine($"  note={Describe(note.GetPostIt())} bench={Describe(note.m_benchSlot)} packageSlot={note.IsPackageSlot}");
                    foreach (var col in t.GetComponentsInChildren<Collider>(true)) sb.AppendLine("  " + Collider(col));
                }
            }
        }
        finally { _sampling = false; }
    }

    private IEnumerator PackageFixture(CoopRunner runner)
    {
        var coop = runner.Coop;
        var position = coop.Session.World.CapturePosition();
        var carriers = coop.Objects.CaptureCarriers();
        var wc = Object.FindObjectOfType<WorkshopController>();
        if (!wc.CarryingEmpty()) throw new InvalidOperationException("Package fixture needs empty hands.");
        coop.Session.Stop("G7 disposable package fixture setup");
        var item = CareerStatus.Get().GetInventory().ToArray().First(p => p != null);
        if (!wc.TrySpawnPackageInHands(out var package, item) || package == null)
            throw new InvalidOperationException("Native package fixture creation failed.");
        var path = Object.FindObjectOfType<SaveLoadSystem>().CreateSaveGame(SaveLoadSystem.s_autoFreeBuildSaveName, false, "");
        var deadline = Time.realtimeSinceStartup + 30;
        while (SaveLoadSystem.WaitingOnThread() && Time.realtimeSinceStartup < deadline) yield return null;
        if (SaveLoadSystem.WaitingOnThread()) throw new IOException("Package fixture save timed out.");
        var file = File.Exists(path + ".zip") ? path + ".zip" : path;
        var bytes = File.ReadAllBytes(file);
        Network.CoopSavePair.WriteSidecar(file, bytes, Network.CoopSavePair.Create(bytes, position, carriers));
        var target = Path.Combine(AutoTest.IsolatedSaveDir, "ReloadFixture", "autofreebuild.binary.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.Copy(file, target, true); File.Copy(file + ".coop", target + ".coop", true);
        Event("package-fixture-saved", new { file = Path.GetFileName(target), unsupported = NativeWorkstations.Unsupported() });
        Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
    }

    private static IEnumerator Screenshot(string path)
    {
        yield return new WaitForEndOfFrame();
        var texture = ScreenCapture.CaptureScreenshotAsTexture(1);
        try { File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture)); }
        finally { Object.Destroy(texture); }
    }

    private static object Pipes(string where, ulong id, ComputerSave pc)
    {
        static string Part(PartInstance p) => p == null ? "null" : p.GetPart() == null ? "nodesc:" + p.GetPartId() : p.GetPartId();
        var pipes = pc?.pipesInstalled;
        var list = new List<string>();
        if (pipes != null) for (var i = 0; i < pipes.Length; i++)
        {
            var pipe = pipes[i];
            list.Add(pipe == null ? $"{i}:NULL" : $"{i}:{Part(pipe.m_from.m_component)}->{Part(pipe.m_to.m_component)} pts={pipe.m_points?.Length}");
        }
        var loops = new List<string>();
        if (pc?.m_loops != null) for (var i = 0; i < pc.m_loops.Count; i++)
        {
            var loop = pc.m_loops[i];
            loops.Add(loop == null ? $"{i}:NULL" : $"{i}:complete={loop.m_complete} parts=[{string.Join(",", loop.m_components?.ToArray().Select(Part) ?? Array.Empty<string>())}]");
        }
        return new { where, pc = id, save = pc == null ? 0 : pc.Pointer.ToInt64(), sku = pc?.caseID?.GetPartId(), pipes = pipes?.Length ?? -1,
            nullPipes = list.Count(x => x.EndsWith(":NULL", StringComparison.Ordinal)), bad = list.Count(x => x.Contains("null") || x.Contains("nodesc") || x.EndsWith(":NULL", StringComparison.Ordinal)), list, loops };
    }

    /// <summary>Pending native PC loads with what each still waits for.</summary>
    private static object LoadOps()
    {
        var db = PartsDatabase.s_instance;
        if (db?.m_loadPCOperations == null) return null;
        var rows = new List<object>();
        for (var i = 0; i < db.m_loadPCOperations.Count; i++)
        {
            var op = db.m_loadPCOperations[i];
            rows.Add(new { sku = op.CompSave?.caseID?.GetPartId(), save = op.CompSave == null ? 0 : op.CompSave.Pointer.ToInt64(), parts = op.Parts?.Count, subParts = op.SubParts?.Count,
                loaded = op.LoadedPrefabs?.Count, loadedSub = op.LoadedSubPrefabs?.Count, slot = op.Slot == null ? null : op.Slot.name, cables = op.LoadCables, spawned = op.OnSpawnedAction != null,
                missing = op.Parts?.ToArray().Where(p => p != null).Select(p => p.m_id).Except(op.LoadedPrefabs?.ToArray().Where(x => x?.Desc != null).Select(x => x.Desc.m_id) ?? Array.Empty<string>()).ToArray() });
        }
        return new { pc = rows, parts = db.m_loadPartOperations?.Count, prefabs = db.m_loadPrefabOperations?.Count, requested = db.m_requestedParts?.Count };
    }

    private void Aim(string command)
    {
        var bits = command.Split(':');
        if (bits.Length is < 3 or > 4 || !int.TryParse(bits[2], out var index) || index < 0) throw new ArgumentException("aim:<bench|inventory|cupboard|peripheral>:<index>[:height-offset]");
        var wc = Object.FindObjectOfType<WorkshopController>();
        if (wc == null || Camera.main == null || LocalPlayer.Root == null) throw new InvalidOperationException("No workshop player.");
        Component target = bits[1] switch
        {
            "bench" => Object.FindObjectOfType<SaveLoadSystem>().benchSlots[index],
            "inventory" => Object.FindObjectsOfType<InWorldInventory>(true).OrderBy(x => PathOf(x.transform)).ElementAt(index),
            "cupboard" => Object.FindObjectsOfType<Cupboard>(true).OrderBy(x => PathOf(x.transform)).ElementAt(index),
            "peripheral" => Object.FindObjectsOfType<PeripheralSlot>(true).OrderBy(x => PathOf(x.transform)).ElementAt(index),
            "note" => Object.FindObjectsOfType<PostItSlot>(true).OrderBy(x => PathOf(x.transform)).ElementAt(index),
            _ => throw new ArgumentException("Unknown aim target.")
        };
        var col = target is BenchSlot bench ? bench.m_caseCol : target.GetComponentInChildren<Collider>();
        var point = col != null ? col.bounds.center : target.transform.position + Vector3.up;
        // Empty benches have a case-sized aim volume above their clickable surface. Let the operator aim lower
        // without invoking any interaction directly; the following pickup/drop still uses ordinary game input.
        if (bits.Length == 4)
        {
            if (!float.TryParse(bits[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
                !float.IsFinite(offset) || Math.Abs(offset) > 1) throw new ArgumentException("Aim height offset must be within one metre.");
            point.y += offset;
        }
        var reset = (target as BenchSlot)?.PlayerResetPosition;
        var root = LocalPlayer.Root;
        var pos = reset != null ? reset.transform.position : point + target.transform.forward * 1.3f;
        pos.y = root.position.y;
        var cc = LocalPlayer.Controller;
        var wasEnabled = cc != null && cc.enabled;
        if (wasEnabled) cc.enabled = false;
        try { root.position = pos; }
        finally { if (wasEnabled) cc.enabled = true; }
        var cam = Camera.main;
        var dir = point - cam.transform.position;
        var yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
        foreach (var look in root.GetComponentsInChildren<MouseLook>(true))
        {
            var parentYaw = look.transform.parent != null ? look.transform.parent.eulerAngles.y : 0;
            look.rotationX = look.transform == root ? yaw : yaw - parentYaw;
            look.rotationY = -pitch;
        }
        cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        Event("camera-setup", new { target = Describe(target), position = pos.ToString(), aim = point.ToString() });
    }

    private static object Pc(ComputerSave pc)
    {
        if (pc == null) return null;
        var parts = new SortedDictionary<string, string>();
        var pointers = new SortedDictionary<string, string>();
        foreach (var property in typeof(ComputerSave).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(x => x.GetIndexParameters().Length == 0))
        {
            if (property.PropertyType == typeof(PartInstance))
            {
                var part = property.GetValue(pc) as PartInstance;
                parts[property.Name] = part?.GetPartId() ?? "null";
                pointers[property.Name] = Describe(part);
            }
            else if (property.PropertyType.Name.Contains("Array") && property.PropertyType.IsGenericType && property.PropertyType.GetGenericArguments().FirstOrDefault() == typeof(PartInstance))
            {
                if (property.GetValue(pc) is System.Collections.IEnumerable array)
                {
                    var i = 0;
                    foreach (var item in array)
                    {
                        var part = item as PartInstance;
                        parts[$"{property.Name}[{i}]"] = part?.GetPartId() ?? "null";
                        pointers[$"{property.Name}[{i++}]"] = Describe(part);
                    }
                }
            }
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts));
        return new { pointer = Describe(pc), topologyHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), parts, pointers };
    }

    private static string Describe(object value)
    {
        if (value == null) return "null";
        if (value is PartInstance part) return $"Part@{part.Pointer:x}:{part.GetPartId()} job={part.m_jobId} condition={part.Condition} inInventory={part.GetIsInInventory()}";
        if (value is Case ca) return $"Case@{ca.Pointer:x}:{PathOf(ca.transform)} running={ca.IsRunning()} cables={ca.GetComponentsInChildren<CableInstance>(true).Length}";
        if (value is Component c) return $"{c.GetType().Name}@{c.Pointer:x}:{PathOf(c.transform)}";
        if (value is GameObject go) return $"GameObject@{go.Pointer:x}:{PathOf(go.transform)}";
        if (value is ComputerSave pc) return $"PC@{pc.Pointer:x}:{pc.caseID?.GetPartId()}";
        if (value is Il2CppObjectBase il) return $"{value.GetType().Name}@{il.Pointer:x}";
        return value.ToString();
    }

    private static string Collider(Collider col) => col == null ? "null" : $"{Describe(col)} enabled={col.enabled} trigger={col.isTrigger} layer={col.gameObject.layer} center={col.bounds.center} size={col.bounds.size}";
    private static string PathOf(Transform t)
    {
        var name = $"{t.name}[{t.GetSiblingIndex()}]";
        for (var p = t.parent; p != null; p = p.parent) name = $"{p.name}[{p.GetSiblingIndex()}]/" + name;
        return name;
    }

    internal void Event(string kind, object detail)
    {
        File.AppendAllText(Path.Combine(_dir, "events.jsonl"), JsonSerializer.Serialize(new { utc = DateTime.UtcNow.ToString("O"), frame = Time.frameCount, time = Time.realtimeSinceStartup, kind, detail }) + "\n");
        if (kind is "capture" or "scene" or "enabled" or "hook-error") Plugin.Logger.LogInfo($"PHASE3 {kind}: {JsonSerializer.Serialize(detail)}");
    }
}
