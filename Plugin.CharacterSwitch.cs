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
/// Two edges, both cheap:
/// <list type="bullet">
/// <item><b>Leaving World</b> (<see cref="Stellar.Abstractions.Services.IClientState.PhaseChanged"/> World → TitleScreen /
/// CharSelect, Plugin.cs). The primary edge: the framework raises it synchronously at logout BEFORE it clears the
/// player record, so the outgoing character's folder key and name are still valid for the final history flush, and
/// nothing of the next character has arrived yet. The chat log is cut here too (see <see cref="_logCutoff"/>).</item>
/// <item><b>CharId changed</b> between ticks with no leave edge seen (TickWhisperHistory, IsWorldActive-gated — so it only
/// notices at the next world entry). A fallback: by then the new character's login history may already be in, so it
/// resets the state but does NOT cut the log.</item>
/// </list>
/// </summary>
public sealed partial class Plugin
{
    // The charId the per-character state belongs to; 0 = nobody yet (boot, or just reset at logout) → the next character
    // seen ADOPTS what accumulated meanwhile (it is theirs: nothing of the previous one survives a reset).
    private long _stateChar;

    // The framework's chat ring (IChat.RecentMessages) is NOT cleared at logout, so the previous character's chat would
    // stay in the log. This is the newest ring message at the last logout; the log shows only what arrived after it
    // (the next login's history replay included). Null = show the whole ring. Reference identity: ring entries are the
    // immutable records the framework stores, so the same object is found again until it front-drops off the ring
    // (then everything left is newer and the whole ring shows).
    private ChatMessage? _logCutoff;

    // After a reset the conversation list is re-merged from IFriends once the NEW character's lists have been polled:
    // IFriends.Changed fires only when its polled text differs, so a re-login whose list is unchanged would never refill
    // it. The delay outlasts FriendsService.PollInterval (2.5 s, in-world only) so the merge never reads the previous
    // character's cached lists; a Changed in the meantime (= a fresh poll) does the merge and cancels the timer.
    private const float FriendsRemergeDelaySec = 3f;
    private bool _friendsRemergePending;
    private float _friendsRemergeAt = -1f;

    private void ResetCharacterState(bool cutLog)
    {
        // History first, while the folder key and SelfName() still describe the outgoing character.
        FlushHistory();          // .WhisperHistory.cs
        ResetHistoryState();     // _histKey → null: re-resolved for whoever is in the world next
        _histPreKey.Clear();     // parked for a folder key that will now never be resolved
        _uidLogged = false;

        // Conversations (.Whisper.cs) — incl. unread counts, closed marks and the friend map they are named from.
        _convs.Clear();
        _convOrder.Clear();
        _convOrderDirty = true;
        _closedAt.Clear();
        _friendById.Clear();
        _friendViewDirty = true;               // .FriendPicker.cs — hidden until the re-merge (FriendSource)
        _friendsRemergePending = true;
        _friendsRemergeAt = -1f;
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

        if (cutLog)
        {
            var ring = _chat.RecentMessages;
            _logCutoff = ring.Count > 0 ? ring[ring.Count - 1] : null;
        }
        _stateChar = 0;
        _tabResetPending = true;
        _logDirty = true;
    }

    // From TickWhisperHistory (Framework.Update, world-gated) with the live charId.
    private void TickCharacterSwitch(long me)
    {
        if (me == 0) return;
        if (_stateChar == 0) { _stateChar = me; return; }
        if (me == _stateChar) return;
        _services.Log.Info("[ChatTools] character changed without a logout edge; resetting per-character state");
        ResetCharacterState(cutLog: false);
        _stateChar = me;
    }

    // From TickWhisper (world-gated): the delayed conversation re-merge after a reset.
    private void TickFriendsRemerge()
    {
        if (!_friendsRemergePending) return;
        float now = _services.Framework.TimeNow;
        if (_friendsRemergeAt < 0f) { _friendsRemergeAt = now + FriendsRemergeDelaySec; return; }
        if (now >= _friendsRemergeAt) OnFriendsChanged();   // .Whisper.cs — clears the pending flag
    }

    // The friend list the picker shows: empty while a reset waits for the new character's lists (IFriends still holds
    // the previous character's until its first in-world poll).
    private IReadOnlyList<FriendInfo> FriendSource()
        => _friendsRemergePending ? Array.Empty<FriendInfo>() : _friends.Friends;

    // First ring index the log may show (just past the logout cutoff). A linear scan, but only on a dirty rebuild.
    private int LogStartIndex(IReadOnlyList<ChatMessage> ring)
    {
        if (_logCutoff == null) return 0;
        for (int i = ring.Count - 1; i >= 0; i--)
            if (ReferenceEquals(ring[i], _logCutoff)) return i + 1;
        _logCutoff = null;   // front-dropped: everything left arrived after it
        return 0;
    }
}
