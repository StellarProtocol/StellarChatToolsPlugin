using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// LOCAL-ONLY log lines (approved design "option B"): when a send never left the client (.SendCooldown.cs
/// <see cref="OnLocalSendFailed"/>) a red notice is shown in the log of the tab the user is looking at — a whisper
/// conversation included — as one compact line styled like the System notices (.SystemNotice.cs): the time in muted grey,
/// then the message in red (<see cref="LocalNoticeHex"/>). The draft stays in the composer; no Retry, no Send-pill change.
///
/// These rows exist ONLY in this plugin's filtered view: they never enter the framework chat ring (so they can't evict
/// real chat), never go through <c>MessageReceived</c> (so nothing records, auto-replies to or unread-counts them),
/// are System-channel rows (translate skips those, .Translate.cs) and are never sent. Each one belongs to the tab (and
/// conversation) it was raised in and is merged in by time on that view's rebuild. A small session-only list, dropped
/// at logout (.CharacterSwitch.cs) — it may disappear on relog.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxLocalNotices = 20;
    private const string LocalNoticeHex = "#FF4D4F";

    private readonly List<(ChatMessage Msg, ChatTab Tab, long Peer)> _localNotices = new();
    // Membership by REFERENCE: ChatMessage is a record (value equality), and a local row must never match a real one.
    private readonly HashSet<ChatMessage> _localNoticeSet = new(ReferenceEqualityComparer.Instance);
    private long _localNoticeSeq;

    private bool IsLocalNotice(ChatMessage m) => _localNoticeSet.Contains(m);

    // OnLocalSendFailed → one red line in the current view.
    private void AddSendFailedNotice()
    {
        // Stamp it at the bottom of the current view: wire stamps are server time, so a PC clock behind the server must
        // not sort the notice above messages that already arrived.
        var ts = DateTime.UtcNow;
        if (_filtered.Count > 0 && _filtered[^1].Timestamp > ts) ts = _filtered[^1].Timestamp;
        var msg = new ChatMessage(ChatChannel.System, string.Empty, 0L, _loc.T("chat.composer.notSentOffline"), ts,
            ChatMessageType.System, ReadOnlyMemory<byte>.Empty, null,
            MsgId: long.MinValue / 2 - ++_localNoticeSeq);   // far from the framework's own negative notice ids
        if (_localNotices.Count >= MaxLocalNotices) { _localNoticeSet.Remove(_localNotices[0].Msg); _localNotices.RemoveAt(0); }
        _localNotices.Add((msg, _activeTab, _activeTab == ChatTab.Whisper ? _selectedPeer : 0));
        _localNoticeSet.Add(msg);
        _logDirty = true;     // .Log.cs — rare event: a full rebuild merges it in
        JumpLogToBottom();    // .NewBelow.cs — like an own send: show where it landed
    }

    // From EnsureFiltered (.Log.cs), on the time-sorted view: insert this view's local notices by time (stable: after
    // rows with the same stamp).
    private void MergeLocalNotices(List<ChatMessage> view)
    {
        foreach (var (msg, tab, peer) in _localNotices)
        {
            if (tab != _activeTab || (tab == ChatTab.Whisper && peer != _selectedPeer)) continue;
            int at = view.Count;
            while (at > 0 && view[at - 1].Timestamp > msg.Timestamp) at--;
            view.Insert(at, msg);
        }
    }

    // NoticeLine (.SystemNotice.cs) body for a local row: "<time>  <red text>".
    private static string LocalNoticeLine(ChatMessage m, string mutedHex)
        => $"<color={mutedHex}>{m.Timestamp.ToLocalTime():HH:mm}</color>  <color={LocalNoticeHex}>{m.Text}</color>";

    // Logout (.CharacterSwitch.cs): session-only, per character.
    private void ClearLocalNotices()
    {
        _localNotices.Clear();
        _localNoticeSet.Clear();
    }
}
