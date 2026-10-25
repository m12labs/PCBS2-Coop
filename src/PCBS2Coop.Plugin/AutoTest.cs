using System.Collections;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PCBS2Coop;

/// <summary>
/// Opt-in self test for two local instances. Off unless PCBS2COOP_AUTOTEST is "host" or "join".
/// It clicks Host/Join, accepts the overwrite prompt, walks the client in a circle and saves screenshots
/// to BepInEx/PCBS2Coop-diag. Normal play never runs this.
/// </summary>
internal sealed class AutoTest
{
    private readonly string _role;
    private readonly string _dir;
    private int _step;
    private float _at;
    private Vector3 _center;
    private bool _walking;

    private AutoTest(string role)
    {
        _role = role;
        _dir = Path.Combine(Paths.BepInExRootPath, "PCBS2Coop-diag");
        Directory.CreateDirectory(_dir);
    }

    public static AutoTest FromEnvironment()
    {
        var role = Environment.GetEnvironmentVariable("PCBS2COOP_AUTOTEST")?.Trim().ToLowerInvariant();
        if (role is not ("host" or "join")) return null;
        Plugin.Logger.LogWarning($"AUTOTEST enabled as '{role}'. Overwrite prompts are auto-accepted.");
        Harmony.CreateAndPatchAll(typeof(AcceptMessageBoxes));
        return new AutoTest(role);
    }

    public void Update(CoopRunner r)
    {
        var now = Time.realtimeSinceStartup;
        if (_walking && _role == "join") Walk(now);
        if (now < _at) return;
        switch (_step)
        {
            case 0 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
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
                if (!r.Menu.Dialog.IsOpen)
                {
                    Plugin.Logger.LogError("AUTOTEST: FAILED - the dialog did not open from the button click.");
                    Next(float.MaxValue);
                    break;
                }
                Plugin.Logger.LogInfo("AUTOTEST: clicking the dialog's apply button with pointer events.");
                Click(r.Menu.Dialog.ApplyButton);
                Next(now);
                break;
            case 5 when r.Scene == CoopRunner.WorkshopScene && LocalPlayer.Find() != null:
                Next(now + 8);
                break;
            case 6:
                _center = LocalPlayer.Root.position + LocalPlayer.Root.forward * 3f;
                _walking = true;
                Next(now + 6);
                break;
            case 7:
                Shot(r, "workshop");
                Next(now + 3);
                break;
            case 8:
                Shot(r, "workshop-2");
                Plugin.Logger.LogInfo("AUTOTEST: finished.");
                Next(float.MaxValue);
                break;
        }
    }

    private void Next(float at) { _step++; _at = at; }

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
            Plugin.Logger.LogInfo($"AUTOTEST: auto-accepting message box '{title}'.");
            yesAction?.Invoke();
            return false;
        }
    }
}
