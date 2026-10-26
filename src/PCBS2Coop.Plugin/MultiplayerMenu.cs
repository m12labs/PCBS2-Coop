using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// Adds a "Multiplayer" panel (cloned from Free Build) to the main menu, with Host and Join
/// sub-buttons that open a dialog cloned from the game's message box.
/// </summary>
internal sealed class MultiplayerMenu
{
    private readonly NewMainMenu _menu;
    private readonly CanvasGroup _sourceGroup;
    private readonly CanvasGroup _panelGroup;
    private readonly CoopDialog _dialog;

    public GameObject Panel { get; }
    public CoopDialog Dialog => _dialog;
    public GameObject HostButton { get; private set; }
    public GameObject JoinButton { get; private set; }
    public Action OpenHost { get; }
    public Action OpenJoin { get; }

    private MultiplayerMenu(NewMainMenu menu, Transform freeBuild, GameObject panel, CoopDialog dialog)
    {
        _menu = menu;
        Panel = panel;
        _dialog = dialog;
        _sourceGroup = freeBuild.GetComponent<CanvasGroup>();
        _panelGroup = panel.GetComponent<CanvasGroup>();
        OpenHost = () => _dialog.Open(CoopDialog.Mode.Host);
        OpenJoin = () => _dialog.Open(CoopDialog.Mode.Join);
    }

    /// <summary>Build the panel if the main menu is ready. Returns null if it isn't yet.</summary>
    public static MultiplayerMenu TryBuild(CoopController coop)
    {
        var menu = Object.FindObjectOfType<NewMainMenu>();
        if (menu == null) return null;
        var freeBuild = menu.transform.Find("MainMenu/Button2-FreeBuild");
        var template = menu.transform.parent?.Find("DLC Menu/NewConfirm");
        if (freeBuild == null || template == null) return null;

        var panel = Object.Instantiate(freeBuild.gameObject, freeBuild.parent);
        panel.name = "Button2b-Multiplayer";
        panel.transform.SetSiblingIndex(freeBuild.GetSiblingIndex() + 1);
        var p = panel.transform;
        Ui.SetText(Ui.Child(p, "Header"), "Multiplayer");
        Ui.SetText(Ui.Child(p, "Text"), "Steam or LAN co-op");
        Ui.DestroyChild(p, "Leaderboard");

        var continueSlot = Ui.Child(p, "ContinueFreebuild");
        var host = Ui.Child(p, "LoadGame");
        var join = Ui.Child(p, "NewGame");
        host.name = "Host";
        join.name = "Join";
        host.GetComponent<RectTransform>().anchoredPosition = continueSlot.GetComponent<RectTransform>().anchoredPosition;
        join.GetComponent<RectTransform>().anchoredPosition = freeBuild.Find("LoadGame").GetComponent<RectTransform>().anchoredPosition;
        Object.Destroy(continueSlot.gameObject);
        host.gameObject.SetActive(true);
        join.gameObject.SetActive(true);
        Ui.SetText(Ui.Child(host, "Text (TMP)"), "Host");
        Ui.SetText(Ui.Child(join, "Text (TMP)"), "Join");

        var dialog = CoopDialog.Build(menu.transform, template, coop);
        var result = new MultiplayerMenu(menu, freeBuild, panel, dialog);
        Ui.HookMenuButton(host.gameObject, result.OpenHost);
        Ui.HookMenuButton(join.gameObject, result.OpenJoin);
        result.HostButton = host.gameObject;
        result.JoinButton = join.gameObject;
        LogWiring(p);
        Plugin.Logger.LogInfo("Multiplayer panel added to main menu.");
        var message = coop.TakeMenuMessage();
        if (message != null) dialog.OpenMessage(message);
        return result;
    }

    public bool IsAlive => Panel != null;

    /// <summary>Keep the cloned panel's fade in step with the Free Build panel it was copied from.</summary>
    public void Update()
    {
        if (_sourceGroup != null && _panelGroup != null)
        {
            // The menu activates its panels one by one while fading in; follow Free Build.
            var active = _sourceGroup.gameObject.activeSelf;
            if (Panel.activeSelf != active) Panel.SetActive(active);
            _panelGroup.alpha = _sourceGroup.alpha;
            _panelGroup.interactable = _sourceGroup.interactable;
            _panelGroup.blocksRaycasts = _sourceGroup.blocksRaycasts;
        }
        _dialog.Update();
    }

    /// <summary>Log how the cloned panel's click handling is wired, to confirm no game action is left on it.</summary>
    private static void LogWiring(Transform panel)
    {
        foreach (var b in panel.GetComponentsInChildren<IndividualMenuButton>(true))
            Plugin.Logger.LogInfo($"[wiring] IMB {b.name}: workingButton={PathOf(b.workingButton)} children={b.childButtonsAsButton?.Count ?? 0} method='{b.methodToLoad}'");
        foreach (var b in panel.GetComponentsInChildren<Button>(true)) LogButton("Button in panel", b);
        foreach (var b in panel.GetComponentsInChildren<IndividualMenuButton>(true))
            if (b.workingButton != null) LogButton($"workingButton of {b.name}", b.workingButton);
        foreach (var t in panel.GetComponentsInChildren<EventTrigger>(true))
            Plugin.Logger.LogInfo($"[wiring] EventTrigger {t.name}: {t.triggers.Count} entries");
    }

    private static void LogButton(string what, Button b)
    {
        var n = b.onClick.GetPersistentEventCount();
        Plugin.Logger.LogInfo($"[wiring] {what} {PathOf(b)}: {n} persistent");
        for (var i = 0; i < n; i++)
            Plugin.Logger.LogInfo($"[wiring]   -> {b.onClick.GetPersistentTarget(i)?.name}.{b.onClick.GetPersistentMethodName(i)}");
    }

    private static string PathOf(Component c)
    {
        if (c == null) return "null";
        var s = c.name;
        for (var t = c.transform.parent; t != null; t = t.parent) s = t.name + "/" + s;
        return s;
    }
}

/// <summary>
/// Host / Join popup built from the game's "NewConfirm" message box.
/// Host: "New world" and "Continue" (the one co-op Free Build world). Join: connect, then wait for the host's world.
/// </summary>
internal sealed class CoopDialog
{
    public enum Mode { Host, Join }

    private readonly GameObject _root;
    private readonly TMP_Text _title;
    private readonly TMP_Text _status;
    private readonly TMP_InputField _ip;
    private readonly TMP_InputField _port;
    private readonly GameObject _ipRow;
    private readonly GameObject _portRow;
    private readonly Button _steamButton, _lanButton;
    private readonly TMP_Text _applyText;
    private readonly Button _apply;
    private readonly Button _continue;
    private readonly CoopController _coop;
    private Mode _mode;
    private bool _waiting;
    private bool _confirmNew;
    private bool _steam;
    private readonly Color _normalColor;

    public bool IsOpen => _root != null && _root.activeSelf;
    public GameObject ContinueButton => _continue.gameObject;

    private CoopDialog(GameObject root, CoopController coop)
    {
        _root = root;
        _coop = coop;
        var box = Ui.Child(root.transform, "NewMessageBox/MessageBoxGroup");
        _title = Ui.Child(box, "Title").GetComponent<TMP_Text>();
        var body = Ui.Child(box, "Body").GetComponent<TMP_Text>();
        Ui.Child(box, "SubTitle").GetComponent<TMP_Text>().text = "";
        _status = body;
        _normalColor = body.color;
        _status.text = "";

        var fieldStyle = body;
        var labelSize = body.fontSize;
        // Rows sit between the title and the buttons.
        var ipRow = Ui.NewRect("IpRow", box);
        Ui.Place(ipRow, new Vector2(-260, 10), new Vector2(900, 90));
        var ipLabel = Ui.NewText("Label", ipRow, fieldStyle, "IP address", labelSize, TextAlignmentOptions.MidlineRight);
        Ui.Place(ipLabel.rectTransform, new Vector2(-330, 0), new Vector2(300, 90));
        _ip = Ui.NewInput("IpField", ipRow, fieldStyle, "127.0.0.1", labelSize);
        Ui.Place(_ip.GetComponent<RectTransform>(), new Vector2(170, 0), new Vector2(620, 90));
        _ip.characterLimit = 15;
        _ip.text = "127.0.0.1";
        _ipRow = ipRow.gameObject;

        var portRow = Ui.NewRect("PortRow", box);
        _portRow = portRow.gameObject;
        Ui.Place(portRow, new Vector2(590, 10), new Vector2(560, 90));
        var portLabel = Ui.NewText("Label", portRow, fieldStyle, "Port", labelSize, TextAlignmentOptions.MidlineRight);
        Ui.Place(portLabel.rectTransform, new Vector2(-170, 0), new Vector2(200, 90));
        _port = Ui.NewInput("PortField", portRow, fieldStyle, Protocol.DefaultPort.ToString(), labelSize);
        Ui.Place(_port.GetComponent<RectTransform>(), new Vector2(110, 0), new Vector2(300, 90));
        _port.contentType = TMP_InputField.ContentType.IntegerNumber;
        _port.characterLimit = 5;
        _port.text = Protocol.DefaultPort.ToString();

        var apply = Ui.Child(box, "ApplyButton");
        var back = Ui.Child(box, "BackButton");
        // Continue sits on the far side of Apply from Back, on the same row.
        var cont = Object.Instantiate(apply.gameObject, apply.parent);
        cont.name = "ContinueButton";
        var applyRt = apply.GetComponent<RectTransform>();
        var backRt = back.GetComponent<RectTransform>();
        cont.GetComponent<RectTransform>().anchoredPosition = applyRt.anchoredPosition + (applyRt.anchoredPosition - backRt.anchoredPosition);
        _applyText = apply.GetComponentInChildren<TMP_Text>(true);
        _apply = Ui.MakeButton(apply.gameObject, OnApply);
        _continue = Ui.MakeButton(cont, OnContinue);
        cont.GetComponentInChildren<TMP_Text>(true).text = "Continue";
        Ui.MakeButton(back.gameObject, OnBack);
        Ui.SetText(back, "Back");
        _steamButton = MethodButton("Steam", new Vector2(-260, 130), true);
        _lanButton = MethodButton("LAN", new Vector2(260, 130), false);

        Button MethodButton(string name, Vector2 position, bool steam)
        {
            var go = Object.Instantiate(apply.gameObject, apply.parent);
            go.name = name + "Method";
            Ui.Place(go.GetComponent<RectTransform>(), position, new Vector2(420, 80));
            return Ui.MakeButton(go, () =>
            {
                if (_waiting) return;
                _steam = steam; SteamBootstrap.PreferSteam = steam; _confirmNew = false;
                UpdateMethod();
            });
        }
    }

    public static CoopDialog Build(Transform canvas, Transform template, CoopController coop)
    {
        var root = Object.Instantiate(template.gameObject, canvas);
        root.name = "CoopDialog";
        root.SetActive(false);
        // The template belongs to the DLC menu; drop its focus/sound handlers so it can't push that UI state.
        foreach (var c in root.GetComponents<MonoBehaviour>())
            if (c.GetIl2CppType().Name is "DirectUI" or "UISounds") Object.DestroyImmediate(c);
        var rt = root.GetComponent<RectTransform>();
        Ui.Stretch(rt, 0, 0);
        root.transform.SetAsLastSibling();
        return new CoopDialog(root, coop);
    }

    public void Open(Mode mode)
    {
        _mode = mode;
        _waiting = false;
        _confirmNew = false;
        _steam = SteamBootstrap.PreferSteam;
        _coop.Stop(null, "Opening the co-op dialog.");
        _title.text = mode == Mode.Host ? "Host Game" : "Join Game";
        _applyText.text = mode == Mode.Host ? "New world" : "Connect";
        var hasWorld = mode == Mode.Host && CoopSaves.HasCoopWorld();
        _continue.gameObject.SetActive(mode == Mode.Host);
        _continue.interactable = hasWorld;
        UpdateMethod();
        _root.SetActive(true);
        _root.transform.SetAsLastSibling();
        EventSystem.current?.SetSelectedGameObject(_steam ? _steamButton.gameObject : (mode == Mode.Join ? _ip : _port).gameObject);
    }

    private void UpdateMethod()
    {
        _ipRow.SetActive(!_steam && _mode == Mode.Join);
        _portRow.SetActive(!_steam);
        _steamButton.GetComponentInChildren<TMP_Text>(true).text = _steam ? "Steam (selected)" : "Steam";
        _lanButton.GetComponentInChildren<TMP_Text>(true).text = !_steam ? "LAN (selected)" : "LAN";
        _steamButton.interactable = !_waiting;
        _lanButton.interactable = !_waiting;
        var available = !_steam || SteamBootstrap.Service?.Available == true;
        _apply.interactable = available && !_waiting && (_mode == Mode.Host || !_steam);
        _continue.interactable = available && !_waiting && CoopSaves.HasCoopWorld();
        _applyText.text = _mode == Mode.Host ? "New world" : _steam ? "Accept an invite" : "Connect";
        var text = _steam
            ? _mode == Mode.Host ? "Host through Steam. Invite a friend from the in-game pause menu once the world is ready."
                : "Open PCBS2 on both PCs with Steam running, then accept the host's Steam invite. Keep this game open."
            : _mode == Mode.Host ? "Players join with this PC's IP address and the port below." : "Enter the host's IP address and port.";
        if (!available) text = SteamBootstrap.Service?.UnavailableReason ?? SteamBootstrap.Reason;
        SetStatus(text, !available);
    }

    public void OpenSteamWaiting()
    {
        Open(Mode.Join); _steam = true; _waiting = true; UpdateMethod();
        SetStatus("Joining the Steam lobby…", false);
    }

    public void Close()
    {
        _waiting = false;
        if (_root != null) _root.SetActive(false);
    }

    public GameObject ApplyButton => _apply.gameObject;

    /// <summary>Show a message on the main menu (for example why the guest left the host's world).</summary>
    public void OpenMessage(string text)
    {
        Open(Mode.Join);
        SetStatus(text, true);
    }

    public void Update()
    {
        if (!IsOpen || !_waiting) return;
        var s = _coop.Session;
        if (_mode == Mode.Host && s.IsRunning) { Close(); return; }
        if (_coop.SteamStarting) return;
        if (_coop.GuestLoadStarted)
        {
            Close();
            return;
        }
        if (s.IsJoined)
        {
            var p = s.SnapshotProgress;
            SetStatus(p >= 0 ? $"Receiving the host's world… {p * 100:F0}%" : "Connected. Waiting for the host's world…", false);
        }
        else if (_coop.LastFailure != null)
        {
            _waiting = false;
            UpdateMethod();
            SetStatus("Could not join: " + _coop.LastFailure, true);
        }
    }

    private void OnApply()
    {
        var port = Protocol.DefaultPort;
        if (!_steam && (!int.TryParse(_port.text, out port) || port < 1 || port > 65535))
        {
            SetStatus("Port must be a number from 1 to 65535.", true);
            return;
        }
        if (_mode == Mode.Host)
        {
            if (CoopSaves.HasCoopWorld() && !_confirmNew)
            {
                _confirmNew = true;
                SetStatus("This replaces your co-op world with a new one. Press New world again to confirm, or Continue to keep it.", true);
                return;
            }
            StartHost(port, continueWorld: false);
            return;
        }
        if (_steam) return;
        if (!_coop.Join(_ip.text, port, out var joinError)) { SetStatus(joinError, true); return; }
        _waiting = true;
        _apply.interactable = false;
        SetStatus($"Connecting to {_ip.text.Trim()}:{port}…", false);
    }

    private void OnContinue()
    {
        var port = Protocol.DefaultPort;
        if (!_steam && (!int.TryParse(_port.text, out port) || port < 1 || port > 65535))
        {
            SetStatus("Port must be a number from 1 to 65535.", true);
            return;
        }
        StartHost(port, continueWorld: true);
    }

    private void StartHost(int port, bool continueWorld)
    {
        if (_steam)
        {
            if (!_coop.HostSteam(continueWorld, out var why)) { SetStatus(why, true); return; }
            _waiting = true; UpdateMethod();
            SetStatus("Creating a private Steam lobby…", false);
            return;
        }
        if (!_coop.HostWorld(port, continueWorld, out var error)) { SetStatus(error, true); return; }
        Close();
    }

    private void OnBack()
    {
        _coop.Stop(null, "Co-op dialog closed.");
        Close();
    }

    private void SetStatus(string text, bool error)
    {
        _status.text = text;
        // The message box is white; keep the game's own body text colour for normal text.
        _status.color = error ? new Color(0.85f, 0.2f, 0.15f) : _normalColor;
    }
}
