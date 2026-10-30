using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// The co-op menu, built from copies of the game's own main menu entries so it looks and behaves the same.
/// <list type="bullet">
/// <item>Main list: a "Co-op" entry (a copy of Options) just above Options.</item>
/// <item>Clicking it swaps the list in place: the main entries fade out and the co-op ones fade in, with "CO-OP" under
/// the logo. Entries: Host Co-op (a copy of Free Build: Continue / Load Game / New Game on hover), Join a Friend,
/// Co-op Options, Back. Back or Esc swaps back.</item>
/// <item>The bottom row (social icons, news) moves with whichever list is shown. It gains a GitHub icon linking the
/// mod's repository with the mod version under it, so a screenshot shows whether the mod is loaded.</item>
/// <item>New Game, Load Game, Join a Friend and Co-op Options open a side panel next to the list (<see cref="CoopDialog"/>).</item>
/// </list>
/// The co-op list is its own container laid over the main one. Each list is hidden through a CanvasGroup on its
/// container, so the game's own per-entry fades (start-up, returning from the load screen) can't show the main list
/// underneath the co-op one.
/// </summary>
internal sealed class CoopMenu
{
    public const string RepositoryUrl = "https://github.com/m12labs/PCBS2-Coop";
    private const float FadeOutTime = 0.18f, FadeInTime = 0.22f, Stagger = 0.06f;

    private static int _failedMenu;
    private static Texture2D _mark;

    private readonly NewMainMenu _menu;
    private readonly RectTransform _mainList, _coopList;
    private readonly CanvasGroup _mainGroup, _coopGroup, _titleGroup, _extrasGroup;
    private readonly Transform _extras;
    private readonly CanvasGroup _freeBuildGroup, _entryGroup;
    private readonly List<CanvasGroup> _panels = new();
    private int _extrasIndex;
    private enum Step { Idle, Out, In }
    private Step _step;
    private bool _toCoop;
    private float _t;
    /// <summary>Main entries whose CanvasGroup ignores the list's (Options does): made to follow it while hidden.</summary>
    private readonly List<CanvasGroup> _mainIgnoring = new();
    private readonly CanvasGroup _youTubeGroup, _gitHubGroup;
    private readonly RectTransform _gitHubRect;
    private readonly TMP_Text _version;
    private readonly Vector2 _mainPos, _coopPos;
    private int _fitWidth, _fitHeight;

    /// <summary>The "Co-op" entry on the main list.</summary>
    public GameObject Entry { get; }
    public GameObject ContinueButton { get; }
    public GameObject LoadButton { get; }
    public GameObject NewGameButton { get; }
    public GameObject JoinButton { get; }
    public GameObject OptionsButton { get; }
    public GameObject BackButton { get; }
    public CoopDialog Dialog { get; }
    /// <summary>The co-op list is showing (or fading in).</summary>
    public bool CoopShown { get; private set; }
    /// <summary>No fade is running and the co-op list's entries can be clicked.</summary>
    public bool CoopReady => CoopShown && _step == Step.Idle;
    public bool IsAlive => Entry != null;

    /// <summary>Build the menu if the main menu is ready. Returns null if it isn't yet.</summary>
    public static CoopMenu TryBuild(CoopController coop)
    {
        var menu = Object.FindObjectOfType<NewMainMenu>();
        if (menu == null || menu.GetInstanceID() == _failedMenu) return null;
        var list = menu.transform.Find("MainMenu");
        var freeBuild = list?.Find("Button2-FreeBuild");
        var settings = list?.Find("Button4-Settings");
        var quit = list?.Find("Button5-QuitButton");
        var extras = list?.Find("MenuExtras");
        if (freeBuild == null || settings == null || quit == null || extras == null || freeBuild.Find("NewGame") == null) return null;
        try { return new CoopMenu(coop, menu, list.GetComponent<RectTransform>(), freeBuild, settings, quit, extras); }
        catch (Exception e)
        {
            _failedMenu = menu.GetInstanceID();
            Plugin.Logger.LogError("[menu] could not build the co-op menu: " + e);
            return null;
        }
    }

    private CoopMenu(CoopController coop, NewMainMenu menu, RectTransform list, Transform freeBuild, Transform settings, Transform quit,
        Transform extras)
    {
        _menu = menu;
        _mainList = list;
        _extras = extras;
        // Self tests switch to a disposable save folder only after the menu is up; leave the owner's co-op folder alone.
        if (!AutoTest.IsEnabled && !SteamProbe.Enabled)
        {
            CoopSaves.DropOrphanPairs();
        }

        // Main list: "Co-op" (a copy of Options) just above Options. It follows Free Build's start-up fade.
        Entry = Panel(settings, list, "Button4a-Coop", "Co-op", "Play Free Build with a friend", () => ShowCoop());
        Entry.transform.SetSiblingIndex(settings.GetSiblingIndex());
        _freeBuildGroup = freeBuild.GetComponent<CanvasGroup>();
        _entryGroup = Entry.GetComponent<CanvasGroup>();
        _mainGroup = list.gameObject.GetComponent<CanvasGroup>() ?? list.gameObject.AddComponent<CanvasGroup>();
        for (var i = 0; i < list.childCount; i++)
        {
            var g = list.GetChild(i).GetComponent<CanvasGroup>();
            if (g != null && g.ignoreParentGroups) _mainIgnoring.Add(g);
        }
        Plugin.Logger.LogInfo($"[menu] main entries that ignore the list's fade: [{string.Join(", ", _mainIgnoring.Select(g => g.name))}].");

        // Co-op list: its own container over the main one, laid out the same way.
        _coopList = Ui.NewRect("CoopMenu", list.parent);
        _coopList.SetSiblingIndex(list.GetSiblingIndex() + 1);
        CopyRect(list, _coopList);
        CopyLayout(list.GetComponent<VerticalLayoutGroup>(), _coopList.gameObject.AddComponent<VerticalLayoutGroup>());
        _coopGroup = _coopList.gameObject.AddComponent<CanvasGroup>();
        _titleGroup = AddTitle(_coopList, freeBuild, settings);

        var host = Panel(freeBuild, _coopList, "CoopHost", "Host Co-op", "Play your co-op worlds with a friend", null);
        var hostRect = host.GetComponent<RectTransform>();
        ContinueButton = SubButton(host.transform, "ContinueFreebuild", "Continue", () => Dialog.StartHost(HostStart.Continue));
        LoadButton = SubButton(host.transform, "LoadGame", "Load Game", () => Dialog.Open(CoopDialog.Mode.Load, hostRect));
        NewGameButton = SubButton(host.transform, "NewGame", "New Game", () => Dialog.Open(CoopDialog.Mode.NewWorld, hostRect));
        JoinButton = Panel(settings, _coopList, "CoopJoin", "Join a Friend", "Accept a Steam invite, or join by IP",
            () => Dialog.Open(CoopDialog.Mode.Join, JoinButton.GetComponent<RectTransform>()));
        OptionsButton = Panel(settings, _coopList, "CoopOptions", "Co-op Options", "Steam or LAN, and the LAN port",
            () => Dialog.Open(CoopDialog.Mode.Options, OptionsButton.GetComponent<RectTransform>()));
        BackButton = Panel(quit, _coopList, "CoopBack", null, null, ShowMain);
        Ui.SetText(Ui.Child(BackButton.transform, "QuitButton/HeaderText"), "Back");
        BackButton.transform.Find("QuitButton/QuitIcon")?.gameObject.SetActive(false);
        _panels.Add(_titleGroup);
        foreach (var p in new[] { host, JoinButton, OptionsButton, BackButton }) _panels.Add(p.GetComponent<CanvasGroup>() ?? p.AddComponent<CanvasGroup>());

        _extrasGroup = extras.gameObject.GetComponent<CanvasGroup>() ?? extras.gameObject.AddComponent<CanvasGroup>();
        (_youTubeGroup, _gitHubGroup, _gitHubRect, _version) = AddGitHub(extras);
        Dialog = CoopDialog.Build(menu.transform, settings, freeBuild.Find("NewGame"), coop, _coopList, hostRect);
        Dialog.WorldsChanged = RefreshWorlds;

        SetGroup(_coopGroup, 0, false);
        _coopList.gameObject.SetActive(false);
        _mainPos = _mainList.anchoredPosition;
        _coopPos = _coopList.anchoredPosition;
        Fit();
        Plugin.Logger.LogInfo("[menu] co-op menu added.");

        if (coop.TakeReturnToCoopMenu()) ShowCoop(instant: true);
        var message = coop.TakeMenuMessage();
        if (message != null) ShowMessage(message);
    }

    // ---------------- showing the lists ----------------

    public void ShowCoop(bool instant = false) => Go(true, instant);
    public void ShowMain() => Go(false, false);

    /// <summary>Show a message over the co-op list (for example why a Steam join failed).</summary>
    public void ShowMessage(string text) { Go(true, true); Dialog.OpenMessage(text); }

    /// <summary>A Steam invite was accepted: show the co-op list with the joining popup.</summary>
    public void ShowSteamJoining() { Go(true, true); Dialog.OpenSteamJoining(); }

    /// <summary>Open the New Game popup (probes).</summary>
    public void OpenNewWorld() { Go(true, true); Dialog.Open(CoopDialog.Mode.NewWorld); }

    private void Go(bool coop, bool instant)
    {
        if (CoopShown == coop && _step == Step.Idle) return;
        if (!coop && Dialog.IsOpen) Dialog.Cancel();
        if (instant || _step != Step.Idle) { Swap(coop); Finish(); return; }
        _toCoop = coop; _step = Step.Out; _t = 0;
        if (!CoopShown) FollowMainGroup(true);
        SetGroup(CoopShown ? _coopGroup : _mainGroup, CoopShown ? _coopGroup.alpha : _mainGroup.alpha, false);
    }

    private void Swap(bool coop)
    {
        EventSystem.current?.SetSelectedGameObject(null);
        if (coop == CoopShown && _extras.parent == (coop ? _coopList : _mainList)) return;
        if (coop)
        {
            if (_extras.parent == _mainList) _extrasIndex = _extras.GetSiblingIndex();
            FollowMainGroup(true);
            SetGroup(_mainGroup, 0, false);
            RefreshWorlds();
            _coopList.gameObject.SetActive(true);
            _extras.SetParent(_coopList, false);
            _extras.SetAsLastSibling();
            foreach (var p in _panels) p.alpha = 0;
            SetGroup(_coopGroup, 1, false);
        }
        else
        {
            _extras.SetParent(_mainList, false);
            _extras.SetSiblingIndex(Math.Min(_extrasIndex, _mainList.childCount - 1));
            _coopList.gameObject.SetActive(false);
            SetGroup(_mainGroup, 0, false);
        }
        _extrasGroup.alpha = 0;
        CoopShown = coop;
        LayoutRebuilder.ForceRebuildLayoutImmediate(coop ? _coopList : _mainList);
    }

    private void Finish()
    {
        _step = Step.Idle;
        _extrasGroup.alpha = 1;
        if (CoopShown)
        {
            foreach (var p in _panels) SetGroup(p, 1, true);
            SetGroup(_coopGroup, 1, true);
            SetGroup(_mainGroup, 0, false);
        }
        else
        {
            SetGroup(_mainGroup, 1, true);
            FollowMainGroup(false);
        }
    }

    private void Animate(float dt)
    {
        if (_step == Step.Idle) return;
        _t += dt;
        if (_step == Step.Out)
        {
            var a = Mathf.Clamp01(1 - _t / FadeOutTime);
            if (CoopShown) _coopGroup.alpha = a; else _mainGroup.alpha = a;
            if (_t < FadeOutTime) return;
            Swap(_toCoop);
            _step = Step.In; _t = 0;
            return;
        }
        if (CoopShown)
        {
            for (var i = 0; i < _panels.Count; i++) _panels[i].alpha = Mathf.Clamp01((_t - i * Stagger) / FadeInTime);
            _extrasGroup.alpha = Mathf.Clamp01((_t - _panels.Count * Stagger) / FadeInTime);
            if (_t >= FadeInTime + _panels.Count * Stagger) Finish();
        }
        else
        {
            _mainGroup.alpha = _extrasGroup.alpha = Mathf.Clamp01(_t / FadeInTime);
            if (_t >= FadeInTime) Finish();
        }
    }

    /// <summary>Continue and Load Game show only when there is a co-op world, as Free Build's do for its saves.</summary>
    public void RefreshWorlds()
    {
        var has = CoopSaves.HasCoopWorld();
        ContinueButton.SetActive(has);
        LoadButton.SetActive(has);
        NewGameButton.SetActive(true);
    }

    public void Update()
    {
        // Until the co-op list replaces it, "Co-op" fades in and out with Free Build (the game drives Free Build's fade).
        if (_freeBuildGroup != null && _entryGroup != null)
        {
            var active = _freeBuildGroup.gameObject.activeSelf;
            if (Entry.activeSelf != active) Entry.SetActive(active);
            _entryGroup.alpha = _freeBuildGroup.alpha;
            _entryGroup.interactable = _freeBuildGroup.interactable;
            _entryGroup.blocksRaycasts = _freeBuildGroup.blocksRaycasts;
        }

        // The game fades its own social icons in at start-up; the GitHub copy follows YouTube.
        if (_youTubeGroup != null && _gitHubGroup != null)
        {
            _gitHubGroup.alpha = _youTubeGroup.alpha;
            _gitHubGroup.interactable = _youTubeGroup.interactable;
            _gitHubGroup.blocksRaycasts = _youTubeGroup.blocksRaycasts;
        }
        // The version sits under the GitHub icon but outside it, so the icon's button colours never touch it.
        if (_version != null && _gitHubRect != null)
        {
            var g = _gitHubRect;
            var v = _version.rectTransform;
            v.anchorMin = g.anchorMin;
            v.anchorMax = g.anchorMax;
            v.anchoredPosition = new Vector2(g.anchoredPosition.x + (0.5f - g.pivot.x) * g.rect.width, g.anchoredPosition.y - g.pivot.y * g.rect.height - 4);
        }
        Fit();

        Animate(Time.unscaledDeltaTime);
        if (CoopShown && _step == Step.Idle)
        {
            // While the side panel waits for a connection or a lobby, the list can't be clicked.
            var clickable = !Dialog.Busy;
            if (_coopGroup.blocksRaycasts != clickable) SetGroup(_coopGroup, 1, clickable);
        }

        if (Ui.KeyDown(KeyCode.Escape))
        {
            if (Dialog.IsOpen) Dialog.Cancel();
            else if (CoopShown && _step == Step.Idle) ShowMain();
        }
        Dialog.Update();
    }

    // ---------------- building ----------------

    /// <summary>A copy of a main menu entry with new texts and click action.</summary>
    private static GameObject Panel(Transform source, Transform parent, string name, string header, string sub, Action onClick)
    {
        var go = Object.Instantiate(source.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        var group = go.GetComponent<CanvasGroup>();
        if (group != null) { SetGroup(group, 1, true); group.ignoreParentGroups = false; }
        var item = go.GetComponent<IndividualMenuButton>();
        if (item != null) { item.shouldFadeIn = false; item.shouldFadeOut = false; }
        if (header != null) Ui.SetText(go.transform.Find("HeaderText") ?? Ui.Child(go.transform, "Header"), header);
        if (sub != null) Ui.SetText(go.transform.Find("Subtext") ?? Ui.Child(go.transform, "Text"), sub);
        if (onClick != null) Ui.ReplaceClick(go, onClick);
        return go;
    }

    private static GameObject SubButton(Transform panel, string path, string text, Action onClick)
    {
        var t = Ui.Child(panel, path);
        Ui.SetText(Ui.Child(t, "Text (TMP)"), text);
        Ui.HookMenuButton(t.gameObject, onClick);
        return t.gameObject;
    }

    private static void CopyRect(RectTransform from, RectTransform to)
    {
        to.anchorMin = from.anchorMin;
        to.anchorMax = from.anchorMax;
        to.pivot = from.pivot;
        to.anchoredPosition = from.anchoredPosition;
        to.sizeDelta = from.sizeDelta;
        to.localScale = from.localScale;
        to.localRotation = from.localRotation;
    }

    private static void CopyLayout(VerticalLayoutGroup from, VerticalLayoutGroup to)
    {
        if (from == null) return;
        var p = from.padding;
        to.padding = new RectOffset { left = p.left, right = p.right, top = p.top, bottom = p.bottom };
        to.spacing = from.spacing;
        to.childAlignment = from.childAlignment;
        to.childControlWidth = from.childControlWidth;
        to.childControlHeight = from.childControlHeight;
        to.childForceExpandWidth = from.childForceExpandWidth;
        to.childForceExpandHeight = from.childForceExpandHeight;
        to.childScaleWidth = from.childScaleWidth;
        to.childScaleHeight = from.childScaleHeight;
        to.reverseArrangement = from.reverseArrangement;
    }

    /// <summary>"CO-OP" in the menu's heading font: the co-op list's first row, under the logo, left-aligned with it.</summary>
    private static CanvasGroup AddTitle(RectTransform list, Transform freeBuild, Transform entry)
    {
        var row = Ui.NewRect("CoopTitle", list);
        row.sizeDelta = new Vector2(entry.GetComponent<RectTransform>().rect.width, 130);
        var style = Ui.Child(freeBuild, "Header").GetComponent<TMP_Text>();
        var text = Ui.NewText("Text", row, style, "CO-OP", 110, TextAlignmentOptions.BottomLeft);
        Ui.Stretch(text.rectTransform, 0, 0);
        text.characterSpacing = 20;
        return row.gameObject.AddComponent<CanvasGroup>();
    }

    /// <summary>
    /// Six entries and the bottom row don't fit a 16:9 screen at full size (the game's five just do). Scale both lists
    /// by the same amount, just enough to fit, keeping their top-left corner where the game put it.
    /// </summary>
    private void Fit()
    {
        if (Screen.width == _fitWidth && Screen.height == _fitHeight) return;
        _fitWidth = Screen.width; _fitHeight = Screen.height;
        var canvas = _menu.GetComponent<RectTransform>();
        _mainList.localScale = _coopList.localScale = Vector3.one;
        _mainList.anchoredPosition = _mainPos;
        _coopList.anchoredPosition = _coopPos;
        var content = Math.Max(ContentHeight(_mainList), ContentHeight(_coopList));
        var top = canvas.InverseTransformPoint(TopLeft(_mainList)).y;
        var room = top - canvas.rect.yMin - 24;
        var units = _mainList.lossyScale.y / Math.Max(1e-6f, canvas.lossyScale.y);
        var scale = Mathf.Clamp(room / Math.Max(1f, content * units), 0.5f, 1f);
        ScaleFromTopLeft(_mainList, scale);
        ScaleFromTopLeft(_coopList, scale);
        Plugin.Logger.LogInfo($"[menu] lists scaled to {scale:F3} for {Screen.width}x{Screen.height} (content {content:F0}, room {room:F0}).");
    }

    /// <summary>Height the list's layout needs once every entry it shows is in, with the bottom row.</summary>
    private float ContentHeight(RectTransform list)
    {
        var layout = list.GetComponent<VerticalLayoutGroup>();
        float height = layout != null ? layout.padding.top : 0;
        var n = 0;
        for (var i = 0; i < list.childCount; i++)
        {
            var c = list.GetChild(i);
            // The game shows the survey entry only when its survey flag is set; MenuExtras is counted below.
            if (c == _extras || (c.name.Contains("Survey") && !_menu.m_betaSurvey)) continue;
            if (list == _mainList || c.gameObject.activeSelf) { height += c.GetComponent<RectTransform>().rect.height; n++; }
        }
        height += _extras.GetComponent<RectTransform>().rect.height; n++;
        return height + (layout != null ? layout.spacing * (n - 1) : 0);
    }

    private static Vector3 TopLeft(RectTransform rt) => rt.TransformPoint(new Vector3(rt.rect.xMin, rt.rect.yMax, 0));

    private static void ScaleFromTopLeft(RectTransform rt, float scale)
    {
        var before = TopLeft(rt);
        rt.localScale = new Vector3(scale, scale, 1);
        rt.position += before - TopLeft(rt);
    }

    /// <summary>
    /// Options' CanvasGroup ignores its parent's, so hiding the main list would leave it showing (and clickable) under
    /// the co-op list. While the co-op list is up, every such entry follows the list's group.
    /// </summary>
    private void FollowMainGroup(bool follow)
    {
        foreach (var g in _mainIgnoring) if (g != null) g.ignoreParentGroups = !follow;
    }

    /// <summary>A GitHub icon after YouTube (a copy of it, same look), with the mod version under it.</summary>
    private static (CanvasGroup YouTube, CanvasGroup GitHub, RectTransform Icon, TMP_Text Version) AddGitHub(Transform extras)
    {
        var social = extras.Find("Social");
        var youtube = social?.Find("YouTube");
        if (youtube == null) { Plugin.Logger.LogWarning("[menu] no YouTube icon to copy for the GitHub link."); return (null, null, null, null); }
        var socialRect = social.GetComponent<RectTransform>();
        var width = socialRect.rect.width > 0 ? socialRect.rect.width : socialRect.sizeDelta.x;
        var icons = social.childCount;

        var gh = Object.Instantiate(youtube.gameObject, social);
        gh.name = "GitHub";
        gh.transform.SetSiblingIndex(youtube.GetSiblingIndex() + 1);
        var tint = Color.white;
        for (var i = 0; i < gh.transform.childCount; i++)
        {
            var child = gh.transform.GetChild(i);
            if (!child.name.StartsWith("Icon", StringComparison.Ordinal)) continue;
            var image = child.GetComponent<Image>();
            if (image != null) tint = image.color;
            child.gameObject.SetActive(false);
        }
        var mark = Ui.NewRect("GitHubMark", gh.transform);
        Ui.Place(mark, Vector2.zero, new Vector2(64, 64));
        var raw = mark.gameObject.AddComponent<RawImage>();
        raw.texture = MarkTexture();
        raw.color = tint;
        raw.raycastTarget = false;
        Ui.ReplaceClick(gh, () => Application.OpenURL(RepositoryUrl));

        // Room for one more icon at the same spacing.
        if (icons > 0 && width > 0)
        {
            var wider = width + width / icons;
            socialRect.sizeDelta = new Vector2(socialRect.sizeDelta.x + (wider - width), socialRect.sizeDelta.y);
            (social.GetComponent<LayoutElement>() ?? social.gameObject.AddComponent<LayoutElement>()).preferredWidth = wider;
        }

        // The news links' text is white; other text in the row isn't.
        TMP_Text style = null;
        foreach (var t in extras.GetComponentsInChildren<TMP_Text>(true))
            if (style == null || t.name == "NewsText") style = t;
        TMP_Text version = null;
        if (style != null)
        {
            // A sibling of the icon (left out of the row's layout), placed under it every frame by Update.
            version = Ui.NewText("CoopVersion", social, style, "Co-op " + ModVersion.Value, 26, TextAlignmentOptions.Top);
            version.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            version.rectTransform.pivot = new Vector2(0.5f, 1);
            version.rectTransform.sizeDelta = new Vector2(260, 40);
            version.color = new Color(1, 1, 1, 0.8f);
        }
        return (youtube.GetComponent<CanvasGroup>(), gh.GetComponent<CanvasGroup>(), gh.GetComponent<RectTransform>(), version);
    }

    private static Texture2D MarkTexture()
    {
        if (_mark != null) return _mark;
        const int size = 128;
        var alpha = GitHubMark.Coverage(size);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "PCBS2Coop GitHub", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                tex.SetPixel(x, size - 1 - y, new Color(1, 1, 1, alpha[y * size + x]));
        tex.Apply(false);
        return _mark = tex;
    }

    /// <summary>
    /// Show or hide a group; <paramref name="clickable"/> only stops clicks. Never clear <c>interactable</c>: the game's
    /// buttons (<c>ButtonTextDiffColor</c>: social icons, news links, Continue/New Game) switch to their disabled
    /// look (white squares, grey text) and don't switch back.
    /// </summary>
    private static void SetGroup(CanvasGroup g, float alpha, bool clickable)
    {
        g.alpha = alpha;
        g.interactable = true;
        g.blocksRaycasts = clickable;
    }
}
