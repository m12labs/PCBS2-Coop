using PCBS2Coop.Network;

namespace PCBS2Coop;

internal sealed partial class Phase6Probe
{
    private bool WaterGestureCommand(string command)
    {
        if (!command.StartsWith("gesture-water:", StringComparison.Ordinal)) return false;
        try
        {
            var args = command.Split(':');
            if (args.Length < 3) throw new ArgumentException("gesture-water:<pc>:<report|remove|start|end|fill|drain>[:value]");
            var pc = RequirePc(args[1]);
            var (_, mesh) = NativeBench(pc);
            var connectors = mesh.GetComponentsInChildren<WaterConnector>(true)
                .OrderBy(c => GesturePath(c.transform, mesh.transform), StringComparer.Ordinal).ToArray();
            int ConnectorIndex(WaterConnector connector) => Array.FindIndex(connectors, c => c.Pointer == connector?.Pointer);
            var pipes = mesh.GetComponentsInChildren<WaterPipe>(true)
                .OrderBy(p => ConnectorIndex(p.m_from)).ThenBy(p => ConnectorIndex(p.m_to)).ToArray();
            var loopController = mesh.GetComponentInChildren<WaterLoopController>(true);
            switch (args[2])
            {
                case "report" when args.Length == 3:
                {
                    var state = GameController.Get()?.GetCurrentState();
                    var route = state?.TryCast<RoutePipeState>();
                    var preview = route?.m_case?.Pointer == mesh.Pointer
                        ? typeof(RoutePipeState).GetProperty("m_pipe", _gesturePrivateInstance)?.GetValue(route) as WaterPipe
                        : null;
                    object Nodes(WaterPipe pipe) => pipe.m_nodes?.ToArray().Where(n => n != null).Select(n =>
                    {
                        var position = mesh.transform.InverseTransformPoint(n.transform.position);
                        return new { position.x, position.y, position.z, n.bendFitting };
                    }).ToArray();
                    Event("gesture-water-report", new
                    {
                        pc = pc.Id.Value,
                        powered = mesh.IsRunning(),
                        state = state?.GetIl2CppType().Name,
                        connectors = connectors.Select((c, index) => new { index, path = GesturePath(c.transform, mesh.transform), connected = c.IsConnected() }),
                        pipes = pipes.Select((p, index) => new
                        {
                            index, from = ConnectorIndex(p.m_from), to = ConnectorIndex(p.m_to),
                            part = p.m_pipePartDesc?.m_id,
                            nodes = Nodes(p)
                        }),
                        preview = preview == null ? null : new { part = preview.m_pipePartDesc?.m_id, nodes = Nodes(preview) },
                        loops = loopController?.Loops?.ToArray().Select(loop => new
                        { full = loop.IsFull(), complete = loop.IsComplete(), animating = loop.IsAnimating(), coolant = loop.m_coolant?.m_id })
                    });
                    break;
                }
                case "remove" when args.Length == 4:
                {
                    var work = RequireWork(pc, mesh);
                    var pipe = At(pipes, args[3]);
                    var scope = new WorkingOnComputerState.__c__DisplayClass68_0 { __4__this = work };
                    new WorkingOnComputerState.__c__DisplayClass68_1 { pipe = pipe, field_Public___c__DisplayClass68_0_0 = scope }._DisassemblyTick_b__3();
                    WaterGestureTrace(pc, "pipe removal", "WorkingOnComputerState._DisassemblyTick_b__3");
                    break;
                }
                case "start" when args.Length == 4:
                {
                    var work = RequireWork(pc, mesh);
                    var connector = At(connectors, args[3]);
                    if (connector.IsConnected()) throw new InvalidOperationException("Choose an unconnected pipe fitting.");
                    new WorkingOnComputerState.__c__DisplayClass71_0 { __4__this = work, connector = connector }._PipingTick_b__0();
                    WaterGestureTrace(pc, "pipe start", "WorkingOnComputerState._PipingTick_b__0");
                    break;
                }
                case "end" when args.Length == 4:
                {
                    var state = GameController.Get()?.GetCurrentState()?.TryCast<ConnectPipeState>();
                    if (state?.m_case?.Pointer != mesh.Pointer) throw new InvalidOperationException("No pipe choice is open on this PC.");
                    var to = At(connectors, args[3]);
                    var targets = typeof(ConnectPipeState).GetProperty("m_to", _gesturePrivateInstance)?.GetValue(state)
                        as Il2CppSystem.Collections.Generic.List<WaterConnector>;
                    if (targets?.ToArray().Any(c => c != null && c.Pointer == to.Pointer) != true)
                        throw new InvalidOperationException("The native pipe tool does not offer that fitting.");
                    new ConnectPipeState.__c__DisplayClass11_0 { __4__this = state, to = to }._HandleInput_b__1();
                    WaterGestureTrace(pc, "pipe end", "ConnectPipeState._HandleInput_b__1");
                    break;
                }
                case "fill" when args.Length == 4:
                case "drain" when args.Length == 3:
                {
                    RequireWork(pc, mesh);
                    var loop = loopController?.Loops?.ToArray().SingleOrDefault(l => l.IsComplete())
                        ?? throw new InvalidOperationException("The PC needs exactly one complete native water loop.");
                    var reservoir = loop.GetReservoir() ?? throw new InvalidOperationException("The loop has no reservoir.");
                    if (args[2] == "fill")
                    {
                        if (mesh.IsRunning()) throw new InvalidOperationException("Power off first. This probe skips FillLoopTick's power gesture.");
                        var coolant = PartsDatabase.s_instance.GetDesc(args[3], null);
                        if (coolant?.m_type != PartDesc.Type.PIPE_COOLANT) throw new InvalidOperationException("Choose a coolant SKU.");
                        loopController.FillLoop(reservoir, coolant);
                    }
                    else loopController.EmptyLoop(reservoir);
                    WaterGestureTrace(pc, args[2], args[2] == "fill" ? "WaterLoopController.FillLoop" : "WaterLoopController.EmptyLoop");
                    break;
                }
                default:
                    throw new ArgumentException("Unknown water gesture or wrong number of arguments.");
            }
        }
        catch (Exception e) { Event("gesture-water-error", new { command, error = e.ToString() }); }
        return true;
    }

    private static T At<T>(T[] values, string text) => int.TryParse(text, out var index) && index >= 0 && index < values.Length
        ? values[index] : throw new ArgumentException("Choose an index from gesture-water report.");

    private void WaterGestureTrace(PcFact pc, string action, string callback) => Event("gesture-water-entrypoint", new
    { pc = pc.Id.Value, action, callback, bypasses = "pointer reach and hold input", acceptanceRequiresNativeResult = true });
}
