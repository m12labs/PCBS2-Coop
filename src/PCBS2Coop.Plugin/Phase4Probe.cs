using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Opt-in native research, never a multiplayer component adapter. Runs only inside the named
/// phase 4 disposable-save guard. Observers do not suppress native calls. Explicit commands are labelled
/// supplemental native calls; they do not certify the ordinary input path or two-peer synchronization.</summary>
internal sealed class Phase4Probe
{
    private static Phase4Probe _current;
    private readonly Phase3Probe _probe;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private string _lastState;
    private bool _sampling, _autoNext, _manualNext;
    private readonly List<(Slot Slot, ComponentPC Preview)> _directPreviews = new();
    private long _manualState;
    private bool _blockInput = true;
    private readonly List<(Rewired.Controller Controller, bool Enabled)> _controllers = new();
    private int _capture;
    private long _call;
    private readonly Dictionary<long, float> _calls = new();
    public Phase4Probe(Phase3Probe probe)
    {
        _probe = probe; _current = this;
        var harmony = new Harmony(Plugin.Guid + ".phase4probe");
        Observe(typeof(Slot), "InstantiatePart", "InstantiatePreloadedPart", "SetComponent", "RemoveComponent", "UninstallAfterLoad");
        // OrientComponentStep/DoOrientation run every frame only while a part is being oriented.
        Observe(typeof(InstallingPartState), "OnStateEnter", "OnStateDestroy", "HandleExit", "StartInstallComponent", "InitUpgrades", "OrientComponentStep", "DoOrientation");
        // Fan intake/exhaust flips (N6). Not set_Direction (a one-line setter shared with unrelated classes)
        // or CheckFanOrientation (runs every frame for every fan); reports carry the fan rotation instead.
        Observe(typeof(Fan), "FlipFanFlow", "SingleSidedFanFlip");
        Observe(typeof(WorkingOnComputerState), "Uninstall");
        Observe(typeof(Case), "OnChangePart", "PostChangePart", "PostDestroyPart", "RebuildSlots", "FixupBlockers", "ConnectCables");
        Observe(typeof(CareerStatus), "AddItemToInventory");
        Observe(typeof(Pin), "TogglePin", "PopOpen", "Close", "OnDropInSlot");
        Observe(typeof(ScrewPin), "TogglePin", "PinToggle");
        Observe(typeof(AM3Slot), "TogglePin", "PopOpen", "OpenSocket", "CloseSocket");
        Observe(typeof(LGASlot), "TogglePin", "PopOpen");
        // OnStateDestroy is an empty, folded stub used by unrelated methods. A typed Harmony
        // trampoline there is unsafe even when this is the only hook on its native address (B1/B2).
        Observe(typeof(SimpleScrewState), "OnStateEnter", "HandleExit");
        Observe(typeof(Screw), "StartInstall", "StartUninstall", "ForceTight");
        Observe(typeof(SaveLoadSystem), "RecordOpenPins", "OpenPins", "GetPartInstancesForSlots");
        Observe(typeof(GameController), "SetCurrentState", "GoToPreviousState");
        void Observe(Type type, params string[] names)
        {
            foreach (var name in names)
            {
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name).ToArray();
                if (methods.Length == 0) Emit("missing-method", type.Name + "." + name);
                foreach (var method in methods)
                {
                    try
                    {
                        var code = GamePatches.NativeCode(method);
                        // Empty methods across unrelated classes share one RET stub. Even one typed hook
                        // there intercepts unrelated/null receivers before our observer can check its guard.
                        if (code == IntPtr.Zero || System.Runtime.InteropServices.Marshal.ReadByte(code) is 0xC3 or 0xC2)
                        { Emit("hook-empty-skipped", new { type = type.Name, name }); continue; }
                        if (GamePatches.FoldedWith(method) is { } folded)
                        { Emit("hook-alias", new { method = method.ToString(), owner = folded.DeclaringType?.Name + "." + folded.Name }); continue; }
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(Phase4Probe), nameof(Before)),
                            postfix: new HarmonyMethod(typeof(Phase4Probe), method.ReturnType == typeof(void) ? nameof(AfterVoid) : nameof(AfterValue)));
                        Emit("hook", new { type = type.Name, method = method.ToString(), address = GamePatches.NativeCode(method).ToInt64() });
                    }
                    catch (Exception e) { Emit("hook-error", new { type = type.Name, name, error = e.ToString() }); }
                }
            }
        }
    }

    private void Emit(string kind, object data) => _probe.Event("p4-" + kind, data);
    private bool Safe => Environment.CurrentManagedThreadId == _thread && _probe.Safe;
    public void Update()
    {
        if (!Safe) return;
        if (_blockInput && Rewired.ReInput.isReady)
        {
            foreach (var c in new Rewired.Controller[] { Rewired.ReInput.controllers.Keyboard, Rewired.ReInput.controllers.Mouse })
            {
                if (c == null) continue;
                if (!_controllers.Any(x => x.Controller.Pointer == c.Pointer)) _controllers.Add((c, c.enabled));
                c.enabled = false;
            }
        }
        var state = JsonSerializer.Serialize(StateInfo());
        if (state != _lastState) { Emit("state", StateInfo()); _lastState = state; }
    }

    public bool Command(string command, CoopRunner runner)
    {
        if (!command.StartsWith("p4-", StringComparison.Ordinal)) return false;
        if (!Safe) throw new InvalidOperationException("Phase 4 needs confirmed disposable save isolation.");
        var bits = command.Split(':');
        switch (bits[0])
        {
            case "p4-input":
                if (bits.Length != 2 || bits[1] is not ("on" or "off")) throw new ArgumentException("p4-input:on|off");
                _blockInput = bits[1] == "off";
                if (!_blockInput) { foreach (var (controller, enabled) in _controllers) controller.enabled = enabled; _controllers.Clear(); }
                Emit("input-mode", new { physicalInput = !_blockInput });
                break;
            case "p4-report":
                Report(bits.Length > 1 ? bits[1] : "capture", bits.Length > 2 && bits[2] == "save"); break;
            case "p4-assembly":
            {
                var objects = runner.Coop.Objects;
                objects.Assembly.CheckMeshes();
                objects.CheckProjection(runner.Coop.Session.World.Local, runner.Coop.Session.LocalIdentity.Player);
                Emit("assembly-check", new { passed = true, pcs = objects.Model.Pcs.Select(pc => new
                {
                    id = pc.Id.Value, resources = pc.Resources.Count, integral = pc.Resources.Count(r => r.IntegralPart != ""),
                    mechanics = pc.Resources.Count(r => r.Kind != PCBS2Coop.Network.AssemblyResourceKind.Slot),
                    slots = pc.Resources.Where(r => r.Kind == PCBS2Coop.Network.AssemblyResourceKind.Slot).Select(r => new
                        { id = r.Id.Value, parent = r.Parent.Value, r.Binding, occupant = r.Occupant.Value, r.IntegralPart })
                }) });
                break;
            }
            case "p4-catalog":
                Write("catalog.json", CareerStatus.Get().GetInventory().ToArray().Where(p => p != null)
                    .Select(p => new { sku = p.GetPartId(), type = p.GetPart()?.m_type.ToString(), nativeType = p.GetIl2CppType().FullName }).ToArray()); break;
            case "p4-preview":
            case "p4-clear-preview":
            {
                var work = GameController.Get().GetCurrentState()?.TryCast<WorkingOnComputerState>() ?? throw new InvalidOperationException("Work on a PC first.");
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (bits[0] == "p4-preview")
                {
                    var part = PartInstance.NewPartInstance(bits[1], 0, null);
                    if (bits.Length > 2 && bits[2] == "direct")
                    {
                        if (!PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out var loaded)) throw new InvalidOperationException("Preload this asset first.");
                        var slot = work.Case.ssdSlots.First(s => s != null && s.GetComponent() == null);
                        Direct(command, "native InstantiatePreloadedPart Placeholder=true on an empty SSD slot; bypasses inventory eligibility and does not commit");
                        var preview = slot.InstantiatePreloadedPart(part, loaded, false, true, false, false);
                        _directPreviews.Add((slot, preview));
                        Emit("preview-mode", new { present = NativeAssembly.HasPreview });
                        break;
                    }
                    Direct(command, "native PopulatePlaceholders selection path, no component commit or inventory gesture");
                    GameController.Get().selectedItem = part;
                    typeof(WorkingOnComputerState).GetMethod("PopulatePlaceholders", flags).Invoke(work, new object[] { part.ShallowCopy() });
                }
                else
                {
                    Direct(command, "native ClearPlaceholders teardown, no component mutation");
                    foreach (var (slot, preview) in _directPreviews)
                        if (preview != null && NativeAssembly.IsPreview(preview))
                        {
                            if (slot != null && slot.GetComponent()?.Pointer == preview.Pointer) slot.RemoveComponent();
                            if (preview != null) Object.Destroy(preview.gameObject);
                        }
                    _directPreviews.Clear();
                    typeof(WorkingOnComputerState).GetMethod("ClearPlaceholders", flags).Invoke(work, null);
                    GameController.Get().selectedItem = null;
                }
                Emit("preview-mode", new { present = NativeAssembly.HasPreview });
                break;
            }
            case "p4-create":
            {
                var part = CareerStatus.Get().GetInventory().ToArray().First(p => p != null && p.GetPartId() == bits[1] && p.GetPart().m_type == PartDesc.Type.CASE);
                Direct(command, "pinned catalog creation intent through the existing host-ordered lifecycle; bypasses inventory click");
                runner.Coop.Lifecycle.RequestCreate(part);
                break;
            }
            case "p4-disconnect":
            {
                var mesh = Object.FindObjectOfType<SaveLoadSystem>().benchSlots[int.Parse(bits[1])].GetCaseInSlot();
                if (mesh == null) throw new InvalidOperationException("No PC on this bench.");
                var connectors = mesh.GetComponentsInChildren<PowerConnector>(true).Select(c => c.Pointer).ToHashSet();
                var cable = mesh.currentCables.ToArray().First(c => c != null && c.m_start != null && c.m_end != null &&
                    connectors.Contains(c.m_start.Pointer) && connectors.Contains(c.m_end.Pointer));
                Direct(command, "native internal-cable disconnect for capture-integrity negative; not phase 5 synchronization");
                Emit("disconnect-fixture", new { from = cable.m_start.id, to = cable.m_end.id });
                cable.Uninstall(false, true);
                break;
            }
            case "p4-preload":
            {
                var part = CareerStatus.Get().GetInventory().ToArray().First(p => p != null && p.GetPartId() == bits[1]);
                Direct(command, "native asset preload only; no physical install");
                PartsDatabase.TestDelegate loaded = (System.Action<PartsDatabase.LoadedPart, Transform>)
                    ((PartsDatabase.LoadedPart asset, Transform parent) =>
                    {
                        if (!Safe) return;
                        Emit("asset-ready", new { sku = bits[1], loaded = asset != null });
                    });
                PartsDatabase.s_instance.InitPartLoading(part.GetPart(), loaded, null);
                break;
            }
            case "p4-fit":
            {
                var part = PartInstance.NewPartInstance(bits[1], 0, null);
                var ca = GameController.Get().GetCurrentState()?.TryCast<WorkingOnComputerState>()?.Case ?? throw new InvalidOperationException("Work on a case first.");
                var ready = PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out var loaded);
                var rows = ca.GetComponentsInChildren<Slot>(true).Where(s => s.GetComponent() == null).Select(s => new
                    { pointer = s.Pointer.ToInt64(), path = PathOf(s.transform, ca.transform), type = s.m_slotType.ToString(), fits = s.CanFillWithItem(part) }).ToArray();
                Write("fit-" + part.GetPartId() + ".json", new { sku = bits[1], assetReady = ready, slots = rows });
                break;
            }
            case "p4-case":
            {
                if (runner.Coop.Session.HasPeer) throw new InvalidOperationException("Native fixture requires a host without a guest.");
                var sl = Object.FindObjectOfType<SaveLoadSystem>(); var bench = sl.benchSlots[int.Parse(bits[1])];
                if (bench.GetCaseInSlot() != null || bench.m_caseLoadingToSlot) throw new InvalidOperationException("Fixture bench must be empty.");
                var item = CareerStatus.Get().GetInventory().ToArray().First(p => p != null && p.GetPartId() == bits[2] && p.GetPart().m_type == PartDesc.Type.CASE);
                var record = NativePcLifecycle.NewFromCatalog(item);
                Direct(command, "empty native PC fixture, not a host-ordered PC creation or component synchronization test");
                UnityEngine.Events.UnityAction<bool, Case, BenchSlot> complete = (System.Action<bool, Case, BenchSlot>)
                    ((bool powered, Case mesh, BenchSlot slot) =>
                    {
                        if (!Safe) return;
                        NativeObjectState.ShowBench(slot); Emit("case-ready", new { sku = bits[2], mesh = Describe(mesh) });
                    });
                sl.LoadComputerToSlot(record, bench, true, false, complete);
                NativeObjectState.ShowBench(bench);
                break;
            }
            case "p4-work":
            {
                var bench = Object.FindObjectOfType<SaveLoadSystem>().benchSlots[int.Parse(bits[1])];
                var ca = bench.GetCaseInSlot() ?? throw new InvalidOperationException("No case on that bench.");
                var actions = bench.GetInteractions().ToArray();
                Emit("work-actions", actions.Select(a => new { a.m_name, control = a.m_control.ToString() }));
                var action = actions.Single(a => a.m_control == WorldInteraction.Control.DEFAULT);
                Direct(command, "native bench DEFAULT interaction delegate, bypasses pointer/reach input");
                action.m_action.Invoke();
                break;
            }
            case "p4-install":
            {
                var slot = Find<Slot>(long.Parse(bits[1]));
                if (slot.GetComponent() != null) throw new InvalidOperationException("Destination occupied.");
                var catalog = CareerStatus.Get().GetInventory().ToArray().First(p => p != null && p.GetPartId() == bits[2]);
                var work = GameController.Get().GetCurrentState()?.TryCast<WorkingOnComputerState>();
                if (work == null || slot.GetCase() != work.Case) throw new InvalidOperationException("Work on this case first.");
                var part = PartInstance.NewPartInstance(catalog.GetPartId(), 0, null);
                var mode = bits.Length > 3 ? bits[3] : "";
                var generation = runner.Coop.Session.World.Generation;
                void Install()
                {
                    if (!Safe) return;
                    var current = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>();
                    if (current == null || current.Pointer != work.Pointer || slot == null ||
                        generation != runner.Coop.Session.World.Generation || slot.GetCase() != current.Case || slot.GetComponent() != null)
                    { Emit("install-discarded", new { command, reason = "native work, target or generation changed before asset readiness" }); return; }
                    if (!slot.CanFillWithItem(part)) throw new InvalidOperationException("Native CanFillWithItem refused this asset-ready part.");
                    _autoNext = mode == "auto"; _manualNext = mode == "manual";
                    Direct(command, "asset-ready fresh Free Build part + InstallingPartState/SetCurrentState, bypasses inventory/target gesture");
                    try { GameController.Get().SetCurrentState(new InstallingPartState(current.Case, Camera.main, part, slot)); }
                    finally { _autoNext = false; _manualNext = false; }
                }
                if (PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out var cached)) Install();
                else
                {
                    Emit("install-loading", new { command, sku = catalog.GetPartId() });
                    PartsDatabase.TestDelegate loaded = (System.Action<PartsDatabase.LoadedPart, Transform>)
                        ((PartsDatabase.LoadedPart asset, Transform parent) =>
                        {
                            if (!Safe) return;
                            try { if (asset == null) throw new InvalidDataException("Native asset load returned null."); Install(); }
                            catch (Exception e) { Emit("install-error", new { command, error = e.ToString() }); }
                        });
                    PartsDatabase.s_instance.InitPartLoading(part.GetPart(), loaded, null);
                }
                break;
            }
            case "p4-remove":
            {
                var slot = Find<Slot>(long.Parse(bits[1]));
                var component = slot.GetComponent() ?? throw new InvalidOperationException("Empty slot.");
                var work = GameController.Get().GetCurrentState()?.TryCast<WorkingOnComputerState>();
                if (work == null || work.Case != slot.GetCase()) throw new InvalidOperationException("Work on this case first.");
                _autoNext = bits.Length > 2 && bits[2] == "auto";
                _manualNext = bits.Length > 2 && bits[2] == "manual";
                Direct(command, "native WorkingOnComputerState.Uninstall coroutine, bypasses target/hold gesture");
                var method = typeof(WorkingOnComputerState).GetMethod("Uninstall", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var iterator = (Il2CppSystem.Collections.IEnumerator)method.Invoke(work, new object[] { component });
                runner.StartCoroutine(iterator);
                break;
            }
            case "p4-replay-install":
            case "p4-replay-remove":
            {
                // 4e milestone 2: the detached replay engine on this game alone (no second player, no network).
                var slot = Find<Slot>(long.Parse(bits[1]));
                var mesh = slot.GetCase() ?? throw new InvalidOperationException("That slot is not in a case.");
                var bench = Object.FindObjectOfType<SaveLoadSystem>().benchSlots.FirstOrDefault(b => b != null && b.GetCaseInSlot()?.Pointer == mesh.Pointer)
                    ?? throw new InvalidOperationException("That case is not on a bench.");
                var plan = new NativeEditReplay.Plan { Label = command, Case = mesh, Bench = bench, Slot = slot, Cables = NativeEditReplay.CableChoice.Current() };
                if (bits[0] == "p4-replay-install")
                {
                    if (slot.GetComponent() != null) throw new InvalidOperationException("Destination occupied.");
                    plan.Part = PartInstance.NewPartInstance(bits[2], 0, null) ?? throw new InvalidOperationException("Unknown SKU.");
                    if (bits.Length > 3) plan.Degrees = float.Parse(bits[3], System.Globalization.CultureInfo.InvariantCulture);
                    plan.Mode = 1; plan.Selected = 1;
                }
                else
                {
                    plan.Component = slot.GetComponent() ?? throw new InvalidOperationException("Empty slot.");
                    plan.UnplugOnly = bits.Length > 2 && bits[2] == "unplug";
                    plan.Mode = 0; plan.Selected = 0;
                }
                var started = Time.realtimeSinceStartup;
                plan.Finished = error =>
                {
                    if (!Safe) return;
                    Emit("replay-finished", new { command, error, seconds = Time.realtimeSinceStartup - started, state = StateInfo(),
                        slotNow = Describe(slot != null ? slot.GetComponent() : null) });
                };
                Direct(command, "detached InstallingPartState replay (4e engine); this player's state machine is not entered");
                runner.Coop.Replays.Start(plan);
                Emit("replay-started", new { command, state = StateInfo(), workingOnThisCase = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerStateBase>()?.m_case?.Pointer == mesh.Pointer });
                break;
            }
            case "p4-cancel":
            {
                _autoNext = false;
                var state = GameController.Get().GetCurrentState();
                var install = state?.TryCast<InstallingPartState>();
                if (install == null) throw new InvalidOperationException("No install/remove state to cancel.");
                Direct(command, "InstallingPartState.HandleExit; regular native ticks perform rollback");
                var leave = (bool)typeof(InstallingPartState).GetMethod("HandleExit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(install, null);
                Emit("cancel-result", new { callerMustLeave = leave });
                // HandleExit can either start reverse ticks (false), or delegate immediate departure to its
                // caller (true, e.g. an unaccepted orientation preview). Honor both native contracts.
                if (leave && GameController.Get().GetCurrentState()?.Pointer == install.Pointer) GameController.Get().GoToPreviousState();
                break;
            }
            case "p4-accept":
            {
                var install = GameController.Get().GetCurrentState()?.TryCast<InstallingPartState>() ?? throw new InvalidOperationException("No install state to accept.");
                Direct(command, "diagnostic orientation acceptance field; bypasses the native click/drag input");
                typeof(InstallingPartState).GetProperty("m_accept").SetValue(install, true);
                break;
            }
            case "p4-pin":
            {
                var pin = Find<CPin>(long.Parse(bits[1]));
                Direct(command, "native CPin.TogglePin, bypasses target/click gesture");
                pin.TogglePin(); break;
            }
            case "p4-screw":
            {
                var screw = Find<Screw>(long.Parse(bits[1]));
                Direct(command, "isolated direct screw mechanics, bypasses gesture/state loop");
                if (bits[2] == "uninstall") screw.StartUninstall();
                else if (bits[2] == "install") screw.StartInstall();
                else if (bits[2] == "tight") screw.ForceTight(false);
                else screw.Tighten(float.Parse(bits[2], System.Globalization.CultureInfo.InvariantCulture), false);
                break;
            }
            default: throw new ArgumentException("Unknown phase 4 command.");
        }
        return true;
    }
    private void Direct(string command, string purpose) => Emit("direct-call", new { command, purpose, inputVerified = false });
    private static T Find<T>(long pointer) where T : Component => Object.FindObjectsOfType<T>(true).Single(x => x.Pointer.ToInt64() == pointer);
    private void Write(string name, object data) => File.WriteAllText(Path.Combine(_probe.EvidenceDir, name), JsonSerializer.Serialize(data));

    private void Report(string tag, bool save)
    {
        if (tag.Length is < 1 or > 48 || tag.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))) throw new ArgumentException("Invalid capture tag.");
        _sampling = true;
        try
        {
            var sl = Object.FindObjectOfType<SaveLoadSystem>();
            var pcs = new List<object>();
            for (var i = 0; i < sl.benchSlots.Length; i++)
            {
                var bench = sl.benchSlots[i]; var ca = bench?.GetCaseInSlot();
                if (ca == null) continue;
                ComputerSave record = null;
                if (save)
                {
                    // Explicit capture boundary. Never run SaveComputer merely to poll progress.
                    record = bench.GetComputer(true);
                    var bytes = NativePcCodec.Write(record);
                    File.WriteAllBytes(Path.Combine(_probe.EvidenceDir, $"p4-{_capture}-{tag}-bench{i}.bin"), bytes);
                }
                var paths = record == null ? null : NativeObjectState.Parts(record);
                var slots = ca.GetComponentsInChildren<Slot>(true).Select(slot =>
                {
                    var component = slot.GetComponent();
                    var part = component != null ? component.GetPartInstance() : null;
                    return new {
                    pointer = slot.Pointer.ToInt64(), path = PathOf(slot.transform, ca.transform), nativeType = slot.GetIl2CppType().FullName,
                    slotType = slot.m_slotType.ToString(), slot.partType, genericType = slot.GenericType.ToString(), active = slot.gameObject.activeInHierarchy,
                    part = Describe(slot.part), componentPart = Describe(part), component = component != null ? Describe(component) : null,
                    placement = Placement(part),
                    destroyedComponent = component is not null && component == null,
                    savePaths = paths?.Where(x => x.Value != null && part != null && x.Value.Pointer == part.Pointer).Select(x => x.Key).ToArray(),
                    fields = Fields(slot, "slotOrderNumber", "firstSlotOccupied", "m_parentSlot", "parentSlot", "m_proxySlot", "m_pciSlotsCovered", "m_autoInstantiatePartID", "closePins", "mustBeOpenPins", "blockedBy", "m_dynamicBlockedBy", "m_dynamicComponentCollidesWith", "disableStandOffs")
                    };
                }).OrderBy(x => x.path).ToArray();
                pcs.Add(new { bench = i, caseName = ca.name, pointer = ca.Pointer.ToInt64(),
                    slots, arrays = Arrays(ca),
                    motherboardArrays = ca.GetComponentsInChildren<Motherboard>(true).Select(m => new { pointer = m.Pointer.ToInt64(), path = PathOf(m.transform, ca.transform), arrays = Arrays(m) }),
                    componentArrays = ca.GetComponentsInChildren<ComponentPC>(true).Select(c => new { component = Describe(c), arrays = Arrays(Concrete(c)) }).Where(x => x.arrays.Count > 0),
                    animators = ca.GetComponentsInChildren<Animator>(true).Select(a => new { path = PathOf(a.transform, ca.transform), controller = a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : null, layers = a.layerCount }),
                    pins = ca.GetComponentsInChildren<CPin>(true).Select(p => new { pointer = p.Pointer.ToInt64(), path = PathOf(p.transform, ca.transform), type = p.GetIl2CppType().FullName,
                        open = p.IsOpen(), animating = p.IsAnimating(), fields = Fields(p, "open", "locked", "m_open", "m_locked", "m_animating", "childPins", "blockedBy") }),
                    screws = ca.GetComponentsInChildren<Screw>(true).Select(s => new { pointer = s.Pointer.ToInt64(), path = PathOf(s.transform, ca.transform),
                        fields = Fields(s, "m_requiresStandOff", "m_rotation", "m_offset", "m_speed", "m_isStandOff", "m_AffiliatedWithComponent", "m_hideWhenRemoved"), active = s.gameObject.activeInHierarchy }),
                    fans = ca.GetComponentsInChildren<Fan>(true).Select(f => new { pointer = f.Pointer.ToInt64(), path = PathOf(f.transform, ca.transform),
                        name = f.gameObject.name, direction = f.Direction.ToString(), blow = f.GetBlowDirection().ToString("F2"),
                        localEuler = f.transform.localEulerAngles.ToString("F1"), worldForward = f.transform.forward.ToString("F2"), active = f.gameObject.activeInHierarchy }),
                    saved = record == null ? null : new { parts = paths.Select(x => new { path = x.Key, part = Describe(x.Value) }),
                        fields = Fields(record, "casePciCovers", "miscCaseSlots", "optionalCaseSlots", "m_bespokeCaseSlots", "m_openPins", "thermalPasteApplied") }
                });
            }
            var result = new { tag, explicitNativeSaveCapture = save, state = StateInfo(), inventory = CareerStatus.Get()?.GetInventory()?.Count, pcs };
            var file = $"p4-{_capture++}-{tag}.json"; Write(file, result);
            Emit("report", new { file, explicitNativeSaveCapture = save });
        }
        finally { _sampling = false; }
    }

    private static Dictionary<string, object> Arrays(object obj)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // A component's typed arrays establish ordering, including null entries. They are not network IDs.
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (p.GetIndexParameters().Length != 0 || !p.Name.Contains("Slot", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.ContainsKey(p.Name)) continue;
            try
            {
                var value = p.GetValue(obj);
                if (value is Slot single) { result[p.Name] = Describe(single); continue; }
                if (value is not IEnumerable items || value is string) continue;
                var rows = new List<string>(); var valid = false;
                foreach (var item in items) { rows.Add(Describe(item)); if (item is Slot) valid = true; }
                if (valid) result[p.Name] = rows;
            }
            catch (Exception e) { result[p.Name] = "error:" + e.GetBaseException().Message; }
        }
        return result;
    }
    private static Dictionary<string, object> Fields(object obj, params string[] names)
    {
        if (obj is Component c) obj = Concrete(c);
        var result = new Dictionary<string, object>();
        foreach (var name in names)
        {
            try
            {
                var property = obj?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null) continue;
                var value = property.GetValue(obj);
                if (Items(value) is { } rows) result[name] = rows;
                else result[name] = value is null or bool or float or int or string ? value : Describe(value);
            }
            catch (Exception e) { result[name] = "error:" + e.GetBaseException().Message; }
        }
        return result;
    }
    private static List<string> Items(object value)
    {
        if (value == null || value is string) return null;
        var rows = new List<string>();
        if (value is IEnumerable items) { foreach (var item in items) rows.Add(Describe(item)); return rows; }
        var count = value.GetType().GetProperty("Count"); var index = value.GetType().GetProperty("Item");
        if (count == null || index == null) return null;
        var n = (int)count.GetValue(value);
        if (n is < 0 or > 4096) throw new InvalidDataException("Invalid diagnostic collection size.");
        for (var i = 0; i < n; i++) rows.Add(Describe(index.GetValue(value, new object[] { i })));
        return rows;
    }
    private static object Concrete(Component value)
    {
        var type = typeof(Case).Assembly.GetType(value.GetIl2CppType().FullName);
        if (type == null || type == value.GetType()) return value;
        return typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(type).Invoke(value, null) ?? value;
    }
    private static object Placement(PartInstance part)
    {
        if (part == null) return null;
        var position = part.GetSlotCaseRelativePosition();
        return new { orientation = part.GetSlotOrientation(), x = position.x, y = position.y, z = position.z,
            flags = Fields(part, "m_flipped", "m_autoInstatiated", "m_partCondition", "m_filth") };
    }
    private static string PathOf(Transform t, Transform root = null)
    {
        var path = t.name + "[" + t.GetSiblingIndex() + "]";
        while (t.parent != null && t != root) { t = t.parent; path = t.name + "[" + t.GetSiblingIndex() + "]/" + path; }
        return path;
    }
    private static string Describe(object value)
    {
        if (value == null) return null;
        if (value is Object unity && unity == null) return "destroyed";
        if (value is PartInstance part) return $"{part.GetIl2CppType().Name}@{part.Pointer.ToInt64()}:{part.GetPartId()}";
        if (value is GameObjectRelativePath relative) return "path:" + string.Join("/", relative.m_path.ToArray());
        if (value is Component c) return $"{c.GetIl2CppType().Name}@{c.Pointer.ToInt64()}:{PathOf(c.transform)}";
        if (value is Il2CppObjectBase il) return $"{value.GetType().Name}@{il.Pointer.ToInt64()}";
        return value.ToString();
    }
    private static object StateInfo()
    {
        var state = GameController.Get()?.GetCurrentState();
        var install = state?.TryCast<InstallingPartState>();
        return new { type = state?.GetIl2CppType().Name, pointer = state?.Pointer.ToInt64(),
            install = install == null ? null : Fields(install, "m_part", "m_slot", "m_component", "m_dir", "m_accept", "m_auto", "m_autoStandoff", "m_oneClickScrew", "m_screws", "m_doneScrews", "m_standOffs", "m_doneStandOffs", "ForceConnect", "ForceUpgrades") };
    }
    private static void Before(MethodBase __originalMethod, object __instance, object[] __args, out long __state)
    {
        __state = 0; var p = _current;
        if (p == null || !p.Safe || p._sampling) return;
        try
        {
            if (__originalMethod.DeclaringType == typeof(InstallingPartState) && __originalMethod.Name == "OnStateEnter")
            {
                var install = (__instance as Il2CppObjectBase)?.TryCast<InstallingPartState>();
                if (p._autoNext && install != null)
                {
                    install.ForceUpgrades = true; p._autoNext = false;
                    p.Emit("direct-setup", "ForceUpgrades before OnStateEnter for supplemental automatic lifecycle probe");
                }
                if (p._manualNext && install != null) { p._manualState = install.Pointer.ToInt64(); p._manualNext = false; }
            }
            __state = ++p._call; p._calls[__state] = Time.realtimeSinceStartup;
            p.Emit("enter", new { call = __state, method = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
                instance = Describe(__instance), args = __args.Select(Describe), state = StateInfo() });
        }
        catch (Exception e) { p.Emit("observer-error", e.ToString()); }
    }
    private static void AfterVoid(MethodBase __originalMethod, long __state) => After(__originalMethod, __state, null);
    private static void AfterValue(MethodBase __originalMethod, long __state, object __result) => After(__originalMethod, __state, __result);
    private static void After(MethodBase method, long call, object result)
    {
        if (call == 0) return; var p = _current;
        try
        {
            if (method.DeclaringType == typeof(InstallingPartState) && method.Name == "OnStateEnter" && p._manualState != 0)
            {
                var install = GameController.Get().GetCurrentState()?.TryCast<InstallingPartState>();
                if (install != null && install.Pointer.ToInt64() == p._manualState)
                {
                    foreach (var name in new[] { "m_auto", "m_autoStandoff", "m_oneClickScrew" })
                        typeof(InstallingPartState).GetProperty(name).SetValue(install, false);
                    p.Emit("direct-setup", "disabled automatic flags on this diagnostic state only to hold/cancel it");
                    p._manualState = 0;
                }
            }
            p._calls.Remove(call, out var time);
            p.Emit("exit", new { call, method = method.DeclaringType?.Name + "." + method.Name, elapsed = Time.realtimeSinceStartup - time,
                result = Describe(result), state = StateInfo() });
        }
        catch (Exception e) { p.Emit("observer-error", e.ToString()); }
    }
}
