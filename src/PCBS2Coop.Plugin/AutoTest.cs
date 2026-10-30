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
/// owner's solo saves are never read or overwritten. Overwrite prompts are accepted only inside that folder.</item>
/// <item>Host: Co-op → Host Co-op → New Game → Start (co-op folder inside the disposable one); once its world is ready it submits three
/// markers before the guest is in, so the guest must receive them through the snapshot.</item>
/// <item>Guest: Co-op → Join a Friend → Connect later, receives and loads the host's world, then walks a circle.</item>
/// <item>Both: aim the camera at the other player's capsule for a screenshot; five markers each (the guest also one
/// invalid marker that must be refused). The host pauses its game while markers flow and saves once (the guest must
/// receive the copy). Both log "AUTOTEST: world summary …" and PASSED/FAILED.</item>
/// <item>Departure: the host returns to the main menu; the guest must follow with the reason and both must get
/// their save folder back.</item>
/// </list>
/// Census files and screenshots go to BepInEx/PCBS2Coop-diag. Normal play never runs this.
/// </summary>
internal sealed class AutoTest
{
    private const int MarkersPerSide = 5;
    private const int HostPreMarkers = 3;
    /// <summary>Host markers before the guest is in, one while it loads, then five from each side.</summary>
    private const int ExpectedMarkers = HostPreMarkers + 1 + 2 * MarkersPerSide;

    private readonly string _role;
    private readonly string _dir;
    private int _step;
    private float _at;
    private float _deadline;
    private Vector3 _center;
    private bool _walking;
    private bool _invalidRefused;
    private bool _passed;
    private long _statesAtPause;
    private WorldSync _hookedWorld;

    public static bool IsEnabled => Role is "host" or "join";
    private static string Role => Environment.GetEnvironmentVariable("PCBS2COOP_AUTOTEST")?.Trim().ToLowerInvariant();

    /// <summary>Probe host relaunch: reuse this run's disposable saves and press Continue instead of New Game.</summary>
    private static bool ProbeResume => Phase3Probe.Enabled && Environment.GetEnvironmentVariable("PCBS2COOP_PROBE_RESUME") == "1";

    /// <summary>The disposable save folder, set once the redirect is confirmed. Null means saves are not isolated.</summary>
    internal static string IsolatedSaveDir { get; private set; }

    /// <summary>Move the isolation to another disposable folder (probe use only).</summary>
    internal static void SetIsolatedSaveDir(string dir) => IsolatedSaveDir = dir;

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
        // Phase 3 audit uses the same isolated host/join setup, then leaves the controls to the operator.
        // Do not run the marker/pause/departure sequence underneath the object-action probes.
        if (Phase3Probe.Enabled && _step >= 5) return;
        var now = Time.realtimeSinceStartup;
        if (_walking && _role == "join") Walk(now);
        HookWorld(r);
        if (now < _at) return;
        if (_role == "host") HostSteps(r, now);
        else JoinSteps(r, now);
    }

    private void CommonSteps(CoopRunner r, float now, float startDelay)
    {
        switch (_step)
        {
            case 0 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
                if (!IsolateSaves()) { Fail("could not redirect saves to a disposable folder; stopping before any game is started."); break; }
                Census("menu");
                Next(now + startDelay);
                break;
            case 1:
                Shot(r, "menu");
                Plugin.Logger.LogInfo("AUTOTEST: clicking the main menu's 'Co-op' entry with pointer events.");
                Click(r.Menu.Entry);
                Next(now + 2);
                break;
            case 2:
            {
                if (!r.Menu.CoopReady) { Fail("the co-op list did not open from the 'Co-op' entry."); break; }
                Shot(r, "coop-menu");
                if (ProbeResume && _role == "host")
                {
                    if (!r.Menu.ContinueButton.activeInHierarchy) { Fail("resume run has no world to continue."); break; }
                    Plugin.Logger.LogInfo("AUTOTEST: resuming this probe run; clicking Host Co-op > 'Continue' with pointer events.");
                    Click(r.Menu.ContinueButton);
                    _deadline = now + 240;
                    _step = 4; Next(now);
                    break;
                }
                var target = _role == "host" ? r.Menu.NewGameButton : r.Menu.JoinButton;
                Plugin.Logger.LogInfo($"AUTOTEST: clicking '{target.name}' with pointer events.");
                Click(target);
                Next(now + 1.5f);
                break;
            }
            case 3:
                Plugin.Logger.LogInfo($"AUTOTEST: dialog open = {r.Menu.Dialog.IsOpen} ({r.Menu.Dialog.Current}), worlds = {CoopSaves.WorldSaves().Count}, scene = {r.Scene}.");
                Shot(r, "dialog");
                Next(now + 1.5f);
                break;
            case 4:
                if (!r.Menu.Dialog.IsOpen) { Fail("the popup did not open from the button click."); break; }
                Plugin.Logger.LogInfo($"AUTOTEST: clicking the popup's '{(_role == "host" ? "Start" : "Connect")}' button with pointer events.");
                Click(r.Menu.Dialog.ApplyButton);
                _deadline = now + 240;
                Next(now);
                break;
        }
    }

    private void HostSteps(CoopRunner r, float now)
    {
        var s = r.Coop.Session;
        if (_step <= 4) { CommonSteps(r, now, 6); return; }
        switch (_step)
        {
            // ---- round 1: new world, late join, changes, pause, save, host leaves ----
            case 5 when r.Scene == CoopRunner.WorkshopScene && r.Coop.WorldReady && s.World is { Generation: > 0 }:
                Plugin.Logger.LogInfo($"AUTOTEST: host world ready: save folder '{SaveLoadSystem.s_saveDir}', {Counts()}.");
                Census("workshop");
                for (var i = 0; i < HostPreMarkers; i++) Submit(s.World, $"host-pre-{i}");
                _deadline = now + 240;
                Next(now);
                break;
            case 5 when now > _deadline:
                Fail($"host world not ready (scene {r.Scene}, waiting for: {r.Coop.WorldWaiting}).");
                break;
            case 6 when s.SendingSnapshot || s.GuestState == GuestState.Loading:
                // A change made after the snapshot was taken: the guest must hold it and apply it after loading.
                Submit(s.World, "host-during-load");
                Next(now);
                break;
            case 6 when now > _deadline:
                Fail($"no guest started loading the world (guest state {s.GuestState}).");
                break;
            case 7 when s.GuestState == GuestState.InWorld && r.Coop.HasRemoteView && RemoteDistance(r) > 2f:
                AimAtRemote(r);
                Next(now + 1);
                break;
            case 7 when now > _deadline:
                Fail($"the guest never appeared away from the host (guest state {s.GuestState}, view {r.Coop.HasRemoteView}).");
                break;
            case 8:
                Plugin.Logger.LogInfo("AUTOTEST: " + r.Coop.RemoteViewReport);
                Shot(r, "workshop");
                var gc = UnityEngine.Object.FindObjectOfType<GameController>();
                gc?.PauseGame();
                Plugin.Logger.LogInfo($"AUTOTEST: host paused its game (paused={gc?.GetPaused()}, timeScale={Time.timeScale}); markers keep flowing.");
                _statesAtPause = s.Stats.PlayerStatesReceived;
                for (var i = 0; i < MarkersPerSide; i++) Submit(s.World, $"host-{i}");
                Next(now + 3);
                break;
            case 9:
            {
                var gc2 = UnityEngine.Object.FindObjectOfType<GameController>();
                Plugin.Logger.LogInfo($"AUTOTEST: during 3 s of host pause: {s.Stats.PlayerStatesReceived - _statesAtPause} positions received, markers now {r.Coop.Markers.Applied.Count}.");
                gc2?.UnpauseGame();
                var saves = UnityEngine.Object.FindObjectOfType<SaveLoadSystem>();
                Plugin.Logger.LogInfo("AUTOTEST: host saves its world (SaveCurrentGame); the guest should receive a copy.");
                saves?.SaveCurrentGame();
                _deadline = now + 60;
                Next(now + 1);
                break;
            }
            case 10 when r.Coop.Markers.Applied.Count >= ExpectedMarkers || now > _deadline:
                Summary(r, r.Coop.Markers.Applied.Count == ExpectedMarkers && s.World?.Divergence == null, "");
                Census("shared");
                Next(now + 15); // let the guest finish its own checks
                break;
            case 11:
                Plugin.Logger.LogInfo("AUTOTEST: host leaving the world (departure test).");
                UnityEngine.Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
                _deadline = now + 60;
                Next(now);
                break;
            case 12 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
            {
                var restored = SaveLoadSystem.s_saveDir == IsolatedSaveDir;
                Plugin.Logger.LogInfo($"AUTOTEST: host back on the main menu; session running={s.IsRunning}; save folder '{SaveLoadSystem.s_saveDir}' restored={restored}.");
                Plugin.Logger.LogInfo($"AUTOTEST: departure {(restored && !s.IsRunning ? "OK" : "FAILED")} (host).");
                Next(now + 3);
                break;
            }

            // ---- round 2: host continues the co-op world, guest rejoins, guest leaves first ----
            case 13:
                Plugin.Logger.LogInfo($"AUTOTEST: round 2 - host continues the co-op world (back on the co-op list: {r.Menu.CoopReady}).");
                if (!r.Menu.CoopReady) Click(r.Menu.Entry);
                Next(now + 1.5f);
                break;
            case 14:
            {
                var canContinue = r.Menu.CoopReady && r.Menu.ContinueButton.activeInHierarchy;
                Plugin.Logger.LogInfo($"AUTOTEST: co-op list shown = {r.Menu.CoopShown}, Continue shown = {canContinue}, worlds = [{string.Join(", ", CoopSaves.WorldSaves().Select(f => f.Name))}].");
                Shot(r, "coop-menu-2");
                if (!canContinue) { Fail("Continue is not offered after a co-op save."); break; }
                Click(r.Menu.ContinueButton);
                _deadline = now + 240;
                Next(now);
                break;
            }
            case 15 when r.Scene == CoopRunner.WorkshopScene && r.Coop.WorldReady && s.World is { Generation: > 0 }:
            {
                var level = UnityEngine.Object.FindObjectOfType<LevelLoadPersistency>();
                var saves = UnityEngine.Object.FindObjectOfType<SaveLoadSystem>();
                Plugin.Logger.LogInfo($"AUTOTEST: continued world ready: loaded from save={saves?.WasLoadedFromSave()}, save name '{level?.SaveName}', save folder '{SaveLoadSystem.s_saveDir}', markers {r.Coop.Markers.Applied.Count}, {Counts()}.");
                Next(now);
                break;
            }
            case 15 when now > _deadline:
                Fail($"continued world not ready (scene {r.Scene}, waiting for: {r.Coop.WorldWaiting}).");
                break;
            case 16 when s.GuestState == GuestState.InWorld:
                Plugin.Logger.LogInfo("AUTOTEST: the guest rejoined the continued world.");
                Next(now);
                break;
            case 16 when now > _deadline:
                Fail("the guest did not rejoin.");
                break;
            case 17 when !s.IsJoined:
            {
                var ok = s.IsRunning && r.Scene == CoopRunner.WorkshopScene && s.World is { Generation: > 0 } && s.GuestState == GuestState.Connected;
                Plugin.Logger.LogInfo($"AUTOTEST: the guest left; host still hosting={s.IsRunning} in {r.Scene}, generation {s.World?.Generation}.");
                Plugin.Logger.LogInfo($"AUTOTEST: round 2 {(ok ? "OK" : "FAILED")} (host).");
                Plugin.Logger.LogInfo("AUTOTEST: finished.");
                Next(float.MaxValue);
                break;
            }
            case 17 when now > _deadline + 120:
                Fail("the guest never left.");
                break;
        }
    }

    private void JoinSteps(CoopRunner r, float now)
    {
        var s = r.Coop.Session;
        if (_step <= 4) { CommonSteps(r, now, 30); return; }
        switch (_step)
        {
            // ---- round 1 ----
            case 5 when r.Scene == CoopRunner.WorkshopScene && s.GuestState == GuestState.InWorld:
                Plugin.Logger.LogInfo($"AUTOTEST: guest is in the host's world: save folder '{SaveLoadSystem.s_saveDir}', held changes {s.World?.Stats.Held}, markers so far [{string.Join(",", r.Coop.Markers.Applied)}], {Counts()}.");
                Census("workshop");
                // Walk away from the shared spawn point so each player can see the other.
                _center = LocalPlayer.Root.position + LocalPlayer.Root.forward * 3f;
                _walking = true;
                _deadline = now + 60;
                Next(now + 4);
                break;
            case 5 when now > _deadline:
                Fail($"did not enter the host's world (guest state {s.GuestState}, scene {r.Scene}, last failure '{r.Coop.LastFailure}').");
                break;
            case 6 when r.Coop.HasRemoteView:
                _walking = false;
                AimAtRemote(r);
                Next(now + 1);
                break;
            case 6 when now > _deadline:
                Fail("the host's capsule never appeared.");
                break;
            case 7:
                Plugin.Logger.LogInfo("AUTOTEST: " + r.Coop.RemoteViewReport);
                Shot(r, "workshop");
                _walking = true;
                for (var i = 0; i < MarkersPerSide; i++) Submit(s.World, $"join-{i}");
                s.World.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker("").ToBytes(), out _); // must be refused by the host
                _deadline = now + 60;
                Next(now + 1);
                break;
            case 8 when (r.Coop.Markers.Applied.Count >= ExpectedMarkers && r.Coop.CopiesReceived >= 1) || now > _deadline:
                Plugin.Logger.LogInfo($"AUTOTEST: copies of the host's world received: {r.Coop.CopiesReceived}; joined folder: {string.Join(", ", Directory.Exists(CoopSaves.JoinedDir) ? Directory.GetFiles(CoopSaves.JoinedDir).Select(Path.GetFileName) : Array.Empty<string>())}.");
                Summary(r, r.Coop.Markers.Applied.Count == ExpectedMarkers && s.World?.Divergence == null && _invalidRefused && r.Coop.CopiesReceived >= 1 && s.World.Stats.Held >= 1,
                    $", invalid marker refused: {_invalidRefused}, copies received: {r.Coop.CopiesReceived}, held during load: {s.World?.Stats.Held}");
                Census("shared");
                _walking = false;
                _deadline = now + 120;
                Next(now);
                break;
            case 9 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
                Next(now + 2);
                break;
            case 9 when now > _deadline:
                Fail("the guest did not return to the main menu after the host left.");
                break;
            case 10:
            {
                var restored = SaveLoadSystem.s_saveDir == IsolatedSaveDir;
                var shown = r.Menu?.Dialog?.IsOpen == true;
                Plugin.Logger.LogInfo($"AUTOTEST: guest back on the main menu; message shown={shown} ('{r.Coop.LastFailure}'); save folder '{SaveLoadSystem.s_saveDir}' restored={restored}.");
                Shot(r, "departure");
                Plugin.Logger.LogInfo($"AUTOTEST: departure {(restored && shown && !s.IsRunning ? "OK" : "FAILED")} (guest).");
                Next(now + 20); // the host continues its world meanwhile
                break;
            }

            // ---- round 2: rejoin the continued world, then leave first ----
            case 11:
                // Back on the co-op list with the departure message open: close it, then Join a Friend.
                if (r.Menu.Dialog.IsOpen && r.Menu.Dialog.Current != CoopDialog.Mode.Join) { r.Menu.Dialog.Close(); _at = now + 0.5f; break; }
                if (!r.Menu.Dialog.IsOpen) { Click(r.Menu.CoopReady ? r.Menu.JoinButton : r.Menu.Entry); _at = now + 1.5f; break; }
                Plugin.Logger.LogInfo("AUTOTEST: round 2 - guest rejoins.");
                Click(r.Menu.Dialog.ApplyButton);
                _deadline = now + 240;
                Next(now);
                break;
            case 12 when r.Scene == CoopRunner.WorkshopScene && s.GuestState == GuestState.InWorld:
                Plugin.Logger.LogInfo($"AUTOTEST: guest rejoined: generation {s.World?.Generation}, markers [{string.Join(",", r.Coop.Markers.Applied)}], {Counts()}.");
                Next(now + 3);
                break;
            case 12 when now > _deadline:
                Fail($"did not rejoin (guest state {s.GuestState}, last failure '{r.Coop.LastFailure}').");
                break;
            case 13:
                Plugin.Logger.LogInfo("AUTOTEST: guest leaving the world.");
                UnityEngine.Object.FindObjectOfType<NewLoadingScreen>().LoadScene(CoopRunner.MenuScene, false);
                _deadline = now + 60;
                Next(now);
                break;
            case 14 when r.Scene == CoopRunner.MenuScene && r.Menu != null:
            {
                var restored = SaveLoadSystem.s_saveDir == IsolatedSaveDir;
                Plugin.Logger.LogInfo($"AUTOTEST: round 2 {(restored && !s.IsRunning ? "OK" : "FAILED")} (guest): back on the menu, save folder '{SaveLoadSystem.s_saveDir}'.");
                Plugin.Logger.LogInfo("AUTOTEST: finished.");
                Next(float.MaxValue);
                break;
            }
        }
    }

    private static float RemoteDistance(CoopRunner r)
    {
        var cam = Camera.main;
        var pos = r.Coop.RemoteViewPosition;
        return cam == null || pos == null ? 0 : Vector3.Distance(cam.transform.position, pos.Value);
    }

    private void Summary(CoopRunner r, bool ok, string extra)
    {
        _passed = ok;
        Plugin.Logger.LogInfo($"AUTOTEST: world summary ({_role}) {Diagnostics.WorldSummary(r.Coop)}");
        Plugin.Logger.LogInfo($"AUTOTEST: {(ok ? "PASSED" : "FAILED")} - markers {r.Coop.Markers.Applied.Count}/{ExpectedMarkers}{extra}. Compare both world summaries.");
    }

    private static string Counts() =>
        $"cases {UnityEngine.Object.FindObjectsOfType<Case>().Length}, occupied bench slots {UnityEngine.Object.FindObjectsOfType<BenchSlot>().Count(b => b.GetComponentInChildren<Case>() != null)}";

    private static void Submit(WorldSync world, string text)
    {
        if (!world.Submit(CommandTypes.DiagnosticMarker, new DiagnosticMarker(text).ToBytes(), out var op))
            Plugin.Logger.LogError($"AUTOTEST: could not submit marker {text}.");
        else Plugin.Logger.LogInfo($"AUTOTEST: submitted marker {text} as {op}.");
    }

    /// <summary>Turn the local camera towards the other player's capsule so the screenshot shows it.</summary>
    private static void AimAtRemote(CoopRunner r)
    {
        var target = r.Coop.RemoteViewPosition;
        var root = LocalPlayer.Root;
        var cam = Camera.main;
        if (target == null || root == null || cam == null) return;
        var dir = target.Value + Vector3.up * 0.9f - cam.transform.position;
        var yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
        foreach (var look in root.GetComponentsInChildren<MouseLook>(true))
        {
            var parentYaw = look.transform.parent != null ? look.transform.parent.eulerAngles.y : 0;
            look.rotationX = look.transform == root ? yaw : yaw - parentYaw;
            look.rotationY = -pitch;
        }
        cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        Plugin.Logger.LogInfo($"AUTOTEST: aimed camera at the other player (yaw {yaw:F0}, pitch {pitch:F0}).");
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

    /// <summary>Point the game's save folder at a fresh disposable folder and confirm the game sees it.</summary>
    private bool IsolateSaves()
    {
        try
        {
            var dir = Phase3Probe.Enabled
                ? Path.Combine(_dir, Phase3Probe.Phase, Phase3Probe.RunId, $"saves-{_role}")
                : Path.Combine(_dir, $"saves-{_role}");
            // Audit runs use fresh, named folders and retain earlier evidence. A resume reopens only its own run's folder.
            if (ProbeResume)
            {
                if (_role != "host") throw new IOException("Only a probe host can resume a run.");
                if (!File.Exists(Path.Combine(dir, "Coop", "autofreebuild.binary.zip"))) throw new IOException("Resume needs this run's existing co-op save.");
            }
            else if (Phase3Probe.Enabled && Directory.Exists(dir)) throw new IOException("Use a fresh probe run ID; its saves already exist.");
            if (!Phase3Probe.Enabled && Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var original = SaveLoadSystem.s_saveDir;
            var target = dir.Replace('\\', '/');
            SaveLoadSystem.s_saveDir = target;
            if (SaveLoadSystem.s_saveDir != target) return false;
            IsolatedSaveDir = target;
            if (Phase3Probe.Enabled)
            {
                // PC import/export is a separate game folder; isolate it as well as world saves.
                SaveLoadSystem.s_pcSharingDefaultDir = target + "/PCs";
                Directory.CreateDirectory(SaveLoadSystem.s_pcSharingDefaultDir);
            }
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
        private static bool Prefix(string title, string body, Il2CppSystem.Action yesAction)
        {
            if (title == "Save Game Error") Plugin.Logger.LogError("AUTOTEST: native save error: " + body);
            // Accepting "New Game" overwrites the Free Build save in the current save folder. Only do that when the
            // folder is the disposable one and the game still points at it.
            if (IsolatedSaveDir == null || SaveLoadSystem.s_saveDir == null || !SaveLoadSystem.s_saveDir.StartsWith(IsolatedSaveDir, StringComparison.Ordinal))
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
