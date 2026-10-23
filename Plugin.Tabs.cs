using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Channel READ-tabs — a NEW concept distinct from the composer's send-target. A segmented-pill tab bar filters
/// the log to one <see cref="ChatChannel"/> bucket (All = no filter; Current = Say). Each tab is tinted with its
/// channel's colour (the same theme slots the message rows use; All = the theme accent). Every label is bold; the
/// active tab is the full colour on a low-alpha pill of it, inactive tabs are a muted shade of it. Tabs with messages newer than last-viewed
/// show a small red unread-dot icon. History-replay messages (<see cref="ChatMessage.IsHistory"/>) never count as
/// unread, and while All is open every shown message counts as seen for its channel tab.
/// </summary>
public sealed partial class Plugin
{
    private enum ChatTab { All, World, Current, Party, Guild, Whisper, System }

    private static readonly ChatTab[] Tabs =
        { ChatTab.All, ChatTab.World, ChatTab.Current, ChatTab.Party, ChatTab.Guild, ChatTab.Whisper, ChatTab.System };

    private ChatTab _activeTab = ChatTab.All;

    private const string UnreadDotResource = "Stellar.ChatTools.Assets.unread_dot.png";
    private const int    UnreadDotSize     = 8;      // display px of the 32x32 dot PNG
    private const float  TabPillAlpha      = 0.25f;  // active pill = channel colour at this alpha
    private const float  TabMutedMix       = 0.55f;  // inactive text = 55% channel colour, 45% window background
    private const float  TabActiveMinContrast = 4.5f;  // WCAG AA body text — active label vs its pill
    private const float  TabMutedMinContrast  = 3.0f;  // WCAG AA large/UI — muted label vs the track
    // The recessed track's fill (also the background the contrast floors are measured against).
    private static readonly ColorRgba TabTrackFill = new(0f, 0f, 0f, 0.18f);
    private static readonly ColorRgba Transparent  = new(0f, 0f, 0f, 0f);

    private byte[]? _unreadDotPng;   // embedded unread-dot icon, read once
    private bool _unreadDotTried;

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
        // The active tab is "being viewed", so a message for it is seen immediately (never shows unread). All shows
        // EVERY channel, so while it is open the message's own channel tab counts as seen too — otherwise reading
        // everything in All would still leave each channel tab lit. (A message arriving while a DIFFERENT channel
        // tab is open still lights its own tab.)
        var seenTab = _activeTab == ChatTab.All ? ChannelTab(msg.Channel) : _activeTab;
        if (seenTab is { } t && TabChannel(t) == msg.Channel && msg.MsgId > SeenFor(t))
            _lastSeen[t] = msg.MsgId;
    }

    // Reverse of TabChannel: the channel tab that shows ch (null for a channel with no tab, e.g. Unknown).
    private static ChatTab? ChannelTab(ChatChannel ch)
    {
        foreach (var t in Tabs)
            if (TabChannel(t) == ch) return t;
        return null;
    }

    private long SeenFor(ChatTab tab) => _lastSeen.TryGetValue(tab, out var v) ? v : 0L;

    private bool HasUnread(ChatTab tab)
        => TabChannel(tab) is { } ch && NewestFor(ch) > SeenFor(tab);

    private void SetActiveTab(ChatTab tab)
    {
        if (tab != _activeTab) { _tabResetPending = true; ResetNewBelow(); }   // .Log.cs — re-pin the log to bottom + re-measure heights (ResetScroll); .NewBelow.cs — new tab, nothing pending below
        _activeTab = tab;
        if (TabChannel(tab) is { } ch) _lastSeen[tab] = NewestFor(ch);   // opening a tab clears its unread
        else                                                               // opening All = everything is seen
            foreach (var t in Tabs)
                if (TabChannel(t) is { } c) _lastSeen[t] = NewestFor(c);
        OnTabOpened(tab);   // .Composer.cs — default the composer send-target to the tab's channel
    }

    // ── Tab colours ───────────────────────────────────────────────────────────────────────────────────────────────
    // A tab's base colour = its channel's theme slot (the SAME IColorSlot the message rows use, read live so a Theme
    // edit re-tints the tab with no rebuild); All has no channel, so it takes the theme accent.
    private ColorRgba TabBaseColor(ChatTab tab)
        => TabChannel(tab) is { } ch && ChannelColor(ch) is { } c ? c : _services.Theme.Colors.MenuAccent;

    // The opaque colour the tab text actually sits on: the recessed track (black @ 0.18) over the window background.
    private ColorRgba TabTrackColor() => Over(TabTrackFill, _services.Theme.Colors.MenuBackground);

    // Active label: the FULL channel colour, bold, on its own low-alpha pill. Contrast-floored against that pill —
    // the default World purple / Whisper violet (and Crimson's accent, and most Light-preset slots) measure below
    // 4.5:1 on their own tinted pill, so they are lifted toward white (darkened toward black on a light theme) for
    // TAB TEXT ONLY. The theme slots themselves are untouched, so message rows keep the exact user/theme colour.
    private ColorRgba TabActiveText(ChatTab tab)
    {
        var c = TabBaseColor(tab);
        return EnsureContrast(c, Over(c with { A = TabPillAlpha }, TabTrackColor()), TabActiveMinContrast);
    }

    // Inactive label: the channel colour BLENDED toward the window background (55% channel / 45% bg) as an OPAQUE
    // colour, not the same colour at 55% alpha. Alpha text would composite against whatever is behind the (possibly
    // translucent) chat window — the game world — so its legibility would swing with the scene; an explicit blend is
    // deterministic and lets the contrast floor below be computed exactly. Floored at 3:1 (WCAG non-text/large) vs
    // the track so inactive tabs stay clearly clickable rather than reading as dead text.
    private ColorRgba TabMutedText(ChatTab tab)
    {
        var bg = _services.Theme.Colors.MenuBackground;
        return EnsureContrast(Mix(TabBaseColor(tab), bg, 1f - TabMutedMix), TabTrackColor(), TabMutedMinContrast);
    }

    private ColorRgba? TabPillFill(ChatTab tab) => TabBaseColor(tab) with { A = TabPillAlpha };

    // Nudges fg toward white (dark background) or black (light background) in 15% steps until it reaches minRatio
    // against the opaque bg. Pure struct math — no allocation on the per-refresh colour funcs.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        // 0.179 = the luminance where white and black give equal contrast; pick whichever end gains more.
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);

    // WCAG relative luminance (sRGB → linear).
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    private byte[]? UnreadDotPng()
    {
        if (_unreadDotTried) return _unreadDotPng;
        _unreadDotTried = true;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(UnreadDotResource);
            if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); _unreadDotPng = ms.ToArray(); }
        }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] unread dot icon load failed: {ex.Message}"); }
        return _unreadDotPng;
    }

    // The tab strip: All · World · Current · Party · Guild · Whisper · System — rendered as a SEGMENTED-PILL control.
    // The whole row sits in a recessed rounded BubbleElement "track" — the same darker-fill treatment the sticker
    // picker uses for its tab strip, so the two strips read as one window. Each tab is an EQUAL-WIDTH segment: a
    // CellElement with Weight:1 (NOT a fixed Width), so all 7 tabs split the bar evenly and shrink to fit ANY window
    // width — they can never overflow.
    //
    // Each segment: CellElement(Weight:1) → SelectableElement (click = SetActiveTab) → ConditionalElement(active) →
    // one of TWO complete BubbleElements: the ACTIVE bubble (channel-colour pill via FillColorFn, full-colour label,
    // NO unread-dot slot) or the INACTIVE bubble (static transparent fill, muted label + the constant-width dot slot).
    // Both labels are bold. One bubble with Func fill/colour can't replace the pair: the dot slot exists only in the
    // inactive state, and toggling it would be exactly the width-swinging inner Cond warned about next. The Cond wraps the WHOLE bubble rather than sitting inside it — a SetActive-toggled
    // Cond among a bubble's width-measured descendants makes it re-solve on stale layout (WindowBuilder-Patterns.md
    // "Never put a ConditionalElement INSIDE the bubble's measured child"). The pill FILL spans the full segment
    // because both CellElement and the SelectableElement host force-expand their child's width (the host also
    // force-expands the Cond, so it fills exactly like the bare bubble did). Colours and labels resolve through ()=>
    // funcs, so a live language / theme / unread / active-tab change shows with no element-tree rebuild (the tree is
    // built once — see Plugin.cs) and no per-refresh string allocation (the label is the catalog string as-is).
    private HudElement BuildTabBar()
    {
        var cells = new List<HudElement>(Tabs.Length);
        foreach (var t in Tabs)
        {
            var tab = t;   // capture
            cells.Add(new CellElement(
                new SelectableElement(
                    new ConditionalElement(() => _activeTab == tab,
                        new BubbleElement(BuildTabLabel(tab, active: true))
                        {
                            CornerRadius = 8f, PaddingX = 2f, PaddingY = 7f,
                            // The channel-colour pill. Re-pulled every refresh so a Theme slot edit re-tints it live.
                            FillColorFn = () => TabPillFill(tab),
                        },
                        new BubbleElement(BuildTabLabel(tab, active: false))
                        {
                            // Explicit transparent (NOT null) — null falls back to the bubble's themed accent fill.
                            CornerRadius = 8f, PaddingX = 2f, PaddingY = 7f, FillColor = Transparent,
                        }),
                    // No Selected: arg — the active bubble's pill is the SOLE active indicator. Passing Selected made the
                    // Selectable draw its OWN accent fill behind the pill too (the doubled-pill look). Hover feedback is
                    // unaffected — the Selectable's hover wash is independent of Selected.
                    OnClick: () => SetActiveTab(tab)),
                Weight: 1f));
        }
        // Recessed track: the whole row in a subtly-darker rounded bubble (the sticker picker's tab-strip treatment).
        // Dropped into BuildRoot's Column it force-expands to the full window width; the Weight:1 cells then split it.
        return new BubbleElement(new RowElement(cells.ToArray(), Gap: 3f))
            { FillColor = TabTrackFill, CornerRadius = 10f, PaddingX = 4f, PaddingY = 4f };
    }

    // Label + unread-dot slot, centred as a group. The toolkit has no overlay/absolute-position element, so the dot
    // sits just AFTER the label, raised toward the cap line (superscript-style). Its slot is a CONSTANT width in both
    // states (the dot branch and the empty-spacer branch both reserve UnreadDotSize, min AND preferred), so the dot
    // appearing never re-flows the label — only a fixed ~10 px is reserved, which the tightened bubble PaddingX (4 → 2)
    // mostly pays for at the 360 px minimum window width. This Cond IS inside the bubble, but both branches report the
    // same width, so the bubble's measured width cannot swing when it toggles.
    //
    // The ACTIVE variant gets NO dot slot: its channel is the one being viewed, so HasUnread is always false for it
    // (SetActiveTab / NoteIncoming keep its last-seen current). Reserving the slot there pushed the label ~5 px
    // left of the pill's centre. Dropping it can't shift neighbours — the segment width comes from the Weight:1 cell,
    // not from this content.
    private HudElement BuildTabLabel(ChatTab tab, bool active)
    {
        var label = new TextElement(() => _loc.T(TabLabelKey(tab)),
            active ? () => TabActiveText(tab) : () => TabMutedText(tab),
            Emphasis: true, Align: TextAlign.Center);   // every tab is bold — state reads from colour + the pill
        if (active) return new RowElement(new HudElement[] { label }, Justify: RowJustify.Center);
        return BuildInactiveTabLabel(tab, label);
    }

    private HudElement BuildInactiveTabLabel(ChatTab tab, HudElement label) => new RowElement(new HudElement[]
    {
        label,
        new ConditionalElement(() => HasUnread(tab),
            new ColumnElement(new HudElement[]
            {
                // Row wrapper so the fixed-size image isn't width-stretched (WindowBuilder-Patterns.md force-expand).
                new RowElement(new HudElement[] { new ImageElement(UnreadDotPng, UnreadDotSize, UnreadDotSize) }),
                // Spacer below lifts the dot above the label's centre line; its Width also pins the slot's MIN width
                // (an ImageElement only sets a preferred width, so a tight row could otherwise squeeze the dot).
                new SpacerElement(Width: UnreadDotSize, Height: 5f),
            }),
            new SpacerElement(Width: UnreadDotSize, Height: UnreadDotSize)),
    }, Gap: 2f, Justify: RowJustify.Center);
}
