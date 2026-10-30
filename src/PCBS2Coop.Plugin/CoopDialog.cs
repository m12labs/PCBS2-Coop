using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// The co-op side panel: a dark card that slides out to the right of the co-op list, its top level with the entry that
/// opened it, using the menu's fonts and copies of the game's white Free Build buttons. Modes: New Game (world name),
/// Load Game (the co-op worlds, newest first, with delete), Join a Friend (IP and port), Co-op Options (Steam or LAN,
/// LAN port, autosave interval), messages, and waiting for a Steam lobby or a connection. Join and Options have no title (the entry next
/// to the card already names it). The list stays clickable (another entry switches the card), except while waiting.
/// </summary>
internal sealed class CoopDialog
{
    public enum Mode { NewWorld, Load, Join, Options, Message, Waiting }

    private const float Width = 1034, Pad = 50, RowHeight = 84, LabelWidth = 280, Gap = 26, TitleHeight = 90;
    private const float ButtonWidth = 292, ButtonHeight = 75, ListGap = 12, VisibleWorlds = 6;
    private const float SideGap = 60, SlideTime = 0.22f, SlideDistance = 60;
    private static readonly Color BodyColor = new(0.09f, 0.09f, 0.1f, 0.95f);
    private static readonly Color FieldColor = new(0.2f, 0.2f, 0.23f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.47f, 0.42f, 1f);
    private static readonly Color DateColor = new(0.35f, 0.35f, 0.4f, 1f);
    private static readonly Color Accent = new(0.23f, 0.38f, 1f, 1f);

    private readonly CoopController _coop;
    private readonly Transform _buttonTemplate;
    private readonly RectTransform _card, _list, _defaultAnchor;
    private readonly CanvasGroup _group;
    private readonly TMP_Text _title, _status;
    private readonly Color _textColor;
    private readonly float _textSize;
    private readonly RectTransform _nameRow, _ipRow, _portRow, _methodRow, _autosaveRow, _worldView, _worldContent;
    private readonly TMP_InputField _name, _ip, _port;
    private readonly GameObject _apply, _back;
    private readonly Image _steamMark, _lanMark;
    private readonly Image[] _autosaveMarks;
    private int _autosave;
    private RectTransform _anchor;
    private Mode _mode;
    private bool _hostWaiting, _steam;
    private int _worldCount;
    private string _pendingDelete;
    private float _shownAt;
    private Vector3 _target;

    public bool IsOpen => _card != null && _card.gameObject.activeSelf;
    public Mode Current => _mode;
    /// <summary>Waiting for a lobby or a connection: the list behind is not clickable.</summary>
    public bool Busy => IsOpen && _mode == Mode.Waiting;
    public GameObject ApplyButton => _apply;
    /// <summary>A world was deleted (the menu updates Continue / Load Game).</summary>
    public Action WorldsChanged { get; set; }

    /// <param name="entry">The menu's Options entry (fonts).</param>
    /// <param name="button">A Free Build sub-button (the white buttons).</param>
    /// <param name="list">The co-op list (the card takes its scale and sits to its right).</param>
    /// <param name="defaultAnchor">Entry the card lines up with when no entry opened it (messages).</param>
    public static CoopDialog Build(Transform canvas, Transform entry, Transform button, CoopController coop, RectTransform list, RectTransform defaultAnchor)
        => new(canvas, entry, button, coop, list, defaultAnchor);

    private CoopDialog(Transform canvas, Transform entry, Transform button, CoopController coop, RectTransform list, RectTransform defaultAnchor)
    {
        _coop = coop;
        _buttonTemplate = button;
        _list = list;
        _defaultAnchor = defaultAnchor;
        var titleStyle = Ui.Child(entry, "HeaderText").GetComponent<TMP_Text>();
        var style = Ui.Child(entry, "Subtext").GetComponent<TMP_Text>();
        _textColor = style.color;
        _textSize = style.fontSize;

        _card = Ui.NewRect("CoopSidePanel", canvas);
        _card.anchorMin = _card.anchorMax = new Vector2(0.5f, 0.5f);
        _card.pivot = new Vector2(0, 1);
        _card.gameObject.AddComponent<Image>().color = BodyColor;
        _group = _card.gameObject.AddComponent<CanvasGroup>();

        _title = Ui.NewText("Title", _card, titleStyle, "", titleStyle.fontSize, TextAlignmentOptions.MidlineLeft);

        _nameRow = Row("Name", "World name", Width - 2 * Pad - LabelWidth, out _name);
        _name.characterLimit = CoopWorldNames.MaxNameLength;
        _ipRow = Row("Ip", "IP address", Width - 2 * Pad - LabelWidth, out _ip);
        _ip.characterLimit = 64;
        _portRow = Row("Port", "Port", 240, out _port);
        _port.contentType = TMP_InputField.ContentType.IntegerNumber;
        _port.characterLimit = 5;

        _methodRow = Ui.NewRect("MethodRow", _card);
        var steam = Button(_methodRow, "Steam", () => { _steam = true; ShowOptions(); });
        var lan = Button(_methodRow, "LAN", () => { _steam = false; ShowOptions(); });
        TopLeft(steam.GetComponent<RectTransform>(), 0, 0, ButtonWidth, ButtonHeight);
        TopLeft(lan.GetComponent<RectTransform>(), ButtonWidth + Gap, 0, ButtonWidth, ButtonHeight);
        _steamMark = Mark(steam);
        _lanMark = Mark(lan);

        // Autosave interval (minutes; 0 = off): a label and one button per choice.
        _autosaveRow = Ui.NewRect("AutosaveRow", _card);
        TopLeft(Ui.NewText("Label", _autosaveRow, style, "Autosave", _textSize, TextAlignmentOptions.MidlineLeft).rectTransform, 0, 0, LabelWidth, ButtonHeight);
        var choices = CoopSettings.AutosaveChoices;
        var choiceWidth = (Width - 2 * Pad - LabelWidth - (choices.Length - 1) * ListGap) / choices.Length;
        _autosaveMarks = new Image[choices.Length];
        for (var i = 0; i < choices.Length; i++)
        {
            var minutes = choices[i];
            var choice = Button(_autosaveRow, minutes == 0 ? "Off" : $"{minutes} min", () => { _autosave = minutes; ShowOptions(); });
            TopLeft(choice.GetComponent<RectTransform>(), LabelWidth + i * (choiceWidth + ListGap), 0, choiceWidth, ButtonHeight);
            _autosaveMarks[i] = Mark(choice);
        }

        // Load Game: a scrolling list of worlds.
        _worldView = Ui.NewRect("Worlds", _card);
        _worldView.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0); // catches the mouse wheel between rows
        _worldView.gameObject.AddComponent<RectMask2D>();
        _worldContent = Ui.NewRect("Content", _worldView);
        _worldContent.anchorMin = new Vector2(0, 1);
        _worldContent.anchorMax = new Vector2(1, 1);
        _worldContent.pivot = new Vector2(0.5f, 1);
        var scroll = _worldView.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = _worldView;
        scroll.content = _worldContent;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40;

        _status = Ui.NewText("Status", _card, style, "", _textSize, TextAlignmentOptions.TopLeft);
        _status.enableWordWrapping = true;
        _apply = Button(_card, "Apply", OnApply);
        _back = Button(_card, "Back", Cancel);
        _card.gameObject.SetActive(false);

        RectTransform Row(string name, string label, float fieldWidth, out TMP_InputField field)
        {
            var row = Ui.NewRect(name + "Row", _card);
            TopLeft(Ui.NewText("Label", row, style, label, _textSize, TextAlignmentOptions.MidlineLeft).rectTransform, 0, 0, LabelWidth, RowHeight);
            field = Ui.NewInput(name + "Field", row, style, "", _textSize);
            field.targetGraphic.color = FieldColor;
            TopLeft(field.GetComponent<RectTransform>(), LabelWidth, 0, fieldWidth, RowHeight);
            return row;
        }
    }

    // ---------------- opening ----------------

    /// <summary>Open the card in <paramref name="mode"/>, level with <paramref name="anchor"/> (an entry of the list).</summary>
    public void Open(Mode mode, RectTransform anchor = null)
    {
        if (mode is Mode.NewWorld or Mode.Join or Mode.Load) _coop.Stop(null, "Opening a co-op panel.");
        if (!IsOpen || (anchor != null && anchor != _anchor) || mode != _mode) _shownAt = Time.realtimeSinceStartup;
        if (anchor != null) _anchor = anchor;
        _mode = mode;
        _hostWaiting = false;
        _pendingDelete = null;
        GameObject focus = null;
        _title.text = "";
        switch (mode)
        {
            case Mode.NewWorld:
                _title.text = "New Co-op World";
                Ui.SetText(_apply.transform, "Start");
                _name.text = CoopWorldNames.NextDefaultName(CoopSaves.WorldStems());
                SetStatus(HostInfo(), !HostAvailable(), layout: false);
                focus = _name.gameObject;
                break;
            case Mode.Load:
                _title.text = "Load a Co-op World";
                FillWorlds();
                SetStatus(_worldCount > 0 ? "Click a world to host it. Newest first." : "No co-op worlds yet. Start one with New Game.", false, layout: false);
                break;
            case Mode.Join:
                Ui.SetText(_apply.transform, "Connect");
                _ip.text = CoopSettings.LastIp;
                _port.text = CoopSettings.LastJoinPort.ToString();
                SetStatus("To join through Steam, accept the host's invite in Steam instead. Keep this game on the main menu.", false, layout: false);
                focus = _ip.gameObject;
                break;
            case Mode.Options:
                Ui.SetText(_apply.transform, "Save");
                _steam = SteamBootstrap.WantsSteam;
                _port.text = CoopSettings.Port.ToString();
                _autosave = CoopSettings.AutosaveMinutes;
                break;
            case Mode.Message:
                _title.text = "Co-op";
                break;
        }
        Ui.SetText(_back.transform, mode switch { Mode.Message => "OK", Mode.Waiting => "Cancel", _ => "Back" });
        _card.gameObject.SetActive(true);
        _card.SetAsLastSibling();
        if (mode == Mode.Options) ShowOptions(); else Layout();
        EventSystem.current?.SetSelectedGameObject(focus);
    }

    /// <summary>Show a message (for example why the last session ended).</summary>
    public void OpenMessage(string text, string title = "Co-op")
    {
        Open(Mode.Message);
        _title.text = title;
        SetStatus(text, true);
    }

    public void OpenSteamJoining() => OpenWaiting("Joining a Friend", "Joining the Steam lobby…", host: false);

    private void OpenWaiting(string title, string text, bool host)
    {
        Open(Mode.Waiting);
        _hostWaiting = host;
        _title.text = title;
        SetStatus(text, false);
    }

    public void Close()
    {
        _hostWaiting = false;
        if (_card != null) _card.gameObject.SetActive(false);
    }

    /// <summary>Back / OK / Cancel (and Esc): a pending connection or Steam lobby is dropped.</summary>
    public void Cancel()
    {
        if (_mode == Mode.Waiting) _coop.Stop(null, "Co-op panel closed.");
        Close();
    }

    private void ShowOptions()
    {
        var available = SteamBootstrap.Service?.Available == true;
        _steamMark.enabled = _steam;
        _lanMark.enabled = !_steam;
        for (var i = 0; i < _autosaveMarks.Length; i++) _autosaveMarks[i].enabled = CoopSettings.AutosaveChoices[i] == _autosave;
        if (_steam && !available) SetStatus("Steam isn't available: " + (SteamBootstrap.Service?.UnavailableReason ?? SteamBootstrap.Reason), true);
        else SetStatus((_steam
            ? "Host through Steam: invite a friend from the pause menu once your world is ready. Friends join by accepting the invite."
            : "Host on your network (or a VPN): friends join with this PC's IP address and this port.") +
            (_autosave > 0 ? $" Your hosted world saves every {_autosave} minutes, when you exit, and from the Save screen."
                : " Autosave is off: your hosted world saves when you exit and from the Save screen."), false);
    }

    // ---------------- Load Game ----------------

    /// <summary>One row per co-op world save (name and time; click to host it) with a delete button.</summary>
    private void FillWorlds()
    {
        for (var i = _worldContent.childCount - 1; i >= 0; i--) Object.Destroy(_worldContent.GetChild(i).gameObject);
        var files = CoopSaves.WorldSaves();
        _worldCount = files.Count;
        var inner = Width - 2 * Pad;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var saveName = file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? file.Name[..^4] : file.Name;
            var y = i * (ButtonHeight + ListGap);
            var row = Button(_worldContent, CoopSaves.DisplayName(file.Name), () => StartHost(HostStart.Load(saveName)));
            TopLeft(row.GetComponent<RectTransform>(), 0, y, inner - ButtonHeight - ListGap, ButtonHeight);
            var label = Ui.Child(row.transform, "Text (TMP)").GetComponent<TMP_Text>();
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.margin = new Vector4(24, 0, 300, 0);
            var date = Ui.NewText("Date", row.transform, label, file.LastWriteTime.ToString("HH:mm  d MMM yyyy"), label.fontSize * 0.85f, TextAlignmentOptions.MidlineRight);
            Ui.Stretch(date.rectTransform, 24, 0);
            date.color = DateColor;
            var delete = Button(_worldContent, "X", () => AskDelete(file));
            TopLeft(delete.GetComponent<RectTransform>(), inner - ButtonHeight, y, ButtonHeight, ButtonHeight);
        }
        _worldContent.sizeDelta = new Vector2(0, Math.Max(0, files.Count * (ButtonHeight + ListGap) - ListGap));
        _worldContent.anchoredPosition = Vector2.zero;
    }

    /// <summary>First click asks, a second click on the same X deletes the save and its pair.</summary>
    private void AskDelete(FileInfo file)
    {
        var name = CoopSaves.DisplayName(file.Name);
        if (_pendingDelete != file.FullName)
        {
            _pendingDelete = file.FullName;
            SetStatus($"Delete \"{name}\"? Click its X again to delete it.", true);
            return;
        }
        try { CoopSaves.DeleteSave(file); }
        catch (Exception e) { SetStatus("Could not delete it: " + e.Message, true); return; }
        WorldsChanged?.Invoke();
        if (CoopSaves.WorldSaves().Count == 0) { Close(); return; }
        Open(Mode.Load);
        SetStatus($"Deleted \"{name}\".", false);
    }

    // ---------------- layout ----------------

    /// <summary>Stack what the mode uses (title, rows, worlds, text, buttons) and size the card to fit.</summary>
    private void Layout()
    {
        foreach (var r in new[] { _nameRow, _ipRow, _portRow, _methodRow, _autosaveRow, _worldView }) r.gameObject.SetActive(false);
        var inner = Width - 2 * Pad;
        var y = Pad;
        void Put(RectTransform r, float h) { r.gameObject.SetActive(true); TopLeft(r, Pad, y, inner, h); y += h + Gap; }

        var titled = _title.text.Length > 0;
        _title.gameObject.SetActive(titled);
        if (titled) { TopLeft(_title.rectTransform, Pad, y - 14, inner, TitleHeight); y += TitleHeight - 14 + Gap / 2; }
        switch (_mode)
        {
            case Mode.NewWorld: Put(_nameRow, RowHeight); break;
            case Mode.Join: Put(_ipRow, RowHeight); Put(_portRow, RowHeight); break;
            case Mode.Options:
                Put(_methodRow, ButtonHeight + 14);
                if (!_steam) Put(_portRow, RowHeight);
                Put(_autosaveRow, ButtonHeight + 14);
                break;
            case Mode.Load when _worldCount > 0:
                Put(_worldView, Math.Min(_worldCount, VisibleWorlds) * (ButtonHeight + ListGap) - ListGap);
                break;
        }
        var textHeight = Mathf.Max(_textSize * 1.3f, _status.GetPreferredValues(_status.text, inner, 0).y);
        TopLeft(_status.rectTransform, Pad, y, inner, textHeight);
        y += textHeight + Gap * 1.5f;

        var hasApply = _mode is Mode.NewWorld or Mode.Join or Mode.Options;
        _apply.SetActive(hasApply);
        var backX = Width - Pad - ButtonWidth;
        TopLeft(_back.GetComponent<RectTransform>(), backX, y, ButtonWidth, ButtonHeight);
        if (hasApply) TopLeft(_apply.GetComponent<RectTransform>(), backX - Gap - ButtonWidth, y, ButtonWidth, ButtonHeight);
        y += ButtonHeight + Pad;

        _card.sizeDelta = new Vector2(Width, y);
        Place();
    }

    /// <summary>To the right of the list, top level with the anchor entry, kept on screen.</summary>
    private void Place()
    {
        var parent = _card.parent.GetComponent<RectTransform>();
        var scale = _list.localScale.x;
        _card.localScale = new Vector3(scale, scale, 1);
        var a = _anchor != null && _anchor.gameObject.activeInHierarchy ? _anchor : _defaultAnchor;
        var topRight = parent.InverseTransformPoint(a.TransformPoint(new Vector3(a.rect.xMax, a.rect.yMax, 0)));
        var y = topRight.y;
        var height = _card.sizeDelta.y * scale;
        var bottom = parent.rect.yMin + 24;
        if (y - height < bottom) y = bottom + height;
        _target = new Vector3(topRight.x + SideGap * scale, y, 0);
    }

    public void Update()
    {
        if (!IsOpen) return;
        var t = Mathf.Clamp01((Time.realtimeSinceStartup - _shownAt) / SlideTime);
        var ease = 1 - (1 - t) * (1 - t) * (1 - t);
        _card.localPosition = _target + new Vector3((1 - ease) * SlideDistance * _card.localScale.x, 0, 0);
        _group.alpha = ease;
        if (_mode != Mode.Waiting) return;

        var s = _coop.Session;
        if (_hostWaiting)
        {
            if (s.IsRunning) { Close(); return; }
            if (!_coop.SteamStarting && _coop.LastFailure != null) OpenMessage("Could not host: " + _coop.LastFailure);
            return;
        }
        if (_coop.SteamStarting) return;
        if (_coop.GuestLoadStarted) { Close(); return; }
        if (s.IsJoined)
        {
            var p = s.SnapshotProgress;
            var text = p >= 0 ? $"Receiving the host's world… {p * 100:F0}%" : "Connected. Waiting for the host's world…";
            if (_status.text != text) SetStatus(text, false);
        }
        else if (_coop.LastFailure != null) OpenMessage("Could not join: " + _coop.LastFailure);
    }

    // ---------------- actions ----------------

    /// <summary>Host the world <paramref name="start"/> names with the method chosen in Co-op Options.</summary>
    public void StartHost(HostStart start)
    {
        if (!HostAvailable()) { OpenMessage(HostInfo()); return; }
        if (SteamBootstrap.PreferSteam)
        {
            if (!_coop.HostSteam(start, out var why)) { OpenMessage(why); return; }
            OpenWaiting("Hosting", "Creating a private Steam lobby…", host: true);
            return;
        }
        if (!_coop.HostWorld(CoopSettings.Port, start, out var error)) { OpenMessage(error); return; }
        Close();
    }

    private void OnApply()
    {
        switch (_mode)
        {
            case Mode.NewWorld:
            {
                var stem = CoopWorldNames.StemFromName(_name.text, out var error);
                if (stem == null) { SetStatus(error, true); return; }
                if (CoopSaves.WorldExists(stem)) { SetStatus($"There is already a world called \"{CoopWorldNames.WorldName(stem)}\".", true); return; }
                StartHost(HostStart.New(stem));
                return;
            }
            case Mode.Join:
            {
                if (!ReadPort(out var port)) return;
                var ip = _ip.text.Trim();
                if (!_coop.Join(ip, port, out var error)) { SetStatus(error, true); return; }
                CoopSettings.LastIp = ip;
                CoopSettings.LastJoinPort = port;
                OpenWaiting("Joining a Friend", $"Connecting to {ip}:{port}…", host: false);
                return;
            }
            case Mode.Options:
            {
                var port = CoopSettings.Port;
                if (!_steam && !ReadPort(out port)) return;
                SteamBootstrap.PreferSteam = _steam;
                CoopSettings.Port = port;
                CoopSettings.AutosaveMinutes = _autosave;
                Close();
                return;
            }
        }
    }

    private bool ReadPort(out int port)
    {
        if (int.TryParse(_port.text, out port) && port is >= 1 and <= 65535) return true;
        SetStatus("Port must be a number from 1 to 65535.", true);
        return false;
    }

    private static bool HostAvailable() => !SteamBootstrap.WantsSteam || SteamBootstrap.Service?.Available == true;

    private static string HostInfo()
    {
        if (!HostAvailable()) return "Steam isn't available: " + (SteamBootstrap.Service?.UnavailableReason ?? SteamBootstrap.Reason) + " Choose LAN in Co-op Options.";
        return SteamBootstrap.PreferSteam
            ? "Hosted through Steam. Invite a friend from the pause menu once the world is ready."
            : $"Hosted on LAN port {CoopSettings.Port}. Friends join with this PC's IP address. Change it in Co-op Options.";
    }

    private void SetStatus(string text, bool error, bool layout = true)
    {
        _status.text = text;
        _status.color = error ? ErrorColor : _textColor;
        if (layout && IsOpen) Layout();
    }

    // ---------------- building helpers ----------------

    /// <summary>A copy of a Free Build sub-button (white, the game's hover look) running <paramref name="onClick"/>.</summary>
    private GameObject Button(Transform parent, string text, Action onClick)
    {
        var go = Object.Instantiate(_buttonTemplate.gameObject, parent);
        go.name = text + "Button";
        go.SetActive(true);
        Ui.SetText(Ui.Child(go.transform, "Text (TMP)"), text);
        Ui.HookMenuButton(go, onClick);
        return go;
    }

    /// <summary>A blue bar under a method button, shown when it is the chosen one.</summary>
    private static Image Mark(GameObject button)
    {
        var rt = Ui.NewRect("Selected", button.transform);
        rt.anchorMin = new Vector2(0, 0);
        rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(0.5f, 1);
        rt.anchoredPosition = new Vector2(0, -6);
        rt.sizeDelta = new Vector2(0, 8);
        var image = rt.gameObject.AddComponent<Image>();
        image.color = Accent;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>Place <paramref name="rt"/> by its top-left corner, <paramref name="y"/> measured down from the parent's top.</summary>
    private static void TopLeft(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(width, height);
    }
}
