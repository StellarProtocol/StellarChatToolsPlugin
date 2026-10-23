using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Channel READ-tabs — a NEW concept distinct from the composer's send-target. A segmented-pill tab bar filters
/// the log to one <see cref="ChatChannel"/> bucket (All = no filter; Current = Say). The active tab is a filled
/// accent pill in a recessed track; tabs with messages newer than last-viewed show an unread dot. History-replay
/// messages (<see cref="ChatMessage.IsHistory"/>) never count as unread.
/// </summary>
public sealed partial class Plugin
{
    private enum ChatTab { All, World, Current, Party, Guild, Whisper, System }

    private static readonly ChatTab[] Tabs =
        { ChatTab.All, ChatTab.World, ChatTab.Current, ChatTab.Party, ChatTab.Guild, ChatTab.Whisper, ChatTab.System };

    private ChatTab _activeTab = ChatTab.All;

    // Unread bookkeeping. _newest = highest non-history MsgId seen per channel; _lastSeen = the _newest value
    // captured when a tab was last opened. A channel tab is unread when its channel's _newest exceeds the tab's
    // last-seen. Keyed off MsgId (not a bool) so it stays correct even if the framework log is a capped ring.
    private readonly Dictionary<ChatChannel, long> _newest = new();
    private readonly Dictionary<ChatTab, long> _lastSeen = new();

    // Tab -> the ChatChannel it filters to (All and anything unmapped => null = no filter / no unread).
    private static ChatChannel? TabChannel(ChatTab tab) => tab switch
    {
        ChatTab.World   => ChatChannel.World,
        ChatTab.Current => ChatChannel.Say,      // Say == the game's "Current"/Scene channel
        ChatTab.Party   => ChatChannel.Party,
        ChatTab.Guild   => ChatChannel.Guild,
        ChatTab.Whisper => ChatChannel.Whisper,
        ChatTab.System  => ChatChannel.System,
        _               => null,                 // All
    };

    private string TabLabelKey(ChatTab tab) => tab switch
    {
        ChatTab.All     => "chat.tab.all",
        ChatTab.World   => "chat.tab.world",
        ChatTab.Current => "chat.tab.current",
        ChatTab.Party   => "chat.tab.party",
        ChatTab.Guild   => "chat.tab.guild",
        ChatTab.Whisper => "chat.tab.whisper",
        _               => "chat.tab.system",
    };

    // Seed last-seen from the messages already in the log at construction so the login history replay doesn't
    // light up every tab as "unread" on first open.
    private void SeedUnread()
    {
        var m = _chat.RecentMessages;
        for (int i = 0; i < m.Count; i++)
        {
            var msg = m[i];
            if (msg.IsHistory || IsUnrenderable(msg)) continue;   // hidden rows (.Log.cs) never seed unread — they're never shown
            if (msg.MsgId > NewestFor(msg.Channel)) _newest[msg.Channel] = msg.MsgId;
        }
        foreach (var tab in Tabs)
            if (TabChannel(tab) is { } ch) _lastSeen[tab] = NewestFor(ch);
    }

    private long NewestFor(ChatChannel ch) => _newest.TryGetValue(ch, out var v) ? v : 0L;

    // Called from the inbound handler for every received message (unread + log-dirty signal).
    private void NoteIncoming(ChatMessage msg)
    {
        _logDirty = true;   // .Log.cs — force the filtered view to rebuild (even a hidden msg can front-drop the 500-ring + shift indices)
        // Hidden rows (.Log.cs IsUnrenderable) and history never contribute to unread — they never occupy a log row.
        // Mirror the IsHistory early-return: bail BEFORE touching _newest/_lastSeen, leaving _logDirty set as above.
        if (IsUnrenderable(msg)) return;
        // Every SHOWN text body — login history included (the history batches are the login freeze) — is handed to the
        // framework's TMP glyph prewarm NOW, so its missing glyphs rasterize a few per frame before the row is first
        // laid out, instead of all inside one window apply. Sticker rows draw no text body. Best-effort hint only.
        if (msg.StickerId == 0 && !string.IsNullOrEmpty(msg.Text)) _services.Windows.PrewarmText(msg.Text);
        if (msg.IsHistory) return;
        NoteNewBelow(msg);   // .NewBelow.cs — counts it for the "↓ N new messages" bar if scrolled up + in the active tab
        if (msg.MsgId > NewestFor(msg.Channel)) _newest[msg.Channel] = msg.MsgId;
        // The active tab is "being viewed", so a message for it is seen immediately (never shows unread).
        if (TabChannel(_activeTab) is { } active && active == msg.Channel && msg.MsgId > SeenFor(_activeTab))
            _lastSeen[_activeTab] = msg.MsgId;
    }

    private long SeenFor(ChatTab tab) => _lastSeen.TryGetValue(tab, out var v) ? v : 0L;

    private bool HasUnread(ChatTab tab)
        => TabChannel(tab) is { } ch && NewestFor(ch) > SeenFor(tab);

    private void SetActiveTab(ChatTab tab)
    {
        if (tab != _activeTab) { _tabResetPending = true; ResetNewBelow(); }   // .Log.cs — re-pin the log to bottom + re-measure heights (ResetScroll); .NewBelow.cs — new tab, nothing pending below
        _activeTab = tab;
        if (TabChannel(tab) is { } ch) _lastSeen[tab] = NewestFor(ch);   // opening a tab clears its unread
        OnTabOpened(tab);   // .Composer.cs — default the composer send-target to the tab's channel
    }

    // The tab strip: All · World · Current · Party · Guild · Whisper · System — rendered as a SEGMENTED-PILL control
    // (replaces the old underline/column design). The whole row sits in a recessed rounded BubbleElement "track" —
    // the same darker-fill treatment the sticker picker uses for its tab strip, so the two strips read as one
    // window. Each tab is an EQUAL-WIDTH segment: a CellElement with Weight:1 (NOT a fixed Width), so all 7 tabs
    // split the bar evenly and shrink to fit ANY window width — they can never overflow, and there is no "run the
    // window wide" caveat any more. The active tab is a filled accent pill; inactive tabs are transparent segments
    // with legible MUTED text. (Inactive text stays clearly readable on purpose — the whole redesign is to stop
    // inactive tabs reading as dead, un-clickable text, so they are MenuMuted, not dropped to a near-invisible alpha.)
    //
    // Each segment: CellElement(Weight:1) → SelectableElement (click = SetActiveTab, Selected = active) → a
    // BubbleElement holding a centred label. The pill FILL spans the full segment (rather than hugging the label)
    // because both CellElement and the SelectableElement host force-expand their child's width (WindowBuilder-
    // Patterns.md "force-expand"); the 7 filled segments then read as one continuous segmented control inside the
    // track. The fill colour resolves per-draw via the framework's new BubbleElement.FillColorFn — MenuAccent when
    // the tab is active, a fully-transparent ColorRgba otherwise (an explicit transparent, NOT null — null would
    // fall back to the bubble's themed default accent fill). Label text, the unread " •" suffix and the text colour
    // (bright MenuText on the active pill, MenuMuted elsewhere) all resolve through ()=> funcs, so a live language /
    // theme / unread / active-tab change shows with no element-tree rebuild (the tree is built once — see Plugin.cs).
    private HudElement BuildTabBar()
    {
        var cells = new List<HudElement>(Tabs.Length);
        foreach (var t in Tabs)
        {
            var tab = t;   // capture
            cells.Add(new CellElement(
                new SelectableElement(
                    new BubbleElement(
                        new TextElement(
                            () => HasUnread(tab) ? _loc.T(TabLabelKey(tab)) + " •" : _loc.T(TabLabelKey(tab)),
                            () => _activeTab == tab
                                ? _services.Theme.Colors.MenuText     // bright white on the accent pill
                                : _services.Theme.Colors.MenuMuted,   // legible muted on the transparent segment
                            Align: TextAlign.Center))
                    {
                        CornerRadius = 8f, PaddingX = 4f, PaddingY = 7f,
                        // The pill itself: accent fill when active, transparent otherwise. Returning an explicit
                        // transparent (NOT null) is required — null falls back to the bubble's themed default fill.
                        FillColorFn = () => _activeTab == tab
                            ? _services.Theme.Colors.MenuAccent
                            : new ColorRgba(0f, 0f, 0f, 0f),
                    },
                    // No Selected: arg — the BubbleElement.FillColorFn pill is the SOLE active indicator. Passing
                    // Selected here made the Selectable draw its OWN "stronger accent fill" behind the pill too, so the
                    // active tab rendered two overlapping accent layers (the doubled-pill look, magenta bleeding below/
                    // left). Hover feedback is unaffected — the Selectable's hover wash is independent of Selected.
                    OnClick: () => SetActiveTab(tab)),
                Weight: 1f));
        }
        // Recessed track: the whole row in a subtly-darker rounded bubble (the sticker picker's tab-strip treatment).
        // Dropped into BuildRoot's Column it force-expands to the full window width; the Weight:1 cells then split it.
        return new BubbleElement(new RowElement(cells.ToArray(), Gap: 3f))
            { FillColor = new ColorRgba(0f, 0f, 0f, 0.18f), CornerRadius = 10f, PaddingX = 4f, PaddingY = 4f };
    }
}
