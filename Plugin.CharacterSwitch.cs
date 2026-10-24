using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// Per-CHARACTER state reset. The plugin lives for the whole game session, but whispers, conversations, the learned own
/// identity, auto-reply cooldowns, unread marks and the whisper send target all belong to ONE character — after
/// logout → character select → another character, B must not see A's conversations, and replying must never whisper
/// A's peer as B. Avatar caches and translations are keyed by charId / message and stay (they are not character-owned).
///
/// Edges:
/// <list type="bullet">
/// <item><b><see cref="Stellar.Abstractions.Services.IClientState.Logout"/></b> — the primary edge. Raised synchronously
/// from the game's OnLogout postfix BEFORE the framework clears the player record, so the outgoing character's history
/// folder key and name are still valid for the final flush, and nothing of the next character has arrived yet.
/// ⚠️ NOT <c>PhaseChanged</c>: on a logout from World the framework deliberately leaves Phase at World and only promotes
/// it to TitleScreen when the login view (<c>login_main</c>) is actually seen (Game-Phases-and-Tick-Gating.md §4). An
/// Account-Switcher auto-login never shows it, so the whole switch ran World → World with NO PhaseChanged at all.</item>
/// <item><b>PhaseChanged leaving World</b> — kept as a second trigger (idempotent: one reset per logout).</item>
/// <item><b>CharId changed</b> between stable in-world ticks with no edge seen (TickWhisperHistory) — a fallback. It only
/// notices at the next world entry, after the new character's login replay is already in the ring, so the log is cut
/// at the ring tail last seen while the OLD character was confirmed in the world (<see cref="_lastConfirmedTail"/>),
/// and what arrived after it is re-noted as the new character's.</item>
/// </list>
/// </summary>
public sealed partial class Plugin
{
    // The charId the per-character state belongs to; 0 = nobody yet (boot, or just reset at logout) → the next character
    // confirmed in the world ADOPTS what accumulated meanwhile (it is theirs: nothing of the previous one survives a reset).
    private long _stateChar;
    private long _prevStateChar;   // the character the last reset dropped (a re-login of the SAME one can re-merge at once)
    private bool _charResetDone;   // a reset ran since the last adoption → a second edge for the same logout is a no-op

    // The framework's chat ring (IChat.RecentMessages) is NOT cleared at logout, so the previous character's chat would
    // stay in the log. This is the newest ring message at the reset point; the log shows only what arrived after it (the
    // next login's history replay included). Null = show the whole ring. Reference identity: ring entries are the
    // immutable records the framework stores, so the same object is found again until it front-drops off the ring
    // (then everything left is newer and the whole ring shows).
    private ChatMessage? _logCutoff;
    // The newest CHAT (non-notice) message at the same point. RecentMessages is two framework rings merged by arrival —
    // client system notices (SystemLabel set) are capped separately and rotate faster — so a notice cut point can drop
    // off its ring while older chat is still there. LogStartIndex then falls back to this one.
    private ChatMessage? _logCutoffChat;
    // The ring's newest message (and newest chat message) on the last stable in-world tick of the CURRENT character —
    // the fallback's cut points.
    private ChatMessage? _lastConfirmedTail, _lastConfirmedChatTail;

    // After a reset the cached IFriends lists are the PREVIOUS character's until the new one's are polled (in-world only,
    // every 2.5 s, and Changed fires only when the polled text differs). So nothing is merged from them until a Changed
    // arrives — except when the same character logs back in, whose cached lists are its own (merged at adoption).
    private bool _friendsRemergePending;

    private void OnClientLogout()
    {
        _services.Log.Info("[ChatTools] logout → resetting per-character state");
        ResetCharacterState(fallback: false);
        // The world-entry HideChat (Plugin.cs OnPhaseChanged) never fires on a World → World switch either.
        HideChat();
    }

    private void ResetCharacterState(bool fallback)
    {
        if (_charResetDone && !fallback) return;   // Logout then PhaseChanged(World→TitleScreen) for the same logout
        _charResetDone = true;
        // History first, while the folder key and SelfName() still describe the outgoing character. (On the fallback
        // SelfName() may already be the new character — the index name can be mislabelled once; the lines are not.)
        FlushHistory();          // .WhisperHistory.cs
        ResetHistoryState();     // _histKey → null: re-resolved for whoever is in the world next
        _histPreKey.Clear();     // parked for a key that will now never be resolved (the fallback re-notes below)
        _uidLogged = false;

        // Conversations (.Whisper.cs) — incl. unread counts, closed marks and the friend map they are named from.
        _convs.Clear();
        _convOrder.Clear();
        _convOrderDirty = true;
        _closedAt.Clear();
        _serverReadDue.Clear();                // .Whisper.cs — pending game read marks were the previous character's
        _friendById.Clear();
        _friendViewDirty = true;               // .FriendPicker.cs — hidden until the re-merge (FriendSource)
        _friendsRemergePending = true;
        _selectedPeer = 0;
        _chipPeer = 0; _chipName = null;       // .SendTarget.cs — the whisper chip's cached name
        _phPeer = 0;

        // Whisper send target + draft: leaving the Whisper tab drops whisper mode (.Composer.cs OnTabOpened).
        if (_activeTab == ChatTab.Whisper) SetActiveTab(ChatTab.All);
        else if (_sendTargetIndex == 4) _sendTargetIndex = SavedSendTarget();
        _inputText = string.Empty;

        // Learned own identity + pending echo (.Bubbles.cs), auto-reply memory (.AutoReply.cs).
        _selfCharId = 0;
        _selfName = null;
        _pendingSelfText = null;
        _pendingSelfStickerId = 0;
        _autoReplyCooldownBySender.Clear();
        _lastAutoReplyAt = DateTime.MinValue;

        // Channel-tab unread (.Tabs.cs) + the "new below" bar: they counted the previous character's messages.
        _newest.Clear();
        _lastSeen.Clear();
        ResetNewBelow();
        ClearLocalNotices();   // .LocalNotice.cs — session-only, per character

        var ring = _chat.RecentMessages;
        _logCutoff = fallback ? _lastConfirmedTail : ring.Count > 0 ? ring[ring.Count - 1] : null;
        _logCutoffChat = fallback ? _lastConfirmedChatTail : ChatTail(ring);
        _lastConfirmedTail = _lastConfirmedChatTail = null;
        _prevStateChar = _stateChar;
        _stateChar = 0;
        _tabResetPending = true;
        _logDirty = true;
    }

    // From TickWhisperHistory, ONLY on a stable logged-in in-world tick (the charId can read the previous character at
    // the title screen), with the live charId.
    private void TickCharacterSwitch(long me)
    {
        if (me == 0) return;
        if (_stateChar != 0 && me != _stateChar)
        {
            _services.Log.Info("[ChatTools] character changed without a logout edge; resetting per-character state");
            ResetCharacterState(fallback: true);
            _stateChar = me;
            _charResetDone = false;
            RenoteSinceCutoff();   // the new character's login replay already arrived under the old state
        }
        else if (_stateChar == 0)
        {
            _stateChar = me;
            _charResetDone = false;
            if (_friendsRemergePending && me == _prevStateChar) OnFriendsChanged();   // re-login: the cached lists are ours
        }
        var ring = _chat.RecentMessages;
        _lastConfirmedTail = ring.Count > 0 ? ring[ring.Count - 1] : null;
        _lastConfirmedChatTail = ChatTail(ring);
    }

    // Fallback only: feed the ring messages after the cut back through the per-character bookkeeping, now that self is
    // the new character (conversation list + saved history; IsHistory rows never count as unread anyway).
    private void RenoteSinceCutoff()
    {
        var ring = _chat.RecentMessages;
        for (int i = LogStartIndex(ring); i < ring.Count; i++)
        {
            var m = ring[i];
            if (m.Channel != ChatChannel.Whisper || !WhisperInvolvesSelf(m)) continue;
            NoteWhisper(m);
            NoteWhisperHistory(m);
        }
    }

    // The friend list the picker shows: empty while a reset waits for the new character's lists.
    private IReadOnlyList<FriendInfo> FriendSource()
        => _friendsRemergePending ? Array.Empty<FriendInfo>() : _friends.Friends;

    // First ring index the log may show (just past the reset cutoff). A linear scan, but only on a dirty rebuild.
    private int LogStartIndex(IReadOnlyList<ChatMessage> ring)
    {
        if (_logCutoff != null)
        {
            for (int i = ring.Count - 1; i >= 0; i--)
                if (ReferenceEquals(ring[i], _logCutoff)) return i + 1;
            _logCutoff = null;   // front-dropped off its ring
        }
        // The cut point was a notice its own (faster) ring has dropped — or it was chat and is gone too: cut after the
        // newest chat message of the reset point instead. Not found either → everything left arrived after it.
        if (_logCutoffChat != null)
        {
            for (int i = ring.Count - 1; i >= 0; i--)
                if (ReferenceEquals(ring[i], _logCutoffChat)) return i + 1;
            _logCutoffChat = null;
        }
        return 0;
    }

    // Newest non-notice entry of the ring (null = none). Usually the last or second-to-last entry.
    private static ChatMessage? ChatTail(IReadOnlyList<ChatMessage> ring)
    {
        for (int i = ring.Count - 1; i >= 0; i--)
            if (ring[i].SystemLabel == null) return ring[i];
        return null;
    }

    // Second discriminator behind the cutoff: a whisper with a recorded target names both parties, so a targeted one
    // where we are neither sender nor target belongs to another character (its PeerId was resolved against THAT self
    // when it arrived). Untargeted records (incoming, no target on the wire) can't be told apart and are kept.
    private bool WhisperInvolvesSelf(ChatMessage m)
    {
        long me = SelfCharId();
        if (me == 0 || m.SenderId == me || m.TargetId == me) return true;
        return m.TargetId == 0;
    }
}
