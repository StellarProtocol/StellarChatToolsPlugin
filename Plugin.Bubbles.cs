using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Bubble-style message rows (the game-native chat look, compacted). Each log slot is a
/// <see cref="ConditionalElement"/> that picks a LEFT (others) or RIGHT (self) layout per message:
/// a small rounded avatar chip, a rounded "speech bubble" (a forced-selected <see cref="SelectableElement"/>
/// whose SwatchBg chip is the only rounded, tintable container the SDK exposes), and a muted timestamp on
/// the opposite side. Self-detection is layered: the party roster's <c>IsSelf</c> member when available, and
/// a send round-trip fallback that learns the local name/charId from our own echoed messages.
///
/// Fidelity notes (what existing elements could NOT reach, for the escalation report):
///  • The bubble background is the accent-tinted SwatchBg chip (≈0.28α cyan wash), NOT the game's opaque
///    near-white bubble — the renderer hard-codes that tint and exposes no plugin-set fill colour on a
///    rounded, child-hosting container. A new element (a rounded panel with a plugin colour + child host)
///    would close this.
///  • Avatars are colour-coded chips (hue hashed from the sender name), NOT class/role icons: the chat wire
///    carries no profession — <c>SenderMeta.Job</c> is hard-coded null at every framework construction site
///    (WireProtocol.ChitChat.cs:98,137; PandaChatProbe.Receive.Chat.cs), so there is nothing to map.
/// </summary>
public sealed partial class Plugin
{
    private const float AvatarSize   = 20f;   // compact — well under the game's big portraits
    private const float TimeColWidth = 46f;
    private const float BubbleWeight  = 2f;    // bubble : gutter = 2 : 1 → bubble ~66% of the free row width
    private const float GutterWeight  = 1f;

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

    // One log slot: lazily builds the LEFT (others) or RIGHT (self) branch as messages of each kind pass
    // through (ConditionalElement defers branch construction until first shown — WindowBuilder.Layout.cs).
    private HudElement BuildBubbleSlot(int i)
        => new ConditionalElement(() => IsSelfRow(i), BuildBubbleRow(i, self: true), BuildBubbleRow(i, self: false));

    private HudElement BuildBubbleRow(int i, bool self)
    {
        var avatar = new SwatchElement(() => self ? SelfAvatarColor : AvatarColor(i), AvatarSize);

        // Bubble body: name line ([name] + muted level badge) over the wrapping message text. The whole chip
        // is clickable so a whisper row still opens a reply (OnRowClick no-ops on non-whisper rows). Forced
        // Selected so the rounded SwatchBg chip is actually painted (its rest state is fully transparent).
        var bubble = new CellElement(new SelectableElement(
            new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => LogSenderName(i), () => LogSenderColor(i), Emphasis: true, NoWrap: true),
                    new TextElement(() => LogLevelBadge(i), MutedCol, NoWrap: true),
                }, Gap: 4f),
                new TextElement(() => LogText(i), () => LogMsgColor(i)),
            }, Gap: 1f),
            OnClick: () => OnRowClick(i),
            Selected: () => true), Weight: BubbleWeight);

        var time   = new CellElement(new TextElement(() => LogTime(i), MutedCol, NoWrap: true), Width: TimeColWidth);
        var gutter = new CellElement(new TextElement(() => string.Empty), Weight: GutterWeight);

        // Others: avatar · bubble · time · (gutter pushes them left). Self mirrors it to the right.
        var children = self
            ? new HudElement[] { gutter, time, bubble, avatar }
            : new HudElement[] { avatar, bubble, time, gutter };
        return new RowElement(children, Gap: 5f, Justify: self ? RowJustify.Right : RowJustify.Left);
    }

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
