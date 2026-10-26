using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Opt-in COMPACT message rows (⚙ "Compact messages", off by default): one plain line per message,
/// <c>12:03 Name: text</c> — no avatar, no bubble background, no left/right self split. Why: with the window open, the
/// cost is the VISIBLE graphics on the window canvas (WindowBuilder-Patterns.md "Virtual lists: off-viewport row cull"
/// A/B), ~5 per bubble row (avatar, bubble background, name, body, time); a compact text row draws 3 (time, name, body).
///
/// Live switch: the row is a whole-row branch of the slot's ConditionalElement chain (.Bubbles.cs BuildBubbleSlot), so
/// the inactive layout is SetActive(false), not transparent, and an inactive branch is skipped by layout. Row heights
/// follow via the list's RowRevision (<see cref="LogRowRevision"/>): flipping the mode changes every row's revision, so
/// each latched row re-measures IN PLACE as it is shown (no ResetScroll → the scroll position is kept; a bottom-pinned
/// view stays pinned).
///
/// Layout: [head] [body cell, Weight 1]. Two texts side by side in a Row squeeze EACH OTHER rather than letting the second
/// wrap (WindowBuilder-Patterns.md "Row layout") because a NoWrap text reports min width 0 — so the head (time + name)
/// sits in a transparent zero-padding <see cref="BubbleElement"/>, whose binding pins min = preferred width; the head
/// then keeps its natural width and the body cell (min 0) takes the rest and wraps under itself (a hanging indent). The
/// head is top-aligned against a multi-line body by a Fill ConditionalElement (flexibleHeight 1 → the Row's HLG stretches
/// it to the row height) around a Column (UpperLeft). Rows are MiddleLeft otherwise. No Cond sits inside the head's
/// measured child (the BubbleElement rule): the plain-name vs whisper-link choice is a whole-head Cond outside it.
/// </summary>
public sealed partial class Plugin
{
    private const string CfgCompact = "display.compactMessages";
    private const float CompactGap = 5f;              // time ↔ name ↔ body
    private const float CompactHeadMaxWidth = 320f;   // explicit cap: the head's parent is sized FROM it (MaxWidth 0 collapses)
    private const float CompactStickerSize = 28f;     // a sticker stays a picture in compact mode, just small
    // Whisper tab right-pane floor in compact mode: a line wraps, so it only needs time + a name + a few words.
    private const float WhisperPaneMinWidthCompact = 260f;
    // Our own name: pink (the design's self colour); a darker shade on a light panel for contrast.
    private static readonly ColorRgba SelfNamePink      = new(0xE8 / 255f, 0x89 / 255f, 0xC4 / 255f);
    private static readonly ColorRgba SelfNamePinkLight = new(0xB0 / 255f, 0x40 / 255f, 0x80 / 255f);

    private bool _compactMessages;

    private void LoadCompactConfig() => _compactMessages = _cfg.Get(CfgCompact, false);

    private void SetCompactMessages(bool on)
    {
        if (_compactMessages == on) return;
        _compactMessages = on;
        _nameHoverSlot = -1;   // the hovered element may be in the branch that just went inactive
        _trHoverSlot = -1;
        _compactTrPillHoverSlot = -1;
        _cfg.Set(CfgCompact, on);
        _cfg.Save();
    }

    private bool CompactOn() => _compactMessages;

    // ⚙ settings (.Settings.cs): capsule toggle + sibling label, then the one-line muted hint.
    private HudElement BuildCompactSettings() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", CompactOn, SetCompactMessages),
            new TextElement(() => _loc.T("chat.settings.compact")),
        }, Gap: 6f),
        new TextElement(() => _loc.T("chat.settings.compactHint"), MutedCol),
    }, Gap: 4f);

    // VirtualListElement.RowRevision (.Log.cs): the translate revision with the display mode folded into bit 0, so a mode
    // switch re-measures every row in place and a translate change still re-measures its one row.
    private long LogRowRevision(int dataIndex) => (TranslateRowRevision(dataIndex) << 1) | (_compactMessages ? 1L : 0L);

    // ── Row ────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Right-click anywhere on the line = the message menu (.MessageMenu.cs), same Bare target as a bubble / notice. The
    // sticker-vs-text choice is a whole-row branch: the text row needs a Weight body cell, the sticker row a fixed image.
    // The text row is also the 文A hover zone (RowElement.OnHover: no tint/padding, FrontWindowBlocks-gated) — the bubble
    // mode's [bubble · pill] row, here the whole line, so moving onto the pill keeps it shown. The name link / name-card
    // selectables inside it keep their own hover + click; the right-click target around it still opens the menu.
    private HudElement BuildCompactRow(int i) => MessageRightClickTarget(new ConditionalElement(() => IsStickerRow(i),
        new RowElement(new HudElement[] { BuildCompactHead(i, top: false), BuildStickerImage(i, CompactStickerSize), new SpacerElement() }, Gap: CompactGap),
        new RowElement(new HudElement[]
        {
            BuildCompactHead(i, top: true),
            new CellElement(BuildCompactBody(i), Weight: 1f),
            BuildCompactTrSlot(i),
        }, Gap: CompactGap) { OnHover = on => OnTrHover(i, on) }), i);

    // Body column: the line text, then — only while a translate state is live — the bubble's own status line
    // ("文A Translated from Thai to English" / "Translating…" / "Translation failed · click to retry", .Bubbles.cs). It sits
    // in the body cell, so it lines up under the body's hanging indent. The Cond is fine here (no BubbleElement measures
    // this column), and every translate state change bumps the row's RowRevision (LogRowRevision) → re-measured in place.
    // RichEmoji: the sanitized body (inline <sprite=N> emoji) exactly as the bubble shows it; translated when shown.
    private HudElement BuildCompactBody(int i) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => CompactBodyText(i), () => CompactBodyColor(i)) { RichEmoji = true },
        new ConditionalElement(() => TrBubbleShown(i), BuildTrStatusLine(i)),
    }, Gap: TrBodyGap);

    // Translation (.Translate.cs): while a row's translate state is live, the line shows that body (the translation, or
    // the dimmed original while pending) instead.
    private string CompactBodyText(int slot) => TrBubbleShown(slot) ? TrBodyText(slot) : LogText(slot);

    // A shown real translation is drawn in the info blue — the compact line has no bubble to carry the bubble mode's
    // blue outline (TrBubbleOutline, same condition: not "Already in X", not pending/failed). Pending = dimmed original.
    private ColorRgba? CompactBodyColor(int slot)
    {
        if (!TrBubbleShown(slot)) return LogMsgColor(slot);
        if (TrBubbleOutline(slot) == null) return TrBodyColor(slot);
        return Luminance(_services.Theme.Colors.MenuBackground) >= 0.179f ? CompactTrTextLight : CompactTrText;
    }

    // ── 文A slot ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Same rules as the bubble's BuildTranslateSlot: a CONSTANT-width cell on every eligible line (someone else's
    // translatable text — own lines, stickers, notices and emoji-only lines get none, as bubble mode), holding the pill
    // only while ShowTrButton (hover with the setting on, or a live translate state); with the setting off the slot stays
    // reserved and empty, exactly like the bubble gutter. Idle, the cell holds a bare SpacerElement — no graphic; the pill
    // branch is SetActive(false), not transparent.
    //
    // Height: the bubble pill is 28 tall (20 strut + the Selectable's 4+4 padding), taller than a text line, so here it
    // would grow the row on hover (a latched variable-list height doesn't follow a hover). The compact pill is therefore a
    // BARE selectable (no padding) around a CompactTrPillH strut, and the idle spacer has the SAME height — so idle and
    // hovered rows measure identically by construction; nothing re-wraps or jumps. Bare drops the selectable's hover wash,
    // so the fill brightens on hover instead (CompactTrPillFill). Top-aligned against a wrapping body (Fill Cond → Column),
    // like the head, so the pill sits at the right end of the FIRST line.
    private const float CompactTrPillW = 26f, CompactTrPillH = 18f, CompactTrPillRadius = 6f;
    private const float CompactTrPillHoverAlpha = 0.15f;   // added to the fill's alpha while the pointer is on the pill
    // Info blue for a translated compact body; a darker shade on a light panel for contrast.
    private static readonly ColorRgba CompactTrText      = new(0x9C / 255f, 0xC8 / 255f, 0xFF / 255f);
    private static readonly ColorRgba CompactTrTextLight = new(0x2E / 255f, 0x6A / 255f, 0xB0 / 255f);

    private int _compactTrPillHoverSlot = -1;

    private HudElement BuildCompactTrSlot(int i)
    {
        var glyph = new TextElement(() => TrIcon, () => TrButtonGlyph(i), FontSize: TrButtonFontSize, Align: TextAlign.Center, NoWrap: true)
            { Bold = true };   // Bold = the TMP path, which honours FontSize on this surface (plain Text ignores it)
        var pill = new CellElement(new BubbleElement(new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new SpacerElement(Width: 0f, Height: CompactTrPillH), glyph, new SpacerElement(Width: 0f, Height: CompactTrPillH),
                }, Gap: IconPillGap, Justify: RowJustify.Center),
                OnClick: () => OnTranslateClick(i))   // .Translate.cs — translate / show original / retry, as the bubble pill
            { Bare = true, OnHover = on => OnCompactTrPillHover(i, on) })
            {
                CornerRadius = CompactTrPillRadius, PaddingX = 0f, PaddingY = 0f, FillColor = Transparent,
                FillColorFn = () => CompactTrPillFill(i), OutlineColorFn = () => TrButtonOutline(i),
            }, Width: CompactTrPillW);
        var cell = new CellElement(new ConditionalElement(() => ShowTrButton(i), pill,
            new SpacerElement(Width: CompactTrPillW, Height: CompactTrPillH)), Width: CompactTrPillW);
        return new ConditionalElement(() => CompactTrEligible(i), new ColumnElement(new HudElement[] { cell }), Fill: true);
    }

    private bool CompactTrEligible(int slot) => TryMsg(slot, out var m) && !IsSelf(m) && CanTranslate(m);

    private void OnCompactTrPillHover(int slot, bool on)
    {
        if (on) _compactTrPillHoverSlot = slot;
        else if (_compactTrPillHoverSlot == slot) _compactTrPillHoverSlot = -1;
    }

    private ColorRgba? CompactTrPillFill(int slot)
    {
        var f = TrButtonFill(slot);
        if (_compactTrPillHoverSlot != slot || f is not { } c) return f;
        return c with { A = Math.Min(1f, c.A + CompactTrPillHoverAlpha) };
    }

    // [time] [name:] — or, for a whisper outside the Whisper tab, [time] [Whisper · from|to] [name link] [suffix:].
    private HudElement BuildCompactHead(int i, bool top)
    {
        HudElement Head(HudElement name) => new BubbleElement(new RowElement(new HudElement[]
            {
                new TextElement(() => LogTime(i), MutedCol, NoWrap: true),
                name,
            }, Gap: CompactGap))
            { FillColor = Transparent, PaddingX = 0f, PaddingY = 0f, CornerRadius = 0f, MaxWidth = CompactHeadMaxWidth };

        // Plain name: click = the sender's name card (.NameCard.cs) — the avatar's job in bubble mode.
        HudElement plain = Head(new SelectableElement(
            new TextElement(() => CompactName(i), () => CompactNameColor(i), Emphasis: true, NoWrap: true),
            () => OpenNameCard(SenderCharId(i, IsSelfRow(i))))
            { Bare = true, OnHover = on => OnNameHover(i, on) });

        // Whisper link: the existing tag + link pieces (.Bubbles.cs), self resolved per poll (no self/other branch here).
        // The row gap is ≈0 (WhisperTagGap) — the prefix carries its own pad, exactly as in the bubble's name row.
        HudElement link = Head(new RowElement(new HudElement[]
        {
            new TextElement(() => WhisperTagPrefix(i, IsSelfRow(i)), () => ChannelColor(ChatChannel.Whisper), FontSize: WhisperTagFontSize, NoWrap: true)
                { Bold = true, RichEmoji = true },
            new SelectableElement(
                new TextElement(() => NameLinkText(i, IsSelfRow(i)), () => NameLinkColor(i), Emphasis: true, NoWrap: true) { RichEmoji = true },
                () => OnRowClick(i))   // .Log.cs — opens the conversation
                { Bare = true, OnHover = on => OnNameHover(i, on) },
            new TextElement(() => CompactLinkSuffix(i), () => ChannelColor(ChatChannel.Whisper), FontSize: WhisperTagFontSize, NoWrap: true)
                { Bold = true, RichEmoji = true },
        }, Gap: WhisperTagGap));

        HudElement head = new ConditionalElement(() => NameIsLink(i, IsSelfRow(i)), link, plain);
        // Top-align against a wrapping body: a Fill Cond is stretched to the row height by the HLG; the Column inside it
        // lays the head out at its preferred height from the top.
        return top ? new ConditionalElement(static () => true, new ColumnElement(new HudElement[] { head }), Fill: true) : head;
    }

    // "Name:" — cached per pool slot (the Func is polled every refresh), rebuilt only when the slot's name instance changes.
    private readonly string?[] _compactNameRaw = new string?[LogPoolSize], _compactNameText = new string?[LogPoolSize];

    private string CompactName(int slot)
    {
        if (!TryMsg(slot, out var m)) return "";
        var n = m.SenderName ?? "";
        if ((uint)slot >= (uint)LogPoolSize) return n + ":";
        if (ReferenceEquals(_compactNameRaw[slot], n) && _compactNameText[slot] is { } hit) return hit;
        _compactNameRaw[slot] = n;
        return _compactNameText[slot] = n + ":";
    }

    // The whisper suffix ("" for "from", the template's text after {0} for "to") plus the colon. The suffix is one of two
    // stable strings per language, so one cache entry for the non-empty one avoids a per-poll concat.
    private string? _compactSuffixRaw, _compactSuffixText;

    private string CompactLinkSuffix(int slot)
    {
        var s = WhisperTagSuffix(slot, IsSelfRow(slot));
        if (s.Length == 0) return ":";
        if (!ReferenceEquals(s, _compactSuffixRaw) || _compactSuffixText == null) { _compactSuffixRaw = s; _compactSuffixText = s + ":"; }
        return _compactSuffixText;
    }

    // Own name pink; everyone else the per-channel name colour (whisper coral inside the Whisper tab — LogSenderColor).
    // Hover brightens it like the whisper link, the cue that the name opens the card.
    private ColorRgba? CompactNameColor(int slot)
    {
        if (!TryMsg(slot, out var m)) return null;
        bool light = Luminance(_services.Theme.Colors.MenuBackground) >= 0.179f;
        ColorRgba? c = IsSelf(m) ? (light ? SelfNamePinkLight : SelfNamePink) : LogSenderColor(slot);
        if (_nameHoverSlot != slot || c is not { } v) return c;
        return Mix(v, light ? new ColorRgba(0f, 0f, 0f) : new ColorRgba(1f, 1f, 1f), NameLinkHoverMix);
    }
}
