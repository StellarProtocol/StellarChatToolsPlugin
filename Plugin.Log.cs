using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The rolling chat log: per-channel colour slots, a recycled bubble POOL, the active-tab filter, and the
/// per-message accessors the bubble rows read. Rows are a <see cref="VirtualListElement"/> (variable-height,
/// stick-to-bottom) windowed over the FILTERED list — only ~viewport rows are materialized and recycled as you
/// scroll (the framework owns scroll windowing), newest at the bottom. Bubble rendering (avatar, speech bubble,
/// self/other alignment) lives in Plugin.Bubbles.cs; this file owns the data plumbing.
/// </summary>
public sealed partial class Plugin
{
    // Indices into _chat.RecentMessages that pass the active-tab filter, rebuilt only when dirty. Reused
    // across frames (Clear + re-add) so steady-state draw does not allocate.
    private readonly List<int> _filtered = new(MaxLogRows);
    private bool _logDirty = true;
    private ChatTab _lastFilterTab = (ChatTab)(-1);   // force first EnsureFiltered to build

    // The virtual list OWNS windowing now: OnWindow pushes the first visible FILTERED row here each poll, and
    // LogMsgIndex maps pool slot k -> filtered row _windowFirst + k. (Was a newest-MaxLogRows display window.)
    private int _windowFirst;

    // Recycled bubble pool size (K): ~viewport-worth of GameObjects, reused as you scroll (NOT display positions).
    // Sized for the log viewport at the window's MAX height (MaxHeight 1100 - title/tabs/composer/gaps ≈ 960px)
    // ÷ the ~28px shortest bubble ≈ 34 rows worst-case, + buffer for partial rows / scroll velocity. 40 keeps a
    // ~6-row margin over that 34 — safely above the "pool too small -> top of viewport blanks" caveat — while
    // cutting 10 live bubble GameObjects (and their per-frame layout/draw) vs the old 50. Do NOT drop below ~40
    // or the top of the viewport can blank at max height. Shrink further only if profiling shows it's safe.
    private const int LogPoolSize = 40;

    // Edge-triggered one-shot for VirtualListElement.ResetScroll: SetActiveTab (.Tabs.cs) sets it on a tab change;
    // consumed+cleared on the next poll so the new filter re-pins to the bottom and re-measures row heights.
    private bool _tabResetPending;
    private bool TakeTabResetPending()
    {
        if (!_tabResetPending) return false;
        _tabResetPending = false;
        ResetNewBelow();   // .NewBelow.cs — the re-pin lands at the bottom, so nothing is left unseen below
        return true;
    }

    private void RegisterColors()
    {
        var reg = _services.Theme.ColorRegistry;
        // Keep the existing keys for Party/Whisper so prior user overrides persist; add the four new channels.
        // Light preset gets a darker variant for contrast on a light panel; the three dark presets share one.
        IColorSlot Slot(string key, string labelKey, ColorRgba dark, ColorRgba light) =>
            reg.Register(key, _loc.T(labelKey), new Dictionary<ThemePreset, ColorRgba>
            {
                [ThemePreset.Default] = dark,
                [ThemePreset.Dark]    = dark,
                [ThemePreset.Crimson] = dark,
                [ThemePreset.Light]   = light,
            });

        _sayColor     = Slot("ChatTools.Channel.Say",     "chat.color.say",     new ColorRgba(0.86f, 0.90f, 0.92f), new ColorRgba(0.20f, 0.24f, 0.28f));
        _worldColor   = Slot("ChatTools.Channel.World",   "chat.color.world",   new ColorRgba(0.70f, 0.52f, 1.00f), new ColorRgba(0.42f, 0.28f, 0.66f));
        _partyColor   = Slot("ChatTools.Channel.Party",   "chat.color.party",   new ColorRgba(0.40f, 0.85f, 1.00f), new ColorRgba(0.07f, 0.42f, 0.66f));
        _guildColor   = Slot("ChatTools.Channel.Guild",   "chat.color.guild",   new ColorRgba(0.55f, 0.90f, 0.55f), new ColorRgba(0.14f, 0.46f, 0.16f));
        _whisperColor = Slot("ChatTools.Channel.Whisper", "chat.color.whisper", new ColorRgba(0.85f, 0.50f, 1.00f), new ColorRgba(0.52f, 0.18f, 0.72f));
        _systemColor  = Slot("ChatTools.Channel.System",  "chat.color.system",  new ColorRgba(1.00f, 0.80f, 0.45f), new ColorRgba(0.60f, 0.42f, 0.10f));
    }

    private HudElement BuildLogScroll()
    {
        var pool = new HudElement[LogPoolSize];
        for (int i = 0; i < LogPoolSize; i++) { var k = i; pool[k] = BuildBubbleSlot(k); }   // .Bubbles.cs — recycled pool slots
        // VirtualListElement materializes only ~LogPoolSize bubble GameObjects regardless of log length (the win over
        // the old eager ListElement that pre-built 200). VariableHeight: bubbles wrap to different heights, so each
        // slot is MEASURED; RowHeight 28 seeds the estimate for un-measured rows (the SHORTEST bubble height).
        // StickToBottom: newest at the bottom, auto-follows when at the bottom, doesn't yank you when scrolled up.
        // ResetScroll re-pins to bottom + forgets the measured-height model on a tab switch. The scroll host
        // flex-grows (BuildScrollViewport sets flexibleHeight=1), so the log still fills a resized window exactly
        // like the old ScrollElement; Height 200 is the viewport baseline.
        return new VirtualListElement(
            Count:     FilteredCount,
            RowHeight: 28f,
            Pool:      pool,
            OnWindow:  first => _windowFirst = first,
            Height:    200f)
        {
            VariableHeight = true,
            StickToBottom  = true,
            ResetScroll    = TakeTabResetPending,
            // Per-row identity so the variable list can tell when the 500-entry RecentMessages ring front-drops and
            // re-maps a filtered index to a DIFFERENT message at constant Count (which otherwise leaves a tall sticker
            // row sized by a short text row's stale cached height → permanent overlap). See StableRowId.
            RowId          = StableRowId,
            // "↓ N new messages" bar (.NewBelow.cs): the list reports its own at-bottom pin decision, and a bump of
            // the token jumps to the newest row + re-pins (following unmeasured bottom rows until they settle).
            OnAtBottomChanged     = OnLogAtBottomChanged,
            ScrollToBottomRequest = () => _scrollToBottomSeq,
        };
    }

    private ColorRgba? MutedCol() => _services.Theme.Colors.MenuMuted;

    // A message we cannot render: a non-text payload type (hyperlink/notice/voice/picture) that carries no
    // readable text and no sticker. The framework probe discards chat_hypertext/multi_lang_notice/voice, so these
    // leak an empty or punctuation-only msg_text fragment (e.g. a lone "(") with a blank sender + id 0. Hide them.
    // See Chat-System-Architecture.md §3-4 (known, pre-existing framework decode limitation — we only hide here).
    private static bool IsUnrenderable(in ChatMessage m)
    {
        if (m.StickerId != 0) return false;                  // whole-message sticker = renderable (StickerId surfaced)
        if (m.Type == ChatMessageType.Regular) return false; // real player text: never hide (even one char / punctuation)
        // Non-Regular types (System / ItemLink / Emote / Unknown): hide only when there is no readable glyph left.
        return !HasReadableText(m.Text);
    }

    // true if the string contains at least one letter or digit (CJK counts as a letter -> kept). Pure whitespace or
    // punctuation-only fragments (the "(" junk) return false.
    private static bool HasReadableText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        for (int i = 0; i < s.Length; i++) if (char.IsLetterOrDigit(s[i])) return true;
        return false;
    }

    // Rebuild the filtered index list when a message arrived (_logDirty) or the tab changed. Cheap no-op when
    // clean. Called from VisibleLogCount (which the list binding reads first each frame) and defensively from
    // the slot accessors, so every accessor sees the same list within a frame.
    private void EnsureFiltered()
    {
        if (!_logDirty && _activeTab == _lastFilterTab) return;
        _filtered.Clear();
        var ch = TabChannel(_activeTab);   // null = All (no filter)
        var m = _chat.RecentMessages;
        for (int i = 0; i < m.Count; i++)
        {
            var cm = m[i];
            if (IsUnrenderable(cm)) continue;                     // drop un-decodable special-type leftovers on EVERY tab (incl. All)
            if (ch == null || cm.Channel == ch.Value) _filtered.Add(i);
        }

        // RecentMessages is pure append/ARRIVAL order, and on login the server sends chat history batched PER
        // CHANNEL — each channel's whole time-span concatenated after the last → the ring is globally out of time
        // order (login dump looks scrambled). Timestamp is WIRE-sourced (Unix seconds → UTC, same domain live +
        // history), so stable-sort the filtered indices ascending by Timestamp to re-interleave them
        // chronologically: oldest first → newest last = bottom, matching StickToBottom. OrderBy is a STABLE sort,
        // so same-second ties keep arrival order (the indices were added above in ascending arrival order, which
        // IS monotonic with arrival). Live messages already arrive in time order → a no-op for them; only the
        // scrambled login burst gets reordered. Must materialize (ToArray) BEFORE Clear — OrderBy is lazy over
        // _filtered itself. ⚠️ Do NOT tie-break on MsgId: it is per-peer for whispers, not globally monotonic.
        var ordered = _filtered.OrderBy(i => m[i].Timestamp).ToArray();
        _filtered.Clear();
        _filtered.AddRange(ordered);

        _lastFilterTab = _activeTab;
        _logDirty = false;
    }

    // Total logical rows the virtual list windows over = the active tab's filtered set (NO display cap — the Pool
    // size, not this count, bounds live GameObjects now). Rebuilds the filtered view first if dirty.
    private int FilteredCount()
    {
        EnsureFiltered();
        return _filtered.Count;
    }

    // Pool slot -> index into RecentMessages. The virtual list owns windowing (_windowFirst = first visible
    // filtered row, pushed via OnWindow); pool slot k shows filtered row _windowFirst + k.
    private int LogMsgIndex(int poolSlot)
    {
        EnsureFiltered();
        int fi = _windowFirst + poolSlot;
        return (fi >= 0 && fi < _filtered.Count) ? _filtered[fi] : -1;
    }

    private bool TryMsg(int slot, out ChatMessage msg)
    {
        var m = _chat.RecentMessages;
        int i = LogMsgIndex(slot);
        if (i >= 0 && i < m.Count) { msg = m[i]; return true; }
        msg = default!;
        return false;
    }

    // Stable per-message identity for the variable list's height-cache verification (VirtualListElement.RowId).
    // ⚠️ The framework feeds the DATA index (= filtered-row index, _windowFirst-INDEPENDENT), NOT a pool slot — so
    // resolve it straight through _filtered here, never via TryMsg/LogMsgIndex (those add _windowFirst and would
    // read the wrong row). Every ChatMessage field is immutable, so this id is CONSTANT for a given message no
    // matter which ring index it drifts to as the 500-entry RecentMessages ring front-drops — which is exactly what
    // lets the list notice a data index now points at a different message and re-verify that row's cached height.
    // MsgId is server-unique globally EXCEPT whispers (per-peer, see EnsureFiltered's sort note), so fold in SenderId
    // (distinct per peer) plus Timestamp/StickerId/Text so a per-peer whisper #5 and another peer's #5 don't collide.
    // A rare collision only costs a missed verify (one stale latch survives a shift longer), never wrong behaviour.
    private long StableRowId(int dataIndex)
    {
        EnsureFiltered();
        if (dataIndex < 0 || dataIndex >= _filtered.Count) return 0L;
        var m = _chat.RecentMessages;
        int ri = _filtered[dataIndex];
        if (ri < 0 || ri >= m.Count) return 0L;
        var msg = m[ri];
        long id = 17L;
        id = id * 31L + msg.MsgId;
        id = id * 31L + msg.SenderId;
        id = id * 31L + msg.Timestamp.Ticks;
        id = id * 31L + msg.StickerId;
        id = id * 31L + (msg.Text?.GetHashCode() ?? 0);
        return id;
    }

    // Compact timestamp (HH:mm — the game drops seconds in its bubble layout).
    private string LogTime(int slot)
        => TryMsg(slot, out var m) ? m.Timestamp.ToLocalTime().ToString("HH:mm") : "";

    private string LogText(int slot) => TryMsg(slot, out var m) ? m.Text : "";

    // Sender name coloured by channel; System uses muted (no dedicated bright colour on a notice line).
    private ColorRgba? LogSenderColor(int slot)
    {
        if (!TryMsg(slot, out var m)) return null;
        return m.Channel == ChatChannel.System ? _services.Theme.Colors.MenuMuted : ChannelColor(m.Channel);
    }

    // Message body stays default text except System lines, which read as muted notices.
    private ColorRgba? LogMsgColor(int slot)
    {
        if (!TryMsg(slot, out var m)) return null;
        return m.Channel == ChatChannel.System ? _services.Theme.Colors.MenuMuted : (ColorRgba?)null;
    }

    private ColorRgba? ChannelColor(ChatChannel ch) => ch switch
    {
        ChatChannel.Say     => _sayColor.Value,
        ChatChannel.World   => _worldColor.Value,
        ChatChannel.Party   => _partyColor.Value,
        ChatChannel.Guild   => _guildColor.Value,
        ChatChannel.Whisper => _whisperColor.Value,
        ChatChannel.System  => _systemColor.Value,
        _                   => (ColorRgba?)null,   // Unknown -> framework default text colour
    };

    // Clicking a whisper row targets a reply to that sender (Phase 1 whisper = reply-based; no name->ID lookup
    // for fresh whispers yet). Non-whisper rows are inert.
    private void OnRowClick(int slot)
    {
        if (!TryMsg(slot, out var m)) return;
        if (m.Channel == ChatChannel.Whisper && m.SenderId != 0) SetReply(m);   // .Composer.cs
    }
}
