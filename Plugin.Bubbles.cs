using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Bubble-style message rows (the game-native chat look, compacted). Each log slot is a
/// <see cref="ConditionalElement"/> that picks a LEFT (others) or RIGHT (self) layout per message:
/// a small rounded avatar chip, a content-hugging <see cref="BubbleElement"/> (the framework's rounded chat
/// bubble — width hugs its child so a short message makes a narrow bubble and a long one wraps at the cap),
/// and a muted timestamp on the opposite side. Self-detection is layered: the party roster's <c>IsSelf</c>
/// member when available, and a send round-trip fallback that learns the local name/charId from our own
/// echoed messages.
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
    private const float AvatarSize     = 20f;    // compact — well under the game's big portraits
    private const float TimeColWidth   = 46f;
    // Hard width cap on a bubble (canvas px). Short messages hug; past the cap a long message wraps and the
    // bubble grows in height. Static like the game's chat (it does NOT track the window width — a Row offers
    // no "available width" to clamp to). Sized to fit the default 440px window; this is the single tuning knob
    // for the wrap width. (Shrinking the window to its 360px minimum can clip a long bubble — tune here.)
    private const float BubbleMaxWidth = 300f;
    // Vertical breathing room below each bubble row (canvas px), matching the game's spaced-out chat. The variable
    // VirtualListElement stacks rows at their MEASURED preferred heights and has no built-in row gap, so the gap is
    // folded into each row's measured height as a trailing SpacerElement (see BuildBubbleSlot) rather than a
    // framework RowGap (which would need a framework redeploy).
    private const float BubbleRowGap = 3f;

    // Learned local-player identity (0/null = not yet learned). Used to right-align our own messages AND to key our
    // own avatar lookup (prefer the learned charId over a 0 SenderId on an echoed self row).
    private long _selfCharId;
    private string? _selfName;

    // Send round-trip learning: the text/channel we last dispatched, matched against the echoed inbound copy
    // to discover our own SenderName/SenderId when the party roster isn't available (e.g. solo play).
    private string? _pendingSelfText;
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
        // self and other branches identically.
        => new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => IsSelfRow(i), BuildBubbleRow(i, self: true), BuildBubbleRow(i, self: false)),
            new SpacerElement(Height: BubbleRowGap),
        });

    private HudElement BuildBubbleRow(int i, bool self)
    {
        // Real player photo (download + disk/LRU cached in Plugin.Avatars.cs). Circular via a CornerRadius = half the
        // size stencil mask. Everyone resolves to the shared default face until (and unless) their real photo loads.
        var avatar = new GameTextureElement(() => AvatarTextureFor(SenderCharId(i, self)), (int)AvatarSize, (int)AvatarSize)
            { CornerRadius = (int)(AvatarSize / 2f) };

        // Bubble body: the sender/Lv line over the wrapping message text, in a content-hugging BubbleElement.
        // The bubble is a DIRECT child of the row (no CellElement/Weight wrapper — that would force a width and
        // defeat the hug). MaxWidth caps a long message so it wraps; FillColor stays null = themed accent tint.
        var bubble = new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                BuildSenderName(i, self),
            }, Gap: 4f),
            new TextElement(() => LogText(i), () => LogMsgColor(i)),
        }, Gap: 1f))
        { MaxWidth = BubbleMaxWidth };

        var time   = new CellElement(new TextElement(() => LogTime(i), MutedCol, NoWrap: true), Width: TimeColWidth);
        var gutter = new SpacerElement();   // flexible — soaks the row slack so the bubble hugs and the row packs

        // Others: avatar · bubble · time · (flex gutter → packs left). Self mirrors it, gutter first → packs right.
        var children = self
            ? new HudElement[] { gutter, time, bubble, avatar }
            : new HudElement[] { avatar, bubble, time, gutter };
        return new RowElement(children, Gap: 5f);
    }

    // The sender name. Only an INBOUND (other) whisper is repliable, so for those the name is rendered as a
    // low-emphasis ButtonElement whose click opens a reply to that whisperer — it HUGS its label, so it never
    // paints a full-width chip behind the bubble (the whole point of dropping the Selectable-as-bubble). Every
    // other row keeps the name as plain per-channel-coloured text: a ButtonElement can't carry a custom label
    // colour, and non-whispers aren't repliable, so there's no reason to button them (and the colour is kept).
    private HudElement BuildSenderName(int i, bool self)
    {
        HudElement plain = new TextElement(() => LogSenderName(i), () => LogSenderColor(i), Emphasis: true, NoWrap: true);
        if (self) return plain;   // our own echoed whisper isn't a reply target
        return new ConditionalElement(() => IsWhisperRow(i),
            new ButtonElement(() => LogSenderName(i), () => OnRowClick(i), Style: MenuButtonStyle.Glass),
            plain);
    }

    private bool IsWhisperRow(int slot) => TryMsg(slot, out var m) && m.Channel == ChatChannel.Whisper && m.SenderId != 0;

    // The charId whose avatar photo this row shows (0 = unknown → the default face). For our own echoed rows the
    // SenderId can be 0, so prefer the learned _selfCharId when we have it. Fed to AvatarTextureFor (Plugin.Avatars.cs).
    private long SenderCharId(int slot, bool self)
    {
        if (self && _selfCharId != 0) return _selfCharId;
        return TryMsg(slot, out var m) ? m.SenderId : 0;
    }

    private string LogSenderName(int slot) => TryMsg(slot, out var m) ? m.SenderName : "";

    // ---- Self-detection -------------------------------------------------------------------------------

    private bool IsSelfRow(int slot) => TryMsg(slot, out var m) && IsSelf(m);

    private bool IsSelf(ChatMessage m)
    {
        LearnSelfFromRoster();
        if (_selfCharId != 0 && m.SenderId == _selfCharId) return true;
        if (!string.IsNullOrEmpty(_selfName) && string.Equals(m.SenderName, _selfName, StringComparison.Ordinal)) return true;
        return false;
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
        _pendingSelfChannel = echoChannel;
        _pendingSelfAt = DateTime.UtcNow;
    }

    // Called from the inbound handler (Plugin.AutoReply.cs). When a fresh message matches what we just sent,
    // adopt its sender identity as ours. Only needed until learned once.
    private void LearnSelfFromEcho(ChatMessage msg)
    {
        if (_selfCharId != 0 && !string.IsNullOrEmpty(_selfName)) return;
        if (msg.IsHistory || _pendingSelfText == null) return;
        if (DateTime.UtcNow - _pendingSelfAt > TimeSpan.FromSeconds(6)) { _pendingSelfText = null; return; }
        if (msg.Channel != _pendingSelfChannel) return;
        if (!string.Equals(msg.Text, _pendingSelfText, StringComparison.Ordinal)) return;

        if (!string.IsNullOrEmpty(msg.SenderName)) _selfName = msg.SenderName;
        if (msg.SenderId != 0) _selfCharId = msg.SenderId;
        _pendingSelfText = null;   // learned (or consumed) — stop matching
    }
}
