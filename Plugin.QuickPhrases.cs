using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The quick-phrase popover behind the composer's ＋ — the ONLY place phrases are managed (the old Settings editor is
/// gone). Same height as the sticker picker (the two toggle in the same spot, mutually exclusive), laid out as:
///   header  : "Quick phrases" (bold, muted) … "7 / 30" (muted, orange at the limit)
///   list    : scrollable rounded tiles [:: grip] [phrase…] [✕ on hover] — click = send, ✕ = delete (no confirm),
///             drag = reorder (framework ListElement.OnReorder, the sticker Favorites pattern), order persisted
///   add row : [New phrase… field] [Add] — Enter adds too; trimmed, empty / duplicate ignored, ≤ 140 chars
/// Limit <see cref="MaxQuickPhrases"/>: at it, Add greys out and the counter turns orange. A longer list from an older
/// config (hand-edited) is kept as-is and shown up to the pool size; it only blocks adding.
///
/// Send = the composer's own path (<see cref="SendPhrase"/>): a cooling-down target refuses it with the popover left open
/// (the Send pill's countdown is the feedback, same as Enter); a real send closes it.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxQuickPhrases   = 30;
    private const int QuickPhrasePool   = 48;    // pre-allocated rows (the tree is fixed at Register); ≥ the limit so an
                                                 // older over-limit list still shows
    private const int MaxPhraseLength   = MaxMessageLength;   // a phrase is sent as one message → the game's 140 cap
    private const float QuickHeaderH    = 20f;   // header strut (≥ the 15-px bold title's line box)
    private const float QuickGripW      = 14f;
    private const float QuickDeleteW    = 24f;   // the ✕ slot: glyph + the inner Selectable's 6+6 padding; CONSTANT, so
                                                 // showing ✕ on hover never moves the phrase text
    // Same total height as the sticker popover's body (.Stickers.cs BuildStickerPopover): its tab strip (40 cell + 4 + 4
    // bubble padding) + gap + separator slot (5) + gap + the fixed grid — so toggling ＋ ↔ ☺ never resizes the composer.
    private const float StickerPopoverBodyH = StickerTabCell + 8f + 4f + 5f + 4f + StickerGridHeight;
    private const float QuickListH = StickerPopoverBodyH - QuickHeaderH - ComposerControlSize - 2f * 4f;

    private bool _quickOpen;
    private string _quickEditText = string.Empty;
    private List<string> _quickPhrases = new();   // user-configurable; populated by LoadConfig ("quickPhrases")
    private int _quickHoverSlot = -1;
    private int _quickCounterN = -1;
    private string _quickCounterText = "";

    // ＋ quick-phrase toggle — closes the sticker picker when it opens (mutually exclusive popovers).
    private void ToggleQuickPhrases()
    {
        _quickOpen = !_quickOpen;
        if (_quickOpen) _stickerOpen = false;
        _quickHoverSlot = -1;
    }

    private HudElement BuildQuickPopover() => new PanelElement(new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("chat.quick.title"), () => _services.Theme.Colors.MenuMuted, NoWrap: true) { Bold = true },
            new SpacerElement(),   // flexible → counter right-aligned
            new TextElement(QuickCounter, QuickCounterColor, NoWrap: true),
            new SpacerElement(Width: 1f, Height: QuickHeaderH),   // height strut
        }, Gap: 4f),
        // Fixed height (like the sticker grid) so the popover keeps its size however many phrases there are.
        new ScrollElement(new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _quickPhrases.Count == 0,
                new TextElement(() => _loc.T("chat.quick.emptyHint"), () => _services.Theme.Colors.MenuMuted)),
            new ListElement(() => Math.Min(_quickPhrases.Count, QuickPhrasePool), BuildQuickRows()) { OnReorder = OnQuickReorder },
        }, Gap: 4f), Height: QuickListH) { FixedHeight = true },
        // Add row: the same 28-tall field box as the composer + a 28-tall pill.
        new RowElement(new HudElement[]
        {
            new CellElement(new InputElement(() => _quickEditText, s => { SetQuickEdit(s); AddQuickPhrase(); }, 160f, OnChange: SetQuickEdit)
                { RichEmoji = true, Placeholder = () => _loc.T("chat.quick.placeholder") }, Weight: 1f),
            BuildPill(() => _loc.T("chat.quick.add"), AddPillText, AddPillFill, AddQuickPhrase),
        }, Gap: 4f),
    }, Gap: 4f));

    // One tile per slot: Bubble (the recessed tile fill, rounded like the composer pills) → Selectable (click = send +
    // the hover wash, the row highlight) → [grip] [phrase, ellipsized in a weighted cell] [✕ slot]. Selectable, not
    // Button: the reorder ticker swallows the release click after a drag only for Selectables (WindowBuilder-Patterns.md).
    // The ✕ is a nested Selectable (its own click target; uGUI routes the click to the innermost Button) whose glyph is
    // transparent unless the row is hovered — constant structure, colour-only state, so nothing ever shifts.
    private HudElement[] BuildQuickRows()
    {
        var slots = new HudElement[QuickPhrasePool];
        for (int i = 0; i < QuickPhrasePool; i++)
        {
            var idx = i;
            slots[idx] = new BubbleElement(new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => "::", () => _services.Theme.Colors.MenuMuted, Align: TextAlign.Center, NoWrap: true),
                        Width: QuickGripW),
                    new CellElement(new TextElement(() => QuickPhrase(idx), NoWrap: true) { Ellipsize = true }, Weight: 1f),
                    new CellElement(new SelectableElement(
                            new TextElement(() => "✕", () => _quickHoverSlot == idx ? _services.Theme.Colors.MenuMuted : Transparent,
                                Align: TextAlign.Center, NoWrap: true),
                            () => RemoveQuickPhrase(idx)),
                        Width: QuickDeleteW),
                    new SpacerElement(Width: 1f, Height: PillStrutHeight),   // 20 strut + the Selectable's 4+4 = 28 tall
                }, Gap: 4f),
                () => { if (SendPhrase(QuickPhrase(idx))) _quickOpen = false; })   // a cooldown refusal keeps it open
            {
                OnHover = on => { if (on) _quickHoverSlot = idx; else if (_quickHoverSlot == idx) _quickHoverSlot = -1; },
            })
            { CornerRadius = PillRadius, PaddingX = 0f, PaddingY = 0f, FillColor = TabTrackFill };
        }
        return slots;
    }

    private string QuickPhrase(int i) => i >= 0 && i < _quickPhrases.Count ? _quickPhrases[i] : "";

    private bool QuickAtLimit() => _quickPhrases.Count >= MaxQuickPhrases;

    private string QuickCounter()
    {
        int n = _quickPhrases.Count;
        if (n != _quickCounterN) { _quickCounterN = n; _quickCounterText = n + " / " + MaxQuickPhrases; }
        return _quickCounterText;
    }

    private ColorRgba? QuickCounterColor() => QuickAtLimit()
        ? EnsureContrast(CounterWarn, _services.Theme.Colors.MenuBackground with { A = 1f }, TabActiveMinContrast)   // .SendCooldown.cs
        : _services.Theme.Colors.MenuMuted;

    // ≤ 140 chars, like the composer; the FieldBinding re-seeds the field from _quickEditText, so a cut shows at once.
    private void SetQuickEdit(string s) => _quickEditText = s.Length > MaxPhraseLength ? s.Substring(0, MaxPhraseLength) : s;

    private bool CanAddQuickPhrase()
    {
        if (QuickAtLimit()) return false;
        var t = _quickEditText.Trim();
        return t.Length > 0 && !_quickPhrases.Contains(t);
    }

    // Add pill = the Send pill's look in the accent colour: live when there is something new to add, greyed otherwise.
    private ColorRgba? AddPillFill() => CanAddQuickPhrase()
        ? _services.Theme.Colors.MenuAccent with { A = SendFillAlpha }
        : _services.Theme.Colors.MenuMuted with { A = DisabledFillAlpha };

    private ColorRgba? AddPillText() => CanAddQuickPhrase()
        ? EnsureContrast(new ColorRgba(1f, 1f, 1f), Over(_services.Theme.Colors.MenuAccent with { A = SendFillAlpha }, TabTrackColor()), TabActiveMinContrast)
        : _services.Theme.Colors.MenuMuted;

    private void AddQuickPhrase()
    {
        if (!CanAddQuickPhrase()) return;   // limit / empty / duplicate — a refused Enter keeps the text to edit
        _quickPhrases.Add(_quickEditText.Trim());
        _quickEditText = string.Empty;
        SaveQuickPhrases();   // .Config.cs
    }

    private void RemoveQuickPhrase(int i)
    {
        if (i < 0 || i >= _quickPhrases.Count) return;
        _quickPhrases.RemoveAt(i);
        SaveQuickPhrases();
    }

    // ListElement.OnReorder — remove-then-insert indices over the shown rows (= the list, up to the pool size).
    private void OnQuickReorder(int from, int to)
    {
        if (from < 0 || from >= _quickPhrases.Count || to < 0 || to >= _quickPhrases.Count || from == to) return;
        var p = _quickPhrases[from];
        _quickPhrases.RemoveAt(from);
        _quickPhrases.Insert(to, p);
        SaveQuickPhrases();
    }
}
