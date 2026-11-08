using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Deterministic entry points into the game's hooked cable and power gestures.
/// They skip pointer/hold selection but retain the production request, edit, replay and settle paths.</summary>
internal sealed partial class Phase6Probe
{
    private bool WiringGestureCommand(string command)
    {
        if (!command.StartsWith("gesture-power:", StringComparison.Ordinal) &&
            !command.StartsWith("gesture-unplug:", StringComparison.Ordinal) &&
            !command.StartsWith("gesture-cable-start:", StringComparison.Ordinal) &&
            !command.StartsWith("gesture-cable-end:", StringComparison.Ordinal)) return false;
        try
        {
            var bits = command.Split(':');
            switch (bits[0])
            {
                case "gesture-power":
                    if (bits.Length != 3 || bits[2] is not ("case" or "hud"))
                        throw new ArgumentException("gesture-power:<pc>:case|hud");
                    GesturePower(RequirePc(bits[1]), bits[2]);
                    break;
                case "gesture-unplug":
                    if (bits.Length is < 4 or > 5 || (bits.Length == 5 && bits[4] != "end"))
                        throw new ArgumentException("gesture-unplug:<pc>:<from-connector-id>:<to-connector-id>[:end]");
                    GestureUnplug(RequirePc(bits[1]), bits[2], bits[3], bits.Length == 5);
                    break;
                case "gesture-cable-start":
                    if (bits.Length != 3) throw new ArgumentException("gesture-cable-start:<pc>:<connector-id>");
                    GestureCableStart(RequirePc(bits[1]), bits[2]);
                    break;
                case "gesture-cable-end":
                    if (bits.Length != 2) throw new ArgumentException("gesture-cable-end:<connector-id>");
                    GestureCableEnd(bits[1]);
                    break;
            }
        }
        catch (Exception e) { Event("gesture-wiring-error", new { command, error = e.ToString() }); }
        return true;
    }

    private void GesturePower(PcFact pc, string source)
    {
        var (_, mesh) = NativeBench(pc);
        if (source == "case")
        {
            var button = mesh.GetComponentInChildren<PowerButton>(true)
                ?? throw new InvalidOperationException("The selected PC has no native power button.");
            Event("gesture-direct-call", new
            {
                command = "gesture-power",
                pc = pc.Id.Value,
                source,
                path = "PowerButton.Press → NativeComponentEdits.PowerButtonIntercept → RequestPower",
                bypasses = new[] { "pointer/raycast/press animation input" },
                poweredBefore = mesh.IsRunning()
            });
            button.Press();
        }
        else
        {
            var work = GameController.Get()?.GetCurrentState()?.TryCast<WorkingOnComputerState>()
                ?? throw new InvalidOperationException("The HUD power command requires this PC's active work state.");
            if (work.m_case?.Pointer != mesh.Pointer) throw new InvalidOperationException("The active work state targets a different PC.");
            Event("gesture-direct-call", new
            {
                command = "gesture-power",
                pc = pc.Id.Value,
                source,
                path = "WorkingOnComputerStateBase.PowerClicked → NativeComponentEdits.PowerClickIntercept → RequestPower",
                bypasses = new[] { "HUD pointer/raycast/click input" },
                poweredBefore = mesh.IsRunning()
            });
            work.PowerClicked();
        }
        Event("gesture-wiring-entrypoint-returned", new { command = "gesture-power", pc = pc.Id.Value, source, acceptedMustBeConfirmedByNativeEditTrace = true });
    }

    private void GestureUnplug(PcFact pc, string fromId, string toId, bool selectEnd)
    {
        var (_, mesh) = NativeBench(pc);
        var work = RequireWork(pc, mesh);
        var cable = mesh.GetComponentsInChildren<CableInstance>(true).FirstOrDefault(candidate =>
            candidate != null && candidate.m_start != null && candidate.m_end != null &&
            candidate.m_start.id == fromId && candidate.m_end.id == toId)
            ?? throw new InvalidOperationException($"No native cable runs from '{fromId}' to '{toId}' on this PC.");
        var plug = selectEnd
            ? cable.GetCableEnd() ?? (cable.m_plugs is { Length: > 0 } endPlugs ? endPlugs[^1] : null)
            : (cable.m_plugs is { Length: > 0 } startPlugs ? startPlugs[0] : null) ?? cable.GetCableEnd()
            ?? throw new InvalidOperationException("The selected cable has no native end plug.");
        var closure = new WorkingOnComputerState.__c__DisplayClass67_0 { __4__this = work, cableEnd = plug };
        Event("gesture-direct-call", new
        {
            command = "gesture-unplug",
            pc = pc.Id.Value,
            from = fromId,
            to = toId,
            callback = "WorkingOnComputerState.__c__DisplayClass67_0._RemoveCableTick_b__0",
            selectedPlug = selectEnd ? "cable end (GetCableEnd)" : "first native plug (start end)",
            bypasses = new[] { "pointer/reach/hold input" },
            nativeUnplugAnimationRuns = true
        });
        closure._RemoveCableTick_b__0();
        Event("gesture-wiring-entrypoint-returned", new { command = "gesture-unplug", pc = pc.Id.Value, from = fromId, to = toId, acceptedMustBeConfirmedByNativeEditTrace = true });
    }

    private void GestureCableStart(PcFact pc, string connectorId)
    {
        var (_, mesh) = NativeBench(pc);
        var work = RequireWork(pc, mesh);
        var connector = mesh.GetComponentsInChildren<PowerConnector>(true).FirstOrDefault(candidate => candidate != null && candidate.id == connectorId)
            ?? throw new InvalidOperationException($"Connector '{connectorId}' is not present on this PC.");
        var closure = new WorkingOnComputerState.__c__DisplayClass70_0 { __4__this = work, connector = connector };
        Event("gesture-direct-call", new
        {
            command = "gesture-cable-start",
            pc = pc.Id.Value,
            connector = connectorId,
            callback = "WorkingOnComputerState.__c__DisplayClass70_0._CablingTick_b__0",
            bypasses = new[] { "pointer/reach/long-press input" },
            nativeConnectStateAndAutomaticSplitterPathRuns = true
        });
        closure._CablingTick_b__0();
        Event("gesture-wiring-entrypoint-returned", new { command = "gesture-cable-start", pc = pc.Id.Value, connector = connectorId, acceptedMustBeConfirmedByNativeEditTrace = true });
    }

    private void GestureCableEnd(string connectorId)
    {
        var coop = RequireGestureWorld();
        var state = GameController.Get()?.GetCurrentState()?.TryCast<ConnectCableState>()
            ?? throw new InvalidOperationException("There is no active native cable connection choice; run gesture-cable-start first.");
        var source = state.m_case;
        var pcId = source == null ? default : coop.Objects.IdOf(source);
        if (source == null || pcId.Value == 0)
            throw new InvalidOperationException("The active cable state is not attached to a bound PC.");
        var pc = coop.Objects.Model.Pcs.SingleOrDefault(x => x.Id == pcId)
            ?? throw new InvalidOperationException("The active cable state PC is not in the admitted world.");
        if (pc.Location.Place != PcPlace.Bench || NativeBench(pc).Mesh.Pointer != source.Pointer)
            throw new InvalidOperationException("The active cable state no longer targets its bound bench PC.");
        var target = state.PowerCons?.ToArray().SingleOrDefault(candidate => candidate != null && candidate.id == connectorId)
            ?? throw new InvalidOperationException("The active ConnectCableState does not offer that connector as a valid target.");
        var closure = new ConnectCableState.__c__DisplayClass20_0 { __4__this = state, to = target };
        Event("gesture-direct-call", new
        {
            command = "gesture-cable-end",
            pc = pc.Id.Value,
            connector = connectorId,
            callback = "ConnectCableState.__c__DisplayClass20_0._HandleInput_b__0",
            bypasses = new[] { "pointer/raycast/connector click input" },
            nativeConnectAnimationAndCommitRun = true
        });
        closure._HandleInput_b__0();
        Event("gesture-wiring-entrypoint-returned", new { command = "gesture-cable-end", pc = pc.Id.Value, connector = connectorId, acceptedMustBeConfirmedByNativeEditTrace = true });
    }
}
