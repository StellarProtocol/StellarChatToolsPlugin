using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// ⚙ "All tab" channel filter: the user picks which channels the All tab shows (the approved prototype —
/// <c>_scratch/prototypes/all-tab-filter.html</c>). Only All is filtered; every channel tab is unchanged. A message
/// shows in All iff its channel is on, EXCEPT our own sends always show — sending to a hidden channel from All must not
/// look like the message vanished. Date separators and local notices (.DateSeparators.cs / .LocalNotice.cs) are merged
/// in after the filter, so they are always shown; real System-channel notices follow the System chip.
///
/// Persisted as the HIDDEN set (channel names, <see cref="CfgAllTabHidden"/>), not the shown set: a default config is
/// "nothing hidden" = no change for existing users, and a channel added in a later version defaults to shown. At least
/// one chip always stays on — clicking the last one is a no-op (an empty All tab reads as broken chat).
///
/// Unread: while All is open, a message only counts as seen for its channel tab when All actually SHOWS it
/// (.Tabs.cs NoteIncoming / SetActiveTab), and the "↓ N new" bar ignores hidden channels (.NewBelow.cs).
/// </summary>
public sealed partial class Plugin
{
    private const string CfgAllTabHidden = "allTab.hiddenChannels";
    private const float AllTabChipOffMix = 0.35f;   // off-chip text = muted grey pulled 35% toward the track (dimmed)

    // Chip order = the tab strip's order and labels (All itself excluded).
    private static readonly ChatTab[] AllTabChips =
        { ChatTab.World, ChatTab.Current, ChatTab.Party, ChatTab.Guild, ChatTab.Whisper, ChatTab.System };

    private readonly HashSet<ChatChannel> _allTabHidden = new();

    // From LoadConfig (.Config.cs). Stored by NAME so an enum renumber can't silently hide a different channel; unknown
    // names and channels without a chip are dropped. A hand-edited "hide everything" falls back to showing everything.
    private void LoadAllTabConfig()
    {
        _allTabHidden.Clear();
        var saved = _cfg.Get<string[]>(CfgAllTabHidden, null!);
        if (saved == null) return;
        foreach (var s in saved)
            if (Enum.TryParse<ChatChannel>(s, out var ch) && ChannelTab(ch) != null) _allTabHidden.Add(ch);
        if (_allTabHidden.Count >= AllTabChips.Length) _allTabHidden.Clear();
    }

    private void SaveAllTabConfig()
    {
        var names = new string[_allTabHidden.Count];
        int i = 0;
        foreach (var ch in _allTabHidden) names[i++] = ch.ToString();
        _cfg.Set(CfgAllTabHidden, names);
        _cfg.Save();
    }

    private bool AllTabShows(ChatChannel ch) => _allTabHidden.Count == 0 || !_allTabHidden.Contains(ch);

    // The All-tab test for one message. IsSelf only runs for a hidden channel (the common no-filter case is one Count check).
    private bool AllTabShows(ChatMessage m) => AllTabShows(m.Channel) || IsSelf(m);   // .Bubbles.cs

    private void ToggleAllTabChannel(ChatChannel ch)
    {
        bool shown = !_allTabHidden.Remove(ch);
        if (shown)
        {
            if (_allTabHidden.Count >= AllTabChips.Length - 1) return;   // the last chip that is on: no-op
            _allTabHidden.Add(ch);
        }
        SaveAllTabConfig();
        if (_activeTab != ChatTab.All) return;   // other tabs aren't filtered; All rebuilds when next opened anyway
        // Re-shown in the open All tab = its messages are now on screen, so its channel tab counts as seen (as opening All does).
        if (!shown && ChannelTab(ch) is { } t) _lastSeen[t] = NewestFor(ch);
        // Same path as a tab switch: a full re-filter, then re-pin to the bottom + re-measure row heights (ResetScroll).
        // The row set changes wholesale, so a kept mid-log scroll offset would point at unrelated rows anyway.
        _logDirty = true;
        _tabResetPending = true;
        ResetNewBelow();   // .NewBelow.cs
    }

    // ⚙ settings (.Settings.cs): bold header, muted hint, then the six chips. The toolkit has no wrapping row, so the
    // prototype's wrapped chip row is two fixed rows of three (it wraps after the third chip at this width anyway).
    private HudElement BuildAllTabSettings() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("chat.settings.allTab.title"), Emphasis: true),
        new TextElement(() => _loc.T("chat.settings.allTab.hint"), MutedCol),
        new RowElement(new[] { BuildAllTabChip(AllTabChips[0]), BuildAllTabChip(AllTabChips[1]), BuildAllTabChip(AllTabChips[2]) }, Gap: 5f),
        new RowElement(new[] { BuildAllTabChip(AllTabChips[3]), BuildAllTabChip(AllTabChips[4]), BuildAllTabChip(AllTabChips[5]) }, Gap: 5f),
    }, Gap: 4f);

    // One chip = a whole-chip ConditionalElement over two variants, because Bold / Strikethrough are build-time
    // (init) text properties: ON = bold channel-colour text on the active-tab pill (TabActiveText / TabPillFill, .Tabs.cs);
    // OFF = dimmed, struck-through regular text on the recessed track fill. The Cond sits OUTSIDE each chip's bubble,
    // never inside its measured child (WindowBuilder-Patterns.md). No Selected arg: the Selectable's own selected fill
    // would double the pill (same as the tabs); hover still gets the Selectable's normal wash.
    private HudElement BuildAllTabChip(ChatTab tab)
    {
        var ch = TabChannel(tab)!.Value;
        HudElement Chip(TextElement label, Func<ColorRgba?> fill, ColorRgba restFill) => new SelectableElement(
            new BubbleElement(label)
            {
                // FillColor = the restore-default fill (explicit, NOT null — null = the themed accent fill).
                CornerRadius = 13f, PaddingX = 10f, PaddingY = 5f, FillColor = restFill, FillColorFn = fill,
            },
            OnClick: () => ToggleAllTabChannel(ch));
        return new ConditionalElement(() => AllTabShows(ch),
            Chip(new TextElement(() => _loc.T(TabLabelKey(tab)), () => TabActiveText(tab), Align: TextAlign.Center)
                    { Bold = true },
                () => TabPillFill(tab), Transparent),
            Chip(new TextElement(() => _loc.T(TabLabelKey(tab)), AllTabChipOffText, Align: TextAlign.Center)
                    { Strikethrough = true },
                () => TabTrackFill, TabTrackFill));
    }

    // Opaque blend, not alpha text (same reason as TabMutedText): alpha would composite against the game world behind
    // the glass window, so its legibility would swing with the scene.
    private ColorRgba? AllTabChipOffText() => Mix(_services.Theme.Colors.MenuMuted, TabTrackColor(), AllTabChipOffMix);
}
