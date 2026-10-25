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

    private void Awake()
    {
        Instance = this;
        Coop = new CoopController();
        _autoTest = AutoTest.FromEnvironment();
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
            // Going back to the main menu ends the session.
            if (scene == MenuScene && Scene != "" && Scene != "Logos") Coop.Stop("Returned to the main menu.");
            Scene = scene;
            Menu = null;
        }

        if (scene == MenuScene && (Menu == null || !Menu.IsAlive)) Menu = MultiplayerMenu.TryBuild(Coop);
        Menu?.Update();

        Coop.Update(scene == WorkshopScene);
        UpdateStatus(scene == WorkshopScene);
        _autoTest?.Update(this);
    }

    private void OnApplicationQuit() => Coop?.Stop("Game closing.");

    /// <summary>One line at the top-left of the workshop showing the session state.</summary>
    private void UpdateStatus(bool inWorkshop)
    {
        var show = inWorkshop && Coop.Session.IsRunning;
        if (show && _status == null) _status = CreateStatus();
        if (_status == null) return;
        _status.gameObject.SetActive(show);
        if (!show) return;
        var s = Coop.Session;
        _status.text = s.Role == Role.Host
            ? (s.HasPeer ? $"Co-op host · port {s.Port} · Client connected ({s.PingMs} ms)" : $"Co-op host · port {s.Port} · waiting for Client")
            : (s.HasPeer ? $"Co-op client · connected to Host ({s.PingMs} ms)" : "Co-op client · disconnected");
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
        rt.sizeDelta = new Vector2(900, 40);
        text.color = Color.white;
        text.outlineWidth = 0.2f;
        text.outlineColor = Color.black;
        return text;
    }
}
