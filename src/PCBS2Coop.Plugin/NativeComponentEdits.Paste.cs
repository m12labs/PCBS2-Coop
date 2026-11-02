using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Thermal paste on a shared PC (phase 9, owner decisions November 2, 2026). Work mode opens <c>ThermalPasteState</c>
/// (paste on) or <c>ThermalPasteQTipState</c> (paste off) on the PC's CPU; this game opens the tool at once and asks the
/// host for a whole-PC <c>Paste</c> edit (command 9), so the other player waits on that PC. A refused edit closes the
/// tool again. When the tool closes, the editor sends every paste of the PC (on or off), and the other game sets them
/// in place the game's own way: <c>Paste.SetDefaultPaste</c> for paste on, <c>SetApplied(false)</c> and
/// <c>ClearPaste</c> for paste off, then <c>Case.UpdateGameStatus</c> as the tool's own exit does. The game doesn't save
/// the paste picture, so the other game shows its default paste, as after a load.
/// <para>Cleaning (the vacuum) is refused in co-op: dust only builds up when a career day passes, so it waits for
/// career co-op (owner decision).</para>
/// </summary>
internal sealed partial class NativeComponentEdits
{
    private const float PasteTimeout = 600f;

    private static Case PasteCase(State s) => s?.TryCast<ThermalPasteState>()?.m_case ?? s?.TryCast<ThermalPasteQTipState>()?.m_case;

    /// <summary>A game state was just entered: a paste tool on a shared PC asks the host first.</summary>
    internal void PasteEntered(State state)
    {
        var mesh = PasteCase(state);
        if (mesh == null || WireBypass || NativeEditReplay.Active) return;
        var id = _coop.Objects.IdOf(mesh);
        if (id.IsNone) return; // a loose part at a tool table, not a shared PC
        // The Q-tip and the paste tool can follow each other in one session on the same PC.
        if (_local?.Request.Gesture == EditGesture.Paste && _local.Request.Mesh?.Pointer == mesh.Pointer) return;
        if (_pending.Values.Any(r => r.Gesture == EditGesture.Paste && r.Mesh?.Pointer == mesh.Pointer)) return;
        var what = state.TryCast<ThermalPasteQTipState>() != null ? "remove" : "apply";
        var (pc, why) = WiredPc(mesh);
        if (why == null && !Active) why = "The shared world isn't ready on this game yet.";
        if (why == null && (_local != null || _pending.Count > 0)) why = "Your previous change is still finishing.";
        if (why != null) { Refuse(why); StateExit.Leave(state); return; }
        var request = new Request { Pc = pc.Id, Gesture = EditGesture.Paste, Sku = what, Replay = () => { }, Work = state, Mesh = mesh, PastesBefore = Pastes(mesh) };
        Submit(request, ComponentEditWorld.Begin(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, default, 0, EditGesture.Paste, what, default),
            what == "apply" ? "thermal paste" : "thermal paste removal");
        if (_local?.Request != request && !_pending.ContainsValue(request)) StateExit.Leave(state);
    }

    /// <summary>The host refused this player's paste edit: put the pastes back as they were and close the tool.</summary>
    private void PasteRefused(Request request)
    {
        var mesh = request.Mesh;
        if (mesh == null || !mesh) return;
        try
        {
            if (!SamePastes(Pastes(mesh), request.PastesBefore)) SetPastes(mesh, request.PastesBefore);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[paste] restoring the pastes after a refusal failed: " + e.Message); }
        StateExit.Leave(request.Work);
    }

    /// <summary>Every paste of a PC, by its place in the case. The same PC builds the same paste objects on both games.</summary>
    internal static List<PasteFact> Pastes(Case mesh)
    {
        var list = new List<PasteFact>(); var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var paste in mesh.GetComponentsInChildren<Paste>(true))
        {
            if (paste == null) continue;
            var path = PathIn(paste.transform, mesh.transform);
            var n = seen.TryGetValue(path, out var k) ? k : 0; seen[path] = n + 1;
            if (n > 0) path += "#" + n;
            list.Add(new PasteFact(path, paste.m_applied));
        }
        return list;
    }

    private static string PathIn(Transform t, Transform root)
    {
        var path = t.name;
        for (var p = t.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
        return path.Length <= PasteFact.MaxPath ? path : path[^PasteFact.MaxPath..];
    }

    private static bool SamePastes(IReadOnlyList<PasteFact> a, IReadOnlyList<PasteFact> b) => a.Count == b.Count && a.Zip(b).All(x => x.First == x.Second);

    /// <summary>Set each paste on or off the game's own way. Returns how many changed.</summary>
    private static int SetPastes(Case mesh, IReadOnlyList<PasteFact> facts)
    {
        var here = new Dictionary<string, Paste>(StringComparer.Ordinal); var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var paste in mesh.GetComponentsInChildren<Paste>(true))
        {
            if (paste == null) continue;
            var path = PathIn(paste.transform, mesh.transform);
            var n = seen.TryGetValue(path, out var k) ? k : 0; seen[path] = n + 1;
            here[n > 0 ? path + "#" + n : path] = paste;
        }
        var changed = 0;
        foreach (var fact in facts)
        {
            if (!here.TryGetValue(fact.Path, out var paste)) throw new InvalidDataException($"the paste at {fact.Path} is missing on this game");
            if (paste.m_applied == fact.Applied) continue;
            var target = paste; var on = fact.Applied;
            NativeProjection.Run(() =>
            {
                if (on) target.SetDefaultPaste();
                else { target.SetApplied(false); target.ClearPaste(); }
            });
            changed++;
        }
        if (changed > 0) NativeProjection.Run(mesh.UpdateGameStatus);
        return changed;
    }

    /// <summary>Following this player's paste edit: alive while a paste tool is open on that PC; then share the pastes.</summary>
    private void PollPaste()
    {
        var l = _local; var mesh = l.Request.Mesh; var now = Time.realtimeSinceStartup;
        if (mesh == null || !mesh)
        {
            _local = null; _cancel.Add(l.Token);
            Plugin.Logger.LogWarning($"[paste] edit {l.Token}: the PC is gone here; cancelling.");
            return;
        }
        var inTool = InPasteTool(mesh);
        if (inTool && now - l.Started < PasteTimeout) l.Since = now;
        if (inTool) { l.QuietSince = 0; return; }
        if (l.QuietSince == 0) { l.QuietSince = now; return; }
        if (now - l.QuietSince < QuietSeconds) return;
        List<PasteFact> after;
        try
        {
            after = Pastes(mesh);
            if (after.Count is 0 or > PasteFact.MaxPastes) throw new InvalidDataException($"that PC has {after.Count} pastes");
        }
        catch (Exception e)
        {
            _local = null; _cancel.Add(l.Token);
            _coop.ProjectionFailed($"This game's thermal paste edit {l.Token} could not be shared", e);
            return;
        }
        if (SamePastes(after, l.Request.PastesBefore))
        {
            Plugin.Logger.LogInfo($"[paste] edit {l.Token} ({l.Request.Sku}) changed nothing; cancelling.");
            _local = null; _cancel.Add(l.Token);
            return;
        }
        l.Result = EditResult.Pasted(after);
        l.Done = true;
        Plugin.Logger.LogInfo($"[paste] edit {l.Token} finished here: {string.Join(", ", after.Select(p => (p.Applied ? "on " : "off ") + p.Path))}.");
    }

    private static bool InPasteTool(Case mesh)
    {
        var ctrl = GameController.Get();
        if (ctrl == null) return false;
        if (PasteCase(ctrl.GetCurrentState())?.Pointer == mesh.Pointer) return true;
        if (ctrl.m_previousState != null)
            foreach (var s in ctrl.m_previousState) if (PasteCase(s)?.Pointer == mesh.Pointer) return true;
        return false;
    }

    /// <summary>The other player's paste edit was committed: set the pastes here.</summary>
    private void ApplyPastes(PcFact pc, ComponentEdit edit)
    {
        if (!_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) throw new InvalidDataException("the PC is not shown on this game");
        var changed = SetPastes(mesh, edit.Result!.Pastes!);
        Plugin.Logger.LogInfo($"[paste] the other player's thermal paste edit {edit.Token} on PC {pc.Id}: {changed} paste(s) set here.");
        if (_world.IsAuthority) _ready.Add(edit.Token);
    }

    /// <summary>The vacuum (work mode CLEAN): refused in co-op until career co-op.</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState), "VacuumTick")]
    internal static class VacuumRefusal
    {
        private static float s_last = -10f;
        private static bool Prefix()
        {
            if (WireBypass) return true;
            var now = Time.realtimeSinceStartup;
            if (now - s_last > 1f) CoopController.Instance?.Notice("Cleaning isn't shared in co-op yet; it comes with career co-op.");
            s_last = now;
            return false;
        }
    }
}
