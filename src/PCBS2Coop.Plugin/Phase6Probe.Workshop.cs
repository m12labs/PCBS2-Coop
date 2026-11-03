using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Phase 12 operator commands for the opt-in phase 6 probe (isolated saves only). Each one calls the game's own
/// entry point a player's action reaches (the light switch, a seasonal switch, the wallpaper app's apply, the cupboard
/// pickup), so the co-op hooks run as they would for a player.</summary>
internal sealed partial class Phase6Probe
{
    private bool WorkshopCommand(string command)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop == null) return false;
        var bits = command.Split(':');
        try
        {
            switch (bits[0])
            {
                case "room-report": Event("room-report", RoomReport(coop)); return true;
                case "lights":
                {
                    // As the switch's click: the game's own SetOn with the opposite of the current state.
                    var sw = Object.FindObjectsOfType<LightSwitch>().FirstOrDefault();
                    var on = !GameController.Get().IsLightsOn;
                    sw.SetOn(on, true, true);
                    Event("room-command", $"lights {(on ? "on" : "off")}");
                    return true;
                }
                case "season-switch":
                {
                    var id = byte.Parse(bits[1]);
                    var sw = Object.FindObjectsOfType<DecorationSwitch>(true).FirstOrDefault(s => (byte)s.m_id == id);
                    if (sw == null) { Event("room-command", $"no switch {id}"); return true; }
                    sw.OnClickSwitch();
                    Event("room-command", $"season switch {sw.m_id}");
                    return true;
                }
                case "decor-open": coop.Workshop.AppOpened(); Event("room-command", "decor-open"); return true;
                case "decor-close": coop.Workshop.AppClosed(); Event("room-command", "decor-close"); return true;
                case "decor-pick":
                {
                    // decor-pick:<type>[:<n>] as the app's "apply to all" for that area: the n-th other material in the game's catalogue.
                    var type = (Decorator.enSwapObjects)int.Parse(bits[1]);
                    var nth = bits.Length > 2 ? int.Parse(bits[2]) : 0;
                    var dec = Object.FindObjectOfType<Decorator>();
                    var list = new Il2CppSystem.Collections.Generic.List<DecoratorListLink>();
                    dec.CreateSaveList(list);
                    var current = new HashSet<string>();
                    foreach (var l in list) if (l.DecType == type) current.Add(l.CurrentIndex);
                    var names = new List<string>();
                    foreach (var group in dec.DecoratorList)
                        if (group?.SwapList != null)
                            foreach (var m in group.SwapList)
                                if (m != null && m.type == type && !m.isDlcItem && !string.IsNullOrEmpty(m.name) && !current.Contains(m.name) && !names.Contains(m.name)) names.Add(m.name);
                    if (names.Count == 0) { Event("room-command", $"decor-pick {type}: nothing else in the catalogue"); return true; }
                    var pick = names[nth % names.Count];
                    dec.ChangeAllObjectMaterials(type, true, pick);
                    Event("room-command", $"decor-pick {type}: {pick} ({names.Count} choices)");
                    return true;
                }
                case "light-colour":
                {
                    var tm = Object.FindObjectOfType<ThemeManager>();
                    tm.m_currentColour = new Color(float.Parse(bits[1]), float.Parse(bits[2]), float.Parse(bits[3]), 1f); // as the colour wheel does
                    tm.SetLightColor(new Color(float.Parse(bits[1]), float.Parse(bits[2]), float.Parse(bits[3]), 1f));
                    Event("room-command", "light colour set");
                    return true;
                }
                case "wallpaper":
                {
                    // wallpaper:<pc>:<path> as the custom-picture browser's apply; wallpaper:<pc>:USER_WALLPAPER as picking the Windows wallpaper.
                    var pc = new NetObjectId(ulong.Parse(bits[1]));
                    var path = string.Join(":", bits.Skip(2));
                    var os = OsOf(coop, pc);
                    if (os == null) { Event("room-command", $"PC {pc} has no running desktop here"); return true; }
                    var previous = os.m_computer?.m_software?.m_wallpaper;
                    if (path == "USER_WALLPAPER") { os.SetWallpaper(path); coop.Workshop.LocalWallpaper(os, path, true, previous); }
                    else os.SetCustomWallpaper(PCBS.FileExplorer.Tools.LoadSpriteFromFilePath(path), path, true);
                    Event("room-command", $"wallpaper {pc} {path}");
                    return true;
                }
                case "import":
                {
                    // As picking up a PC file from the cupboard: a copy of that PC's record stands in for the file.
                    var pc = new NetObjectId(ulong.Parse(bits[1]));
                    var record = coop.Objects.FindBench(pc)?.GetComputer(true) ?? coop.Objects.RecordFor(pc);
                    if (record == null) { Event("room-command", $"PC {pc} isn't here"); return true; }
                    coop.Lifecycle.RequestImport(NativePcCodec.Read(NativePcCodec.Write(record)));
                    Event("room-command", $"import a copy of PC {pc}");
                    return true;
                }
            }
        }
        catch (Exception e) { Event("room-command", $"{command} failed: {e.Message}"); return true; }
        return false;
    }

    private static OS OsOf(CoopController coop, NetObjectId pc)
    {
        var bench = coop.Objects.FindBench(pc);
        var record = bench?.m_workStation?.m_virtualComputer?.ComputerSave ?? bench?.GetComputer(false);
        return record == null ? null : Object.FindObjectsOfType<OS>().FirstOrDefault(o => o != null && o.m_computer?.Pointer == record.Pointer);
    }

    private static object RoomReport(CoopController coop)
    {
        var dec = Object.FindObjectOfType<Decorator>();
        var items = new List<string>();
        if (dec != null)
        {
            var list = new Il2CppSystem.Collections.Generic.List<DecoratorListLink>();
            dec.CreateSaveList(list);
            foreach (var l in list) items.Add($"{(int)l.DecType}|{l.ID}|{l.SelectionIndex}|{l.CurrentIndex}|{(int)l.Theme}");
        }
        var tm = Object.FindObjectOfType<ThemeManager>();
        var ctrl = GameController.Get();
        var w = coop.Workshop?.Handler;
        var pictures = new List<object>();
        foreach (var pc in coop.Objects.Model.Pcs)
        {
            var bench = coop.Objects.FindBench(pc.Id);
            var record = bench?.m_workStation?.m_virtualComputer?.ComputerSave ?? bench?.GetComputer(false);
            var fact = w?.WallpaperOf(pc.Id);
            var os = OsOf(coop, pc.Id);
            var sprite = os?.m_desktop?.sprite;
            if (fact == null && os == null) continue;
            pictures.Add(new
            {
                pc = pc.Id.ToString(), record = record?.m_software?.m_wallpaper, shared = fact?.Name, hash = fact?.HashText[..12],
                content = fact != null && NativeWorkshop.ReadContent(fact.Hash) != null, screen = sprite == null ? null : $"{sprite.texture?.width}x{sprite.texture?.height}",
            });
        }
        return new
        {
            role = coop.Session.Role.ToString(),
            lightsOn = ctrl?.IsLightsOn, worldLights = w?.LightsOn, season = ctrl?.m_season.ToString(), worldSeason = w?.Season,
            activeDecoration = Object.FindObjectsOfType<DecorationSwitch>(true).Where(s => s.IsDecorationActive()).Select(s => s.m_id.ToString()).ToArray(),
            worldDecoration = w?.ActiveDecoration, decorator = w?.Decorator?.ToString(), loading = dec?.Loading,
            decorations = items.Count, decorHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", items))))[..16],
            decorList = items.ToArray(),
            colour = tm == null ? null : $"{tm.m_currentColour.r:0.00},{tm.m_currentColour.g:0.00},{tm.m_currentColour.b:0.00}", pulse = tm?.GetPulse(),
            pictures,
            pcs = coop.Objects.Model.Pcs.Count, imported = coop.Objects.Model.Pcs.Count(p => p.Provenance.Origin == InstanceOrigin.Imported),
        };
    }
}
