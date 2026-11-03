using System.Security.Cryptography;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Phase 12 content (owner decisions 5 and 6, November 2, 2026): pictures and imported PCs.
/// <list type="bullet">
/// <item>Each game keeps shared content under its SHA-256 in <c>Saves/Coop/Content</c>, so the host still has a world's
/// pictures after Continue, and a guest asks the host for any it doesn't have.</item>
/// <item><b>Custom wallpapers</b>: when this player sets their own picture or their Windows wallpaper on a PC, this game
/// sends the picture (8 MB at most), then the PC's shared picture. Every game shows the shared picture for that PC
/// (<c>OS.GetWallPaperSprite</c>) instead of a file on its own disk or its own Windows wallpaper. The other player's
/// mirrored file-browser apply is skipped: it would pick a file from this player's disk.</item>
/// <item>Content is sent one piece at a time, a few chunks per frame.</item>
/// </list>
/// </summary>
internal sealed partial class NativeWorkshop
{
    private const string UserWallpaper = "USER_WALLPAPER";
    private const int ChunksPerFrame = 32;
    private const float NeedRetry = 30f, StuckLimit = 30f;

    private sealed class Outgoing
    {
        public string What;
        public string Hash;
        public ulong Epoch;
        public List<byte[]> Payloads;
        public int Sent;
        public Action<ulong> After;
        public float StuckSince = -1;
    }

    private readonly Queue<Outgoing> _outgoing = new();
    private readonly Dictionary<string, float> _needed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private float _quietRejectsUntil;

    private void ResetContent() { _outgoing.Clear(); _needed.Clear(); }

    // ---------------- the content store ----------------

    private static string ContentDir => CoopSaves.CoopDir + "/Content";
    private static string PathOf(string hash) => $"{ContentDir}/{hash}.bin";
    private static string Hex(byte[] hash) => Convert.ToHexString(hash);

    public bool HasContent(byte[] hash) => File.Exists(PathOf(Hex(hash)));

    /// <summary>The content with that hash, or null when this game doesn't have it (or the file no longer matches).</summary>
    internal static byte[] ReadContent(byte[] hash)
    {
        var path = PathOf(Hex(hash));
        if (!File.Exists(path) || new FileInfo(path).Length > WorkshopWorld.MaxContentBytes) return null;
        var data = File.ReadAllBytes(path);
        return SHA256.HashData(data).AsSpan().SequenceEqual(hash) ? data : null;
    }

    internal static void StoreContent(byte[] hash, byte[] data)
    {
        var path = PathOf(Hex(hash));
        if (File.Exists(path)) return;
        Directory.CreateDirectory(ContentDir);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void ContentArrived(in MutationContext ctx, byte[] hash, byte[] data)
    {
        var hex = Hex(hash);
        try
        {
            StoreContent(hash, data);
            _needed.Remove(hex);
            if (!ctx.FromLocalPlayer) Plugin.Logger.LogInfo($"[content] {hex[..12]} arrived ({data.Length / 1024} KB).");
            foreach (var fact in Handler.Wallpapers.Where(w => w.HashText == hex).ToList()) ShowWallpaper(fact);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[content] keeping {hex[..12]} failed: {e}"); }
    }

    public void ContentNeeded(in MutationContext ctx, byte[] hash)
    {
        if (!_world.IsAuthority) return;
        var hex = Hex(hash);
        if (_outgoing.Any(o => o.Hash == hex)) return;
        var data = ReadContent(hash);
        if (data == null) { Plugin.Logger.LogWarning($"[content] the guest asked for {hex[..12]}, which this game doesn't have."); return; }
        Plugin.Logger.LogInfo($"[content] the guest is missing {hex[..12]}; sending it ({data.Length / 1024} KB).");
        Queue(data, "a picture for the guest", null);
    }

    /// <summary>Send content (kept here first), then run <paramref name="after"/> with the epoch it was sent under.</summary>
    internal bool SendContent(byte[] data, string what, Action<ulong> after)
    {
        if (data.Length > WorkshopWorld.MaxContentBytes) { _coop.Notice($"{what} is too large to share in co-op (the limit is 8 MB)."); return false; }
        if (!Active || Epoch == null) { _coop.Notice($"{what} isn't shared: the shared world isn't ready on this game yet."); return false; }
        StoreContent(SHA256.HashData(data), data);
        Queue(data, what, after);
        return true;
    }

    private void Queue(byte[] data, string what, Action<ulong> after)
    {
        var epoch = Epoch ?? 0;
        _outgoing.Enqueue(new Outgoing
        {
            What = what, Hash = Hex(SHA256.HashData(data)), Epoch = epoch, Payloads = WorkshopWorld.Send(epoch, data), After = after,
        });
    }

    private void PumpOutgoing()
    {
        if (_outgoing.Count == 0) return;
        var o = _outgoing.Peek();
        if (Epoch != o.Epoch) { _outgoing.Dequeue(); Plugin.Logger.LogWarning($"[content] {o.What} wasn't sent: this player rejoined."); return; }
        for (var n = 0; n < ChunksPerFrame && o.Sent < o.Payloads.Count; n++)
        {
            if (!_world.Submit(CommandTypes.Workshop, o.Payloads[o.Sent], out _))
            {
                if (o.StuckSince < 0) o.StuckSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - o.StuckSince > StuckLimit)
                {
                    _outgoing.Dequeue();
                    _coop.Notice($"{Capital(o.What)} wasn't shared: the connection to the host is too slow.");
                }
                return;
            }
            o.StuckSince = -1; o.Sent++;
        }
        if (o.Sent < o.Payloads.Count) return;
        _outgoing.Dequeue();
        Plugin.Logger.LogInfo($"[content] {o.What} sent ({o.Hash[..12]}, {o.Payloads.Count - 1} chunk(s)).");
        try { o.After?.Invoke(o.Epoch); }
        catch (Exception e) { Plugin.Logger.LogError($"[content] after sending {o.What}: {e}"); }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void ContentRejected(OperationId op, string why)
    {
        if (Time.realtimeSinceStartup < _quietRejectsUntil) return;
        _quietRejectsUntil = Time.realtimeSinceStartup + 5f;
        if (_outgoing.Count > 0) { var o = _outgoing.Dequeue(); _coop.Notice($"{Capital(o.What)} wasn't shared: {why}"); }
        else _coop.Notice("Not shared: " + why);
    }

    /// <summary>Guest: ask the host for the world's pictures this game doesn't have.</summary>
    private void AskForMissing()
    {
        if (_world.IsAuthority || Epoch is not { } epoch) return;
        var now = Time.realtimeSinceStartup;
        foreach (var fact in Handler.Wallpapers)
        {
            var hex = fact.HashText;
            if (HasContent(fact.Hash) || (_needed.TryGetValue(hex, out var at) && now - at < NeedRetry)) continue;
            if (!_world.Submit(CommandTypes.Workshop, WorkshopWorld.Need(epoch, fact.Hash), out _)) return;
            _needed[hex] = now;
            Plugin.Logger.LogInfo($"[content] asked the host for picture {hex[..12]} (PC {fact.Pc}).");
        }
    }

    private float _nextAsk;

    private void UpdateContent()
    {
        PumpOutgoing();
        if (Time.realtimeSinceStartup < _nextAsk) return;
        _nextAsk = Time.realtimeSinceStartup + 2f;
        AskForMissing();
    }

    // ---------------- wallpapers ----------------

    private ComputerSave RecordOf(NetObjectId pc)
    {
        var bench = _coop.Objects.FindBench(pc);
        return bench?.m_workStation?.m_virtualComputer?.ComputerSave ?? bench?.GetComputer(false) ?? _coop.Objects.RecordFor(pc);
    }

    private NetObjectId PcOf(OS os)
    {
        var id = _coop.Objects.IdOf(os?.m_computer);
        if (!id.IsNone || os?.m_workstation == null) return id;
        foreach (var pc in _coop.Objects.Model.Pcs)
            if (_coop.Objects.FindBench(pc.Id)?.m_workStation?.Pointer == os.m_workstation.Pointer) return pc.Id;
        return default;
    }

    private static string UserWallpaperFile()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string file = null;
        try { file = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", null) as string; }
        catch (Exception e) { Plugin.Logger.LogWarning("[content] reading the Windows wallpaper failed: " + e.Message); }
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) return file;
        // Windows keeps its own copy of the picture; the registry can name a file that has since moved.
        var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(copy) ? copy : file;
    }

    /// <summary>This player set a picture of their own on a PC: share it, or put the old wallpaper back when it can't be.</summary>
    internal void LocalWallpaper(OS os, string name, bool aspect, string previous)
    {
        if (!Active) return;
        var pc = PcOf(os);
        if (pc.IsNone) return;
        byte[] data = null; string problem = null;
        try
        {
            var file = name == UserWallpaper ? UserWallpaperFile() : name;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) problem = "the picture file can't be read";
            else if (new FileInfo(file).Length > WorkshopWorld.MaxContentBytes) problem = "it's larger than 8 MB";
            else data = File.ReadAllBytes(file);
        }
        catch (Exception e) { problem = "the picture file can't be read (" + e.Message + ")"; }
        if (data == null || data.Length == 0)
        {
            _coop.Notice($"That wallpaper can't be used in co-op: {problem ?? "the picture is empty"}.");
            Plugin.Logger.LogInfo($"[content] wallpaper on PC {pc} refused: {problem}; back to '{previous}'.");
            if (!string.IsNullOrEmpty(previous) && os.m_computer?.m_software is { } sw)
                NativeProjection.Run(() => { sw.m_wallpaper = previous; os.SetWallpaper(previous); });
            return;
        }
        var hash = SHA256.HashData(data);
        if (Handler.WallpaperOf(pc) is { } known && known.Name == name && known.Hash.AsSpan().SequenceEqual(hash)) return;
        var length = data.Length;
        void Publish(ulong epoch)
        {
            if (_world.Submit(CommandTypes.Workshop, WorkshopWorld.Wallpaper(epoch, pc, name, hash, length, aspect), out _))
                Plugin.Logger.LogInfo($"[content] PC {pc}'s wallpaper shared ({(name == UserWallpaper ? "the Windows wallpaper" : Path.GetFileName(name))}, {length / 1024} KB).");
        }
        // The host already keeps a picture the world uses; then only the PC's choice goes.
        if (Handler.Wallpapers.Any(w => w.Hash.AsSpan().SequenceEqual(hash)) && Epoch is { } now) { StoreContent(hash, data); Publish(now); return; }
        SendContent(data, "the wallpaper picture", Publish);
    }

    public void Wallpaper(in MutationContext ctx, WallpaperFact fact)
    {
        // This player's own screen shows the shared picture too: the game may not read the Windows wallpaper itself.
        if (ctx.FromLocalPlayer) { ShowWallpaper(fact); return; }
        Plugin.Logger.LogInfo($"[content] the other player set a picture on PC {fact.Pc} ({fact.HashText[..12]}).");
        ShowWallpaper(fact, setRecord: true);
    }

    /// <summary>Show a PC's shared picture on its screens here. <paramref name="setRecord"/>: also put the choice in this
    /// game's record of the PC (the host's record is the one phase 6 shares).</summary>
    private void ShowWallpaper(WallpaperFact fact, bool setRecord = false)
    {
        try
        {
            var record = RecordOf(fact.Pc);
            var sw = record?.m_software;
            if (sw == null) return;
            if (!setRecord && sw.m_wallpaper != fact.Name) return;
            NativeProjection.Run(() =>
            {
                sw.m_wallpaper = fact.Name; sw.m_preserveWallpaperAspect = fact.Aspect;
                foreach (var os in Object.FindObjectsOfType<OS>())
                    if (os != null && os.m_computer?.Pointer == record.Pointer) os.SetWallpaper(fact.Name);
            });
            if (!HasContent(fact.Hash)) Plugin.Logger.LogInfo($"[content] PC {fact.Pc}'s picture hasn't arrived yet; its screen shows it when it does.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[content] showing PC {fact.Pc}'s picture failed: {e.Message}"); }
    }

    private void RefreshWallpapers()
    {
        foreach (var fact in Handler.Wallpapers.ToList()) ShowWallpaper(fact);
    }

    private Sprite SpriteFor(WallpaperFact fact)
    {
        var hex = fact.HashText;
        if (_sprites.TryGetValue(hex, out var cached) && cached != null) return cached;
        var data = ReadContent(fact.Hash);
        if (data == null) return null;
        var texture = new Texture2D(2, 2);
        if (!ImageConversion.LoadImage(texture, (Il2CppStructArray<byte>)data)) { Plugin.Logger.LogWarning($"[content] picture {hex[..12]} isn't an image this game can show."); return null; }
        texture.wrapMode = TextureWrapMode.Clamp;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        Object.DontDestroyOnLoad(texture); Object.DontDestroyOnLoad(sprite);
        _sprites[hex] = sprite;
        return sprite;
    }

    // ---------------- hooks ----------------

    /// <summary>A PC with a shared picture shows that picture, whatever this machine has at the chooser's path.</summary>
    [HarmonyPatch(typeof(OS), nameof(OS.GetWallPaperSprite))]
    internal static class WallpaperSpriteHook
    {
        private static bool Prefix(OS __instance, string wallpaper, ref bool Created, ref Sprite __result)
        {
            if (Bypass || string.IsNullOrEmpty(wallpaper)) return true;
            try
            {
                var me = Current;
                if (me == null || !me._coop.Objects.Initialized) return true;
                var pc = me.PcOf(__instance);
                if (pc.IsNone || me.Handler.WallpaperOf(pc) is not { } fact || fact.Name != wallpaper) return true;
                var sprite = me.SpriteFor(fact);
                if (sprite == null) return true;
                __result = sprite; Created = false;
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogError("[content] wallpaper hook failed: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(OS), nameof(OS.SetCustomWallpaper))]
    internal static class CustomWallpaperHook
    {
        private static void Prefix(OS __instance, out string __state) => __state = __instance?.m_computer?.m_software?.m_wallpaper;

        private static void Postfix(OS __instance, string path, bool preserveAspect, string __state)
        {
            if (Bypass || NativeProjection.Active || NativeDesktopInput.Injecting || string.IsNullOrEmpty(path)) return;
            try { Current?.LocalWallpaper(__instance, path, preserveAspect, __state); }
            catch (Exception e) { Plugin.Logger.LogError("[content] custom wallpaper hook failed: " + e); }
        }
    }

    /// <summary>The Windows wallpaper picked in the wallpaper app ("USER_WALLPAPER").</summary>
    [HarmonyPatch(typeof(OS), nameof(OS.SetWallpaper))]
    internal static class UserWallpaperHook
    {
        private static void Prefix(OS __instance, out string __state) => __state = __instance?.m_computer?.m_software?.m_wallpaper;

        private static void Postfix(OS __instance, string wallpaper, string __state)
        {
            if (Bypass || NativeProjection.Active || NativeDesktopInput.Injecting || wallpaper != UserWallpaper) return;
            try
            {
                // Only the player picking it in the wallpaper app, not a boot showing the saved choice.
                if (!Object.FindObjectsOfType<SelectWallpaperApp>().Any(a => a != null && a.isActiveAndEnabled)) return;
                Current?.LocalWallpaper(__instance, UserWallpaper, __instance.m_computer?.m_software?.m_preserveWallpaperAspect ?? true, __state);
            }
            catch (Exception e) { Plugin.Logger.LogError("[content] Windows wallpaper hook failed: " + e); }
        }
    }

    /// <summary>The other player's mirrored "apply" in the custom-picture browser: this game's browser lists this
    /// player's own files, so it would apply an unrelated picture. The other player's game shares theirs.</summary>
    [HarmonyPatch(typeof(SelectWallpaperApp), "OnApplyCustomWallpaper")]
    internal static class MirroredCustomApply
    {
        private static bool Prefix()
        {
            if (Bypass || !NativeDesktopInput.Injecting) return true;
            Plugin.Logger.LogInfo("[content] the other player's custom wallpaper click: their game shares the picture.");
            return false;
        }
    }
}
