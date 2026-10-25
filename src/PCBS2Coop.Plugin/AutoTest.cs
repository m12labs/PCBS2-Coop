using System.Collections;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Opt-in self test for two local instances. Off unless PCBS2COOP_AUTOTEST is "host" or "join".
/// <list type="number">
/// <item>Redirects the game's save folder to a disposable BepInEx/PCBS2Coop-diag/saves-&lt;role&gt; folder, so the
/// owner's solo saves are never read or overwritten. Overwrite prompts are accepted only once that redirect is confirmed.</item>
/// <item>Clicks Host/Join with real pointer events and loads Free Build; the client walks a circle.</item>
/// <item>Once joined, each side submits five diagnostic markers (the guest also one invalid marker that must be refused),
/// then logs "AUTOTEST: world summary …" for comparing both peers.</item>
/// <item>Writes runtime census files and screenshots to BepInEx/PCBS2Coop-diag.</item>
/// </list>
/// Normal play never runs this.
/// </summary>
internal sealed class AutoTest
{
    private const int MarkersPerSide = 5;

    private readonly string _role;
    private readonly string _dir;
    private int _step;
    private float _at;
    private float _deadline;
    private Vector3 _center;
    private bool _walking;
    private bool _invalidRefused;
    private WorldSync _hookedWorld;

    public static bool IsEnabled => Role is "host" or "join";
    private static string Role => Environment.GetEnvironmentVariable("PCBS2COOP_AUTOTEST")?.Trim().ToLowerInvariant();

    /// <summary>The disposable save folder, set once the redirect is confirmed. Null means saves are not isolated.</summary>
    internal static string IsolatedSaveDir { get; private set; }

    private AutoTest(string role)
    {
        _role = role;
        _dir = Diagnostics.Dir;
        Directory.CreateDirectory(_dir);
    }

    public static AutoTest FromEnvironment()
    {
        if (!IsEnabled) return null;
        Plugin.Logger.LogWarning($"AUTOTEST enabled as '{Role}'. Saves go to a disposable folder; overwrite prompts are accepted only there.");
        Harmony.CreateAndPatchAll(typeof(AcceptMessageBoxes));
        return new AutoTest(Role);
    }

    public void Update(CoopRunner r)
    {
        var now = Time.realtimeSinceStartup;
        if (_walking && _role == "join") Walk(now);
        HookWorld(r);
        if (now < _at) return;
        switch (_step)
        {
            case 0 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
                if (!IsolateSaves()) { Fail("could not redirect saves to a disposable folder; stopping before any game is started."); break; }
                Census("menu");
                Next(now + (_role == "host" ? 6 : 12)); // the client waits so the host is listening first
                break;
            case 1:
                Shot(r, "menu");
                Next(now + 1);
                break;
            case 2:
                // Real pointer events, the same path as a mouse click.
                var target = _role == "host" ? r.Menu.HostButton : r.Menu.JoinButton;
                Plugin.Logger.LogInfo($"AUTOTEST: clicking '{target.name}' with pointer events.");
                Click(target);
                Next(now + 1.5f);
                break;
            case 3:
                Plugin.Logger.LogInfo($"AUTOTEST: dialog open = {r.Menu.Dialog.IsOpen}, scene = {r.Scene}.");
                Shot(r, "dialog");
                Next(now + 1.5f);
                break;
            case 4:
                if (!r.Menu.Dialog.IsOpen) { Fail("the dialog did not open from the button click."); break; }
                Plugin.Logger.LogInfo("AUTOTEST: clicking the dialog's apply button with pointer events.");
                Click(r.Menu.Dialog.ApplyButton);
                Next(now);
                break;
            case 5 when r.Scene == CoopRunner.WorkshopScene && LocalPlayer.Find() != null:
                Next(now + 8);
                break;
            case 6:
                Census("workshop");
                _center = LocalPlayer.Root.position + LocalPlayer.Root.forward * 3f;
                _walking = true;
                Next(now + 6);
                break;
            case 7 when r.Coop.Session.IsJoined && r.Coop.Session.World is { Generation: > 0 } && r.Coop.HasRemoteView:
                Plugin.Logger.LogInfo("AUTOTEST: " + r.Coop.RemoteViewReport);
                Shot(r, "workshop");
                SubmitMarkers(r.Coop.Session.World);
                _deadline = now + 30;
                Next(now + 3);
                break;
            case 8:
                Plugin.Logger.LogInfo("AUTOTEST: " + r.Coop.RemoteViewReport);
                Shot(r, "workshop-2");
                Next(now + 1);
                break;
            case 9 when r.Coop.Markers.Applied.Count >= 2 * MarkersPerSide || now > _deadline:
                var ok = r.Coop.Markers.Applied.Count == 2 * MarkersPerSide && r.Coop.Session.World?.Divergence == null && (_role == "host" || _invalidRefused);
                Plugin.Logger.LogInfo($"AUTOTEST: world summary ({_role}) {Diagnostics.WorldSummary(r.Coop)}");
                Plugin.Logger.LogInfo($"AUTOTEST: {(ok ? "PASSED" : "FAILED")} - markers {r.Coop.Markers.Applied.Count}/{2 * MarkersPerSide}" +
                                      (_role == "join" ? $", invalid marker refused: {_invalidRefused}" : "") + ". Compare both world summaries.");
                Plugin.Logger.LogInfo("AUTOTEST: finished.");
                Next(float.MaxValue);
                break;
        }
    }

    private void Next(float at) { _step++; _at = at; }

    private void Fail(string why)
    {
        Plugin.Logger.LogError("AUTOTEST: FAILED - " + why);
        _step = int.MaxValue;
    }

    private void HookWorld(CoopRunner r)
    {
        var w = r.Coop.Session.World;
        if (w == null || w == _hookedWorld) return;
        _hookedWorld = w;
        w.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (reason == RejectReason.Invalid && text.Contains("marker")) { _invalidRefused = true; Plugin.Logger.LogInfo($"AUTOTEST: invalid marker {op} refused as expected."); }
        };
    }

    private void SubmitMarkers(WorldSync world)
    {
        for (var i = 0; i < MarkersPerSide; i++)
            if (!world.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker($"{_role}-{i}").ToBytes(), out var op))
                Plugin.Logger.LogError($"AUTOTEST: could not submit marker {i}.");
            else Plugin.Logger.LogInfo($"AUTOTEST: submitted marker {_role}-{i} as {op}.");
        if (_role == "join")
            world.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("").ToBytes(), out _); // must be refused by the host
    }

    /// <summary>Point the game's save folder at a fresh disposable folder and confirm the game sees it.</summary>
    private bool IsolateSaves()
    {
        try
        {
            var dir = Path.Combine(_dir, $"saves-{_role}");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var original = SaveLoadSystem.s_saveDir;
            var target = dir.Replace('\\', '/');
            SaveLoadSystem.s_saveDir = target;
            if (SaveLoadSystem.s_saveDir != target) return false;
            IsolatedSaveDir = target;
            Plugin.Logger.LogInfo($"AUTOTEST: game save folder redirected from '{original}' to '{target}'.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("AUTOTEST: save redirect failed: " + e);
            return false;
        }
    }

    private void Census(string tag)
    {
        try { Diagnostics.WriteCensus($"{_role}-{tag}"); }
        catch (Exception e) { Plugin.Logger.LogError("AUTOTEST: census failed: " + e); }
    }

    private static void Click(GameObject go)
    {
        var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, pointerPress = go, pointerEnter = go };
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerClickHandler);
    }

    /// <summary>Client walks a 1.5 m circle 3 m in front of where it spawned, so the host sees it move.</summary>
    private void Walk(float now)
    {
        var cc = LocalPlayer.Controller;
        if (cc == null) return;
        var a = now * 1.2f;
        var target = _center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 1.5f;
        var delta = target - cc.transform.position;
        delta.y = -0.5f;
        cc.Move(delta);
    }

    private void Shot(CoopRunner r, string name)
    {
        var path = Path.Combine(_dir, $"autotest-{_role}-{name}.png");
        r.StartCoroutine(Capture(path).WrapToIl2Cpp());
    }

    private static IEnumerator Capture(string path)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture(1);
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        UnityEngine.Object.Destroy(tex);
        Plugin.Logger.LogInfo($"AUTOTEST screenshot: {path}");
    }

    [HarmonyPatch]
    private static class AcceptMessageBoxes
    {
        [HarmonyPatch(typeof(MessageBox), nameof(MessageBox.Show), typeof(string), typeof(string), typeof(Il2CppSystem.Action), typeof(bool), typeof(Il2CppSystem.Action), typeof(Rewired.ActionElementMap), typeof(bool), typeof(bool), typeof(MessageBox.ImmediateMessageBoxHolder), typeof(MessageBox.ImmediateMessageBoxHolder))]
        [HarmonyPrefix]
        private static bool Prefix(string title, Il2CppSystem.Action yesAction)
        {
            // Accepting "New Game" overwrites the Free Build save in the current save folder. Only do that when the
            // folder is the disposable one and the game still points at it.
            if (IsolatedSaveDir == null || SaveLoadSystem.s_saveDir != IsolatedSaveDir)
            {
                Plugin.Logger.LogError($"AUTOTEST: FAILED - message box '{title}' appeared while saves are not isolated; refusing to accept it.");
                return true; // show it normally and leave the decision to a person
            }
            Plugin.Logger.LogInfo($"AUTOTEST: auto-accepting message box '{title}' (saves isolated in {IsolatedSaveDir}).");
            yesAction?.Invoke();
            return false;
        }
    }
}
