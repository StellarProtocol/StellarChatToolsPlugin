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
    private const float  UnreadDotGap      = 2f;     // label → dot-slot gap = the tab row's own Gap (BuildTabLabel)
    private const float  TabPillAlpha      = 0.25f;  // active pill = channel colour at this alpha
    private const float  TabMutedMix       = 0.55f;  // inactive text = 55% channel colour, 45% window background
    private const float  TabActiveMinContrast = 4.5f;  // WCAG AA body text — active label vs its pill
    private const float  TabMutedMinContrast  = 3.0f;  // WCAG AA large/UI — muted label vs the track
    // The recessed track's fill (also the background the contrast floors are measured against).
    private static readonly ColorRgba TabTrackFill = new(0f, 0f, 0f, 0.18f);
    private static readonly ColorRgba Transparent  = new(0f, 0f, 0f, 0f);

    private byte[]? _unreadDotPng;   // embedded unread-dot icon, read once
    private bool _unreadDotTried;

    // Unread bookkeeping. _newest = the ARRIVAL sequence number of the newest live (non-history) shown message per
    // channel; _lastSeen = the _newest value captured when a tab was last opened. A channel tab is unread when its
    // channel's _newest exceeds the tab's last-seen. A plugin-side sequence, NOT MsgId: system notices carry a
    // framework-made NEGATIVE MsgId (never "newer" than 0) and whisper MsgIds are per-peer, not monotonic. Messages
    // already in the log at construction never got a sequence number, so the login history can't light a tab.
    private readonly Dictionary<ChatChannel, long> _newest = new();
    private readonly Dictionary<ChatTab, long> _lastSeen = new();
    private long _arrivalSeq;

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

    private long NewestFor(ChatChannel ch) => _newest.TryGetValue(ch, out var v) ? v : 0L;

    // Called from the inbound handler for every received message (unread + log-append signal).
    private void NoteIncoming(ChatMessage msg)
    {
        QueueLogAppend(msg);   // .Log.cs — append it to the view next frame (history / out-of-order → full rebuild)
        // Hidden rows (.Log.cs IsUnrenderable) and history never contribute to unread — they never occupy a log row.
        // Mirror the IsHistory early-return: bail BEFORE touching _newest/_lastSeen (the log append above already ran).
        if (IsUnrenderable(msg)) return;
        // Every SHOWN text body — login history included (the history batches are the login freeze) — is handed to the
        // framework's TMP glyph prewarm NOW, so its missing glyphs rasterize a few per frame before the row is first
        // laid out, instead of all inside one window apply. Sticker rows draw no text body. Best-effort hint only.
        if (msg.StickerId == 0 && !string.IsNullOrEmpty(msg.Text)) _services.Windows.PrewarmText(ChatMarkup.SafeBody(msg));   // the text the row will draw
        if (msg.IsHistory) return;
        NoteNewBelow(msg);   // .NewBelow.cs — counts it for the "↓ N new messages" bar if scrolled up + in the active tab
        long seq = ++_arrivalSeq;
        _newest[msg.Channel] = seq;
        // The active tab is "being viewed", so a message for it is seen immediately (never shows unread). All shows
        // EVERY channel, so while it is open the message's own channel tab counts as seen too — otherwise reading
        // everything in All would still leave each channel tab lit. (A message arriving while a DIFFERENT channel
        // tab is open still lights its own tab.)
        var seenTab = _activeTab == ChatTab.All ? ChannelTab(msg.Channel) : _activeTab;
        if (seenTab is { } t && TabChannel(t) == msg.Channel)
            _lastSeen[t] = seq;
    }

    // Reverse of TabChannel: the channel tab that shows ch (null for a channel with no tab, e.g. Unknown).
    private static ChatTab? ChannelTab(ChatChannel ch)
    {
        foreach (var t in Tabs)
            if (TabChannel(t) == ch) return t;
        return null;
    }

    private long SeenFor(ChatTab tab) => _lastSeen.TryGetValue(tab, out var v) ? v : 0L;

    // Whisper is per PERSON (.Whisper.cs): its tab lights while any conversation is unread — server counts included —
    // and clears per conversation as each is opened, not when the tab (or All) merely shows the message.
    private bool HasUnread(ChatTab tab)
        => tab == ChatTab.Whisper ? AnyConversationUnread()
         : TabChannel(tab) is { } ch && NewestFor(ch) > SeenFor(tab);

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
    // picker uses for its tab strip, so the two strips read as one window. Each tab is a CellElement with Weight:1
    // (NOT a fixed Width), so the 7 tabs fill the bar and shrink to fit ANY window width — they can never overflow.
    // Weight:1 is NOT an equal split, though: each segment is its content's preferred width + an equal share of the
    // leftover, so segments differ per label. What matters is that each segment's width never changes with state.
    //
    // Each segment: CellElement(Weight:1) → SelectableElement (click = SetActiveTab) → ONE BubbleElement whose content
    // row is identical in every state (BuildTabLabel). Active vs inactive changes ONLY colours, through ()=> funcs: the
    // pill fill (channel tint vs transparent) and the label colour (full vs muted). Every label is bold, so the old
    // two-bubble ConditionalElement switch (bold active / regular inactive) is gone — and with it the drift: the two
    // structurally different variants measured 16 px (~10 units) apart in-game (46b7bbf), because the inactive
    // variant's gap + dot slot did not reach the bubble's measured width while the active variant's spacers did. The
    // pill FILL spans the full segment because both CellElement and the SelectableElement host force-expand their
    // child's width. Colours and labels resolve through funcs, so a live language / theme / unread / active-tab change
    // shows with no element-tree rebuild (the tree is built once — see Plugin.cs) and no per-refresh string allocation
    // (the label is the catalog string as-is).
    private HudElement BuildTabBar()
    {
        var cells = new List<HudElement>(Tabs.Length);
        foreach (var t in Tabs)
        {
            var tab = t;   // capture
            cells.Add(new CellElement(
                new SelectableElement(
                    new BubbleElement(BuildTabLabel(tab))
                    {
                        // FillColor is the build-time (= restore-default) fill: explicit transparent, NOT null — null
                        // falls back to the bubble's themed accent fill. FillColorFn re-pulls the channel pill every
                        // refresh, so a Theme slot edit re-tints it live and inactive tabs stay see-through.
                        CornerRadius = 8f, PaddingX = 2f, PaddingY = 7f, FillColor = Transparent,
                        FillColorFn = () => _activeTab == tab ? TabPillFill(tab) : Transparent,
                    },
                    // No Selected: arg — the bubble's pill is the SOLE active indicator. Passing Selected made the
                    // Selectable draw its OWN accent fill behind the pill too (the doubled-pill look). Hover feedback is
                    // unaffected — the Selectable's hover wash is independent of Selected.
                    OnClick: () => SetActiveTab(tab)),
                Weight: 1f));
        }
        // Recessed track: the whole row in a subtly-darker rounded bubble (the sticker picker's tab-strip treatment).
        // Dropped into BuildRoot's Column it force-expands to the full window width; the Weight:1 cells then split it.
        // ConstrainsWindowMinWidth: the window can't be resized narrower than the tabs' natural width in the CURRENT
        // language (≈622 en / ≈689 fil at 2560x1440), and a saved narrower layout is widened on open — no
        // per-language MinWidth table. The pills inside then hug their natural width rather than squeezing.
        return new BubbleElement(new RowElement(cells.ToArray(), Gap: 3f) { ConstrainsWindowMinWidth = true })
            { FillColor = TabTrackFill, CornerRadius = 10f, PaddingX = 4f, PaddingY = 4f };
    }

    // The tab's content row, the SAME in every state: [left spacer = dot width] gap [bold label] gap [dot slot]. The
    // left side (spacer + gap) mirrors the right (gap + dot slot), so the label stays centred on its pill. The toolkit
    // has no overlay/absolute-position element, so the dot sits inline just AFTER the label, raised toward the cap line.
    //
    // ⚠️ The row's Gap IS the label→dot gap, and it must be non-zero: the framework treats RowElement Gap == 0 as "use
    // the default RowGap" (8), NOT as zero spacing. The old row passed Gap: 0f plus explicit gap spacers, so it really
    // had 8 between EVERY child — left inset 10+8 vs right 8+2+8+8 → the label sat exactly 4 left of centre on every
    // tab (measured in-game: 26 vs 34 insets incl. the Selectable/bubble padding). Spacing via Gap puts exactly one gap
    // on each side of the label, so there are no gap spacers.
    //
    // Every width in this row comes from a SpacerElement with Width set (pins min AND preferred, flexible 0), the label,
    // or the fixed-width dot cell — never from the unread-dot ConditionalElement. The dot column is
    // [Cond(dot) ; Spacer(UnreadDotSize x 5)]: a Column's preferred/min width is the MAX of its children, the spacer
    // always reports UnreadDotSize, and both Cond branches (8 px image / 8 px spacer) are ≤ that — so the column is
    // exactly UnreadDotSize wide whether the dot is shown, hidden, or its branch not yet lazily built. That matters
    // because the Cond IS inside the bubble's measured child (WindowBuilder-Patterns.md warns a SetActive toggle there
    // can be read on stale layout): with its width dominated by an always-present spacer, a stale read can't change the
    // measured width. The spacer below the dot also lifts it above the label's centre line, and its Width keeps the
    // slot from being squeezed (an ImageElement only sets a preferred size).
    //
    // The column sits in a CellElement(Width) because a ColumnElement's force-expand VLG reports flexibleWidth = 1 up
    // to its parent: bare in this row it would soak up ALL the leftover whenever the weighted tab cell is wider than its
    // content, pushing the label left by half that slack. The fixed-width cell's LayoutElement (flexible 0) outranks the
    // VLG's report, so the row has no flexible child and Justify.Center centres the whole content in the pill.
    //
    // The active tab never shows the dot (its channel is the one being viewed, so HasUnread is always false for it —
    // SetActiveTab / NoteIncoming keep its last-seen current); the !active guard just makes it so.
    private HudElement BuildTabLabel(ChatTab tab) => new RowElement(new HudElement[]
    {
        new SpacerElement(Width: UnreadDotSize),
        new TextElement(() => _loc.T(TabLabelKey(tab)),
            () => _activeTab == tab ? TabActiveText(tab) : TabMutedText(tab),
            Emphasis: true, Align: TextAlign.Center),   // every tab is bold — state reads from colour + the pill
        new CellElement(new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _activeTab != tab && HasUnread(tab),
                // Row wrapper so the fixed-size image isn't width-stretched (WindowBuilder-Patterns.md force-expand).
                new RowElement(new HudElement[] { new ImageElement(UnreadDotPng, UnreadDotSize, UnreadDotSize) }),
                new SpacerElement(Width: UnreadDotSize, Height: UnreadDotSize)),
            new SpacerElement(Width: UnreadDotSize, Height: 5f),
        }), Width: UnreadDotSize),
    }, Gap: UnreadDotGap, Justify: RowJustify.Center);
}
