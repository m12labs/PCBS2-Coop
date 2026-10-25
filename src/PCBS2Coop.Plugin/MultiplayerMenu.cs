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
        Ui.SetText(Ui.Child(p, "Text"), "Local co-op (proof of concept)");
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
        dialog.StartFreeBuild = result.StartNewFreeBuild;
        result.HostButton = host.gameObject;
        result.JoinButton = join.gameObject;
        LogWiring(p);
        Plugin.Logger.LogInfo("Multiplayer panel added to main menu.");
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

    /// <summary>Same as Free Build > New Game: the game's PlayFree, including its overwrite prompt.</summary>
    private void StartNewFreeBuild()
    {
        Plugin.Logger.LogInfo("Starting a new Free Build (NewMainMenu.PlayFree).");
        _menu.PlayFree();
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

/// <summary>Host / Join popup built from the game's "NewConfirm" message box.</summary>
internal sealed class CoopDialog
{
    public enum Mode { Host, Join }

    private readonly GameObject _root;
    private readonly TMP_Text _title;
    private readonly TMP_Text _status;
    private readonly TMP_InputField _ip;
    private readonly TMP_InputField _port;
    private readonly GameObject _ipRow;
    private readonly TMP_Text _applyText;
    private readonly Button _apply;
    private readonly CoopController _coop;
    private Mode _mode;
    private bool _waiting;

    public Action StartFreeBuild { get; set; } = () => { };
    public bool IsOpen => _root != null && _root.activeSelf;

    private CoopDialog(GameObject root, CoopController coop)
    {
        _root = root;
        _coop = coop;
        var box = Ui.Child(root.transform, "NewMessageBox/MessageBoxGroup");
        _title = Ui.Child(box, "Title").GetComponent<TMP_Text>();
        var body = Ui.Child(box, "Body").GetComponent<TMP_Text>();
        Ui.Child(box, "SubTitle").GetComponent<TMP_Text>().text = "";
        _status = body;
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
        _applyText = apply.GetComponentInChildren<TMP_Text>(true);
        _apply = Ui.MakeButton(apply.gameObject, OnApply);
        Ui.MakeButton(back.gameObject, OnBack);
        Ui.SetText(back, "Back");
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
        _coop.Stop("Opening the co-op dialog.");
        _title.text = mode == Mode.Host ? "Host Game" : "Join Game";
        _applyText.text = mode == Mode.Host ? "Host" : "Connect";
        _ipRow.SetActive(mode == Mode.Join);
        SetStatus(mode == Mode.Host
            ? "Players join with this PC's IP address and the port below."
            : "Enter the host's IP address and port.", false);
        _apply.interactable = true;
        _root.SetActive(true);
        _root.transform.SetAsLastSibling();
        EventSystem.current?.SetSelectedGameObject((mode == Mode.Join ? _ip : _port).gameObject);
    }

    public void Close()
    {
        _waiting = false;
        if (_root != null) _root.SetActive(false);
    }

    public GameObject ApplyButton => _apply.gameObject;

    public void Update()
    {
        if (!IsOpen || !_waiting) return;
        if (_coop.Session.HasPeer)
        {
            Close();
            StartFreeBuild();
        }
        else if (_coop.LastFailure != null)
        {
            _waiting = false;
            _apply.interactable = true;
            SetStatus("Could not connect: " + _coop.LastFailure, true);
        }
    }

    private void OnApply()
    {
        if (!int.TryParse(_port.text, out var port) || port < 1 || port > 65535)
        {
            SetStatus("Port must be a number from 1 to 65535.", true);
            return;
        }
        if (_mode == Mode.Host)
        {
            if (!_coop.Host(port, out var error)) { SetStatus(error, true); return; }
            Close();
            StartFreeBuild();
            return;
        }
        if (!_coop.Join(_ip.text, port, out var joinError)) { SetStatus(joinError, true); return; }
        _waiting = true;
        _apply.interactable = false;
        SetStatus($"Connecting to {_ip.text.Trim()}:{port}…", false);
    }

    private void OnBack()
    {
        _coop.Stop("Co-op dialog closed.");
        Close();
    }

    private void SetStatus(string text, bool error)
    {
        _status.text = text;
        _status.color = error ? new Color(1f, 0.45f, 0.4f) : Color.white;
    }
}
