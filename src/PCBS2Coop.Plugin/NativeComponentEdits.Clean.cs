using HarmonyLib;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>
/// Cleaning a shared PC (phase 17c, owner decisions November 9, 2026: shared as a result, like thermal paste, in Free
/// Build and Career). The vacuum is the work mode <c>CLEAN</c> of <c>WorkingOnComputerState</c>, whose
/// <c>VacuumTick</c> runs each frame in that mode. Its first tick on a shared PC asks the host for a whole-PC
/// <c>Clean</c> edit (command 9), so the other player waits on that PC; this player's vacuum works at once. A refused
/// edit puts the dust back. When this player leaves the cleaning mode, the editor sends each part's saved dust
/// (<c>PartInstance.m_filth</c>, which the vacuum lowers) and the PC's dust totals (<c>ComputerSave.SetDust</c>, which
/// <c>Case.UpdateDustInSave</c> writes). The other game sets each part's dust, rebuilds its dust picture
/// (<c>ComponentDust.RefreshDust</c>, a random pattern from the amount, as after a load) and sets the totals. A job's
/// cleaning objective reads those totals, so both games then agree on it.
/// </summary>
internal sealed partial class NativeComponentEdits
{
    private const float CleanTimeout = 600f, CleanRetrySeconds = 2f, CleanNoticeSeconds = 5f, DustEpsilon = 1e-4f;
    private IntPtr _cleanRefusedMesh;
    private float _cleanRefusedAt = -100f, _cleanNoticeAt = -100f;

    private static WorkingOnComputerState Cleaning(State s)
    {
        var work = s?.TryCast<WorkingOnComputerState>();
        return work != null && work.m_mode == WorkingOnComputerState.Mode.CLEAN ? work : null;
    }

    /// <summary>A vacuum tick on <paramref name="work"/>'s PC: whether this game's vacuum may run now. The first tick
    /// on a shared PC asks the host for the cleaning edit.</summary>
    internal bool VacuumAllowed(WorkingOnComputerState work)
    {
        var mesh = work?.m_case;
        if (mesh == null || NativeEditReplay.Active) return true;
        if (_coop.Objects.IdOf(mesh).IsNone) return true; // not a shared PC
        // This player's cleaning still running here; once it is being sent, more vacuuming waits for the next one.
        if (_local is { Done: false } l && l.Request.Gesture == EditGesture.Clean && l.Request.Mesh?.Pointer == mesh.Pointer) return true;
        if (_pending.Values.Any(r => r.Gesture == EditGesture.Clean && r.Mesh?.Pointer == mesh.Pointer)) return true;
        var now = Time.realtimeSinceStartup;
        if (_cleanRefusedMesh == mesh.Pointer && now - _cleanRefusedAt < CleanRetrySeconds) return false;
        var (pc, why) = WiredPc(mesh);
        if (why == null && !Active) why = "The shared world isn't ready on this game yet.";
        if (why == null && (_local != null || _pending.Count > 0)) why = "Your previous change is still finishing.";
        DustResult before = null;
        if (why == null)
            try { before = Dust(mesh); }
            catch (Exception e) { why = "This game can't read that PC's dust: " + e.Message; }
        if (why != null) { CleanRefusedHere(mesh, why); return false; }
        var request = new Request { Pc = pc.Id, Gesture = EditGesture.Clean, Sku = "clean", Replay = () => { }, Work = work, Mesh = mesh, DustBefore = before };
        Submit(request, ComponentEditWorld.Begin(_coop.Activity.Get(_world.Local)!.Epoch, pc.Id, default, 0, EditGesture.Clean, "clean", default), "cleaning");
        var running = _local?.Request == request || _pending.ContainsValue(request);
        if (!running) { _cleanRefusedMesh = mesh.Pointer; _cleanRefusedAt = now; }
        return running;
    }

    private void CleanRefusedHere(Case mesh, string why)
    {
        var now = Time.realtimeSinceStartup;
        _cleanRefusedMesh = mesh.Pointer; _cleanRefusedAt = now;
        if (now - _cleanNoticeAt < CleanNoticeSeconds) return;
        _cleanNoticeAt = now;
        Refuse(why);
    }

    /// <summary>The host refused this player's cleaning: put the dust back as it was.</summary>
    private void CleanRefused(Request request)
    {
        _cleanRefusedMesh = request.Mesh?.Pointer ?? IntPtr.Zero; _cleanRefusedAt = Time.realtimeSinceStartup;
        var mesh = request.Mesh;
        if (mesh == null || !mesh || request.DustBefore == null) return;
        try { SetDust(mesh, request.DustBefore); }
        catch (Exception e) { Plugin.Logger.LogWarning("[clean] restoring the dust after a refusal failed: " + e.Message); }
    }

    /// <summary>Each part's dust by where its dust component sits in the case, and the PC's saved totals. The same PC
    /// builds the same dust components on both games.</summary>
    internal static DustResult Dust(Case mesh)
    {
        var parts = new List<DustFact>(); var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dust in mesh.GetComponentsInChildren<ComponentDust>(true))
        {
            var part = dust == null ? null : dust.GetPartInstance();
            if (part == null) continue;
            parts.Add(new DustFact(UniquePath(dust.transform, mesh.transform, seen), Math.Max(0f, part.GetFilth())));
        }
        var save = mesh.m_computer;
        return new DustResult(parts, save == null ? 0f : Math.Max(0f, save.GetCurrentDust(true)), save == null ? 0f : Math.Max(0f, save.GetTotalDust(true)));
    }

    private static string UniquePath(Transform t, Transform root, Dictionary<string, int> seen)
    {
        var path = PathIn(t, root);
        var n = seen.TryGetValue(path, out var k) ? k : 0; seen[path] = n + 1;
        return n > 0 ? path + "#" + n : path;
    }

    private static bool SameDust(DustResult a, DustResult b) =>
        a.Parts.Count == b.Parts.Count && a.Parts.Zip(b.Parts).All(x => x.First.Path == x.Second.Path && Math.Abs(x.First.Filth - x.Second.Filth) <= DustEpsilon) &&
        Math.Abs(a.Current - b.Current) <= DustEpsilon && Math.Abs(a.Total - b.Total) <= DustEpsilon;

    /// <summary>Set each part's dust and the PC's totals the game's own way. Returns how many parts changed.</summary>
    private static int SetDust(Case mesh, DustResult result)
    {
        var here = new Dictionary<string, ComponentDust>(StringComparer.Ordinal); var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dust in mesh.GetComponentsInChildren<ComponentDust>(true))
            if (dust != null && dust.GetPartInstance() != null) here[UniquePath(dust.transform, mesh.transform, seen)] = dust;
        var changed = new List<(ComponentDust Dust, float Filth)>();
        foreach (var fact in result.Parts)
        {
            if (!here.TryGetValue(fact.Path, out var dust)) throw new InvalidDataException($"the dusty part at {fact.Path} is missing on this game");
            if (Math.Abs(dust.GetPartInstance().GetFilth() - fact.Filth) > DustEpsilon) changed.Add((dust, fact.Filth));
        }
        NativeProjection.Run(() =>
        {
            foreach (var (dust, filth) in changed)
            {
                dust.GetPartInstance().SetFilth(filth);
                dust.RefreshDust(true);
            }
            mesh.m_computer?.SetDust(result.Current, result.Total);
        });
        return changed.Count;
    }

    /// <summary>Following this player's cleaning: alive while this game is in the cleaning mode on that PC; then share the
    /// dust.</summary>
    private void PollClean()
    {
        var l = _local; var mesh = l.Request.Mesh; var now = Time.realtimeSinceStartup;
        if (mesh == null || !mesh)
        {
            _local = null; _cancel.Add(l.Token);
            Plugin.Logger.LogWarning($"[clean] edit {l.Token}: the PC is gone here; cancelling.");
            return;
        }
        var cleaning = InCleaning(mesh);
        if (cleaning && now - l.Started < CleanTimeout) l.Since = now;
        if (cleaning) { l.QuietSince = 0; return; }
        if (l.QuietSince == 0) { l.QuietSince = now; return; }
        if (now - l.QuietSince < QuietSeconds) return;
        DustResult after;
        try
        {
            after = Dust(mesh);
            if (after.Parts.Count is 0 or > DustFact.MaxParts) throw new InvalidDataException($"that PC has {after.Parts.Count} dusty parts");
        }
        catch (Exception e)
        {
            _local = null; _cancel.Add(l.Token);
            _coop.ProjectionFailed($"This game's cleaning {l.Token} could not be shared", e);
            return;
        }
        if (l.Request.DustBefore != null && SameDust(after, l.Request.DustBefore))
        {
            Plugin.Logger.LogInfo($"[clean] edit {l.Token} changed no dust; cancelling.");
            _local = null; _cancel.Add(l.Token);
            return;
        }
        l.Result = EditResult.Cleaned(after);
        l.Done = true;
        Plugin.Logger.LogInfo($"[clean] edit {l.Token} finished here: {after.Parts.Count} part(s), dust {after.Current:F2} of {after.Total:F2}.");
    }

    private static bool InCleaning(Case mesh)
    {
        var ctrl = GameController.Get();
        return ctrl != null && Cleaning(ctrl.GetCurrentState())?.m_case?.Pointer == mesh.Pointer;
    }

    /// <summary>The other player's cleaning was committed: set the dust here.</summary>
    private void ApplyDust(PcFact pc, ComponentEdit edit)
    {
        if (!_coop.Objects.Projections.TryGet(pc.Id, out var mesh) || mesh == null) throw new InvalidDataException("the PC is not shown on this game");
        var changed = SetDust(mesh, edit.Result!.Dust!);
        Plugin.Logger.LogInfo($"[clean] the other player's cleaning {edit.Token} on PC {pc.Id}: {changed} part(s) set here.");
        if (_world.IsAuthority) _ready.Add(edit.Token);
    }

    /// <summary>The vacuum on a shared PC goes through <see cref="VacuumAllowed"/>.</summary>
    [HarmonyPatch(typeof(WorkingOnComputerState), "VacuumTick")]
    internal static class VacuumGate
    {
        private static bool Prefix(WorkingOnComputerState __instance)
        {
            if (WireBypass) return true;
            try { return Current?.VacuumAllowed(__instance) ?? true; }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[clean] vacuum gate failed: " + e);
                return false;
            }
        }
    }
}
