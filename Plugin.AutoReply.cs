using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// Inbound message handling: the single always-on <see cref="Stellar.Abstractions.Services.IChat.MessageReceived"/>
/// handler that drives per-tab unread tracking and, when enabled, whisper auto-reply with a per-sender cooldown.
/// </summary>
public sealed partial class Plugin
{
    private bool _autoReplyEnabled;
    private string _autoReplyText = string.Empty;   // localized default assigned in LoadConfig (ctor, after _loc)
    private static readonly TimeSpan AutoReplyCooldown = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTime> _autoReplyCooldownBySender = new(StringComparer.Ordinal);
    // When we last SENT an auto-reply. Its echo must never trigger another one, even if a manual send overwrote the
    // pending-echo note (NoteSelfSend) before the echo landed — so the echo is also recognised by its text for a while.
    private DateTime _lastAutoReplyAt = DateTime.MinValue;
    private static readonly TimeSpan AutoReplyEchoWindow = TimeSpan.FromSeconds(10);

    // Fires on the Unity main thread for every message (live + login history replay).
    private void OnChatMessage(ChatMessage msg)
    {
        // Ask BEFORE LearnSelfFromEcho: a successful learn consumes the pending note, and that very echo is exactly
        // the one auto-reply must skip.
        bool pendingEcho = IsPendingSelfEcho(msg);   // .Bubbles.cs
        NoteIncoming(msg);        // .Tabs.cs    — unread + log-dirty; must run regardless of auto-reply state
        LearnSelfFromEcho(msg);   // .Bubbles.cs — adopt our own identity when a sent message echoes back
        NoteWhisper(msg);         // .Whisper.cs — per-person conversation list (name / preview / unread)
        NoteWhisperHistory(msg);  // .WhisperHistory.cs — keep an on-disk copy (the server wipes it at maintenance)

        if (!_autoReplyEnabled) return;
        if (msg.Channel != ChatChannel.Whisper) return;
        if (msg.IsHistory) return;                  // never auto-reply to the server-replayed history dump
        if (msg.SenderId == 0) return;              // Reply() needs a routable SenderId
        if (string.IsNullOrWhiteSpace(_autoReplyText)) return;
        // Our own outgoing whisper echoes back through MessageReceived too — replying to it would whisper the person
        // we just wrote to (and an auto-reply's echo would chain another).
        if (IsOwnWhisper(msg, pendingEcho)) return;

        var key = msg.SenderName + "|" + msg.SenderId.ToString(CultureInfo.InvariantCulture);
        if (_autoReplyCooldownBySender.TryGetValue(key, out var last) && DateTime.UtcNow - last < AutoReplyCooldown)
            return;

        _autoReplyCooldownBySender[key] = DateTime.UtcNow;
        _lastAutoReplyAt = DateTime.UtcNow;
        NoteSelfSend(ChatChannel.Whisper, _autoReplyText);   // .Bubbles.cs — the auto-reply echoes back too
        _chat.Send(ChatTarget.Reply(msg), _autoReplyText);
    }

    // Is this live whisper one WE sent? Layered so it holds even before our identity is known (first seconds after login,
    // or a solo character the roster can't name). Any "can't tell" answers YES — a skipped auto-reply is harmless, a
    // reply to ourselves is not.
    private bool IsOwnWhisper(ChatMessage msg, bool pendingEcho)
    {
        if (IsSelf(msg)) return true;   // .Bubbles.cs — the framework charId, else the learned roster/echo identity
        long me = 0;
        try { me = _services.PlayerState.CharId; } catch { /* identity not ready */ }
        if (me != 0 && msg.SenderId == me) return true;   // (IsSelf covers it today; kept explicit as the hard floor)
        // The framework's peer rule (ChatService.ResolveWhisperPeer): an outgoing whisper's PeerId is its TARGET, an
        // incoming one's is its SENDER — so a decided PeerId that isn't the sender means the sender is us.
        if (msg.PeerId != 0 && msg.PeerId != msg.SenderId) return true;
        bool selfUnknown = SelfCharId() == 0;
        // PeerId 0 with a target = the framework couldn't decide (our charId wasn't known yet): could be our own echo.
        if (msg.PeerId == 0 && msg.TargetId != 0 && selfUnknown) return true;
        // The text/sticker we just sent. Only trusted while no id can decide — once one can, IsSelf above is
        // authoritative and someone answering "hi" to our "hi" still gets the auto-reply.
        if (pendingEcho && selfUnknown) return true;
        // Our auto-reply's own echo (also stops two auto-repliers with the same text from ping-ponging).
        return DateTime.UtcNow - _lastAutoReplyAt < AutoReplyEchoWindow
            && string.Equals(msg.Text, _autoReplyText, StringComparison.Ordinal);
    }

    private void SetAutoReply(bool enabled)
    {
        if (enabled == _autoReplyEnabled) return;
        _autoReplyEnabled = enabled;
        SaveAutoReplyEnabled();   // .Config.cs
    }
}
