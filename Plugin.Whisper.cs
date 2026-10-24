using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Per-person WHISPER conversations — the data side (the list / header / popups are Plugin.WhisperList.cs and
/// Plugin.FriendPicker.cs). A conversation is keyed by the other party's charId (<see cref="ChatMessage.PeerId"/>,
/// falling back to SenderId / TargetId against our own charId — Whisper-Private-Chat-and-Friends.md "Send / peer
/// identity"). The set is the UNION of the game's own conversation list (<see cref="IFriends.PrivateChats"/>: names,
/// pinning, server unread counts, online state) and the peers seen live on <see cref="IChat.MessageReceived"/> (incoming
/// AND outgoing whispers), sorted pinned-first then newest activity — plus, after a maintenance wipe, the peers only the
/// on-disk history still knows (.WhisperHistory.cs; listed from the saved name / preview, never unread).
///
/// Unread = the server's UnreadCount merged with our own live count, cleared on open. The server list is polled
/// (≈2.5 s), so after we open a conversation it can still report the old count for a while: a server count only counts
/// when its latest message is NEWER than our local read mark, so a just-read conversation can't re-light. Opening a
/// conversation also marks it read in the game (<see cref="IFriends.MarkPrivateRead"/>) and, once per session, asks for
/// its history when we hold little of it (the game sends private history only on open).
/// </summary>
public sealed partial class Plugin
{
    private sealed class Conversation
    {
        public long CharId;
        public string Name = "";
        public bool IsTop;
        public bool? Online;
        public int Level;
        public int ServerUnread, LiveUnread;
        public DateTime ReadMarkUtc;        // local read mark: everything at/before this has been seen
        public DateTime LatestUtc;          // newest message (live, history or the server's latest) — drives the preview
        public DateTime TouchedUtc;         // when we started it from the friend picker (sorts it up before any message)
        public DateTime Activity => LatestUtc > TouchedUtc ? LatestUtc : TouchedUtc;
        public string LatestText = "";
        public int LatestKind;              // 0 text, 3 sticker, 5 voice, 6 link (the game's ChitChatMsgType)
        public bool HistoryRequested;       // RequestPrivateHistory / CreatePrivateChat already asked this session
        public bool SavedOnly;              // listed only from the on-disk history (.WhisperHistory.cs), no live/server data yet
    }

    private const int   HistoryWantRows     = 10;    // below this many shown messages, opening asks for history
    private const float ClosedSuppressSec   = 15f;   // a closed conversation ignores the (lagging) server list this long

    private IFriends _friends = null!;
    private readonly Dictionary<long, Conversation> _convs = new();
    private readonly List<Conversation> _convOrder = new();
    private bool _convOrderDirty = true;
    private readonly Dictionary<long, FriendInfo> _friendById = new();
    private readonly Dictionary<long, float> _closedAt = new();   // charId → IFramework.TimeNow of a local close
    private long _selectedPeer;   // the open conversation (0 = none)

    private void InitWhisper()
    {
        _friends = _services.Friends;
        _friends.Changed += OnFriendsChanged;
        OnFriendsChanged();   // the lists may already be filled when the plugin loads mid-session
    }

    private void DisposeWhisper()
    {
        try { _friends.Changed -= OnFriendsChanged; } catch { /* swallow */ }
    }

    // The other party of a whisper (0 = undecidable). The framework fills PeerId; when it couldn't (our charId wasn't
    // known yet when the record arrived) decide it the same way: our own message → its target, else its sender.
    private long PeerOf(ChatMessage m)
    {
        if (m.PeerId != 0) return m.PeerId;
        var me = SelfCharId();
        if (me != 0 && m.SenderId == me) return m.TargetId;
        return m.SenderId != 0 ? m.SenderId : m.TargetId;
    }

    private Conversation GetOrCreateConv(long charId)
    {
        if (!_convs.TryGetValue(charId, out var c))
        {
            c = new Conversation { CharId = charId };
            if (_friendById.TryGetValue(charId, out var f)) { c.Name = f.Name; c.Level = f.Level; c.Online = f.IsOnline; }
            _convs[charId] = c;
            _convOrderDirty = true;
        }
        return c;
    }

    private bool RecentlyClosed(long charId)
        => _closedAt.TryGetValue(charId, out var at) && _services.Framework.TimeNow - at < ClosedSuppressSec;

    // IFriends.Changed (main thread): refresh the friend map, then merge the game's conversation list.
    private void OnFriendsChanged()
    {
        try
        {
            _friendById.Clear();
            foreach (var f in _friends.Friends) _friendById[f.CharId] = f;
            foreach (var t in _friends.PrivateChats)
            {
                if (t.CharId == 0 || RecentlyClosed(t.CharId)) continue;
                var c = GetOrCreateConv(t.CharId);
                c.SavedOnly = false;
                if (!string.IsNullOrEmpty(t.Name)) c.Name = t.Name;
                c.IsTop = t.IsTop;
                if (t.IsOnline is { } on) c.Online = on;
                if (t.LatestTimestampUtc > c.LatestUtc && t.LatestMsgType >= 0)
                {
                    c.LatestUtc = t.LatestTimestampUtc;
                    c.LatestText = t.LatestText ?? "";
                    c.LatestKind = t.LatestMsgType;
                }
                c.ServerUnread = t.UnreadCount > 0 && t.LatestTimestampUtc > c.ReadMarkUtc ? t.UnreadCount : 0;
            }
            // Friends' name / level / online state also feed conversations that aren't (yet) in the game's list.
            foreach (var c in _convs.Values)
                if (_friendById.TryGetValue(c.CharId, out var f))
                {
                    if (string.IsNullOrEmpty(c.Name)) c.Name = f.Name;
                    if (f.Level > 0) c.Level = f.Level;
                    c.Online ??= f.IsOnline;
                }
            _convOrderDirty = true;
            _friendViewDirty = true;   // .FriendPicker.cs
        }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] friend/conversation refresh failed: {ex.Message}"); }
    }

    // From OnChatMessage for every whisper (live + history). Keeps the conversation's name / preview / unread current.
    private void NoteWhisper(ChatMessage m)
    {
        if (m.Channel != ChatChannel.Whisper) return;
        long peer = PeerOf(m);
        if (peer == 0 || peer == SelfCharId()) return;
        if (m.IsHistory && !_convs.ContainsKey(peer) && RecentlyClosed(peer)) return;   // late history of a closed chat
        if (!m.IsHistory) _closedAt.Remove(peer);   // a new message re-opens a closed conversation
        var c = GetOrCreateConv(peer);
        c.SavedOnly = false;
        bool self = IsSelf(m);
        if (!self)
        {
            if (!string.IsNullOrEmpty(m.SenderName)) c.Name = m.SenderName;
            if (m.Sender is { Level: > 0 } s) c.Level = s.Level;
        }
        if (m.Timestamp >= c.LatestUtc)
        {
            c.LatestUtc = m.Timestamp;
            c.LatestText = m.Text ?? "";
            c.LatestKind = m.StickerId != 0 ? 3 : 0;
        }
        if (!m.IsHistory && !self)
        {
            if (IsViewingConversation(peer)) MarkConversationRead(c);
            else c.LiveUnread++;
        }
        _convOrderDirty = true;
    }

    // The conversation is on screen: Whisper tab open, this peer selected, window shown.
    // (_window is null only while Register is still building the tree — a tab-dot Func polled that early reads "not shown".)
    private bool IsViewingConversation(long peer)
        => peer != 0 && _activeTab == ChatTab.Whisper && _selectedPeer == peer && _window != null && _window.IsShown;

    private static bool HasUnread(Conversation c) => c.LiveUnread > 0 || c.ServerUnread > 0;

    private void MarkConversationRead(Conversation c)
    {
        bool had = HasUnread(c);
        c.LiveUnread = 0;
        c.ServerUnread = 0;
        if (c.LatestUtc > c.ReadMarkUtc) c.ReadMarkUtc = c.LatestUtc;
        if (had) { try { _friends.MarkPrivateRead(c.CharId); } catch { /* queued action; never break the UI */ } }
    }

    // Whisper TAB dot: any conversation with unread, except the one currently on screen.
    private bool AnyConversationUnread()
    {
        foreach (var c in _convs.Values)
            if (HasUnread(c) && !IsViewingConversation(c.CharId)) return true;
        return false;
    }

    // Open a conversation (list click, friend pick, a whisper name clicked in another tab).
    private void SelectPeer(long peer)
    {
        if (peer == 0) return;
        if (_selectedPeer != peer)
        {
            _selectedPeer = peer;
            _tabResetPending = true;   // .Log.cs — re-pin + re-measure for the new filter
            _logDirty = true;
            ResetNewBelow();
        }
        // The composer whispers this person (.Composer.cs) — but only inside the Whisper tab, where whisper mode lives
        // (every caller opens it first; the guard keeps a stray select from re-arming whisper on another tab).
        if (_activeTab == ChatTab.Whisper) _sendTargetIndex = 4;
        var c = GetOrCreateConv(peer);
        MarkConversationRead(c);
        EnsureSavedLoaded(peer);   // .WhisperHistory.cs — read its saved file in the background (merged in when it lands)
        if (!c.HistoryRequested)
        {
            c.HistoryRequested = true;
            if (CountShownWhispers(peer) < HistoryWantRows)
                try { _friends.RequestPrivateHistory(peer); } catch { /* queued action */ }
        }
    }

    private void OpenConversation(long peer)
    {
        if (peer == 0) return;
        SetActiveTab(ChatTab.Whisper);   // .Tabs.cs
        SelectPeer(peer);
    }

    private void CloseConversation(long peer)
    {
        if (peer == 0) return;
        try { _friends.ClosePrivateChat(peer); } catch { /* queued action */ }
        _convs.Remove(peer);
        _closedAt[peer] = _services.Framework.TimeNow;
        _convOrderDirty = true;
        if (_selectedPeer == peer)
        {
            _selectedPeer = 0;
            _tabResetPending = true;
            _logDirty = true;
        }
    }

    // A conversation from the saved index (.WhisperHistory.cs): after maintenance the server's list may have lost it,
    // so it is listed from the saved name / preview / time until live or server data replaces them. Never unread.
    private void AddSavedConversation(SavedPeerInfo p)
    {
        if (p.CharId == 0 || p.CharId == SelfCharId() || p.LastMs <= 0 || RecentlyClosed(p.CharId)) return;
        bool isNew = !_convs.ContainsKey(p.CharId);
        var c = GetOrCreateConv(p.CharId);
        if (isNew) c.SavedOnly = true;
        if (c.Name.Length == 0) c.Name = p.Name;
        if (c.Level == 0) c.Level = p.Level;
        var last = DateTimeOffset.FromUnixTimeMilliseconds(p.LastMs).UtcDateTime;
        if (last > c.LatestUtc) { c.LatestUtc = last; c.LatestText = p.Preview; c.LatestKind = p.Kind; }
        _convOrderDirty = true;
    }

    // Saved history cleared / character changed: drop the conversations only the saved index put in the list.
    private void RemoveSavedOnlyConversations()
    {
        List<long>? gone = null;
        foreach (var c in _convs.Values) if (c.SavedOnly) (gone ??= new()).Add(c.CharId);
        if (gone == null) return;
        foreach (var id in gone)
        {
            _convs.Remove(id);
            if (_selectedPeer == id) { _selectedPeer = 0; _tabResetPending = true; }
        }
        _convOrderDirty = true;
        _logDirty = true;
    }

    private int CountShownWhispers(long peer)
    {
        var m = _chat.RecentMessages;
        int n = 0;
        for (int i = 0; i < m.Count; i++)
            if (m[i].Channel == ChatChannel.Whisper && PeerOf(m[i]) == peer) n++;
        return n;
    }

    // Sorted view: pinned first, then newest activity, then name (stable for equal times).
    private List<Conversation> ConvOrder()
    {
        if (!_convOrderDirty) return _convOrder;
        _convOrder.Clear();
        _convOrder.AddRange(_convs.Values);
        _convOrder.Sort((a, b) =>
        {
            if (a.IsTop != b.IsTop) return a.IsTop ? -1 : 1;
            int t = b.Activity.CompareTo(a.Activity);
            return t != 0 ? t : string.CompareOrdinal(a.Name, b.Name);
        });
        _convOrderDirty = false;
        return _convOrder;
    }

    private Conversation? Conv(long peer) => peer != 0 && _convs.TryGetValue(peer, out var c) ? c : null;

    // Display name for a peer: conversation → friend → the charId (only until the game loads the name).
    private string PeerName(long peer)
    {
        if (Conv(peer) is { Name.Length: > 0 } c) return c.Name;
        if (_friendById.TryGetValue(peer, out var f) && f.Name.Length > 0) return f.Name;
        return peer == 0 ? "" : peer.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool? PeerOnline(long peer)
        => Conv(peer)?.Online ?? (_friendById.TryGetValue(peer, out var f) ? f.IsOnline : null);

    private int PeerLevel(long peer)
        => Conv(peer) is { Level: > 0 } c ? c.Level : _friendById.TryGetValue(peer, out var f) ? f.Level : 0;

    // Framework.Update: an unread conversation that is already on screen (window re-shown on it, or a server count that
    // arrived while viewing) is read — the user is looking at it.
    private void TickWhisper(float _)
    {
        if (_selectedPeer != 0 && Conv(_selectedPeer) is { } c && HasUnread(c) && IsViewingConversation(_selectedPeer))
            MarkConversationRead(c);
        TickWhisperPopups();   // .WhisperList.cs / .FriendPicker.cs — popup placement + close-with-window
    }
}
