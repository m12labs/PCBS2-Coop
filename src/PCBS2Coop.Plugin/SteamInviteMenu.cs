using PCBS2Coop.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Local pause-menu controls. Does not change pause or session/world state.</summary>
internal sealed class SteamInviteMenu
{
    private readonly CoopController _coop;
    private GameObject _root, _friendsRoot;
    private Button _invite, _fallback;
    private TMP_Text _feedback;
    private Transform _rows;
    private TMP_Text _style;
    private Button _source;
    private CanvasGroup _sourceGroup, _group;
    private float _retryAt;
    private readonly Dictionary<ulong, Button> _inviteButtons = new();
    private ISteamService Steam => SteamBootstrap.Service;

    public SteamInviteMenu(CoopController coop) => _coop = coop;

    public void Update(bool workshop)
    {
        var show = workshop && _coop.IsSteamHost && _coop.WorldReady;
        if (!show) { DestroyUi(); return; }
        if (_root == null && Time.realtimeSinceStartup >= _retryAt)
        { _retryAt = Time.realtimeSinceStartup + 1; TryBuild(); }
        if (_root == null) return;
        var controller = Object.FindObjectOfType<GameController>();
        var paused = controller != null && controller.GetPaused() && _source != null && _source.gameObject.activeInHierarchy;
        _root.SetActive(paused);
        if (!paused) { if (_friendsRoot != null) _friendsRoot.SetActive(false); return; }
        _group.alpha = _sourceGroup != null ? _sourceGroup.alpha : 1;
        _group.blocksRaycasts = _group.interactable = _sourceGroup == null || (_sourceGroup.interactable && _sourceGroup.blocksRaycasts);
        _invite.interactable = _fallback.interactable = Steam?.CanInvite == true;
        if (_friendsRoot != null && _friendsRoot.activeSelf)
            foreach (var button in _inviteButtons.Values) button.interactable = Steam?.CanInvite == true && button.name != "Sent" && button.name != "Offline";
    }

    private void DestroyUi()
    {
        if (_root != null) Object.Destroy(_root);
        if (_friendsRoot != null) Object.Destroy(_friendsRoot);
        _root = _friendsRoot = null; _source = null; _sourceGroup = null; _style = null;
        _inviteButtons.Clear();
    }

    private void TryBuild()
    {
        foreach (var menu in Object.FindObjectsOfType<InGameMenu>(true))
        {
            if (menu.newButtons == null) continue;
            foreach (var candidate in menu.newButtons)
            {
                if (candidate?.workingButton == null) continue;
                _source = candidate.workingButton;
                _style = _source.GetComponentInChildren<TMP_Text>(true) ?? candidate.GetComponentInChildren<TMP_Text>(true);
                if (_style == null) continue;
                _sourceGroup = candidate.GetComponent<CanvasGroup>();
                // A nested canvas inherits the game's large reference scale. Use an independent canvas,
                // and follow the actual native Resume control's active/fade state above.
                _root = CanvasRoot("CoopSteamPause", CoopRunner.Instance.transform, 5100);
                _group = _root.AddComponent<CanvasGroup>();
                _invite = Button("Invite Friends", _root.transform, new Vector2(0, -390), new Vector2(440, 72), () =>
                {
                    if (Steam?.OpenInviteOverlay() != true) OpenFriends();
                });
                _fallback = Button("Use in-game friends list", _root.transform, new Vector2(480, -390), new Vector2(440, 72), OpenFriends);
                Plugin.Logger.LogInfo($"[steam-ui] Pause controls follow {menu.name}/{candidate.name}.");
                return;
            }
        }
    }

    private static GameObject CanvasRoot(string name, Transform parent, int order)
    {
        var rect = Ui.NewRect(name, parent);
        var canvas = rect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = order;
        var scaler = rect.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
        rect.gameObject.AddComponent<GraphicRaycaster>();
        return rect.gameObject;
    }

    private Button Button(string label, Transform parent, Vector2 position, Vector2 size, Action action)
    {
        var rect = Ui.NewRect(label, parent); Ui.Place(rect, position, size);
        var image = rect.gameObject.AddComponent<Image>();
        var sourceImage = _source?.targetGraphic?.TryCast<Image>();
        image.sprite = sourceImage?.sprite; image.type = sourceImage != null ? sourceImage.type : Image.Type.Simple;
        image.color = sourceImage != null ? sourceImage.color : new Color(0.2f, 0.22f, 0.25f, 1);
        var button = Ui.MakeButton(rect.gameObject, action);
        if (_source != null) button.colors = _source.colors;
        var text = Ui.NewText("Label", rect, _style, label, 27, TextAlignmentOptions.Center);
        text.richText = false; text.color = Color.white; Ui.Stretch(text.rectTransform, 12, 0);
        return button;
    }

    private void OpenFriends()
    {
        if (Steam?.CanInvite != true) return;
        if (_friendsRoot == null) BuildFriends();
        _friendsRoot.SetActive(true);
        RefreshFriends();
    }

    private void BuildFriends()
    {
        _friendsRoot = CanvasRoot("CoopSteamFriends", CoopRunner.Instance.transform, 5200);
        var backdrop = Ui.NewRect("Backdrop", _friendsRoot.transform); Ui.Stretch(backdrop, 0, 0);
        backdrop.gameObject.AddComponent<Image>().color = new Color(0.03f, 0.04f, 0.06f, 0.97f);
        var title = Ui.NewText("Title", backdrop, _style, "Invite Steam friends", 40, TextAlignmentOptions.Center);
        Ui.Place(title.rectTransform, new Vector2(0, 410), new Vector2(1100, 70));
        _feedback = Ui.NewText("Status", backdrop, _style, "", 24, TextAlignmentOptions.Center);
        _feedback.richText = false;
        Ui.Place(_feedback.rectTransform, new Vector2(0, -330), new Vector2(1400, 60));
        Button("Refresh", backdrop, new Vector2(-230, -420), new Vector2(360, 70), RefreshFriends);
        Button("Back", backdrop, new Vector2(230, -420), new Vector2(360, 70), () => _friendsRoot.SetActive(false));
        var viewport = Ui.NewRect("FriendsViewport", backdrop);
        Ui.Place(viewport, new Vector2(0, 50), new Vector2(1320, 600));
        viewport.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.11f, 0.13f, 1);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = Ui.NewRect("Content", viewport); _rows = content;
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
    }

    private void RefreshFriends()
    {
        for (var i = _rows.childCount - 1; i >= 0; i--) Object.Destroy(_rows.GetChild(i).gameObject);
        _inviteButtons.Clear();
        IReadOnlyList<SteamFriend> friends;
        try { friends = Steam.GetFriends(); }
        catch (Exception e) { _feedback.text = "Could not load Steam friends: " + e.Message; return; }
        _feedback.text = friends.Count == 0 ? "No Steam friends found." : "Scroll to see more friends. Both players must already have PCBS2 open.";
        _rows.GetComponent<RectTransform>().sizeDelta = new Vector2(0, Math.Max(600, friends.Count * 82));
        var rowIndex = 0;
        foreach (var friend in friends)
        {
            var y = -42 - rowIndex++ * 82;
            var row = Ui.NewRect("Friend", _rows);
            row.anchorMin = row.anchorMax = new Vector2(0.5f, 1); row.anchoredPosition = new Vector2(0, y); row.sizeDelta = new Vector2(1300, 76);
            var name = Ui.NewText("Name", row, _style, CleanName(friend.Name) + (friend.Online ? " · Online" : " · Offline"), 26, TextAlignmentOptions.MidlineLeft);
            name.richText = false; name.overflowMode = TextOverflowModes.Ellipsis;
            Ui.Place(name.rectTransform, new Vector2(-150, 0), new Vector2(900, 70));
            Button invite = null;
            invite = Button("Invite", row, new Vector2(490, 0), new Vector2(220, 64), () =>
            {
                var sent = Steam.Invite(friend.Id);
                _feedback.text = sent ? "Invite sent to " + CleanName(friend.Name) + "." : "Steam could not send the invite. Check the connection and lobby slot.";
                if (sent) { invite.name = "Sent"; invite.interactable = false; invite.GetComponentInChildren<TMP_Text>().text = "Sent"; }
            });
            invite.interactable = friend.Online && Steam.CanInvite;
            if (!friend.Online) invite.name = "Offline";
            _inviteButtons[friend.Id] = invite;
        }
        _rows.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
    }
    private static string CleanName(string name) => new string((name ?? "Steam friend").Where(c => !char.IsControl(c)).Take(80).ToArray());
}
