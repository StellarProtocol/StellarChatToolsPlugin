using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Channel READ-tabs — a NEW concept distinct from the composer's send-target. A row of underline tabs filters
/// the log to one <see cref="ChatChannel"/> bucket (All = no filter; Current = Say). The active tab is marked by
/// an accent underline; tabs with messages newer than last-viewed show an unread dot. History-replay messages
/// (<see cref="ChatMessage.IsHistory"/>) never count as unread.
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
            if (msg.IsHistory) continue;
            if (msg.MsgId > NewestFor(msg.Channel)) _newest[msg.Channel] = msg.MsgId;
        }
        foreach (var tab in Tabs)
            if (TabChannel(tab) is { } ch) _lastSeen[tab] = NewestFor(ch);
    }

    private long NewestFor(ChatChannel ch) => _newest.TryGetValue(ch, out var v) ? v : 0L;

    // Called from the inbound handler for every received message (unread + log-dirty signal).
    private void NoteIncoming(ChatMessage msg)
    {
        _logDirty = true;   // .Log.cs — force the filtered view to rebuild even if the ring count is stable
        if (msg.IsHistory) return;
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
        if (tab != _activeTab) _tabResetPending = true;   // .Log.cs — re-pin the log to bottom + re-measure heights (ResetScroll)
        _activeTab = tab;
        if (TabChannel(tab) is { } ch) _lastSeen[tab] = NewestFor(ch);   // opening a tab clears its unread
        OnTabOpened(tab);   // .Composer.cs — default the composer send-target to the tab's channel
    }

    // Uniform tab width (canvas px) so the strip reads as an even row of tabs, not label-sized chips that jitter as
    // the active-label width changes. Each tab is a SelectableElement pinned to this width by a CellElement; the
    // Selectable's VLG adds ~6px of h-padding each side, so the content area ≈ TabWidth−12px at the 11px menu font —
    // room for the WIDEST label ("Whisper"/"Current") PLUS the " •" unread suffix. An over-long label WRAPS rather
    // than clips (NoWrap left off). Every tab reserves the SAME label+gap+underline height, so active vs inactive
    // differ only in the underline COLOUR, never in layout — a tab never changes height. NB: 7 tabs × this width
    // (≈516px incl. 2px gaps) exceeds the 440px default window, so the strip overflows a narrow window rather than
    // shrinking a tab below its label — the user runs the window wide.
    private const float TabWidth = 72f;

    // Resting (inactive) underline alpha, applied to MenuMuted — faint but visible, so every tab reads as a tab
    // rather than plain text. The ACTIVE tab swaps this faint line for the full-strength MenuAccent.
    private const float InactiveUnderlineAlpha = 0.35f;

    // The tab strip: All · World · Current · Party · Guild · Whisper · System — a classic underline tab-bar. (UX
    // feedback on the prior flat-tab version: inactive tabs were too dim and gave no "this is a tab" cue, and the
    // active underline sat right under the label.) Each tab is now an explicit COLUMN — a centred label, a 5px
    // SpacerElement gap, then a 3px underline BarElement present on EVERY tab — so the whole row reads as a tab
    // strip. Text is bright/white (MenuText) on ALL tabs, active AND inactive (never dimmed); the active tab is
    // distinguished by its underline alone — MenuAccent when active, a faint MenuMuted (alpha
    // InactiveUnderlineAlpha) at rest. The column is wrapped in a SelectableElement (NO SelectedBar — the per-tab
    // BarElement IS the indicator) so a click anywhere on the tab fires SetActiveTab, and the active tab also gets
    // the Selectable's subtle accent pill tint as an extra cue. Label, the unread " •" suffix, text colour and the
    // underline colour all resolve at draw time (the underline via BarElement.FillColorFn, honoured on the Default
    // bar path), so a live language switch, live unread, a live theme change, and the active-tab move all show
    // without an element-tree rebuild. CellElement pins each tab to TabWidth (uniform strip).
    private HudElement BuildTabBar()
    {
        var cells = new List<HudElement>(Tabs.Length);
        foreach (var t in Tabs)
        {
            var tab = t;   // capture
            cells.Add(new CellElement(
                new SelectableElement(
                    new ColumnElement(new HudElement[]
                    {
                        new TextElement(
                            () => HasUnread(tab) ? _loc.T(TabLabelKey(tab)) + " •" : _loc.T(TabLabelKey(tab)),
                            () => _services.Theme.Colors.MenuText,   // bright/white on every tab — never dimmed
                            Align: TextAlign.Center),
                        new SpacerElement(Height: 5f),               // "pad it more" — gap between label and underline
                        // Underline on EVERY tab (the "this is a tab" affordance): FillWidth spans the cell; the
                        // colour resolves per-tab at draw time — accent when active, faint muted at rest. Fill (the
                        // static ctor arg) is only a fallback; FillColorFn is what the Default bar path actually uses.
                        new BarElement(() => 1f, _services.Theme.Colors.MenuAccent)
                        {
                            Height = 3f,
                            FillWidth = true,
                            FillColorFn = () => _activeTab == tab
                                ? _services.Theme.Colors.MenuAccent
                                : FaintUnderline(_services.Theme.Colors.MenuMuted),
                        },
                    }, Gap: 0f),
                    OnClick: () => SetActiveTab(tab),
                    Selected: () => _activeTab == tab),   // subtle accent pill on the active tab; NO SelectedBar
                Width: TabWidth));
        }
        return new RowElement(cells.ToArray(), Gap: 2f);
    }

    // Faint resting-underline colour = the theme's muted token dropped to InactiveUnderlineAlpha. Takes the live
    // token so a theme change tracks.
    private static ColorRgba FaintUnderline(ColorRgba muted)
        => new(muted.R, muted.G, muted.B, muted.A * InactiveUnderlineAlpha);
}
