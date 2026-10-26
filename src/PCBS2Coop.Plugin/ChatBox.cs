using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// The co-op chat box and out-of-sync marker (task 3h, owner decisions October 26, 2026).
/// <list type="bullet">
/// <item>Enter in the workshop opens a one-line input at the bottom left; Enter sends, Escape cancels. Lines starting with
/// "/" are commands (<see cref="CoopController.SubmitChat"/>).</item>
/// <item>Recent lines show above it for a few seconds; all recent lines show while typing.</item>
/// <item>While typing, the game's own controls are paused by disabling Rewired's keyboard and mouse controllers
/// (the game reads every action through Rewired); they come back once the keys are released.</item>
/// <item>A small red marker in the top-right corner shows while this game is out of sync.</item>
/// </list>
/// </summary>
internal sealed class ChatBox
{
    private const float LineSeconds = 12f;
    private const int ShownLines = 8, ShownWhileTyping = 14, MaxRefocus = 30;

    private readonly CoopController _coop;
    private GameObject _root;
    private TextMeshProUGUI _log, _marker;
    private TMP_InputField _input;
    private bool _open, _openNextFrame, _submitted, _paused;
    private int _openedFrame, _refocus, _closedFrame = -10;
    private float _resumeBy = -1;
    private readonly List<(Rewired.Controller Controller, bool Was)> _disabled = new();
    private bool _keyReadFailed, _bindingChecked;

    public ChatBox(CoopController coop) => _coop = coop;

    /// <summary>The chat input has focus (game controls are paused).</summary>
    public bool Typing => _open || _openNextFrame;
    public bool MarkerShown => _marker != null && _marker.gameObject.activeSelf;

    public void Update(bool inWorkshop)
    {
        var running = _coop.Session.IsRunning;
        var anyRecent = _coop.ChatLines.Count > 0 && Time.realtimeSinceStartup - _coop.ChatLines[^1].At < LineSeconds;
        if (_root == null && (running || anyRecent) && !Build()) return;
        if (_root == null) return;
        if (running && inWorkshop) CheckBinding();

        if (_open && (!running || !inWorkshop)) Close();
        if (_openNextFrame) Activate();
        else if (!_open && running && inWorkshop && Time.frameCount > _closedFrame + 1 && (KeyDown(KeyCode.Return) || KeyDown(KeyCode.KeypadEnter)) && !GameTyping())
        { _openNextFrame = true; Pause(); }
        else if (_open) KeepFocus();
        if (!_open && !_openNextFrame) Resume();

        _marker.gameObject.SetActive(running && inWorkshop && _coop.OutOfSync != null);
        RenderLog(running);
    }

    // ---------------- building ----------------

    private bool Build()
    {
        TMP_Text style = null;
        foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
            if (t.font != null) { style = t; break; }
        if (style == null) return false;

        _root = new GameObject("PCBS2Coop Chat");
        Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5001;
        _root.AddComponent<GraphicRaycaster>();

        _log = Ui.NewText("Log", _root.transform, style, "", 20, TextAlignmentOptions.BottomLeft);
        Corner(_log.rectTransform, new Vector2(0, 0), new Vector2(16, 72), new Vector2(980, 420));
        _log.enableWordWrapping = true;
        _log.richText = false;
        _log.color = Color.white;
        _log.outlineWidth = 0.2f; _log.outlineColor = Color.black;

        _marker = Ui.NewText("Out of sync", _root.transform, style, "OUT OF SYNC · press Enter, type /sync", 20, TextAlignmentOptions.TopRight);
        Corner(_marker.rectTransform, new Vector2(1, 1), new Vector2(-16, -52), new Vector2(560, 32)); // below the status line
        _marker.color = new Color(1f, 0.32f, 0.28f);
        _marker.outlineWidth = 0.25f; _marker.outlineColor = Color.black;
        _marker.gameObject.SetActive(false);

        _input = Ui.NewInput("Input", _root.transform, style, "Message, or /help", 20);
        Corner((RectTransform)_input.transform, new Vector2(0, 0), new Vector2(16, 20), new Vector2(980, 44));
        _input.characterLimit = Network.ChatLine.MaxLength;
        _input.onSubmit.AddListener((UnityEngine.Events.UnityAction<string>)(Action<string>)(text => Ui.Safe(() => Submit(text))));
        _input.gameObject.SetActive(false);
        return true;
    }

    private static void Corner(RectTransform rt, Vector2 corner, Vector2 offset, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = corner;
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
    }

    // ---------------- typing ----------------

    private void Activate()
    {
        // One frame after Enter, so the opening keypress itself isn't taken as a submit.
        _openNextFrame = false; _open = true; _submitted = false; _openedFrame = Time.frameCount; _refocus = 0;
        _input.gameObject.SetActive(true);
        _input.text = "";
        EventSystem.current?.SetSelectedGameObject(_input.gameObject);
        _input.ActivateInputField();
    }

    private void KeepFocus()
    {
        if (KeyDown(KeyCode.Escape)) { Close(); return; }
        if (_input.isFocused || Time.frameCount - _openedFrame < 3 || _submitted) return;
        // Something else took the keyboard focus (the game's UI or a click): take it back, but don't fight forever.
        if (++_refocus > MaxRefocus)
        {
            Plugin.Logger.LogWarning("[chat] the chat box keeps losing keyboard focus; closing it.");
            Close();
            return;
        }
        EventSystem.current?.SetSelectedGameObject(_input.gameObject);
        _input.ActivateInputField();
    }

    private void Submit(string text)
    {
        if (!_open) return;
        _submitted = true;
        Close();
        _coop.SubmitChat(text);
    }

    private void Close()
    {
        _open = _openNextFrame = false;
        _closedFrame = Time.frameCount; // the Enter that sent a line must not open the box again
        if (_input == null) return;
        _input.text = "";
        _input.DeactivateInputField();
        if (EventSystem.current?.currentSelectedGameObject == _input.gameObject) EventSystem.current.SetSelectedGameObject(null);
        _input.gameObject.SetActive(false);
        _resumeBy = Time.realtimeSinceStartup + 1f;
    }

    /// <summary>Pause the game's controls while typing (every Rewired keyboard/mouse action reads zero).</summary>
    private void Pause()
    {
        if (_paused) return;
        _paused = true; _disabled.Clear();
        try
        {
            if (!Rewired.ReInput.isReady) return;
            var controllers = Rewired.ReInput.controllers;
            foreach (Rewired.Controller c in new Rewired.Controller[] { controllers.Keyboard, controllers.Mouse })
                if (c != null) { _disabled.Add((c, c.enabled)); c.enabled = false; }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[chat] could not pause the game's controls: " + e.Message); }
    }

    /// <summary>Give the controls back once the closing keys are released (so Enter/Escape don't reach the game).</summary>
    private void Resume()
    {
        if (!_paused) return;
        if (AnyKeyHeld() && Time.realtimeSinceStartup < _resumeBy) return;
        foreach (var (c, was) in _disabled)
            try { if (c != null) c.enabled = was; }
            catch (Exception e) { Plugin.Logger.LogWarning("[chat] could not restore a controller: " + e.Message); }
        _disabled.Clear(); _paused = false;
    }

    /// <summary>A game text field has focus (inventory search, PC rename): Enter belongs to it.</summary>
    private static bool GameTyping()
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        if (selected == null) return false;
        return selected.GetComponent<TMP_InputField>()?.isFocused == true || selected.GetComponent<InputField>()?.isFocused == true;
    }

    private bool KeyDown(KeyCode key)
    {
        if (!_keyReadFailed)
            try { return Input.GetKeyDown(key); }
            catch (Exception e) { _keyReadFailed = true; Plugin.Logger.LogWarning("[chat] Unity key input unavailable, using Rewired: " + e.Message); }
        try { return Rewired.ReInput.isReady && Rewired.ReInput.controllers.Keyboard.GetKeyDown(key); }
        catch { return false; }
    }

    private bool AnyKeyHeld()
    {
        if (_keyReadFailed) return false;
        try { return Input.anyKey; } catch { return false; }
    }

    /// <summary>Log once, on entering a session, if the game also binds the chat key (its action would run too).</summary>
    private void CheckBinding()
    {
        if (_bindingChecked) return;
        _bindingChecked = true;
        try
        {
            var inputs = PCBSInput.m_keyboardInputs;
            if (inputs == null) return;
            foreach (var input in inputs)
                if (input != null && input.GetCurrentKeyMapping() is KeyCode.Return or KeyCode.KeypadEnter)
                    Plugin.Logger.LogWarning($"[chat] Enter is also bound to the game's '{input.ActionID}' action.");
        }
        catch (Exception e) { Plugin.Logger.LogInfo("[chat] could not read the game's key bindings: " + e.Message); }
    }

    // ---------------- log ----------------

    private void RenderLog(bool running)
    {
        var now = Time.realtimeSinceStartup;
        var lines = _coop.ChatLines;
        var shown = new List<string>();
        for (var i = lines.Count - 1; i >= 0 && shown.Count < (_open ? ShownWhileTyping : ShownLines); i--)
        {
            if (!_open && now - lines[i].At > LineSeconds) break;
            shown.Add(lines[i].Text);
        }
        shown.Reverse();
        var text = string.Join("\n", shown);
        _log.gameObject.SetActive(text.Length > 0);
        if (_log.text != text) _log.text = text;
    }
}
