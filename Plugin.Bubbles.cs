using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Bubble-style message rows (the game-native chat look, compacted). Each log slot is a
/// <see cref="ConditionalElement"/> that picks a LEFT (others) or RIGHT (self) layout per message:
/// a small rounded avatar chip, a content-hugging <see cref="BubbleElement"/> (the framework's rounded chat
/// bubble — width hugs its child so a short message makes a narrow bubble and a long one wraps at the cap),
/// and a muted timestamp on the opposite side. Self-detection is layered: the framework's character record
/// (<c>IPlayerIdentity.CharId</c>/<c>Name</c>, known from login on), then the party roster's <c>IsSelf</c> member,
/// then a send round-trip fallback that learns the local name/charId from our own echoed messages.
///
/// Why a <see cref="BubbleElement"/> and not the old forced-<see cref="SelectableElement"/> chip: the chip
/// force-expanded to the full row width (a baggy bubble far wider than its text). The bubble HUGS inside a
/// <see cref="RowElement"/> (a Row's HLG respects child width), with a flexible <see cref="SpacerElement"/>
/// as the gutter pushing the row to one side. An explicit <see cref="BubbleElement.MaxWidth"/> is REQUIRED —
/// a content-sized/right-justified Row has no definite "available width", so MaxWidth 0 would never wrap.
/// <see cref="BubbleElement.FillColor"/> is left null = the themed menu-accent @0.28α — the SAME tint the old
/// forced-Selected chip painted (the user already approved it). FillColor is the single knob to change later.
/// The bubble is purely visual (no click): whisper reply is reached by clicking the sender NAME (see
/// <see cref="BuildSenderName"/>), NOT by wrapping the bubble in a SelectableElement (which would repaint a
/// full-width chip behind the hugged bubble — the exact baggy look we removed).
///
/// Fidelity note: avatars are the sender's REAL profile photo, resolved out-of-band by charId through
/// <c>IPlayerProfile.RequestAvatarUrl</c> and downloaded/disk+LRU-cached in <see cref="Plugin"/> (Plugin.Avatars.cs);
/// everyone falls back to the game's shared default face while loading or when they have no custom photo. (The chat
/// wire still carries no profession — <c>SenderMeta.Job</c> is hard-coded null at every framework construction site —
/// so a class/role icon remains underivable; the photo comes from a separate Social RPC, not the chat payload.)
/// </summary>
public sealed partial class Plugin
{
    private const float AvatarSize     = 40f;    // ≈ the bubble's name + one-line-text height (fixed px — eyeball-tuned)
    // Avatar corner radius as a FRACTION of the displayed size — the single knob for every avatar (chat 40px → 10,
    // whisper list/header 32px → 8, friend picker 28px → 7). All of them show the SAME baked texture, so a size-relative
    // radius is the only way one bake reads identically at each size. Rounded SQUARE (0.5 would be a full circle).
    private const float AvatarCornerFraction = 0.25f;
    private const float RowGap         = 10f;    // horizontal spacing between row items — notably the avatar↔bubble gutter
    private const float TimeColWidth   = 46f;
    // Hard width cap on a bubble (canvas px). Short messages hug; past the cap a long message wraps and the
    // bubble grows in height. Static like the game's chat (it does NOT track the window width — a Row offers
    // no "available width" to clamp to). Sized to fit the default 440px window; this is the single tuning knob
    // for the wrap width. (Shrinking the window to its 480px minimum can clip a long bubble — tune here.)
    private const float BubbleMaxWidth = 300f;
    // Vertical breathing room below each bubble row (canvas px), matching the game's spaced-out chat. The variable
    // VirtualListElement stacks rows at their MEASURED preferred heights and has no built-in row gap, so the gap is
    // folded into each row's measured height as a trailing SpacerElement (see BuildBubbleSlot) rather than a
    // framework RowGap (which would need a framework redeploy).
    private const float BubbleRowGap = 3f;
    private const float StickerSize = 64f;   // chat sticker render size (px) — tunable

    // Learned local-player identity (0/null = not yet learned). Used to right-align our own messages AND to key our
    // own avatar lookup (prefer the learned charId over a 0 SenderId on an echoed self row).
    private long _selfCharId;
    private string? _selfName;

    // Send round-trip learning: the text/channel we last dispatched, matched against the echoed inbound copy
    // to discover our own SenderName/SenderId when the party roster isn't available (e.g. solo play). A send is
    // EITHER a text send (match the echo by Text) OR a sticker send (match by StickerId) — never both pending at
    // once, so each note clears the other kind.
    private string? _pendingSelfText;
    private int _pendingSelfStickerId;   // 0 = no sticker send pending
    private ChatChannel _pendingSelfChannel;
    private DateTime _pendingSelfAt;

    // One RECYCLED pool slot (VirtualListElement reuses ~LogPoolSize of these across the whole log, repositioning +
    // rebinding them as you scroll — NOT a fixed display position). It lazily builds the LEFT (others) or RIGHT
    // (self) branch, and the ConditionalElement re-picks per the data row currently bound to the slot (IsSelfRow
    // reads the live filtered row _windowFirst + i via TryMsg), so a recycled slot flips alignment to match whatever message
    // it now shows (ConditionalElement defers branch construction until first shown — WindowBuilder.Layout.cs).
    private HudElement BuildBubbleSlot(int i)
        // Wrap the per-row ConditionalElement in a width-expanding ColumnElement with a trailing fixed-height
        // SpacerElement. That bottom gap is MEASURED into the slot's preferred height (the variable list reads
        // LayoutUtility.GetPreferredHeight on the slot), so it renders as ~BubbleRowGap px of space between
        // consecutive rows. The bubble still HUGS — it lives in the inner RowElement (BuildBubbleRow), NOT as a
        // direct child of this Column (which force-expands child width). Applied once here so it covers BOTH the
        // self and other branches identically. The OUTER Cond picks the compact system-notice line (.SystemNotice.cs)
        // over any bubble — a whole-row switch, never inside a bubble's measured child. Outermost of all: the local-only
        // date separator (.DateSeparators.cs), a System row too, so it must be picked before the notice branch.
        => new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => IsDateSepRow(i), BuildDateSepRow(i),
            new ConditionalElement(() => IsNoticeRow(i), MessageRightClickTarget(BuildNoticeRow(i), i),   // right-click = message menu (.MessageMenu.cs)
                new ConditionalElement(() => IsSelfRow(i), BuildBubbleRow(i, self: true), BuildBubbleRow(i, self: false)))),
            new SpacerElement(Height: BubbleRowGap),
        });

    private HudElement BuildBubbleRow(int i, bool self)
    {
        // Real player photo (download + disk/LRU cached in Plugin.Avatars.cs). The rounded-SQUARE corners are BAKED
        // into the texture's alpha at decode time (BakeRoundedAvatar, driven by AvatarCornerFraction) — so CornerRadius=0
        // here (no per-row stencil Mask, which would break UI batching + add stencil draw calls every drawn frame).
        // Everyone resolves to the shared default face until (and unless) their real photo loads.
        // Click = the game's name card for the sender (.NameCard.cs); keyed by the pool slot for the hover ring.
        var avatar = ClickableAvatar(new GameTextureElement(() => AvatarTextureFor(SenderCharId(i, self)), (int)AvatarSize, (int)AvatarSize)
            { CornerRadius = 0 }, i, AvatarSize, () => SenderCharId(i, self));

        // ⚠️ The sticker-vs-text ConditionalElement MUST wrap the WHOLE bubble — it must NEVER sit INSIDE the bubble's
        // measured child. BubbleBinding.Apply() hugs the bubble each tick by reading LayoutUtility.GetPreferredWidth(
        // bubble.Child) (framework WindowBuilder.Bindings.Misc.cs). A ConditionalElement among those measured descendants
        // is SetActive-toggled in an EARLIER binding pass than the Bubble's width read, with only a DEFERRED
        // MarkLayoutForRebuild between them (framework WindowBuilder.Token.cs ApplyValues order) — so during a scroll
        // (VirtualListElement rebinds the recycled slot to a new row index → the inner Cond re-picks → toggles SetActive)
        // the width read lands on a stale/half-solved layout and pins the bubble narrow for a frame. The wrapping Text
        // then re-wraps 1↔2 lines and the bubble visibly grows/shrinks while scrolling — the regression introduced by
        // 254cf4e (the user validated the pre-254cf4e plain-Column child as stable: "look good now").
        //
        // Fix: lift the Cond OUT so EACH branch is a COMPLETE BubbleElement whose child is a PLAIN Column (no Cond among
        // the width-measured descendants — the stable pre-254cf4e shape). The Cond GO itself has flexibleWidth=0
        // (framework WindowBuilder.Layout.cs BuildConditional) so the Row's HLG won't stretch it, and its VLG reports the
        // active bubble's hugged preferred width — so the bubble still hugs exactly as before, sticker or text.
        //
        // Each bubble is a DIRECT child of the row (no CellElement/Weight wrapper — that would force a width and defeat
        // the hug). MaxWidth caps a long message so it wraps; FillColor stays null = themed accent tint.
        // Name row = [whisper tag][name]. The tag is EMPTY (zero width) on every row but a whisper shown outside the Whisper
        // tab, and the row's gap is ~0 (RowElement Gap 0 means "default 8", so a tiny value is the only true zero) — so an
        // ordinary row is byte-for-byte the name alone. The tag is a constant element whose TEXT changes, never a
        // ConditionalElement: a SetActive toggle inside the bubble's measured child makes it resize while scrolling
        // (WindowBuilder-Patterns.md BubbleElement), while a text change is re-measured by the bubble's own forced rebuild.
        HudElement MakeBubble(HudElement body, Func<ColorRgba?>? outline = null) => new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[] { BuildWhisperTag(i, self, suffix: false), BuildSenderName(i, self), BuildWhisperTag(i, self, suffix: true) }, Gap: WhisperTagGap),
            body,
        }, Gap: 1f))
        { MaxWidth = BubbleMaxWidth, OutlineColorFn = outline };

        // RichEmoji: LogText keeps the inbound <sprite=N> tags the game leaves in the message (and strips every other
        // tag a player could type — ChatMarkup); the framework hosts this body in TextMeshPro with the game's own chat
        // sprite asset so those draw as real emoji images (literal tags until the asset resolves / off-game). Body
        // only — the sender name carries no sprite tags, so it stays a plain Text.
        HudElement textBubble = MakeBubble(new TextElement(() => LogText(i), () => LogMsgColor(i)) { RichEmoji = true });
        // Others' text can be translated (.Translate.cs): the translate state is ANOTHER whole-bubble branch (same rule as
        // sticker-vs-text below — never a Cond inside the measured child), so an untranslated row is byte-for-byte the
        // plain bubble above.
        if (!self) textBubble = new ConditionalElement(() => TrBubbleShown(i), MakeBubble(BuildTranslatedBody(i), () => TrBubbleOutline(i)), textBubble);

        // Whole-bubble branches: sticker-image bubble vs wrapping-text bubble. The Cond now sits directly in the Row's
        // HLG where the single bubble used to — identical visuals, only the Cond's depth moved (out of the measured child).
        HudElement bubble = new ConditionalElement(() => IsStickerRow(i), MakeBubble(BuildStickerImage(i)), textBubble);
        // Right-click = the message menu (.MessageMenu.cs). A Bare target AROUND the whole-bubble Cond (outside the measured
        // child): no visuals/padding, and nested buttons (name links, translate) still take their own left clicks.
        bubble = MessageRightClickTarget(bubble, i);

        var time   = new CellElement(new TextElement(() => LogTime(i), MutedCol, NoWrap: true), Width: TimeColWidth);
        var gutter = new SpacerElement();   // flexible — soaks the row slack so the bubble hugs and the row packs

        // Others: avatar · [bubble · 文A slot] · time · (flex gutter → packs left). The inner row is the hover zone for the
        // translate pill (RowElement.OnHover — adds no tint/padding) and spans bubble + pill, so moving onto the pill keeps
        // it shown. The pill's cell is a CONSTANT width whether the pill is shown or not → hovering never shifts the
        // timestamp, and the 28-tall pill is shorter than the row's 40 avatar → never changes the row height.
        // Self mirrors it, gutter first → packs right (own messages get no translate pill).
        var children = self
            ? new HudElement[] { gutter, time, bubble, avatar }
            : new HudElement[]
            {
                avatar,
                new RowElement(new HudElement[] { bubble, BuildTranslateSlot(i) }, Gap: TrSlotGap) { OnHover = on => OnTrHover(i, on) },
                time, gutter,
            };
        return new RowElement(children, Gap: RowGap);
    }

    private const float TrSlotGap = 4f;   // bubble ↔ 文A pill

    // The constant-width 文A slot: a fixed cell holding the pill only while ShowTrButton (hover, or a live translate state).
    private HudElement BuildTranslateSlot(int i) => new CellElement(new ConditionalElement(() => ShowTrButton(i),
            BuildIconPill(new TextElement(() => TrIcon, () => TrButtonGlyph(i), FontSize: TrButtonFontSize, Align: TextAlign.Center, NoWrap: true)
                    { Bold = true },   // Bold = the TMP path, which honours FontSize on this surface (plain Text ignores it)
                () => TrButtonFill(i), () => OnTranslateClick(i), () => TrButtonOutline(i))),
        Width: ComposerControlSize);

    // Translate-branch body: the (translated / dimmed original) text + one small status line —
    // "文A Translating…" / "文A Translated from Thai to English" / "文A Translation failed · click to retry". Both are constant
    // elements whose TEXT changes (no Cond inside the bubble). The icon is its own TMP text so a CJK glyph never shares
    // a string with Thai (one TMP face per string); the line is NoWrap like the whisper tag.
    private HudElement BuildTranslatedBody(int i) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => TrBodyText(i), () => TrBodyColor(i)) { RichEmoji = true },
        new RowElement(new HudElement[]
        {
            new TextElement(() => TrIcon, () => TrStatusColor(i), FontSize: TrTagFontSize, NoWrap: true) { Bold = true },
            // RichEmoji = TMP rich text: the pending dots use an <alpha> tag to keep a constant width.
            new TextElement(() => TrStatus(i), () => TrStatusColor(i), FontSize: TrTagFontSize, NoWrap: true) { RichEmoji = true },
        }, Gap: TrSlotGap),
    }, Gap: 2f);

    // The sender name. Plain per-channel-coloured text on every row EXCEPT a whisper shown outside the Whisper tab, where
    // the person's name is a LINK that opens their conversation: the inbound whisperer after "Whisper · from", or the
    // recipient inside our own "Whisper · to Bob". The link must not look like a button (no box, tint or padding — it
    // would resize the bubble), so it is a Bare SelectableElement (a click target with no visuals, hugging the text)
    // around a link-styled text: whisper colour + a faint underline at rest, brighter + a solid underline on hover.
    // Both branches are the SAME TMP bold face/size, so the Cond swapping them never changes the name's width (the
    // BubbleElement measured-child rule, WindowBuilder-Patterns.md); hover only re-colours rich-text tags.
    private HudElement BuildSenderName(int i, bool self)
    {
        HudElement plain = new TextElement(() => LogSenderName(i), () => LogSenderColor(i), Emphasis: true, NoWrap: true);
        HudElement link = new SelectableElement(
            // RichEmoji = TMP rich text, for the <u>/<color> tags; the name itself is wrapped in <noparse> (NameLinkText).
            new TextElement(() => NameLinkText(i, self), () => NameLinkColor(i), Emphasis: true, NoWrap: true) { RichEmoji = true },
            () => OnRowClick(i))   // .Log.cs — opens PeerOf(m)'s conversation, for inbound and outbound alike
            { Bare = true, OnHover = on => OnNameHover(i, on) };
        return new ConditionalElement(() => NameIsLink(i, self), link, plain);
    }

    private bool IsWhisperRow(int slot) => TryMsg(slot, out var m) && m.Channel == ChatChannel.Whisper && m.SenderId != 0;

    // Outgoing: always a link while the tag shows (the name IS the recipient). Incoming: needs a sender to open.
    private bool NameIsLink(int slot, bool self) => WhisperTagShown(slot) && (self || IsWhisperRow(slot));

    // ── Name link ───────────────────────────────────────────────────────────────────────────────────────────────────
    // Hover = the Bare selectable's own rect (ticker hover list, FrontWindowBlocks-gated) → exactly the name.
    private int _nameHoverSlot = -1;
    private const float NameLinkHoverMix = 0.35f;     // hover: this far toward white (dark theme) / black (light theme)
    private const string NameLinkRestAlpha = "59";    // rest underline ≈ 35% alpha
    // Per-slot cache of the composed rich-text string — the Func is polled every refresh; rebuilt only on change.
    private readonly Dictionary<int, (string Name, bool Hover, string Col, string Text)> _nameLinkCache = new();

    private void OnNameHover(int slot, bool on)
    {
        if (on) _nameHoverSlot = slot;
        else if (_nameHoverSlot == slot) _nameHoverSlot = -1;
    }

    private string NameLinkName(int slot, bool self)
        => !TryMsg(slot, out var m) ? "" : self ? PeerName(PeerOf(m)) : m.SenderName;   // .Whisper.cs

    // Rest: "<color=#RRGGBB59><u></color>Name</u>" — TMP takes the underline colour from the colour current when <u>
    // opens, so the underline is faint while the glyphs keep the full ColorFn colour. Hover: a plain <u> (underline in
    // the text colour = solid). Underline and colour tags have no width, so rest and hover lay out identically.
    private ColorRgba _whisperHexFor;
    private string? _whisperHex;

    private string WhisperHex()
    {
        var c = _whisperColor.Value;
        if (_whisperHex == null || !c.Equals(_whisperHexFor)) { _whisperHexFor = c; _whisperHex = Hex(c); }   // .SystemNotice.cs
        return _whisperHex;
    }

    private string NameLinkText(int slot, bool self)
    {
        var name = NameLinkName(slot, self);
        if (string.IsNullOrEmpty(name)) return "";
        bool hover = _nameHoverSlot == slot;
        var col = hover ? "" : WhisperHex();   // "#RRGGBB", cached per colour (was one string per row per refresh)
        if (_nameLinkCache.TryGetValue(slot, out var e) && e.Hover == hover
            && string.Equals(e.Name, name, StringComparison.Ordinal) && string.Equals(e.Col, col, StringComparison.Ordinal))
            return e.Text;
        var text = hover
            ? "<u><noparse>" + name + "</noparse></u>"
            : "<color=" + col + NameLinkRestAlpha + "><u></color><noparse>" + name + "</noparse></u>";
        _nameLinkCache[slot] = (name, hover, col, text);   // keyed by slot (a fixed pool) → bounded
        return text;
    }

    private ColorRgba? NameLinkColor(int slot)
    {
        var c = _whisperColor.Value;
        if (_nameHoverSlot != slot) return c;
        var target = Luminance(_services.Theme.Colors.MenuBackground) < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        return Mix(c, target, NameLinkHoverMix);
    }

    // ── Whisper tag (All / mixed tabs) ──────────────────────────────────────────────────────────────────────────────
    // "Whisper · from [Name]" before an incoming whisperer's name, "Whisper · to [Bob]" in place of our own name on an
    // outgoing one — small, bold, in the whisper colour, with the NAME as its own link element between a prefix and a
    // suffix tag (the template's text before / after {0}, so "{0}へ"-style languages keep their word order). Hidden
    // (empty) in the Whisper tab, where the whole pane is one person.
    private const float WhisperTagGap      = 0.01f;   // ≈0: an empty tag must not shift the name (see MakeBubble)
    private const int   WhisperTagFontSize = 12;      // TMP path honours FontSize (Bold makes it TMP, like Emphasis)
    // The gap between a prefix tag and the name. The link has no padding any more (it used to be a padded button), and
    // the row gap must stay ≈0 for ordinary rows — so the prefix carries its own: a space plus an INVISIBLE dot, because
    // TMP drops trailing whitespace from its preferred width (a bare trailing space would measure as nothing).
    private const string TagPad = " <alpha=#00>.</alpha>";

    private HudElement BuildWhisperTag(int i, bool self, bool suffix)
        => new TextElement(() => suffix ? WhisperTagSuffix(i, self) : WhisperTagPrefix(i, self),
                () => ChannelColor(ChatChannel.Whisper), FontSize: WhisperTagFontSize, NoWrap: true)
            { Bold = true, RichEmoji = true };   // RichEmoji = TMP rich text, for the <alpha> pad

    private bool WhisperTagShown(int slot)
        => _activeTab != ChatTab.Whisper && TryMsg(slot, out var m) && m.Channel == ChatChannel.Whisper;

    // Tag parts, rebuilt only when the localized templates change (language switch).
    private string? _tagFromTpl, _tagToTpl;
    private string _tagFromText = "", _tagToPrefix = "", _tagToSuffix = "";

    private void EnsureTagParts()
    {
        var from = _loc.T("chat.whisper.tagFrom");
        var to = _loc.T("chat.whisper.tagTo");
        if (string.Equals(from, _tagFromTpl, StringComparison.Ordinal) && string.Equals(to, _tagToTpl, StringComparison.Ordinal)) return;
        _tagFromTpl = from; _tagToTpl = to;
        _tagFromText = from.TrimEnd() + TagPad;
        int k = to.IndexOf("{0}", StringComparison.Ordinal);
        var pre = k >= 0 ? to.Substring(0, k) : to;
        _tagToSuffix = k >= 0 ? to.Substring(k + 3) : "";
        // Pad only where the template itself spaces the name ("to {0}"); a "{0}へ" / "· {0}" prefix ending in a space is
        // padded the same way, one with no space before {0} is left as written.
        _tagToPrefix = pre.Length == 0 ? "" : char.IsWhiteSpace(pre[pre.Length - 1]) ? pre.TrimEnd() + TagPad : pre;
    }

    private string WhisperTagPrefix(int slot, bool self)
    {
        if (!WhisperTagShown(slot)) return "";
        EnsureTagParts();
        return self ? _tagToPrefix : _tagFromText;
    }

    private string WhisperTagSuffix(int slot, bool self)
    {
        if (!self || !WhisperTagShown(slot)) return "";
        EnsureTagParts();
        return _tagToSuffix;
    }

    // A whole-message picture-emoji/sticker row (StickerId != 0). Its body renders as an image, not the junk
    // "emojiPic=..." placeholder text the wire leaves in Text for these messages.
    private bool IsStickerRow(int slot) => TryMsg(slot, out var m) && m.StickerId != 0;

    // The sticker image for a row. GameTextureElement polls the texture each frame; LoadStickerIcon is cached, so
    // calling it again in the Uv func (to recover the sub-rect) is cheap and avoids shared-state ordering issues.
    private HudElement BuildStickerImage(int i) =>
        new GameTextureElement(
            () => { var id = TryMsg(i, out var m) ? m.StickerId : 0; return id == 0 ? null : _services.GameAssets.LoadStickerIcon(id, out _); },
            (int)StickerSize, (int)StickerSize,
            () => { var id = TryMsg(i, out var m) ? m.StickerId : 0; if (id != 0) { _services.GameAssets.LoadStickerIcon(id, out var uv); return uv; } return new UvRect(0f, 0f, 1f, 1f); });

    // The charId whose avatar photo this row shows (0 = unknown → the default face). For our own echoed rows the
    // SenderId can be 0, so prefer the learned _selfCharId when we have it. Fed to AvatarTextureFor (Plugin.Avatars.cs).
    private long SenderCharId(int slot, bool self)
    {
        if (self && SelfCharId() is var me && me != 0) return me;
        return TryMsg(slot, out var m) ? m.SenderId : 0;
    }

    private string LogSenderName(int slot) => TryMsg(slot, out var m) ? m.SenderName : "";

    // ---- Self-detection -------------------------------------------------------------------------------

    private bool IsSelfRow(int slot) => TryMsg(slot, out var m) && IsSelf(m);

    // The local charId: the framework's character record (IPlayerIdentity.CharId) is authoritative and known from the
    // character select on — so our own LOGIN-HISTORY rows sit on the right from the very first frame (they used to sit
    // left until a send echo taught us who we are). Read live, never cached: it resets to 0 on logout and changes with
    // the character. The learned roster/echo id is only the fallback while the record isn't readable.
    private long SelfCharId()
    {
        long id = 0;
        try { id = _services.PlayerState.CharId; } catch { /* identity not ready */ }
        if (id != 0) return id;
        LearnSelfFromRoster();
        return _selfCharId;
    }

    private bool IsSelf(ChatMessage m)
    {
        var me = SelfCharId();
        // An authoritative id decides every row that carries a sender id; the (non-unique) name match is only for a row
        // with no id (an echo can arrive with SenderId 0) or while no id is known at all.
        if (me != 0 && m.SenderId != 0) return m.SenderId == me;
        var name = SelfName();
        return !string.IsNullOrEmpty(name) && string.Equals(m.SenderName, name, StringComparison.Ordinal);
    }

    private string? SelfName()
    {
        string? n = null;
        try { n = _services.PlayerState.Name; } catch { /* identity not ready */ }
        return !string.IsNullOrEmpty(n) ? n : _selfName;
    }

    // Cheapest, most reliable source: the party roster marks our own row. Populated once in a party; a no-op
    // (leaves identity unlearned) solo — the send round-trip covers that case.
    private void LearnSelfFromRoster()
    {
        if (_selfCharId != 0) return;
        try
        {
            if (_services.PartySnapshot.Self is { } me && me.CharId != 0)
            {
                _selfCharId = me.CharId;
                if (string.IsNullOrEmpty(_selfName)) _selfName = me.Name;
            }
        }
        catch { /* snapshot not ready — try again next call */ }
    }

    // Record an outbound send so the echoed inbound copy can teach us our own identity (solo fallback). The
    // channel is passed from the call site (ChatTarget exposes no kind we could map back from).
    private void NoteSelfSend(ChatChannel echoChannel, string text)
    {
        _pendingSelfText = text;
        _pendingSelfStickerId = 0;   // only one pending kind active at a time (symmetry with NoteSelfSendSticker)
        _pendingSelfChannel = echoChannel;
        _pendingSelfAt = DateTime.UtcNow;
    }

    // Sticker sibling of NoteSelfSend: a sticker carries no text, so the echo is matched by StickerId instead.
    // Clears _pendingSelfText so only one pending kind is ever active (symmetry with NoteSelfSend).
    private void NoteSelfSendSticker(ChatChannel echoChannel, int stickerId)
    {
        _pendingSelfStickerId = stickerId;
        _pendingSelfText = null;
        _pendingSelfChannel = echoChannel;
        _pendingSelfAt = DateTime.UtcNow;
    }

    private static readonly TimeSpan PendingSelfEchoWindow = TimeSpan.FromSeconds(6);

    // Is this fresh message the echo of what we last sent? A text send matches by Text, a sticker send by StickerId (our
    // echo carries back the configId we sent). Read-only: the auto-reply guard asks BEFORE LearnSelfFromEcho consumes it.
    private bool IsPendingSelfEcho(ChatMessage msg)
    {
        if (msg.IsHistory || (_pendingSelfText == null && _pendingSelfStickerId == 0)) return false;
        if (DateTime.UtcNow - _pendingSelfAt > PendingSelfEchoWindow || msg.Channel != _pendingSelfChannel) return false;
        return (_pendingSelfText != null && string.Equals(msg.Text, _pendingSelfText, StringComparison.Ordinal))
            || (_pendingSelfStickerId != 0 && msg.StickerId == _pendingSelfStickerId);
    }

    // Called from the inbound handler (Plugin.AutoReply.cs). When a fresh message matches what we just sent,
    // adopt its sender identity as ours. Only needed until learned once.
    private void LearnSelfFromEcho(ChatMessage msg)
    {
        if (_selfCharId != 0 && !string.IsNullOrEmpty(_selfName)) return;
        if (msg.IsHistory) return;
        if (_pendingSelfText == null && _pendingSelfStickerId == 0) return;   // nothing pending to match against
        if (DateTime.UtcNow - _pendingSelfAt > PendingSelfEchoWindow) { _pendingSelfText = null; _pendingSelfStickerId = 0; return; }
        if (!IsPendingSelfEcho(msg)) return;

        if (!string.IsNullOrEmpty(msg.SenderName)) _selfName = msg.SenderName;
        if (msg.SenderId != 0) _selfCharId = msg.SenderId;
        _pendingSelfText = null;        // learned (or consumed) — stop matching either kind
        _pendingSelfStickerId = 0;
    }
}
