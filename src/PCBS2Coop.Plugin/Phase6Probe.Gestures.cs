using System.Reflection;
using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Deterministic native gesture entry points for owned, isolated two-instance acceptance runs.
/// Commands intentionally bypass cursor/raycast/hold input. They still submit through the production
/// co-op edit handler, and accepted edits enter the game's native editor or generated gesture callback.
/// </summary>
internal sealed partial class Phase6Probe
{
    private bool _gesturePreparingInstall;
    private readonly BindingFlags _gesturePrivateInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private bool GesturesCommand(string command)
    {
        if (WiringGestureCommand(command) || WaterGestureCommand(command)) return true;
        if (!command.StartsWith("gesture-", StringComparison.Ordinal)) return false;
        try
        {
            var bits = command.Split(':');
            switch (bits[0])
            {
                case "gesture-report":
                    if (bits.Length != 2) throw new ArgumentException("gesture-report:<pc>");
                    Event("gesture-report", GestureReport(RequirePc(bits[1])));
                    return true;
                case "gesture-cable-state":
                    if (bits.Length != 2) throw new ArgumentException("gesture-cable-state:<pc>");
                    Event("gesture-cable-state", GestureCableState(RequirePc(bits[1])));
                    return true;
                case "gesture-work":
                    if (bits.Length != 2) throw new ArgumentException("gesture-work:<pc>");
                    GestureWork(RequirePc(bits[1]));
                    return true;
                case "gesture-remove":
                    if (bits.Length != 3) throw new ArgumentException("gesture-remove:<pc>:<slot-resource-id>");
                    GestureRemove(RequirePc(bits[1]), ParseResourceId(bits[2]));
                    return true;
                case "gesture-install":
                    if (bits.Length != 4) throw new ArgumentException("gesture-install:<pc>:<slot-resource-id>:<sku>");
                    GestureInstall(RequirePc(bits[1]), ParseResourceId(bits[2]), bits[3]);
                    return true;
                case "gesture-accept":
                    GestureAccept();
                    return true;
                case "gesture-cancel":
                    GestureCancel();
                    return true;
                default:
                    throw new ArgumentException("Unknown gesture command.");
            }
        }
        catch (Exception e)
        {
            Event("gesture-error", new { command, error = e.ToString() });
            return true;
        }
    }

    private CoopController RequireGestureWorld()
    {
        var coop = CoopRunner.Instance?.Coop;
        if (!Safe || coop == null || !coop.WorldReady || !coop.Objects.Initialized || coop.Session?.World?.Admitted != true)
            throw new InvalidOperationException("The isolated co-op world is not admitted and ready.");
        return coop;
    }

    private PcFact RequirePc(string text)
    {
        var coop = RequireGestureWorld();
        if (!uint.TryParse(text, out var id)) throw new ArgumentException("PC id must be an unsigned integer.");
        var pc = coop.Objects.Model.Pcs.SingleOrDefault(x => x.Id == new NetObjectId(id));
        if (pc == null || pc.Location.Place != PcPlace.Bench) throw new InvalidOperationException("That shared PC is not on a bench.");
        return pc;
    }

    private static NetObjectId ParseResourceId(string text)
    {
        if (!uint.TryParse(text, out var id) || id == 0) throw new ArgumentException("Slot resource id must be a nonzero unsigned integer.");
        return new NetObjectId(id);
    }

    private (BenchSlot Bench, Case Mesh) NativeBench(PcFact pc)
    {
        var coop = RequireGestureWorld();
        var bench = coop.Objects.FindBench(pc.Id) ?? throw new InvalidOperationException("The PC has no native bench binding.");
        var mesh = bench.GetCaseInSlot();
        if (mesh == null || coop.Objects.IdOf(mesh) != pc.Id) throw new InvalidOperationException("The native case binding changed.");
        return (bench, mesh);
    }

    private static WorkingOnComputerState RequireWork(PcFact pc, Case mesh)
    {
        var work = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>();
        if (work?.m_case == null || work.m_case.Pointer != mesh.Pointer)
            throw new InvalidOperationException("Use gesture-work for this PC before invoking its component callbacks.");
        return work;
    }

    private static string GesturePath(Transform transform, Transform root)
    {
        var path = transform.name + "[" + transform.GetSiblingIndex() + "]";
        while (transform.parent != null && transform != root)
        {
            transform = transform.parent;
            path = transform.name + "[" + transform.GetSiblingIndex() + "]/" + path;
        }
        return path;
    }

    private object GestureReport(PcFact pc)
    {
        var coop = RequireGestureWorld();
        var (bench, mesh) = NativeBench(pc);
        var resources = pc.Resources.Select(resource =>
        {
            var native = coop.Objects.Assembly.NativeFor(pc.Id, resource.Id);
            var slot = native?.TryCast<Slot>();
            var occupant = slot == null ? null : slot.GetComponent();
            var part = occupant == null ? null : occupant.GetPartInstance();
            var pin = native?.TryCast<CPin>();
            var screw = native?.TryCast<Screw>();
            return new
            {
                id = resource.Id.Value,
                resource.Parent,
                resource.Binding,
                kind = resource.Kind.ToString(),
                resource.NativeType,
                resource.Occupant,
                resource.IntegralPart,
                nativePresent = native != null,
                path = native == null ? null : GesturePath(native.transform, mesh.transform),
                slotType = slot == null ? null : slot.m_slotType.ToString(),
                sku = part?.GetPartId(),
                open = pin?.IsOpen(),
                animating = pin?.IsAnimating(),
                screwRotation = screw?.m_rotation,
                screwOffset = screw?.m_offset
            };
        }).ToArray();
        var powerConnectors = mesh.GetComponentsInChildren<PowerConnector>(true).Select(connector => new
        {
            connector.id,
            path = GesturePath(connector.transform, mesh.transform),
            part = connector.GetComponentInParent<ComponentPC>()?.GetPartInstance()?.GetPartId()
        }).OrderBy(x => x.path).ToArray();
        var waterConnectors = mesh.GetComponentsInChildren<WaterConnector>(true).Select(connector => new
        {
            path = GesturePath(connector.transform, mesh.transform),
            part = connector.GetComponentInParent<ComponentPC>()?.GetPartInstance()?.GetPartId()
        }).OrderBy(x => x.path).ToArray();
        var cables = mesh.GetComponentsInChildren<CableInstance>(true).Where(cable => cable != null).Select(cable => new
        {
            from = cable.m_start?.id,
            to = cable.m_end?.id,
            part = cable.m_desc?.m_id,
            connector = cable.m_connectorDesc?.m_id,
            installing = cable.Installing,
            uninstalling = cable.Uninstalling,
            path = GesturePath(cable.transform, mesh.transform)
        }).OrderBy(x => x.from).ThenBy(x => x.to).ToArray();
        var plugs = mesh.GetComponentsInChildren<Plug>(true).Where(plug => plug != null).Select(plug => new
        {
            path = GesturePath(plug.transform, mesh.transform),
            cable = plug.GetComponentInParent<CableInstance>()?.m_start?.id + ">" + plug.GetComponentInParent<CableInstance>()?.m_end?.id
        }).OrderBy(x => x.path).ToArray();
        var record = bench.m_workStation?.m_virtualComputer?.ComputerSave ?? bench.GetComputer(false) ?? coop.Objects.RecordFor(pc.Id);
        var nativeRecord = record == null ? null : NativePcCodec.Write(record);
        var work = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>();
        var internalConnectors = pc.BuildProfile?.InternalConnectors.ToHashSet(StringComparer.Ordinal);
        string projectionError = null;
        try { coop.Objects.CheckProjection(coop.Session.World.Local, coop.Session.LocalIdentity.Player); }
        catch (Exception e) { projectionError = e.GetBaseException().Message; }
        return new
        {
            role = coop.Session.Role.ToString(),
            pc = pc.Id.Value,
            generation = coop.Session.World.Generation,
            revision = coop.Session.World.Revision,
            bench = bench.name,
            caseSku = record?.caseID?.GetPartId(),
            workMode = work?.m_mode.ToString(),
            projectionError,
            profile = pc.BuildProfile,
            parts = record == null ? null : NativeObjectState.Parts(record).Select(x => new
            {
                path = x.Key, sku = x.Value.GetPartId(), orientation = x.Value.GetSlotOrientation(), flipped = x.Value.m_flipped
            }).ToArray(),
            pciCovers = record?.casePciCovers?.ToArray(),
            bespokeSlots = record?.m_bespokeCaseSlots?.ToArray(),
            internalWires = record?.cablesInstalled?.Where(c => internalConnectors?.Contains(c.m_from) == true && internalConnectors.Contains(c.m_to))
                .Select(c => new { from = c.m_from, to = c.m_to, c.m_desc, c.m_connectorType, c.m_isNonModularConnector, c.m_playerChoice, pins = c.m_pinMaterialIndices?.ToArray() }).ToArray(),
            recordHash = nativeRecord == null ? null : Convert.ToHexString(SHA256.HashData(nativeRecord)),
            editsBusy = coop.Edits.Busy,
            localWorkingPc = coop.Activity.Get(coop.Session.World.Local)?.WorkingPc.Value ?? 0,
            localActivityPending = coop.Activity.Get(coop.Session.World.Local)?.Pending?.Token.ToString(),
            cables,
            waterPipes = record?.pipesInstalled?.Length ?? 0,
            loops = record?.m_loops?.Count ?? 0,
            resources,
            powerConnectors,
            waterConnectors,
            plugs
        };
    }

    private object GestureCableState(PcFact pc)
    {
        var coop = RequireGestureWorld();
        var (_, mesh) = NativeBench(pc);
        var state = GameController.Get()?.GetCurrentState()?.TryCast<ConnectCableState>();
        if (state == null || state.m_case?.Pointer != mesh.Pointer)
            throw new InvalidOperationException("No active ConnectCableState targets this PC.");
        return new
        {
            pc = pc.Id.Value,
            activeSourceConnector = (typeof(ConnectCableState).GetProperty("m_autoConnector", _gesturePrivateInstance)?.GetValue(state) as PowerConnector)?.id,
            sourceListFrom = state.m_listFrom?.ToArray().Where(x => x != null).Select(x => x.id).ToArray(),
            offeredPowerConnectors = state.PowerCons?.ToArray().Where(x => x != null).Select(x => x.id).ToArray(),
            semantics = new { sourceListFrom = "native ConnectCableState.m_listFrom field", offeredPowerConnectors = "native ConnectCableState.PowerCons field; game-populated endpoint candidates" },
            editsBusy = coop.Edits.Busy,
            actorWorkingPc = coop.Activity.Get(coop.Session.World.Local)?.WorkingPc.Value ?? 0
        };
    }

    private void GestureWork(PcFact pc)
    {
        var (bench, mesh) = NativeBench(pc);
        var current = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>();
        if (current?.m_case?.Pointer == mesh.Pointer)
        {
            Event("gesture-work", new { pc = pc.Id.Value, alreadyWorking = true, current = current.Pointer.ToInt64() });
            return;
        }
        if (current != null) throw new InvalidOperationException("Leave the other PC's work state normally before changing targets.");
        var actions = bench.GetInteractions().ToArray();
        var defaults = actions.Where(action => action.m_control == WorldInteraction.Control.DEFAULT).ToArray();
        if (defaults.Length != 1 || defaults[0].m_action == null) throw new InvalidOperationException("The bench has no unique DEFAULT work interaction.");
        Event("gesture-direct-call", new { command = "gesture-work", pc = pc.Id.Value, path = "BenchSlot.GetInteractions().DEFAULT.m_action", pointerReachInputVerified = false });
        defaults[0].m_action.Invoke();
        var entered = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>();
        Event("gesture-work", new { pc = pc.Id.Value, entered = entered?.m_case?.Pointer == mesh.Pointer, state = entered?.Pointer.ToInt64() });
        if (entered?.m_case?.Pointer != mesh.Pointer) throw new InvalidOperationException("The game's work interaction did not enter the selected PC.");
    }

    private void GestureRemove(PcFact pc, NetObjectId resourceId)
    {
        var coop = RequireGestureWorld();
        var (_, mesh) = NativeBench(pc);
        var work = RequireWork(pc, mesh);
        var fact = pc.Resources.SingleOrDefault(resource => resource.Id == resourceId && resource.Kind == AssemblyResourceKind.Slot)
            ?? throw new InvalidOperationException("That resource is not a shared slot on this PC.");
        var slot = coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>()
            ?? throw new InvalidOperationException("That slot is not bound on this game.");
        if (slot.GetCase()?.Pointer != mesh.Pointer) throw new InvalidOperationException("The target slot moved to another case.");
        var component = slot.GetComponent() ?? throw new InvalidOperationException("The target slot is empty.");
        var closure = new WorkingOnComputerState.__c__DisplayClass68_0 { __4__this = work, comp = component };
        Event("gesture-direct-call", new
        {
            command = "gesture-remove",
            pc = pc.Id.Value,
            resource = fact.Id.Value,
            callback = "WorkingOnComputerState.__c__DisplayClass68_0._DisassemblyTick_b__2",
            slotPath = GesturePath(slot.transform, mesh.transform),
            sku = component.GetPartInstance()?.GetPartId(),
            pointerReachAndHoldInputVerified = false
        });
        // Production Harmony prefix submits NativeComponentEdits.RequestRemove. Its accepted replay invokes this
        // exact generated game callback and runs the game's Uninstall coroutine, including animations/cable step.
        closure._DisassemblyTick_b__2();
        Event("gesture-entrypoint-returned", new { command = "gesture-remove", pc = pc.Id.Value, resource = fact.Id.Value, sku = component.GetPartInstance()?.GetPartId(), acceptedMustBeConfirmedByNativeEditTrace = true });
    }

    private void GestureInstall(PcFact pc, NetObjectId resourceId, string sku)
    {
        var coop = RequireGestureWorld();
        var (_, mesh) = NativeBench(pc);
        var work = RequireWork(pc, mesh);
        if (_gesturePreparingInstall)
            throw new InvalidOperationException("Wait for the current asset load to settle.");
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("The part SKU is required.");
        var fact = pc.Resources.SingleOrDefault(resource => resource.Id == resourceId && resource.Kind == AssemblyResourceKind.Slot)
            ?? throw new InvalidOperationException("That resource is not a shared slot on this PC.");
        var slot = coop.Objects.Assembly.NativeFor(pc.Id, fact.Id)?.TryCast<Slot>()
            ?? throw new InvalidOperationException("That slot is not bound on this game.");
        if (slot.GetCase()?.Pointer != mesh.Pointer || slot.GetComponent() != null)
            throw new InvalidOperationException("The target slot must be empty and remain on this case.");
        var camera = Camera.main ?? throw new InvalidOperationException("No game camera is available for the native install state.");
        var part = PartInstance.NewPartInstance(sku, 0, null) ?? throw new ArgumentException("Unknown part SKU.");
        var world = coop.Session.World;
        var generation = world.Generation;
        _gesturePreparingInstall = true;
        void StartNativeEdit()
        {
            try
            {
                if (!Safe || coop.Session?.World != world || generation != world.Generation || !coop.WorldReady ||
                    GameController.Get()?.GetCurrentState()?.Pointer != work.Pointer ||
                    slot == null || slot.GetCase()?.Pointer != mesh.Pointer || slot.GetComponent() != null)
                {
                    Event("gesture-install-discarded", new { pc = pc.Id.Value, resource = resourceId.Value, sku, reason = "safe context, work state, generation, or target changed before asset readiness" });
                    return;
                }
                // Match NativeComponentEdits.Fits: the game reports false while paused, and a
                // placeholder model cannot provide a meaningful fit result until its prefab loads.
                if (PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out _))
                {
                    var game = GameController.Get();
                    var paused = game != null && game.paused;
                    if (paused) game.paused = false;
                    bool fits;
                    try { fits = slot.CanFillWithItem(part); }
                    finally { if (paused) game.paused = true; }
                    if (!fits) throw new InvalidOperationException("The game's native fit check refused this part.");
                }
                else
                {
                    Event("gesture-install-fit-deferred", new { pc = pc.Id.Value, resource = resourceId.Value, sku, reason = "native prefab not loaded; production edit validation also defers to the editor's game" });
                }
                Event("gesture-direct-call", new
                {
                    command = "gesture-install",
                    pc = pc.Id.Value,
                    resource = resourceId.Value,
                    sku,
                    slotPath = GesturePath(slot.transform, mesh.transform),
                    path = "CoopController.Edits.RequestInstall → accepted callback → InstallingPartState → native editor/replay/settle",
                    bypasses = new[] { "pointer/reach/raycast", "ItemDisplay inventory selection and placeholder-commit callback" },
                    nativeInstallAnimationRuns = true
                });
                coop.Edits.RequestInstall(slot, sku, () =>
                {
                    GameController.Get().SetCurrentState(new InstallingPartState(mesh, camera, part, slot));
                    Event("gesture-install-native-state", new { pc = pc.Id.Value, resource = resourceId.Value, sku, state = GameController.Get()?.GetCurrentState()?.GetIl2CppType().Name });
                });
                Event("gesture-install-requested", new { pc = pc.Id.Value, resource = resourceId.Value, sku, generation });
            }
            catch (Exception e)
            {
                Event("gesture-install-setup-error", new { pc = pc.Id.Value, resource = resourceId.Value, sku, error = e.ToString() });
            }
            finally { _gesturePreparingInstall = false; }
        }

        if (PartsDatabase.s_instance.HasLoadedPart(part.GetPart(), out _)) StartNativeEdit();
        else
        {
            Event("gesture-install-preload", new { pc = pc.Id.Value, resource = resourceId.Value, sku });
            PartsDatabase.TestDelegate loaded = (System.Action<PartsDatabase.LoadedPart, Transform>)((asset, parent) =>
            {
                if (!Safe) { _gesturePreparingInstall = false; return; }
                if (asset == null) { _gesturePreparingInstall = false; Event("gesture-install-setup-error", new { pc = pc.Id.Value, resource = resourceId.Value, sku, error = "Native asset load returned null." }); return; }
                StartNativeEdit();
            });
            try { PartsDatabase.s_instance.InitPartLoading(part.GetPart(), loaded, null); }
            catch { _gesturePreparingInstall = false; throw; }
        }
    }

    private void GestureAccept()
    {
        if (!Safe) throw new InvalidOperationException("Gesture helpers require the isolated probe save guard.");
        var install = GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>()
            ?? throw new InvalidOperationException("No native InstallingPartState is active.");
        var accept = typeof(InstallingPartState).GetProperty("m_accept", _gesturePrivateInstance)
            ?? throw new MissingMemberException("InstallingPartState.m_accept is unavailable.");
        accept.SetValue(install, true);
        Event("gesture-native-helper", new { command = "gesture-accept", state = install.Pointer.ToInt64(), action = "set native orientation accept flag; game update/state lifecycle still settles edit", mouseClickVerified = false });
    }

    private void GestureCancel()
    {
        if (!Safe) throw new InvalidOperationException("Gesture helpers require the isolated probe save guard.");
        var install = GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>()
            ?? throw new InvalidOperationException("No native InstallingPartState is active.");
        var exit = typeof(InstallingPartState).GetMethod("HandleExit", _gesturePrivateInstance)
            ?? throw new MissingMethodException("InstallingPartState.HandleExit is unavailable.");
        var leave = Convert.ToBoolean(exit.Invoke(install, null));
        if (leave && GameController.Get()?.GetCurrentState()?.Pointer == install.Pointer) GameController.Get().GoToPreviousState();
        Event("gesture-native-helper", new { command = "gesture-cancel", state = install.Pointer.ToInt64(), leave, action = "native HandleExit then native previous-state transition", physicalEscVerified = false });
    }
}
