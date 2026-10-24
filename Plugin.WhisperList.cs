using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// The WHISPER tab's layout: <c>[conversation list | selected conversation]</c> (data side: Plugin.Whisper.cs). Every
/// other tab shows the plain log. The log area is ONE row — [Cond(whisper → list pane)] [Cell(Weight 1): Cond(whisper →
/// header) over the log] — so the right pane IS the normal log (.Log.cs) with a per-peer filter: no second list
/// implementation, and a hidden Cond contributes neither width nor a row gap (an inactive child is skipped by the layout).
///
/// Width: the pane is FLEXIBLE — its Weight cell (min 0) takes window content − list − gap, so the log fills exactly the
/// space beside the list. Nothing inside the pane may carry a fixed min width: a min-width child overflows its squeezed
/// Weight cell (the cell's VLG clamps the child UP to its min), which is how a fixed 600 header cell once pushed the whole
/// log off the window's right edge. The pane's floor (<see cref="WhisperPaneMinWidth"/> = one full-width bubble row)
/// is carried by a separate invisible marker row instead: <see cref="RowElement.ConstrainsWindowMinWidth"/> over a
/// Weight cell (min 0) holding a fixed spacer, so the row is laid out at the pane's real width while its PREFERRED width
/// is the floor — MeasureMinWidth's delta (preferred − laid-out) then widens the window by exactly the shortfall. A
/// FIXED-width cell there would not work: min == preferred, so the row is laid out at its preferred width even while
/// overflowing, the delta reads 0 and the window never widens. The LOG row itself must NOT be marked: every bubble built
/// under a marked row is NoCap (hugs its natural width, ignores MaxWidth — WindowBuilder-Patterns.md), which would stop
/// long messages wrapping.
///
/// List rows: [avatar] [name (bold, online dot before it, unread dot slot after it) / one-line preview]. The text column is a
/// fixed-width cell placed LAST, so a long name or preview overflows only to the right, where the list viewport's own
/// mask clips it (the VirtualList hard-clip pattern); name and preview are also ellipsized. Right-click opens a small
/// menu (close conversation / copy name) — the sticker-menu popup pattern (Borderless + DismissOnOutsideClick, rect
/// re-asserted once the hidden window remounts).
/// </summary>
public sealed partial class Plugin
{
    private const float ConvListWidth       = 170f;
    private const float WhisperPaneGap      = 6f;
    // Right pane floor = the widest bubble row (.Bubbles.cs: avatar · bubble at its cap · time, plus the three row gaps
    // around the bubble and before the gutter) + the log viewport's scrollbar lane = 425, so a long whisper wraps INSIDE
    // the pane instead of shoving the avatar past its edge. Window min ≈ 12 + 170 + 6 + 425 + 12 = 625 — about the 621
    // the tab row already asks for in English, so opening Whisper barely moves it.
    private const float LogScrollbarLane    = 9f;     // framework VirtualList viewport offsetMax.x = −9
    private const float WhisperPaneMinWidth = AvatarSize + BubbleMaxWidth + TimeColWidth + 3f * RowGap + LogScrollbarLane;
    private const float ConvRowHeight       = 50f;    // uniform VirtualList pitch (row content ≈ 47)
    private const int   ConvPoolSize        = 24;     // ≈ 960 px max viewport ÷ 50 + buffer
    private const int   ConvAvatarSize      = 32;
    private const float ConvTextWidth       = 115f;   // 170 − 2×4 row pad − 32 avatar − 6 gap − 9 scrollbar lane
    private const int   OnlineDotSize       = 7;
    private const string OnlineDotResource  = "Stellar.ChatTools.Assets.online_dot.png";

    private const string ConvMenuId = "chattools.convmenu";
    private const float  ConvMenuW  = 200f;
    private const float  ConvMenuH  = 76f;    // two items + panel padding (edge-flip estimate only)

    private int _convWindowFirst;
    private byte[]? _onlineDotPng;
    private bool _onlineDotTried;

    private IWindowControl _convMenuWindow = null!;
    private long _convMenuPeer;
    private bool _convMenuOpen, _convMenuPlaced;
    private WindowRect _convMenuRect;

    // Placeholder / header caches (polled every refresh → rebuilt only when the inputs change).
    private long _hdrLevelPeer = -1; private int _hdrLevel; private string? _hdrLevelTpl; private string _hdrLevelText = "";

    private bool WhisperActive() => _activeTab == ChatTab.Whisper;

    private byte[]? OnlineDotPng()
    {
        if (_onlineDotTried) return _onlineDotPng;
        _onlineDotTried = true;
        _onlineDotPng = ReadResource(OnlineDotResource);   // .SendTarget.cs
        return _onlineDotPng;
    }

    // The body's log area for every tab (.Window.cs BuildRoot).
    private HudElement BuildLogArea() => new RowElement(new HudElement[]
    {
        new ConditionalElement(WhisperActive, new CellElement(BuildConversationPane(), Width: ConvListWidth)),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new ConditionalElement(WhisperActive, BuildWhisperHeader()),
            BuildLogScroll(),   // .Log.cs — filtered to the selected peer while Whisper is open
        }, Gap: 4f), Weight: 1f),
    }, Gap: WhisperPaneGap);

    // ── Header ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // A recessed bar (the tab strip's track fill + rounding) over the pane's width-floor marker (see the class note):
    //   [3-unit whisper-colour strip] [avatar 32] [name (bold 15, whisper colour) / "● Online · Lv.60"] … ["Whisper" pill]
    // With no conversation picked: the strip + the muted "pick a conversation" prompt only. The strip's 40-tall strut
    // sets the bar height in BOTH states (taller than the avatar and the two text lines), so picking / switching a
    // conversation never moves the log below. Nothing in the bar carries a big fixed min width (texts report min 0 and
    // are ellipsized; fixed parts ≈ 140 « the pane floor), and the bar row is NOT marked — the separate marker row still
    // carries the floor. Gap 0.01 = a real zero (0 = the default 12), so the marker adds no height.
    private const float HeaderStripW   = 3f;
    private const float HeaderContentH = 40f;   // bar height minus its 6 + 6 padding
    private const float HeaderPadY     = 6f;
    private const int   HeaderSubFontSize = 12, HeaderPillFontSize = 10;

    private HudElement BuildWhisperHeader() => new ColumnElement(new HudElement[]
    {
        new BubbleElement(new RowElement(new HudElement[]
        {
            // The left accent strip: a whisper-coloured block, full content height. A bubble because the toolkit has no
            // per-side border; its strut doubles as the bar's constant-height strut.
            new BubbleElement(new SpacerElement(Width: HeaderStripW, Height: HeaderContentH))
                { FillColor = Transparent, FillColorFn = () => ChannelColor(ChatChannel.Whisper), CornerRadius = 2f, PaddingX = 0f, PaddingY = 0f },
            new ConditionalElement(() => _selectedPeer != 0,
                ClickableAvatar(new GameTextureElement(() => AvatarTextureFor(_selectedPeer), ConvAvatarSize, ConvAvatarSize) { CornerRadius = 0 },
                    AvatarKeyHeader, ConvAvatarSize, () => _selectedPeer)),   // click = name card (.NameCard.cs)
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(HeaderTitle, () => _selectedPeer != 0 ? TabActiveText(ChatTab.Whisper) : _services.Theme.Colors.MenuMuted,
                    Emphasis: true, NoWrap: true) { Ellipsize = true },
                new ConditionalElement(() => _selectedPeer != 0 && HeaderSub().Length > 0, new RowElement(new HudElement[]
                {
                    new ConditionalElement(() => PeerOnline(_selectedPeer) == true,
                        new RowElement(new HudElement[] { new ImageElement(OnlineDotPng, OnlineDotSize, OnlineDotSize) })),
                    // RichEmoji = the TMP path, which honours FontSize on this surface (a plain Text ignores it).
                    new TextElement(HeaderSub, MutedCol, FontSize: HeaderSubFontSize, NoWrap: true) { RichEmoji = true, Ellipsize = true },
                }, Gap: 4f)),
            }, Gap: 1f), Weight: 1f),
            new ConditionalElement(() => _selectedPeer != 0, new BubbleElement(
                new TextElement(() => _loc.T(TabLabelKey(ChatTab.Whisper)), () => TabActiveText(ChatTab.Whisper),
                    FontSize: HeaderPillFontSize, NoWrap: true) { Bold = true })
                { FillColor = Transparent, FillColorFn = () => TabPillFill(ChatTab.Whisper), CornerRadius = PillRadius, PaddingX = 6f, PaddingY = 2f }),
            new SpacerElement(Width: 1f, Height: 1f),   // + the row gap = the bar's right padding (its left is the strip edge)
        }, Gap: 8f))
        { FillColor = TabTrackFill, CornerRadius = 10f, PaddingX = 0f, PaddingY = HeaderPadY },
        // Zero-height marker: Weight cell (min 0, preferred = the spacer's width) → laid out at the pane's real width.
        // No bubble under it, so the NoCap rule doesn't bite.
        new RowElement(new HudElement[]
        {
            new CellElement(new SpacerElement(Width: WhisperPaneMinWidth), Weight: 1f),
        }) { ConstrainsWindowMinWidth = true },
    }, Gap: 0.01f);

    private string HeaderTitle() => _selectedPeer != 0 ? PeerName(_selectedPeer) : _loc.T("chat.whisper.pick");

    // "Online · Lv.60" — each part only when known (online state from the conversation or the friends list; level > 0).
    private string HeaderSub()
    {
        if (_selectedPeer == 0) return "";
        bool? online = PeerOnline(_selectedPeer);
        int lv = PeerLevel(_selectedPeer);
        var status = online is { } on ? _loc.T(on ? "chat.friends.online" : "chat.friends.offline") : "";
        var tpl = _loc.T("chat.whisper.level");
        if (lv != _hdrLevel || _hdrLevelPeer != _selectedPeer || !string.Equals(tpl, _hdrLevelTpl, StringComparison.Ordinal)
            || !string.Equals(status, _hdrStatus, StringComparison.Ordinal))
        {
            _hdrLevel = lv; _hdrLevelPeer = _selectedPeer; _hdrLevelTpl = tpl; _hdrStatus = status;
            var level = lv > 0 ? SafeFormat(tpl, lv) : "";
            _hdrLevelText = status.Length > 0 && level.Length > 0 ? status + " · " + level : status + level;
        }
        return _hdrLevelText;
    }
    private string? _hdrStatus;

    // ── Conversation list ───────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildConversationPane()
    {
        var pool = new HudElement[ConvPoolSize];
        for (int i = 0; i < ConvPoolSize; i++) { var k = i; pool[k] = BuildConversationRow(k); }
        return new ColumnElement(new HudElement[]
        {
            // "+ New chat" in the whisper colours (same pill as the composer controls).
            BuildPill(() => _loc.T("chat.whisper.newChat"), () => TabActiveText(ChatTab.Whisper),
                () => TabPillFill(ChatTab.Whisper), OpenFriendPicker),   // .FriendPicker.cs
            new ConditionalElement(() => ConvOrder().Count == 0,
                new TextElement(() => _loc.T("chat.whisper.empty"), MutedCol)),
            new VirtualListElement(
                Count:     () => ConvOrder().Count,
                RowHeight: ConvRowHeight,
                Pool:      pool,
                OnWindow:  first => _convWindowFirst = first,
                Height:    200f),
        }, Gap: 4f);
    }

    private Conversation? ConvAtSlot(int slot)
    {
        var list = ConvOrder();
        int i = _convWindowFirst + slot;
        return i >= 0 && i < list.Count ? list[i] : null;
    }

    private long ConvIdAt(int slot) => ConvAtSlot(slot)?.CharId ?? 0;

    // Row = Bubble (the highlight) → Bare Selectable (click / hover / right-click, no visuals) → content. A normal
    // SelectableElement pads its child 6 h / 4 v and the old row also opened with an 8-unit unread slot + a 6 gap, so the
    // avatar sat 20 units in. The bubble pads 4 / 4 instead (same 4 vertical → same row height) and the unread dot moved
    // after the name, so the avatar now sits 4 in. The bubble paints exactly what the Selectable did — the menu accent
    // at the framework's selected / hover alphas on the same rounded chip (CornerRadius 0 = the SwatchBg rounding).
    private HudElement BuildConversationRow(int k) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            // Avatar click = name card (.NameCard.cs) — its own inner Button takes the click, so it does NOT also select
            // the conversation; the rest of the row still does.
            ClickableAvatar(new GameTextureElement(() => AvatarTextureFor(ConvIdAt(k)), ConvAvatarSize, ConvAvatarSize) { CornerRadius = 0 },
                AvatarKeyConvBase + k, ConvAvatarSize, () => ConvIdAt(k)),
            new CellElement(new ColumnElement(new HudElement[]
            {
                // [online dot] [name…] [unread dot slot]. The name reports min 0 and is ellipsized (TMP), so a long name
                // shrinks instead of pushing the dot out; the dot slot is a constant-width cell whether shown or not
                // (the tab-dot pattern, .Tabs.cs) → a dot appearing never moves the name.
                new RowElement(new HudElement[]
                {
                    new ConditionalElement(() => ConvAtSlot(k)?.Online == true,
                        new RowElement(new HudElement[] { new ImageElement(OnlineDotPng, OnlineDotSize, OnlineDotSize) })),
                    new TextElement(() => ConvAtSlot(k)?.Name is { Length: > 0 } n ? n : PeerName(ConvIdAt(k)), Emphasis: true, NoWrap: true)
                        { Ellipsize = true },
                    new CellElement(new ConditionalElement(() => ConvAtSlot(k) is { } c && HasUnread(c) && !IsViewingConversation(c.CharId),
                        new RowElement(new HudElement[] { new ImageElement(UnreadDotPng, UnreadDotSize, UnreadDotSize) }),
                        new SpacerElement(Width: UnreadDotSize, Height: UnreadDotSize)), Width: UnreadDotSize),
                }, Gap: 4f),
                new TextElement(() => ConvPreview(k), MutedCol, NoWrap: true) { RichEmoji = true, Ellipsize = true },
            }, Gap: 1f), Width: ConvTextWidth),
        }, Gap: 6f),
        // The row click took the keyboard from the composer (pointer-down selects the row) → hand it straight back, caret at
        // the end of the draft. Re-request rather than a non-focus-stealing row: same FocusRequest path as an emoji pick.
        OnClick: () => { SelectPeer(ConvIdAt(k)); FocusComposer(); })
    {
        Bare = true,
        OnHover = on => { if (on) _convHoverSlot = k; else if (_convHoverSlot == k) _convHoverSlot = -1; },
        OnRightClick = () => OpenConvMenu(ConvIdAt(k)),
    })
    {
        CornerRadius = 0f, PaddingX = ConvRowPad, PaddingY = ConvRowPad, FillColor = Transparent, FillColorFn = () => ConvRowFill(k),
    };

    private int _convHoverSlot = -1;
    private const float ConvRowPad = 4f;
    // The framework SelectableElement's own tints (WindowBuilder SelOn / SelHover): menu accent at these alphas.
    private const float ConvSelectedAlpha = 0.28f, ConvHoverAlpha = 0.14f;

    private ColorRgba? ConvRowFill(int k)
    {
        var accent = _services.Theme.Colors.MenuAccent;
        if (_selectedPeer != 0 && ConvIdAt(k) == _selectedPeer) return accent with { A = ConvSelectedAlpha };
        return _convHoverSlot == k ? accent with { A = ConvHoverAlpha } : Transparent;
    }

    // One-line preview of the latest message: the text (inline <sprite=N> emoji draw as pictures — RichEmoji), or a
    // localized [Sticker] / [Voice] / [Link] for the non-text kinds. Line breaks would add lines → flattened.
    private string ConvPreview(int slot)
    {
        if (ConvAtSlot(slot) is not { } c) return "";
        switch (c.LatestKind)
        {
            case 3: return _loc.T("chat.whisper.preview.sticker");
            case 5: return _loc.T("chat.whisper.preview.voice");
            case 6: return _loc.T("chat.whisper.preview.link");
        }
        return c.LatestText;   // already TMP-safe + flattened (line breaks → spaces) by its setter (.Whisper.cs)
    }

    // ── Right-click menu ────────────────────────────────────────────────────────────────────────────────────────────
    private IWindowControl RegisterConvMenuWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          ConvMenuId,
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, ConvMenuW, ConvMenuH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(new HudElement[]
            {
                new SelectableElement(new TextElement(() => _loc.T("chat.whisper.menu.close")),
                    () => { var p = _convMenuPeer; CloseConvMenu(); CloseConversation(p); }),
                new SelectableElement(new TextElement(() => _loc.T("chat.whisper.menu.copyName")),
                    () => { var p = _convMenuPeer; CloseConvMenu(); CopyToClipboard(PeerName(p)); }),
            }, Gap: 2f), Padding: 8f),
            OnClose: CloseConvMenu));   // framework invokes this on Escape / click-outside

    private void OpenConvMenu(long peer)
    {
        if (peer == 0) { CloseConvMenu(); return; }
        _convMenuPeer = peer;
        _convMenuRect = CursorPopupRect(ConvMenuW, ConvMenuH);
        _convMenuPlaced = false;
        _convMenuOpen = true;
        _convMenuWindow.SetRect(_convMenuRect);   // no-op while still unmounted; re-asserted in TickWhisperPopups
        _convMenuWindow.SetVisible(true);         // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    private void CloseConvMenu()
    {
        if (!_convMenuOpen) return;
        _convMenuOpen = false;
        _convMenuPlaced = false;
        _convMenuWindow?.SetVisible(false);
    }

    // Plugins may reference UnityEngine; IL2CPP-safe clipboard (same as CombatMeter's upload-link copy).
    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { GUIUtility.systemCopyBuffer = text; }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] clipboard copy failed: {ex.Message}"); }
    }

    // A popup rect at the cursor, opening down-right and flipping at the canvas edges. Input.mousePosition is bottom-left
    // SCREEN px; WindowRect is top-left CANVAS units → scale by CanvasWidth/ScreenWidth (k = 1 before the first measure).
    private WindowRect CursorPopupRect(float w, float h)
    {
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        var mp = Input.mousePosition;
        float x = mp.x * k, y = (Screen.height - mp.y) * k;
        if (x + w > cw) x = Mathf.Max(0f, x - w);
        if (y + h > ch) y = Mathf.Max(0f, y - h);
        return new WindowRect(x, y, w, h);
    }

    // Framework.Update (via TickWhisper): re-assert each popup's rect once its remount lands, and close them when the chat
    // window goes away (they are separate windows and would otherwise stay up).
    private void TickWhisperPopups()
    {
        if (!_convMenuOpen && !_friendPickerOpen) return;
        if (!_window.IsShown) { CloseConvMenu(); CloseFriendPicker(); return; }
        if (_convMenuOpen && !_convMenuPlaced && _convMenuWindow.IsShown) { _convMenuWindow.SetRect(_convMenuRect); _convMenuPlaced = true; }
        if (_friendPickerOpen && !_friendPickerPlaced && _friendPickerWindow.IsShown)
        { _friendPickerWindow.SetRect(_friendPickerRect); _friendPickerPlaced = true; }
    }
}
