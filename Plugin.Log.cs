using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The rolling chat log: per-channel colour slots, the pre-built bubble slots, the active-tab filter, and the
/// per-message accessors the bubble rows read. Slots use the fixed-slot + windowed-index pattern applied to
/// the FILTERED list (filter first, then show the newest <see cref="MaxLogRows"/>). Bubble rendering (avatar,
/// speech bubble, self/other alignment) lives in Plugin.Bubbles.cs; this file owns the data plumbing.
/// </summary>
public sealed partial class Plugin
{
    // Indices into _chat.RecentMessages that pass the active-tab filter, rebuilt only when dirty. Reused
    // across frames (Clear + re-add) so steady-state draw does not allocate.
    private readonly List<int> _filtered = new(MaxLogRows);
    private bool _logDirty = true;
    private ChatTab _lastFilterTab = (ChatTab)(-1);   // force first EnsureFiltered to build

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
        _worldColor   = Slot("ChatTools.Channel.World",   "chat.color.world",   new ColorRgba(0.90f, 0.85f, 1.00f), new ColorRgba(0.42f, 0.28f, 0.66f));
        _partyColor   = Slot("ChatTools.Channel.Party",   "chat.color.party",   new ColorRgba(0.40f, 0.85f, 1.00f), new ColorRgba(0.07f, 0.42f, 0.66f));
        _guildColor   = Slot("ChatTools.Channel.Guild",   "chat.color.guild",   new ColorRgba(0.55f, 0.90f, 0.55f), new ColorRgba(0.14f, 0.46f, 0.16f));
        _whisperColor = Slot("ChatTools.Channel.Whisper", "chat.color.whisper", new ColorRgba(0.85f, 0.50f, 1.00f), new ColorRgba(0.52f, 0.18f, 0.72f));
        _systemColor  = Slot("ChatTools.Channel.System",  "chat.color.system",  new ColorRgba(1.00f, 0.80f, 0.45f), new ColorRgba(0.60f, 0.42f, 0.10f));
    }

    private HudElement BuildLogScroll()
    {
        var slots = new HudElement[MaxLogRows];
        for (int i = 0; i < MaxLogRows; i++) { var idx = i; slots[idx] = BuildBubbleSlot(idx); }   // .Bubbles.cs
        // Height here is the PREFERRED (baseline) height; in a Resizable window the scroll's flexibleHeight=1
        // lets it absorb the slack and grow with the window, so the log fills the freed vertical space.
        return new ScrollElement(new ListElement(VisibleLogCount, slots), 200f);
    }

    private ColorRgba? MutedCol() => _services.Theme.Colors.MenuMuted;

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
            if (ch == null || m[i].Channel == ch.Value) _filtered.Add(i);
        _lastFilterTab = _activeTab;
        _logDirty = false;
    }

    private int VisibleLogCount()
    {
        EnsureFiltered();
        return Math.Min(_filtered.Count, MaxLogRows);
    }

    // Slot -> index into RecentMessages, showing the newest MaxLogRows of the filtered set (newest at bottom).
    private int LogMsgIndex(int slot)
    {
        EnsureFiltered();
        int fc = _filtered.Count;
        int shown = Math.Min(fc, MaxLogRows);
        int fi = (fc - shown) + slot;
        return (fi >= 0 && fi < fc) ? _filtered[fi] : -1;
    }

    private bool TryMsg(int slot, out ChatMessage msg)
    {
        var m = _chat.RecentMessages;
        int i = LogMsgIndex(slot);
        if (i >= 0 && i < m.Count) { msg = m[i]; return true; }
        msg = default!;
        return false;
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
