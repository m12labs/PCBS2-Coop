using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Opt-in single-PC Steam/UI proof. Fresh disposable saves, no invitations, quits its own game.</summary>
internal sealed class SteamProbe
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("PCBS2COOP_STEAM_PROBE") == "1" && !AutoTest.IsEnabled;
    private int _step;
    private float _at, _deadline;
    private string _directory, _saves;
    private bool _finished;

    internal void Update(CoopRunner runner)
    {
        if (_finished || Time.realtimeSinceStartup < _at) return;
        var now = Time.realtimeSinceStartup;
        switch (_step)
        {
            case 0 when runner.Scene == CoopRunner.MenuScene && runner.Menu != null:
                _directory = Path.Combine(Diagnostics.Dir, "steam", Guid.NewGuid().ToString("N"));
                _saves = Path.Combine(_directory, "saves").Replace('\\', '/');
                Directory.CreateDirectory(_saves);
                SaveLoadSystem.s_saveDir = _saves;
                if (SaveLoadSystem.s_saveDir != _saves) { Finish(false, "Save isolation failed."); return; }
                Plugin.Logger.LogInfo("STEAM-PROBE: disposable save folder " + _saves);
                if (SteamBootstrap.Service?.Available != true) { Finish(false, SteamBootstrap.Service?.UnavailableReason ?? SteamBootstrap.Reason); return; }
                runner.Menu.OpenHost();
                _step++; _at = now + 2;
                break;
            case 1:
                Capture(runner, "host-dialog", () => Click(runner.Menu.Dialog.ApplyButton));
                _deadline = now + 180; _step++; _at = now + 1;
                break;
            case 2 when runner.Scene == CoopRunner.WorkshopScene && runner.Coop.WorldReady && SteamBootstrap.Service.CanInvite:
                Plugin.Logger.LogInfo("STEAM-PROBE: Steam world ready and lobby can invite.");
                var controller = Object.FindObjectOfType<GameController>();
                var pause = CommonUI.inGameMenu;
                if (controller == null || pause == null) { Finish(false, "Native pause menu is not ready."); return; }
                controller.SetCurrentState(new UIState(pause.gameObject));
                _step++; _at = now + 3;
                break;
            case 2 when now > _deadline || runner.Coop.LastFailure != null:
                Finish(false, runner.Coop.LastFailure ?? "World readiness timed out.");
                break;
            case 3:
            {
                foreach (var menu in Object.FindObjectsOfType<InGameMenu>(true))
                    foreach (var button in menu.newButtons)
                        if (button != null) Plugin.Logger.LogInfo($"STEAM-PROBE: pause source {button.name}, active={button.gameObject.activeInHierarchy}, working={button.workingButton?.name}.");
                var fallback = GameObject.Find("Use in-game friends list");
                if (fallback == null) { Finish(false, "Fallback control not visible in pause menu."); return; }
                Capture(runner, "pause", () => Click(fallback));
                _step++; _at = now + 2;
                break;
            }
            case 4:
                if (GameObject.Find("CoopSteamFriends") == null) { Finish(false, "Friends picker did not open."); return; }
                Capture(runner, "friends");
                Plugin.Logger.LogInfo("STEAM-PROBE: fallback opened; no invites sent.");
                _step++; _at = now + 2;
                break;
            case 5:
                Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
                _step++; _deadline = now + 60;
                break;
            case 6 when runner.Scene == CoopRunner.MenuScene && runner.Menu != null:
                Finish(!runner.Coop.Session.IsRunning && !SteamBootstrap.Service.HasLobby && SaveLoadSystem.s_saveDir == _saves,
                    "Returned to menu; Steam lobby closed; disposable saves retained. Remote invite/relay still requires two PCs.");
                break;
            case 6 when now > _deadline:
                Finish(false, "Return to menu timed out.");
                break;
        }
    }
    private void Finish(bool passed, string reason)
    {
        _finished = true;
        var message = "STEAM-PROBE: " + (passed ? "PASSED" : "FAILED") + ": " + reason;
        Plugin.Logger.LogInfo(message);
        if (_directory != null) File.WriteAllText(Path.Combine(_directory, "result.txt"), message);
        Application.Quit();
    }
    private static void Click(GameObject go)
    {
        var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, pointerPress = go, pointerEnter = go };
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerClickHandler);
    }
    private void Capture(CoopRunner runner, string name, Action after = null) => runner.StartCoroutine(Screenshot(Path.Combine(_directory, name + ".png"), after).WrapToIl2Cpp());
    private static IEnumerator Screenshot(string path, Action after)
    {
        yield return new WaitForEndOfFrame();
        var texture = ScreenCapture.CaptureScreenshotAsTexture(1);
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture)); Object.Destroy(texture);
        after?.Invoke();
    }
}
