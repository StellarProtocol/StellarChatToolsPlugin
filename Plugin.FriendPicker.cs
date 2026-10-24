using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// "+ New chat" friend picker (opened from the Whisper tab's conversation list, .WhisperList.cs). A cursor-positioned
/// popup window — the sticker-menu pattern (Borderless + DismissOnOutsideClick, rect re-asserted once the hidden window
/// remounts) — with a search field and the friend list from <see cref="IFriends.Friends"/>: online friends first, then
/// offline, each A→Z, every row [avatar] [name / "Lv.N · Online"]. Picking a friend opens a conversation in the game
/// (<see cref="IFriends.CreatePrivateChat"/>, which also loads its newest history) and selects it. The visible list is a
/// cached view rebuilt only when the friend list or the search text changes, so the per-refresh row Funcs never allocate.
/// </summary>
public sealed partial class Plugin
{
    private const string FriendPickerId  = "chattools.friendpicker";
    private const float  FriendPickerW   = 250f;
    private const float  FriendPickerH   = 360f;   // title + search + list viewport + padding (edge-flip estimate)
    private const float  FriendRowHeight = 42f;    // uniform VirtualList pitch (row content ≈ 40)
    private const float  FriendListHeight = 260f;
    private const int    FriendPoolSize  = 10;     // 260 ÷ 42 + buffer
    private const int    FriendAvatarSize = 28;

    private IWindowControl _friendPickerWindow = null!;
    private bool _friendPickerOpen, _friendPickerPlaced;
    private WindowRect _friendPickerRect;

    private string _friendFilter = string.Empty;
    private readonly List<FriendInfo> _friendView = new();
    private bool _friendViewDirty = true;
    private int _friendWindowFirst;

    // Per-row "Lv.N · Online" cache (row index → text), cleared whenever the view rebuilds or the language changes.
    private readonly Dictionary<long, string> _friendSubCache = new();
    private string? _friendSubLang;

    private List<FriendInfo> FriendView()
    {
        if (!_friendViewDirty) return _friendView;
        _friendView.Clear();
        var q = _friendFilter.Trim();
        foreach (var f in FriendSource())   // .CharacterSwitch.cs — empty while a reset waits for the new lists
        {
            if (q.Length > 0
                && f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && f.Remark.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _friendView.Add(f);
        }
        _friendView.Sort((a, b) =>
        {
            if (a.IsOnline != b.IsOnline) return a.IsOnline ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _friendSubCache.Clear();
        _friendViewDirty = false;
        return _friendView;
    }

    private FriendInfo? FriendAtSlot(int slot)
    {
        var list = FriendView();
        int i = _friendWindowFirst + slot;
        return i >= 0 && i < list.Count ? list[i] : null;
    }

    private string FriendName(int slot)
        => FriendAtSlot(slot) is { } f ? (f.Name.Length > 0 ? f.Name : f.Remark) : "";

    private string FriendSub(int slot)
    {
        if (FriendAtSlot(slot) is not { } f) return "";
        var lang = _loc.Language;
        if (lang != _friendSubLang) { _friendSubCache.Clear(); _friendSubLang = lang; }
        long key = f.CharId * 2 + (f.IsOnline ? 1 : 0);
        if (_friendSubCache.TryGetValue(key, out var s)) return s;
        var state = _loc.T(f.IsOnline ? "chat.friends.online" : "chat.friends.offline");
        s = f.Level > 0 ? SafeFormat(_loc.T("chat.whisper.level"), f.Level) + " · " + state : state;
        _friendSubCache[key] = s;
        return s;
    }

    private string FriendEmptyHint()
        => _loc.T(FriendSource().Count == 0 ? "chat.friends.empty" : "chat.friends.noMatch");

    private IWindowControl RegisterFriendPickerWindow()
    {
        var pool = new HudElement[FriendPoolSize];
        for (int i = 0; i < FriendPoolSize; i++)
        {
            var k = i;
            pool[k] = new SelectableElement(new RowElement(new HudElement[]
                {
                    new GameTextureElement(() => AvatarTextureFor(FriendAtSlot(k)?.CharId ?? 0), FriendAvatarSize, FriendAvatarSize)
                        { CornerRadius = 0 },
                    new ColumnElement(new HudElement[]
                    {
                        new TextElement(() => FriendName(k), Emphasis: true, NoWrap: true),
                        new TextElement(() => FriendSub(k),
                            () => FriendAtSlot(k)?.IsOnline == true ? OnlineTextColor() : _services.Theme.Colors.MenuMuted, NoWrap: true),
                    }, Gap: 1f),
                }, Gap: 8f),
                OnClick: () => PickFriend(FriendAtSlot(k)?.CharId ?? 0));
        }
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          FriendPickerId,
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, FriendPickerW, FriendPickerH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.T("chat.friends.title"), Emphasis: true),
                // AutoFocus: opening the picker (a user-initiated show) puts the caret in the search box.
                new InputElement(() => _friendFilter, s => SetFriendFilter(s), FriendPickerW - 24f, OnChange: SetFriendFilter)
                    { SingleLine = true, AutoFocus = true, Placeholder = () => _loc.T("chat.friends.search") },
                new ConditionalElement(() => FriendView().Count == 0,
                    new TextElement(FriendEmptyHint, MutedCol)),
                new VirtualListElement(
                    Count:     () => FriendView().Count,
                    RowHeight: FriendRowHeight,
                    Pool:      pool,
                    OnWindow:  first => _friendWindowFirst = first,
                    Height:    FriendListHeight),
            }, Gap: 6f), Padding: 8f),
            OnClose: CloseFriendPicker));   // framework invokes this on Escape / click-outside
    }

    // The friend-list green (the online dot's colour), contrast-floored against the panel like the tab labels.
    private ColorRgba? OnlineTextColor()
        => EnsureContrast(new ColorRgba(0.24f, 0.84f, 0.47f), _services.Theme.Colors.MenuBackground with { A = 1f }, TabMutedMinContrast);

    private void SetFriendFilter(string s)
    {
        s ??= string.Empty;
        if (string.Equals(s, _friendFilter, StringComparison.Ordinal)) return;
        _friendFilter = s;
        _friendViewDirty = true;
    }

    private void OpenFriendPicker()
    {
        if (_friendPickerOpen) { CloseFriendPicker(); return; }
        CloseConvMenu();
        SetFriendFilter(string.Empty);
        _friendPickerRect = CursorPopupRect(FriendPickerW, FriendPickerH);   // .WhisperList.cs
        _friendPickerPlaced = false;
        _friendPickerOpen = true;
        _friendPickerWindow.SetRect(_friendPickerRect);   // no-op while still unmounted; re-asserted in TickWhisperPopups
        _friendPickerWindow.SetVisible(true);             // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    private void CloseFriendPicker()
    {
        if (!_friendPickerOpen) return;
        _friendPickerOpen = false;
        _friendPickerPlaced = false;
        _friendPickerWindow?.SetVisible(false);
    }

    private void PickFriend(long charId)
    {
        if (charId == 0) return;
        CloseFriendPicker();
        var c = GetOrCreateConv(charId);        // .Whisper.cs — shows at once, before the game's list refreshes
        c.TouchedUtc = DateTime.UtcNow;         // sort the just-opened chat to the top (its history may all be older)
        c.HistoryRequested = true;              // CreatePrivateChat already loads the newest history
        _closedAt.Remove(charId);
        try { _friends.CreatePrivateChat(charId); } catch { /* queued action */ }
        OpenConversation(charId);
    }
}
