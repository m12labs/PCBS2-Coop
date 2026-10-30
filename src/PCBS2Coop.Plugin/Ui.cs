using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Small helpers for building uGUI objects from managed code.</summary>
internal static class Ui
{
    public static Transform Child(Transform root, string path) =>
        root.Find(path) ?? throw new InvalidOperationException($"Missing '{path}' under '{root.name}'.");

    public static void SetText(Transform t, string text)
    {
        var tmp = t.GetComponent<TMP_Text>() ?? t.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null) tmp.text = text;
    }

    public static void DestroyChild(Transform root, string path)
    {
        var t = root.Find(path);
        if (t != null) Object.Destroy(t.gameObject);
    }

    /// <summary>
    /// Make a cloned menu sub-button run <paramref name="onClick"/>. The game wires its own actions onto
    /// the Button at runtime, and Instantiate doesn't copy runtime listeners, so the clone only has ours
    /// (plus the click sound the button adds for itself).
    /// </summary>
    public static void HookMenuButton(GameObject go, Action onClick)
    {
        go.GetComponent<EventTrigger>()?.triggers.Clear();
        var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
        button.onClick.AddListener((UnityAction)new Action(() => Safe(onClick)));
    }

    /// <summary>
    /// Make a cloned menu entry run only <paramref name="onClick"/>: its click event is replaced, so neither the
    /// game's serialized calls (for example Options' "ShowOptionsMenu") nor its fade-and-call flags come along.
    /// </summary>
    public static void ReplaceClick(GameObject go, Action onClick)
    {
        foreach (var trigger in go.GetComponents<EventTrigger>()) trigger.triggers.Clear();
        var item = go.GetComponent<IndividualMenuButton>();
        if (item != null) { item.shouldFadeIn = false; item.shouldFadeOut = false; }
        var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener((UnityAction)new Action(() => Safe(onClick)));
    }

    private static bool _keyReadFailed;

    /// <summary>A key went down this frame (Unity input, else Rewired, which the game uses).</summary>
    public static bool KeyDown(KeyCode key)
    {
        if (!_keyReadFailed)
            try { return Input.GetKeyDown(key); }
            catch (Exception e) { _keyReadFailed = true; Plugin.Logger.LogWarning("[menu] Unity key input unavailable, using Rewired: " + e.Message); }
        try { return Rewired.ReInput.isReady && Rewired.ReInput.controllers.Keyboard.GetKeyDown(key); }
        catch { return false; }
    }

    public static Button MakeButton(GameObject go, Action onClick)
    {
        var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Graphic>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener((UnityAction)new Action(() => Safe(onClick)));
        return button;
    }

    public static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    public static TextMeshProUGUI NewText(string name, Transform parent, TMP_Text style, string text, float size, TextAlignmentOptions align)
    {
        var rt = NewRect(name, parent);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = style.font;
        tmp.fontSharedMaterial = style.fontSharedMaterial;
        tmp.color = style.color;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.text = text;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>Build a single-line TMP input field styled with <paramref name="style"/>'s font.</summary>
    public static TMP_InputField NewInput(string name, Transform parent, TMP_Text style, string placeholder, float fontSize)
    {
        var rt = NewRect(name, parent);
        var go = rt.gameObject;
        go.SetActive(false);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.08f, 0.1f, 0.95f);

        var area = NewRect("Text Area", rt);
        Stretch(area, 24, 8);
        area.gameObject.AddComponent<RectMask2D>();

        var ph = NewText("Placeholder", area, style, placeholder, fontSize, TextAlignmentOptions.MidlineLeft);
        ph.color = new Color(1, 1, 1, 0.35f);
        Stretch(ph.rectTransform, 0, 0);
        var text = NewText("Text", area, style, "", fontSize, TextAlignmentOptions.MidlineLeft);
        text.color = Color.white;
        Stretch(text.rectTransform, 0, 0);

        var input = go.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = ph;
        input.fontAsset = style.font;
        input.pointSize = fontSize;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.customCaretColor = true;
        input.caretColor = Color.white;
        input.caretWidth = 3;
        input.selectionColor = new Color(0.3f, 0.6f, 1f, 0.5f);
        go.SetActive(true);
        return input;
    }

    public static void Stretch(RectTransform rt, float padX, float padY)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(padX, padY);
        rt.offsetMax = new Vector2(-padX, -padY);
    }

    public static void Place(RectTransform rt, Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
    }

    public static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError(e); }
    }
}
