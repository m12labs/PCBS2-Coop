using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Open app windows. Mirrored clicks keep both desktops alike while both games watch them, but a guest that
/// joins later boots each PC fresh, and two simultaneous clicks can split the desktops. The host publishes each
/// desktop's open apps when they change (and when a guest arrives); the guest opens and closes apps to match once
/// its desktop has been quiet for a moment. Window positions and what is inside an app are not corrected.</summary>
internal sealed partial class NativeDesktopInput
{
    private const float WindowsSettle = 3f;

    private readonly Dictionary<NetObjectId, string> _publishedWindows = new();
    private ulong _publishedForGuest;
    private float _nextWindows;
    private readonly Dictionary<NetObjectId, (string Ids, float Since)> _hostWindows = new();
    private readonly HashSet<NetObjectId> _bootRestarted = new();
    private const float StuckBoot = 20f;

    private static string WindowIds(OS os)
    {
        var open = os?.m_openWindows;
        if (open == null) return "";
        var ids = new List<string>();
        for (var i = 0; i < open.Count; i++)
        {
            var id = open[i]?.Desc?.m_id;
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return string.Join("\n", ids);
    }

    /// <summary>Host: publish a desktop's open apps when they change or a new guest presence arrives.</summary>
    private void PublishWindows()
    {
        if (!_world.IsAuthority || Time.unscaledTime < _nextWindows) return;
        _nextWindows = Time.unscaledTime + 1f;
        var host = _coop.Activity.Get(ActorSlot.Host);
        var guest = _coop.Activity.Get(ActorSlot.Guest)?.Epoch ?? 0;
        if (host == null) return;
        if (guest != _publishedForGuest) { _publishedWindows.Clear(); _publishedForGuest = guest; }
        if (guest == 0) return; // nobody to tell
        foreach (var pc in _coop.Objects.Model.Pcs.Where(x => x.Location.Place == PcPlace.Bench))
        {
            var vc = ComputerOf(_coop, pc.Id);
            if (vc == null || SurfaceOf(vc) != DesktopSurface.Desktop || Busy(pc.Id)) continue;
            var ids = WindowIds(vc.m_os);
            if (ids.Length > DesktopEvent.MaxText) continue;
            if (_publishedWindows.TryGetValue(pc.Id, out var last) && last == ids) continue;
            var input = new DesktopInput(host.Epoch, pc.Id, new[]
            { new DesktopEvent(DesktopEventKind.Windows, DesktopSurface.Desktop, Time.unscaledTime, 0, 0, Text: ids) });
            if (_world.Submit(CommandTypes.DesktopInput, input.ToBytes(), out _)) _publishedWindows[pc.Id] = ids;
        }
    }

    private void HostWindows(NetObjectId pc, string ids)
    {
        if (_world.IsAuthority) return;
        _hostWindows[pc] = (ids, Time.unscaledTime);
    }

    /// <summary>Guest: open/close apps until this desktop shows the host's set.</summary>
    private void MatchWindows()
    {
        if (_world.IsAuthority || _hostWindows.Count == 0) return;
        foreach (var (pc, target) in _hostWindows.ToArray())
        {
            var vc = ComputerOf(_coop, pc);
            if (vc == null) { _hostWindows.Remove(pc); continue; }
            var surface = SurfaceOf(vc);
            // A PC that was on when this game loaded the world can stay on its boot screen: the game's boot coroutine
            // throws while the workshop UI is still loading (JobTutorialHintUi.SetSpriteAssetForText, game run
            // mirror-8). The host is on its desktop, so boot this one again, once.
            if (surface == DesktopSurface.Boot && Time.unscaledTime - target.Since > StuckBoot && _bootRestarted.Add(pc))
            {
                Plugin.Logger.LogWarning($"[desktop] PC {pc}: still on its boot screen here while the host's is on the desktop; restarting it.");
                try { NativeProjection.Run(() => vc.Restart()); }
                catch (Exception e) { Plugin.Logger.LogWarning($"[desktop] PC {pc}: restart failed: {e.Message}"); }
                _hostWindows[pc] = (target.Ids, Time.unscaledTime);
                continue;
            }
            if (surface != DesktopSurface.Desktop || Busy(pc) || (_local.Pc == pc && _local.Buttons != 0)) continue;
            var os = vc.m_os;
            var here = WindowIds(os);
            if (here == target.Ids) { _hostWindows.Remove(pc); continue; }
            if (Time.unscaledTime - target.Since < WindowsSettle) continue;
            _hostWindows.Remove(pc);
            try
            {
                var wanted = target.Ids.Length == 0 ? new List<string>() : target.Ids.Split('\n').ToList();
                var open = os.m_openWindows;
                NativeProjection.Run(() =>
                {
                    // Close what the host doesn't have (each ID only as many times as the host has it open).
                    var keep = new List<string>(wanted);
                    for (var i = open.Count - 1; i >= 0; i--)
                    {
                        var frame = open[i];
                        var id = frame?.Desc?.m_id;
                        if (id != null && keep.Remove(id)) continue;
                        if (frame != null) os.CloseWindow(frame);
                    }
                    // Open what's missing, in the host's order.
                    foreach (var id in keep)
                    {
                        var desc = PartsDatabase.s_instance?.GetProgram(id);
                        if (desc == null) { Plugin.Logger.LogWarning($"[desktop] PC {pc}: can't open the host's app {id} here."); continue; }
                        os.Launch(desc, true);
                    }
                });
                Plugin.Logger.LogInfo($"[desktop] PC {pc}: open apps differed from the host's (here [{here.Replace('\n', ',')}], host [{target.Ids.Replace('\n', ',')}]); matched them.");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[desktop] PC {pc}: couldn't match the host's open apps: {e.Message}"); }
        }
    }
}
