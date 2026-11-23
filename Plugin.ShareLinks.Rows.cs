using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Share-message ROWS (model: .ShareLinks.cs; approved prototype chattools-links.html §1–2):
///   • Position share — the sender's text, then the link: a pre-drawn PIN + the position text in the info blue,
///     underlined. Hover = lighter text + a faint blue wash + the "Click: track on map" tip (.HoverTip.cs); click =
///     <see cref="IChatPositionShare.TrackPosition"/> (the game's own link click: waypoint + map, no teleport), answered by
///     a small green / red note under the bubble that fades after ~3 s.
///   • Voice — MIC image + muted "6s ·" + the transcript (no audio). • Home share — the card text, a muted "Name's Home"
///     line and a neutral "Home invite" LABEL pill (no action in v1). • Loot-roll / notice hypertext — the System line look
///     with the channel's own tag. • Other shares — the ordinary text bubble, links styled inline (.ShareLinks.cs).
///
/// ⚠️ The clickable link is its OWN element on its own line under the typed text, not a span inside the wrapping text:
/// the toolkit can't hit-test part of a TMP string, and two texts side by side in a Row squeeze each other instead of
/// flowing (WindowBuilder-Patterns.md "Row layout"). Every variant (text before / after the link or not) is a COMPLETE
/// bubble picked by a ConditionalElement outside it — never a Cond inside a bubble's measured child (the BubbleElement
/// rule, .Bubbles.cs). The link chip is a nested Bubble (the hover wash) with an EXPLICIT MaxWidth in bubble mode: with
/// MaxWidth 0 it would cap at its parent, which a hugging bubble sizes FROM the chip, so it could only ever shrink.
/// </summary>
public sealed partial class Plugin
{
    private const float LinkIconSize = 12f;         // pin / mic inline (pre-drawn at this size, tinted live)
    private const float LinkIconGap  = 3f;
    private const float LinkWashAlpha = 0.15f;      // hover wash behind the link
    private const float ShareNoteSec = 3f, ShareNoteFadeSec = 0.6f;
    private const int   ShareNoteFontSize = 12;
    private const float ShareNoteErrLift = 0.30f;   // the red note's text toward white on a dark panel (prototype #ff8a8c)
    private const float HomePillH = 22f;
    private const string PinPng12Res = "Stellar.ChatTools.Assets.link_pin_12.png";
    private const string PinPng14Res = "Stellar.ChatTools.Assets.link_pin_14.png";
    private const string MicPng12Res = "Stellar.ChatTools.Assets.link_mic_12.png";

    private byte[]? _pin12, _pin14, _mic12;
    private bool _linkIconsTried;

    private void EnsureLinkIcons()
    {
        if (_linkIconsTried) return;
        _linkIconsTried = true;
        _pin12 = ReadResource(PinPng12Res); _pin14 = ReadResource(PinPng14Res); _mic12 = ReadResource(MicPng12Res);   // .SendTarget.cs
    }

    private byte[]? Pin12() { EnsureLinkIcons(); return _pin12; }
    private byte[]? Pin14() { EnsureLinkIcons(); return _pin14; }
    private byte[]? Mic12() { EnsureLinkIcons(); return _mic12; }

    // A fixed-width cell pins the icon's width (an ImageElement has no min width — a crowded row would squeeze it).
    private HudElement LinkIcon(Func<byte[]?> png, Func<ColorRgba?> tint)
        => new CellElement(new ImageElement(png, (int)LinkIconSize, (int)LinkIconSize) { Tint = tint }, Width: LinkIconSize);

    // ── Bubble mode ───────────────────────────────────────────────────────────────────────────────────────────────
    // Whole-bubble branches in front of the sticker / text bubble (.Bubbles.cs BuildBubbleRow). `make` = its MakeBubble.
    private HudElement WrapShareBubbles(int i, Func<HudElement, HudElement> make, HudElement other)
        => new ConditionalElement(() => IsPosShareRow(i), PosVariants(i, (b, a) => make(BuildPosBody(i, b, a, compact: false))),
           new ConditionalElement(() => IsVoiceRow(i), make(BuildVoiceBody(i)),
           new ConditionalElement(() => IsHomeRow(i), make(BuildHomeBody(i)), other)));

    // The four text-before / text-after combinations, each a complete element (no empty TextElement taking a line).
    private HudElement PosVariants(int i, Func<bool, bool, HudElement> make)
        => new ConditionalElement(() => PosHasBefore(i),
            new ConditionalElement(() => PosHasAfter(i), make(true, true), make(true, false)),
            new ConditionalElement(() => PosHasAfter(i), make(false, true), make(false, false)));

    private HudElement BuildPosBody(int i, bool before, bool after, bool compact)
    {
        var items = new HudElement[1 + (before ? 1 : 0) + (after ? 1 : 0)];
        int n = 0;
        if (before) items[n++] = new TextElement(() => ShareViewFor(i)?.Before ?? "", () => LogMsgColor(i)) { RichEmoji = true };
        // The chip hugs inside a Row (a Column would stretch its wash to the full width); the flexible spacer soaks the rest.
        items[n++] = new RowElement(new HudElement[] { BuildPosLink(i, compact ? 0f : BubbleMaxWidth - 20f), new SpacerElement() }, Gap: 0.01f);
        if (after) items[n] = new TextElement(() => ShareViewFor(i)?.After ?? "", () => LogMsgColor(i)) { RichEmoji = true };
        return new ColumnElement(items, Gap: 2f);
    }

    private bool PosHasBefore(int slot) => ShareViewFor(slot) is { Row: ShareRow.Position } v && v.Before.Length > 0;
    private bool PosHasAfter(int slot) => ShareViewFor(slot) is { Row: ShareRow.Position } v && v.After.Length > 0;

    // [pin][position text] — Bare selectable (no box of its own) in a padded rounded bubble that carries the hover wash.
    // maxWidth 0 (compact) = cap at the parent row, whose width comes from the line's weighted cell; bubble mode passes
    // the message bubble's inner width (see the class note). The text wraps under itself past the cap.
    private HudElement BuildPosLink(int i, float maxWidth) => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                LinkIcon(Pin12, () => PosLinkColor(i)),
                new TextElement(() => ShareViewFor(i)?.LinkRich ?? "", () => PosLinkColor(i)) { RichEmoji = true },
            }, Gap: LinkIconGap),
            () => OnPosLinkClick(i))
        { Bare = true, OnHover = on => OnPosLinkHover(i, on) })
    {
        FillColor = Transparent, FillColorFn = () => _posHoverSlot == i ? LinkColor() with { A = LinkWashAlpha } : Transparent,
        CornerRadius = 3f, PaddingX = 2f, PaddingY = 0f, MaxWidth = maxWidth,
    };

    private HudElement BuildVoiceBody(int i) => new RowElement(new HudElement[]
    {
        LinkIcon(Mic12, () => LinkColor()),
        new CellElement(new TextElement(() => VoiceText(i), () => LogMsgColor(i)) { RichEmoji = true }, Weight: 1f),
    }, Gap: 4f);

    // Text, then [Name's Home ……… (Home invite)]. The pill is a LABEL: no selectable, no hover (v1 has no visit action).
    private HudElement BuildHomeBody(int i) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => ShareViewFor(i)?.Body ?? "", () => LogMsgColor(i)) { RichEmoji = true },
        new RowElement(new HudElement[]
        {
            new TextElement(() => HomeOwnerLine(i), MutedCol, NoWrap: true),
            new SpacerElement(),
            new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(Width: 10f, Height: HomePillH),
                new TextElement(() => _loc.T("chat.share.homeInvite"), () => NeutralText(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(Width: 10f, Height: HomePillH),
            }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(NeutralFill()), CornerRadius = RoundRadius(HomePillH), PaddingX = 0f, PaddingY = 0f,
                MaxWidth = 200f,   // explicit: a parent-capped bubble in a hugging bubble can only shrink (class note)
            },
        }, Gap: 8f),
    }, Gap: 5f);

    // ── Compact mode ──────────────────────────────────────────────────────────────────────────────────────────────
    // "HH:mm Name: text" + the link / "HH:mm Name: <mic> 6s · transcript": whole-row branches ahead of the text line.
    private HudElement WrapCompactShare(int i, HudElement textRow)
        => new ConditionalElement(() => IsPosShareRow(i),
            new RowElement(new HudElement[]
            {
                BuildCompactHead(i, top: true),
                new CellElement(PosVariants(i, (b, a) => BuildPosBody(i, b, a, compact: true)), Weight: 1f),
            }, Gap: CompactGap),
           new ConditionalElement(() => IsVoiceRow(i),
            new RowElement(new HudElement[] { BuildCompactHead(i, top: true), new CellElement(BuildVoiceBody(i), Weight: 1f) }, Gap: CompactGap),
            textRow));

    // ── Notice line (loot roll on Party, …) ───────────────────────────────────────────────────────────────────────
    // "01:12  Party  Roru rolled the highest points and obtained [Arcane Core]": the System notice look (.SystemNotice.cs),
    // the tag in the message channel's colour. Both modes (a notice is one line either way).
    private HudElement BuildHyperNoticeRow(int i) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => HyperNoticeLine(i)) { RichEmoji = true },
    }) { Padding = 2 };

    private string HyperNoticeLine(int slot)
    {
        if (ShareViewFor(slot) is not { Row: ShareRow.Notice } v) return "";
        var label = ChannelTab(v.Msg.Channel) is { } tab ? _loc.T(TabLabelKey(tab)) : "";
        var muted = MutedHex();
        var chan = ChannelColor(v.Msg.Channel) ?? _services.Theme.Colors.MenuText;
        if (v.NoticeLine != null && string.Equals(label, v.NoticeLabel, StringComparison.Ordinal)
            && ReferenceEquals(muted, v.NoticeMuted) && chan.Equals(v.NoticeChan)) return v.NoticeLine;
        v.NoticeLabel = label; v.NoticeMuted = muted; v.NoticeChan = chan;
        var time = v.Msg.Timestamp.ToLocalTime().ToString("HH:mm");
        return v.NoticeLine = label.Length == 0
            ? $"<color={muted}>{time}</color>  {v.Body}"
            : $"<color={muted}>{time}</color>  <color={Hex(chan)}><noparse>{label}</noparse></color>  {v.Body}";
    }

    // ── Link hover / click ────────────────────────────────────────────────────────────────────────────────────────
    private const int TipKeyLinkBase = 100;   // hover-tip key per pool slot (.HoverTip.cs)
    private int _posHoverSlot = -1;

    private ColorRgba? PosLinkColor(int slot) => _posHoverSlot == slot ? LinkHoverColor() : LinkColor();

    private void OnPosLinkHover(int slot, bool on)
    {
        if (on) _posHoverSlot = slot;
        else if (_posHoverSlot == slot) _posHoverSlot = -1;
        HoverTip(TipKeyLinkBase + slot, on, TrackHint);
    }

    private string TrackHint() => _loc.T("chat.share.trackHint");

    // The note answering the latest click: one at a time (a new click replaces it), keyed by the message itself so a
    // scroll / recycle keeps it on the right row.
    private ChatMessage? _noteMsg;
    private bool _noteOk;
    private string _noteText = "";
    private float _noteUntil;

    private void OnPosLinkClick(int slot)
    {
        HideHoverTip();
        if (ShareViewFor(slot) is not { Row: ShareRow.Position, Pos: { } pos } v) return;
        ChatShareResult r;
        try { r = _chat.TrackPosition(pos); }
        catch (Exception ex)
        {
            _services.Log.Warning($"[ChatTools] track position failed: {ex.GetType().Name}: {ex.Message}");
            r = new ChatShareResult { Status = ChatShareStatus.Failed };
        }
        _noteMsg = v.Msg;
        _noteOk = r.Ok;
        // A refusal the game explains (map locked / unreachable) uses the game's own words, like its tip; else ours.
        _noteText = r.Ok ? _loc.T("chat.share.trackOk")
            : r.GameMessageId != 0 && !string.IsNullOrEmpty(r.Message) ? ChatMarkup.SanitizeWire(r.Message)
            : _loc.T("chat.share.trackFailed");
        _noteUntil = _services.Framework.TimeNow + ShareNoteSec;
    }

    private bool NoteLive() => _noteMsg != null && _services.Framework.TimeNow < _noteUntil;

    private bool ShareNoteShown(int slot) => NoteLive() && TryMsg(slot, out var m) && ReferenceEquals(m, _noteMsg);

    // Folded into the list's RowRevision (.Compact.cs LogRowRevision): the note adds a line under its row, so showing and
    // expiring it re-measures that one row in place. O(1): a reference compare, only while a note is live.
    private long ShareNoteRevision(int dataIndex)
        => NoteLive() && TryDataMsg(dataIndex, out var m) && ReferenceEquals(m, _noteMsg) ? 1L : 0L;

    // Under the bubble (indented past the avatar; mirrored for our own), or under the compact line. Slot-level branch
    // (.Bubbles.cs BuildBubbleSlot), outside every bubble.
    private HudElement BuildShareNoteRow(int i)
    {
        const float indent = AvatarSize + RowGap;
        HudElement Note() => new BubbleElement(new TextElement(() => _noteText, ShareNoteTextColor, FontSize: ShareNoteFontSize, NoWrap: true)
            { RichEmoji = true })   // RichEmoji = the TMP path, which honours FontSize
        { FillColor = Transparent, FillColorFn = ShareNoteFill, CornerRadius = 6f, PaddingX = 8f, PaddingY = 3f, MaxWidth = 420f };
        return new ColumnElement(new HudElement[]
        {
            new SpacerElement(Width: 0f, Height: 2f),
            new ConditionalElement(CompactOn,
                new RowElement(new HudElement[] { Note(), new SpacerElement() }, Gap: 0.01f),
                new ConditionalElement(() => IsSelfRow(i),
                    new RowElement(new HudElement[] { new SpacerElement(), Note(), new SpacerElement(Width: indent) }, Gap: 0.01f),
                    new RowElement(new HudElement[] { new SpacerElement(Width: indent), Note(), new SpacerElement() }, Gap: 0.01f))),
        }, Gap: 0.01f);
    }

    private float ShareNoteFade() => Math.Clamp((_noteUntil - _services.Framework.TimeNow) / ShareNoteFadeSec, 0f, 1f);

    private ColorRgba ShareNoteBase()
    {
        var c = _noteOk ? _services.Theme.Colors.Success : _services.Theme.Colors.Danger;
        return !_noteOk && !LightPanel() ? Mix(c, new ColorRgba(1f, 1f, 1f), ShareNoteErrLift) : c;
    }

    private ColorRgba? ShareNoteTextColor() => ShareNoteBase() with { A = ShareNoteFade() };

    private ColorRgba? ShareNoteFill()
    {
        var c = _noteOk ? _services.Theme.Colors.Success : _services.Theme.Colors.Danger;
        return c with { A = LinkWashAlpha * ShareNoteFade() };
    }
}
