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
    private const float AvatarCorner   = 8f;     // rounded-SQUARE mask (NOT AvatarSize/2, which is a full circle)
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
    // reads the live LogMsgIndex = _windowFirst + i), so a recycled slot flips alignment to match whatever message
    // it now shows (ConditionalElement defers branch construction until first shown — WindowBuilder.Layout.cs).
    private HudElement BuildBubbleSlot(int i)
        // Wrap the per-row ConditionalElement in a width-expanding ColumnElement with a trailing fixed-height
        // SpacerElement. That bottom gap is MEASURED into the slot's preferred height (the variable list reads
        // LayoutUtility.GetPreferredHeight on the slot), so it renders as ~BubbleRowGap px of space between
        // consecutive rows. The bubble still HUGS — it lives in the inner RowElement (BuildBubbleRow), NOT as a
        // direct child of this Column (which force-expands child width). Applied once here so it covers BOTH the
        // self and other branches identically. The OUTER Cond picks the compact system-notice line (.SystemNotice.cs)
        // over any bubble — a whole-row switch, never inside a bubble's measured child.
        => new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => IsNoticeRow(i), BuildNoticeRow(i),
                new ConditionalElement(() => IsSelfRow(i), BuildBubbleRow(i, self: true), BuildBubbleRow(i, self: false))),
            new SpacerElement(Height: BubbleRowGap),
        });

    private HudElement BuildBubbleRow(int i, bool self)
    {
        // Real player photo (download + disk/LRU cached in Plugin.Avatars.cs). The rounded-SQUARE corners are BAKED
        // into the texture's alpha at decode time (RoundCornersInPlace, driven by AvatarCorner) — so CornerRadius=0
        // here (no per-row stencil Mask, which would break UI batching + add stencil draw calls every drawn frame).
        // Everyone resolves to the shared default face until (and unless) their real photo loads.
        var avatar = new GameTextureElement(() => AvatarTextureFor(SenderCharId(i, self)), (int)AvatarSize, (int)AvatarSize)
            { CornerRadius = 0 };

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
        HudElement MakeBubble(HudElement body) => new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[] { BuildWhisperTag(i, self), BuildSenderName(i, self) }, Gap: WhisperTagGap),
            body,
        }, Gap: 1f))
        { MaxWidth = BubbleMaxWidth };

        // Whole-bubble branches: sticker-image bubble vs wrapping-text bubble. The Cond now sits directly in the Row's
        // HLG where the single bubble used to — identical visuals, only the Cond's depth moved (out of the measured child).
        HudElement bubble = new ConditionalElement(() => IsStickerRow(i),
            MakeBubble(BuildStickerImage(i)),
            // RichEmoji: LogText returns m.Text verbatim, keeping the inbound <sprite=N> tags the game leaves in the
            // message; the framework hosts this body in TextMeshPro with the game's own chat sprite asset so those draw
            // as real emoji images (literal tags until the asset resolves / off-game). Body only — the sender name
            // carries no sprite tags, so it stays a plain Text.
            MakeBubble(new TextElement(() => LogText(i), () => LogMsgColor(i)) { RichEmoji = true }));

        var time   = new CellElement(new TextElement(() => LogTime(i), MutedCol, NoWrap: true), Width: TimeColWidth);
        var gutter = new SpacerElement();   // flexible — soaks the row slack so the bubble hugs and the row packs

        // Others: avatar · bubble · time · (flex gutter → packs left). Self mirrors it, gutter first → packs right.
        var children = self
            ? new HudElement[] { gutter, time, bubble, avatar }
            : new HudElement[] { avatar, bubble, time, gutter };
        return new RowElement(children, Gap: RowGap);
    }

    // The sender name. Only an INBOUND (other) whisper is repliable, so for those the name is rendered as a
    // low-emphasis ButtonElement whose click opens a reply to that whisperer — it HUGS its label, so it never
    // paints a full-width chip behind the bubble (the whole point of dropping the Selectable-as-bubble). Every
    // other row keeps the name as plain per-channel-coloured text: a ButtonElement can't carry a custom label
    // colour, and non-whispers aren't repliable, so there's no reason to button them (and the colour is kept).
    private HudElement BuildSenderName(int i, bool self)
    {
        // Our own outgoing whisper outside the Whisper tab shows "Whisper · to Bob" in the tag INSTEAD of our name.
        HudElement plain = new TextElement(() => self && WhisperTagShown(i) ? "" : LogSenderName(i), () => LogSenderColor(i),
            Emphasis: true, NoWrap: true);
        if (self) return plain;   // our own echoed whisper isn't a link to a conversation
        return new ConditionalElement(() => IsWhisperRow(i),
            new ButtonElement(() => LogSenderName(i), () => OnRowClick(i), Style: MenuButtonStyle.Glass),
            plain);
    }

    private bool IsWhisperRow(int slot) => TryMsg(slot, out var m) && m.Channel == ChatChannel.Whisper && m.SenderId != 0;

    // ── Whisper tag (All / mixed tabs) ──────────────────────────────────────────────────────────────────────────────
    // "Whisper · from" before an incoming whisperer's name, "Whisper · to Bob" in place of our own name on an outgoing
    // one — small, bold, in the whisper colour. Hidden (empty) in the Whisper tab, where the whole pane is one person.
    private const float WhisperTagGap      = 0.01f;   // ≈0: an empty tag must not shift the name (see MakeBubble)
    private const int   WhisperTagFontSize = 12;      // TMP path honours FontSize (Bold makes it TMP, like Emphasis)

    private HudElement BuildWhisperTag(int i, bool self)
        => new TextElement(() => WhisperTag(i, self), () => ChannelColor(ChatChannel.Whisper), FontSize: WhisperTagFontSize, NoWrap: true)
            { Bold = true };

    private bool WhisperTagShown(int slot)
        => _activeTab != ChatTab.Whisper && TryMsg(slot, out var m) && m.Channel == ChatChannel.Whisper;

    // "to" tags are cached per peer, keyed on the name they were built with (+ language) — the Func is polled every refresh.
    private readonly Dictionary<long, (string Name, string Text)> _whisperToTags = new();
    private string? _whisperToTpl;

    private string WhisperTag(int slot, bool self)
    {
        if (!WhisperTagShown(slot) || !TryMsg(slot, out var m)) return "";
        // Incoming: the name after it is a padded button (BuildSenderName), which gives the tag its breathing room.
        if (!self) return _loc.T("chat.whisper.tagFrom");
        var tpl = _loc.T("chat.whisper.tagTo");
        if (!string.Equals(tpl, _whisperToTpl, StringComparison.Ordinal)) { _whisperToTags.Clear(); _whisperToTpl = tpl; }
        long peer = PeerOf(m);   // .Whisper.cs
        var name = PeerName(peer);
        if (_whisperToTags.TryGetValue(peer, out var e) && string.Equals(e.Name, name, StringComparison.Ordinal)) return e.Text;
        var text = SafeFormat(tpl, name);
        if (_whisperToTags.Count > 256) _whisperToTags.Clear();   // bounded
        _whisperToTags[peer] = (name, text);
        return text;
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

    // Called from the inbound handler (Plugin.AutoReply.cs). When a fresh message matches what we just sent,
    // adopt its sender identity as ours. Only needed until learned once.
    private void LearnSelfFromEcho(ChatMessage msg)
    {
        if (_selfCharId != 0 && !string.IsNullOrEmpty(_selfName)) return;
        if (msg.IsHistory) return;
        if (_pendingSelfText == null && _pendingSelfStickerId == 0) return;   // nothing pending to match against
        if (DateTime.UtcNow - _pendingSelfAt > TimeSpan.FromSeconds(6)) { _pendingSelfText = null; _pendingSelfStickerId = 0; return; }
        if (msg.Channel != _pendingSelfChannel) return;

        // Match a text send by its Text, or a sticker send by its StickerId (our echo carries back the configId we sent).
        bool matches = (_pendingSelfText != null && string.Equals(msg.Text, _pendingSelfText, StringComparison.Ordinal))
                    || (_pendingSelfStickerId != 0 && msg.StickerId == _pendingSelfStickerId);
        if (!matches) return;

        if (!string.IsNullOrEmpty(msg.SenderName)) _selfName = msg.SenderName;
        if (msg.SenderId != 0) _selfCharId = msg.SenderId;
        _pendingSelfText = null;        // learned (or consumed) — stop matching either kind
        _pendingSelfStickerId = 0;
    }
}
