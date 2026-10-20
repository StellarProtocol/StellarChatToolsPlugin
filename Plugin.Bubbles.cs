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
/// Fidelity note: avatars are colour-coded chips (hue hashed from the sender name), NOT class/role icons —
/// the chat wire carries no profession (<c>SenderMeta.Job</c> is hard-coded null at every framework
/// construction site: WireProtocol.ChitChat.cs; PandaChatProbe.Receive.Chat.cs), so there is nothing to map.
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

    // Learned local-player identity (0/null = not yet learned). Used to right-align our own messages.
    private long _selfCharId;
    private string? _selfName;

    // Send round-trip learning: the text/channel we last dispatched, matched against the echoed inbound copy
    // to discover our own SenderName/SenderId when the party roster isn't available (e.g. solo play).
    private string? _pendingSelfText;
    private ChatChannel _pendingSelfChannel;
    private DateTime _pendingSelfAt;

    // Stable, pleasant avatar palette — index chosen by a cheap name hash so each sender reads distinctly.
    private static readonly ColorRgba[] AvatarPalette =
    {
        new(0.86f, 0.45f, 0.45f, 1f), new(0.86f, 0.62f, 0.38f, 1f),
        new(0.80f, 0.74f, 0.40f, 1f), new(0.50f, 0.78f, 0.50f, 1f),
        new(0.40f, 0.78f, 0.74f, 1f), new(0.45f, 0.66f, 0.90f, 1f),
        new(0.62f, 0.55f, 0.90f, 1f), new(0.85f, 0.55f, 0.82f, 1f),
    };
    private static readonly ColorRgba SelfAvatarColor = new(0.30f, 0.80f, 1f, 1f);

    // One RECYCLED pool slot (VirtualListElement reuses ~LogPoolSize of these across the whole log, repositioning +
    // rebinding them as you scroll — NOT a fixed display position). It lazily builds the LEFT (others) or RIGHT
    // (self) branch, and the ConditionalElement re-picks per the data row currently bound to the slot (IsSelfRow
    // reads the live LogMsgIndex = _windowFirst + i), so a recycled slot flips alignment to match whatever message
    // it now shows (ConditionalElement defers branch construction until first shown — WindowBuilder.Layout.cs).
    private HudElement BuildBubbleSlot(int i)
        => new ConditionalElement(() => IsSelfRow(i), BuildBubbleRow(i, self: true), BuildBubbleRow(i, self: false));

    private HudElement BuildBubbleRow(int i, bool self)
    {
        var avatar = new SwatchElement(() => self ? SelfAvatarColor : AvatarColor(i), AvatarSize);

        // Bubble body: the sender/Lv line over the wrapping message text, in a content-hugging BubbleElement.
        // The bubble is a DIRECT child of the row (no CellElement/Weight wrapper — that would force a width and
        // defeat the hug). MaxWidth caps a long message so it wraps; FillColor stays null = themed accent tint.
        var bubble = new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                BuildSenderName(i, self),
                new TextElement(() => LogLevelBadge(i), MutedCol, NoWrap: true),
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

    // Deterministic palette pick from the sender name (FNV-ish fold; empty name → slot 0).
    private ColorRgba AvatarColor(int slot)
    {
        if (!TryMsg(slot, out var m) || string.IsNullOrEmpty(m.SenderName)) return AvatarPalette[0];
        uint h = 2166136261u;
        foreach (var ch in m.SenderName) { h ^= ch; h *= 16777619u; }
        return AvatarPalette[h % (uint)AvatarPalette.Length];
    }

    private string LogSenderName(int slot) => TryMsg(slot, out var m) ? m.SenderName : "";

    // Muted "Lv14" badge from the one sender-meta field the wire actually populates (Level); blank when 0.
    private string LogLevelBadge(int slot)
        => TryMsg(slot, out var m) && m.Sender is { Level: > 0 } s ? "Lv" + s.Level : "";

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
