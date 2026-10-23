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

    // Fires on the Unity main thread for every message (live + login history replay).
    private void OnChatMessage(ChatMessage msg)
    {
        NoteIncoming(msg);        // .Tabs.cs    — unread + log-dirty; must run regardless of auto-reply state
        LearnSelfFromEcho(msg);   // .Bubbles.cs — adopt our own identity when a sent message echoes back
        NoteWhisper(msg);         // .Whisper.cs — per-person conversation list (name / preview / unread)

        if (!_autoReplyEnabled) return;
        if (msg.Channel != ChatChannel.Whisper) return;
        if (msg.IsHistory) return;                  // never auto-reply to the server-replayed history dump
        if (msg.SenderId == 0) return;              // Reply() needs a routable SenderId
        if (string.IsNullOrWhiteSpace(_autoReplyText)) return;

        var key = msg.SenderName + "|" + msg.SenderId.ToString(CultureInfo.InvariantCulture);
        if (_autoReplyCooldownBySender.TryGetValue(key, out var last) && DateTime.UtcNow - last < AutoReplyCooldown)
            return;

        _autoReplyCooldownBySender[key] = DateTime.UtcNow;
        NoteSelfSend(ChatChannel.Whisper, _autoReplyText);   // .Bubbles.cs — the auto-reply echoes back too
        _chat.Send(ChatTarget.Reply(msg), _autoReplyText);
    }

    private void SetAutoReply(bool enabled)
    {
        if (enabled == _autoReplyEnabled) return;
        _autoReplyEnabled = enabled;
        SaveAutoReplyEnabled();   // .Config.cs
    }
}
