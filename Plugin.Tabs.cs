using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Channel READ-tabs — a NEW concept distinct from the composer's send-target. A row of tab buttons filters
/// the log to one <see cref="ChatChannel"/> bucket (All = no filter; Current = Say). The active tab is
/// highlighted; tabs with messages newer than last-viewed show an unread dot. History-replay messages
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

    // Cyan accent under the tab strip (matches the game's segmented-tab underline). A full-width 3px bar; the
    // SDK has no colour-bindable line primitive, so BarElement at fraction 1.0 with a fixed cyan fill is the
    // closest — see the escalation report (a per-tab underline would need a new framework element).
    private static readonly ColorRgba TabAccent = new(0.30f, 0.82f, 1f, 0.95f);

    // Uniform tab width (canvas px) so the strip reads as an even segmented bar, not a row of label-sized chips.
    // Sized to fit the WIDEST label — "Whisper"/"Current" (and a "World ▾" caret) PLUS the " •" unread suffix —
    // inside the button's fixed-width box: a ButtonElement's content area is Width minus 18px of HLG padding (9px
    // each side) at the 11px menu font, and an over-long label WRAPS to a 2nd line (it does not clip), so the width
    // leaves a few px of slack to keep every tab one line tall. NB: 7 tabs × this width (≈516px incl. 2px gaps)
    // exceeds the 440px default window, so the strip overflows a narrow window rather than shrinking a tab below its
    // label — the user runs the window wide. The active-tab cyan accent and unread dots are width-independent.
    private const float TabWidth = 72f;

    // The tab strip: All · World · Current · Party · Guild · Whisper · System, over the cyan accent line.
    // Active tab highlighted (ButtonElement.Active → accent fill); a trailing bullet marks unread. Labels +
    // the dot resolve at draw time, so a live language switch and live unread state both show without a rebuild.
    private HudElement BuildTabBar()
    {
        var buttons = new List<HudElement>(Tabs.Length);
        foreach (var t in Tabs)
        {
            var tab = t;   // capture
            buttons.Add(new ButtonElement(
                () => HasUnread(tab) ? _loc.T(TabLabelKey(tab)) + " •" : _loc.T(TabLabelKey(tab)),
                () => SetActiveTab(tab),
                Active: () => _activeTab == tab,
                Width: TabWidth));
        }
        return new ColumnElement(new HudElement[]
        {
            new RowElement(buttons.ToArray(), Gap: 2f),
            new BarElement(() => 1f, TabAccent) { Height = 3f, FillWidth = true },
        }, Gap: 2f);
    }
}
