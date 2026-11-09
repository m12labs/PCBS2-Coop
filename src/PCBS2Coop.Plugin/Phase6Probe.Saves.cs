using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Phase 8 operator commands for the opt-in phase 6 probe (isolated saves only, see <see cref="Safe"/>).</summary>
internal sealed partial class Phase6Probe
{
    private WorldSync _softwareSaveProbe;

    /// <summary>Returns false when <paramref name="command"/> isn't a phase 8 command.</summary>
    private bool SavesCommand(string command)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop == null) return false;
        switch (command)
        {
            case "summary-report": Event("summary-report", Summary(coop)); return true;
            case "pose": Event("pose", LocalPlayer.Find() is { } p ? new { x = p.X, y = p.Y, z = p.Z, yaw = p.Yaw } : null); return true;
            case "save": Event("save", coop.SaveWorld("probe")); return true;
            case "save-during-software": ArmSoftwareSaveProbe(coop); return true;
            case "world-reset": Event("world-switch", new { reset = true, nativeMayRun = coop.OnFreeBuildReset() }); return true;
            case "saves-report": Event("saves-report", SavesReport()); return true;
            case "dialog-report": Event("dialog-report", DialogReport()); return true;
            case "exit":
            {
                // As the pause menu's Exit button: the co-op exit hook runs first.
                var panel = Object.FindObjectOfType<ExitPanel>(true);
                if (panel == null) { Event("exit", "no exit panel"); return true; }
                panel.Exit();
                Event("exit", "called ExitPanel.Exit");
                return true;
            }
        }
        if (command.StartsWith("chat:", StringComparison.Ordinal))
        {
            // The chat box's submit handler (typed input is checked separately), e.g. chat:/sync.
            coop.SubmitChat(command[5..]);
            Event("chat", command[5..]);
            return true;
        }
        if (command.StartsWith("world-load:", StringComparison.Ordinal))
        {
            var name = command[11..];
            Event("world-switch", new { load = name, nativeMayRun = coop.OnPauseMenuLoad(name) });
            return true;
        }
        return false;
    }

    private void ArmSoftwareSaveProbe(CoopController coop)
    {
        if (!Safe || coop.Session.Role != Role.Host || coop.Session.World is not { Admitted: true } world || coop.Software == null)
        { Event("software-save-probe", "An admitted isolated host is required."); return; }
        if (_softwareSaveProbe == world) return;
        _softwareSaveProbe = world;
        world.Accepted += SaveWhileBusy;
        Event("software-save-probe", "Waiting for the next accepted software Begin.");

        void SaveWhileBusy(OperationId op, ulong revision)
        {
            if (Safe && coop.Session.World == world && coop.Software?.Handler.Busy != true) return;
            world.Accepted -= SaveWhileBusy;
            _softwareSaveProbe = null;
            if (!Safe || coop.Session.World != world) return;
            try
            {
                // Accepted runs after Begin has applied and before the publisher sends the next chunk.
                var accepted = coop.SaveWorld("probe pending software");
                Event("software-save-probe", new { operation = op.ToString(), revision, busy = coop.Software.Handler.Busy, accepted });
            }
            catch (Exception e) { Event("software-save-probe", e.ToString()); }
        }
    }

    private static object Summary(CoopController coop)
    {
        var world = coop.Session.World;
        if (world == null || !coop.Objects.Initialized) return new { role = coop.Session.Role.ToString(), world = false };
        string Hash(Action<LiteNetLib.Utils.NetDataWriter> write) { var w = new LiteNetLib.Utils.NetDataWriter(); write(w); return Convert.ToHexString(SHA256.HashData(w.CopyData())); }
        string objectsCheck = null, perifCheck = null;
        try { coop.Objects.CheckProjection(world.Local, coop.Session.LocalIdentity.Player); } catch (Exception e) { objectsCheck = e.Message; }
        try { coop.Workstations.CheckNative(); } catch (Exception e) { perifCheck = e.Message; }
        var m = coop.Objects.Model;
        return new
        {
            role = coop.Session.Role.ToString(), generation = world.Generation, revision = world.Revision, chain = world.ChainHash.ToString("X16"),
            divergence = world.Divergence, projectionFault = coop.Objects.ProjectionFault, sync = coop.SyncState, outOfSync = coop.OutOfSync,
            objects = Convert.ToHexString(SHA256.HashData(m.ToBytes())), activity = Hash(coop.Activity.Write), peripherals = Hash(coop.Workstations.Model.Write),
            pcs = m.Pcs.Count, next = m.NextId, retired = m.Retired.Count, objectsCheck, perifCheck, saveMode = CoopSaves.Current.ToString(),
            placement = m.Pcs.Select(p => $"{p.Id.Value}@{p.Location.Place}:{p.Location.Key}{p.Location.Actor}").OrderBy(x => x)
        };
    }

    private static object SavesReport()
    {
        object Files(string dir) => !Directory.Exists(dir) ? Array.Empty<object>() : Directory.GetFiles(dir)
            .Where(f => !f.EndsWith(".coop", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var readable = CoopSaveBackups.Readable(f, out var problem);
                var fi = new FileInfo(f);
                return (object)new { name = fi.Name, bytes = fi.Length, coopBytes = File.Exists(f + ".coop") ? new FileInfo(f + ".coop").Length : -1, modified = fi.LastWriteTimeUtc.ToString("O"), readable, problem };
            }).ToArray();
        return new
        {
            saveDir = SaveLoadSystem.s_saveDir, coopDir = CoopSaves.CoopDir, mode = CoopSaves.Current.ToString(),
            coop = Files(CoopSaves.CoopDir), backup = Files(CoopSaves.BackupDir),
            unreadable = Directory.Exists(CoopSaves.BackupDir) ? Directory.GetFiles(CoopSaves.BackupDir, "*.unreadable*").Select(Path.GetFileName).ToArray() : Array.Empty<string>(),
            newestReadable = CoopSaves.NewestReadable(CoopSaves.Kind)?.Name
        };
    }

    private static object DialogReport()
    {
        var dialog = CoopRunner.Instance?.Menu?.Dialog;
        if (dialog == null) return new { menu = false };
        var apply = dialog.ApplyButton;
        Vector2 screen = default;
        if (apply != null && apply.GetComponent<RectTransform>() is { } rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var canvas = apply.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            screen = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[2]) / 2);
        }
        return new
        {
            open = dialog.IsOpen, mode = dialog.Current.ToString(), text = dialog.StatusText,
            apply = apply != null && apply.activeInHierarchy ? apply.GetComponentInChildren<TMPro.TMP_Text>()?.text : null,
            // Client-area pixel, measured from the top-left corner (for an ordinary mouse click).
            applyAt = new { x = Mathf.RoundToInt(screen.x), y = Mathf.RoundToInt(Screen.height - screen.y) }
        };
    }
}
