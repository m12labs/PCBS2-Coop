using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Main-thread driver for the mod. Lives on a DontDestroyOnLoad object.</summary>
public sealed class CoopRunner : MonoBehaviour
{
    public CoopRunner(IntPtr ptr) : base(ptr) { }

    public const string MenuScene = "Main_Menu";
    public const string WorkshopScene = "Stage_02_Workshop";

    internal static CoopRunner Instance { get; private set; }
    internal CoopController Coop { get; private set; }
    internal MultiplayerMenu Menu { get; private set; }
    internal string Scene { get; private set; } = "";

    private TextMeshProUGUI _status;
    private AutoTest _autoTest;
    private Probe _probe;
    private Phase3Probe _phase3Probe;
    private SteamInviteMenu _steamMenu;
    private SteamProbe _steamProbe;

    private void Awake()
    {
        Instance = this;
        Coop = new CoopController();
        _autoTest = AutoTest.FromEnvironment();
        if (Probe.Enabled) _probe = new Probe();
        if (Phase3Probe.Enabled) _phase3Probe = new Phase3Probe();
        if (SteamProbe.Enabled) _steamProbe = new SteamProbe();
    }

    private void Update()
    {
        try { Tick(); }
        catch (Exception e) { Plugin.Logger.LogError(e); }
    }

    private void Tick()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (scene != Scene)
        {
            Plugin.Logger.LogInfo($"Active scene: '{Scene}' -> '{scene}'");
            Scene = scene;
            Menu = null;
            // Back on the main menu: any session ends and the solo save folder is restored.
            if (scene == MenuScene) Coop.OnMainMenu();
        }

        if (scene == MenuScene && (Menu == null || !Menu.IsAlive)) Menu = MultiplayerMenu.TryBuild(Coop);
        SteamBootstrap.Service?.Poll();
        Menu?.Update();

        Coop.Update(scene == WorkshopScene);
        _steamMenu ??= new SteamInviteMenu(Coop);
        _steamMenu.Update(scene == WorkshopScene);
        UpdateStatus(scene == WorkshopScene);
        _autoTest?.Update(this);
        _probe?.Update(this);
        _phase3Probe?.Update(this);
        _steamProbe?.Update(this);
    }

    private void OnApplicationQuit()
    {
        try { Coop?.OnQuit(); }
        finally { SteamBootstrap.Service?.Dispose(); }
    }

    /// <summary>One line at the top-left of the workshop showing the session state.</summary>
    private void UpdateStatus(bool inWorkshop)
    {
        var show = (inWorkshop && Coop.Session.IsRunning) || Coop.ActiveNotice != null;
        if (show && _status == null) _status = CreateStatus();
        if (_status == null) return;
        _status.gameObject.SetActive(show);
        if (!show) return;
        _status.text = Coop.Session.IsRunning ? Coop.StatusLine + Coop.ProblemSummary : Coop.ActiveNotice;
    }

    private TextMeshProUGUI CreateStatus()
    {
        TMP_Text style = null;
        foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
            if (t.font != null) { style = t; break; }
        if (style == null) return null;

        var go = new GameObject("PCBS2Coop Status");
        go.transform.SetParent(transform, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        var text = Ui.NewText("Text", go.transform, style, "", 22, TextAlignmentOptions.TopLeft);
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(16, -12);
        rt.sizeDelta = new Vector2(1600, 40);
        text.color = Color.white;
        text.richText = false;
        text.outlineWidth = 0.2f;
        text.outlineColor = Color.black;
        return text;
    }
}
