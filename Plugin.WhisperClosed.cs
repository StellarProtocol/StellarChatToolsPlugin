using System;
using System.Collections.Generic;

namespace Stellar.ChatTools;

/// <summary>
/// CLOSED conversations that stay closed across logins. Closing a conversation (<see cref="CloseConversation"/>) used to
/// hold for only <see cref="ClosedSuppressSec"/> (the game's lagging list) — at the next login the game's own
/// conversation list and the on-disk history index (the <c>SavedOnly</c> conversations) both listed it again.
///
/// Each close is now remembered per OWN character in the history folder as <c>whisper.&lt;uid&gt;/closed.json</c>: peer
/// charId → a close MARK (Unix ms) = the newest message time we knew for that conversation when it was closed (the close
/// time itself only when we knew none). A source may re-list a closed peer only with a message NEWER than its mark —
/// then the conversation reopens and the peer leaves the set. A live (non-history) whisper always reopens it, and so does
/// opening it on purpose (<see cref="SelectPeer"/>: friend picker, a name click). Comparing message times with message
/// times (both server-stamped) keeps a skewed PC clock out of it. Clear history (.WhisperHistory.Settings.cs) deletes the
/// file with the rest of the folder and forgets the set.
///
/// The file is read just before the index when the folder key resolves (<see cref="SetHistoryKey"/>, same I/O chain), so for
/// the first second of a login the game's list can still list a closed peer; the load then removes it again. A close
/// made before the load lands is kept in memory and written once the file has been read (never a blind overwrite).
/// </summary>
public sealed partial class Plugin
{
    private readonly Dictionary<long, long> _closedPeers = new();   // peer → close mark (Unix ms)
    private readonly HashSet<long> _reopenedBeforeLoad = new();      // reopened before closed.json was read → drop from it
    private bool _closedLoaded, _closedDirty;

    private static string ClosedFile(string key) => key + "/closed.json";

    private static long ToUnixMs(DateTime utc)
        => utc == default ? 0L : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    // A source (the game's list, the saved index, a history whisper) wants to list `peer` with its newest message at
    // `latestUtc`. True = keep it closed. A message newer than the close mark reopens the conversation.
    private bool ClosedFor(long peer, DateTime latestUtc)
    {
        if (RecentlyClosed(peer)) return true;
        if (!_closedPeers.TryGetValue(peer, out var mark)) return false;
        if (ToUnixMs(latestUtc) <= mark) return true;
        ReopenClosed(peer);
        return false;
    }

    // Persist a close (CloseConversation, before the conversation is dropped from _convs).
    private void RememberClosed(long peer, Conversation? c)
    {
        long mark = Math.Max(ToUnixMs(c?.LatestUtc ?? default), _saved.TryGetValue(peer, out var sp) ? sp.Info.LastMs : 0L);
        if (mark <= 0) mark = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _closedPeers[peer] = mark;
        _reopenedBeforeLoad.Remove(peer);
        SaveClosedPeers();
    }

    // The conversation is back (a newer message, a live whisper, or opened on purpose): forget the close.
    private void ReopenClosed(long peer)
    {
        _closedAt.Remove(peer);
        if (!_closedLoaded) _reopenedBeforeLoad.Add(peer);
        if (_closedPeers.Remove(peer)) SaveClosedPeers();
    }

    private void SaveClosedPeers()
    {
        if (_histKey == null || !_closedLoaded) { _closedDirty = true; return; }   // written once the file has been read
        _closedDirty = false;
        var name = ClosedFile(_histKey);
        var bytes = WhisperHistoryFormat.WriteClosed(new List<KeyValuePair<long, long>>(_closedPeers));
        HistIo(() => _services.Data.Write(name, bytes));
    }

    // I/O worker, from SetHistoryKey's chain step: after the legacy-folder migration (which merges a fallback folder's
    // closed.json in), before the index read.
    private void ReadClosedPeersOnWorker(string key, int gen)
    {
        var map = WhisperHistoryFormat.ReadClosed(_services.Data.Read(ClosedFile(key)));
        _services.Framework.Post(() => OnClosedLoaded(gen, map));
    }

    // I/O worker, from MigrateLegacyFolder: union the fallback folder's closed set into the UID folder's (the later mark
    // wins). False = the write didn't verify (the source is then kept, like the peer files).
    private bool MergeClosedFile(string from, string to)
    {
        var src = WhisperHistoryFormat.ReadClosed(_services.Data.Read(ClosedFile(from)));
        if (src.Count == 0) return true;
        var dst = WhisperHistoryFormat.ReadClosed(_services.Data.Read(ClosedFile(to)));
        foreach (var kv in src)
            if (!dst.TryGetValue(kv.Key, out var mark) || kv.Value > mark) dst[kv.Key] = kv.Value;
        var bytes = WhisperHistoryFormat.WriteClosed(new List<KeyValuePair<long, long>>(dst));
        _services.Data.Write(ClosedFile(to), bytes);
        return _services.Data.Read(ClosedFile(to))?.Length == bytes.Length;
    }

    // Merge the file under this session's closes / reopens, then close what the game's list re-added meanwhile.
    private void OnClosedLoaded(int gen, Dictionary<long, long> fromFile)
    {
        if (gen != _histGen) return;
        bool changed = false;   // the file holds a peer reopened meanwhile → rewrite it without
        foreach (var kv in fromFile)
        {
            if (_reopenedBeforeLoad.Contains(kv.Key)) { changed = true; continue; }
            if (!_closedPeers.ContainsKey(kv.Key)) _closedPeers[kv.Key] = kv.Value;
        }
        _reopenedBeforeLoad.Clear();
        _closedLoaded = true;
        List<long>? drop = null;
        foreach (var c in _convs.Values)
            if (c.CharId != _selectedPeer && _closedPeers.ContainsKey(c.CharId) && ClosedFor(c.CharId, c.LatestUtc)) (drop ??= new()).Add(c.CharId);
        if (drop != null)
        {
            foreach (var id in drop) _convs.Remove(id);
            _convOrderDirty = true;
        }
        if (_closedDirty || changed) SaveClosedPeers();
    }

    // Character change / folder re-key (ResetHistoryState): the set belongs to the previous character.
    private void ResetClosedPeers()
    {
        _closedPeers.Clear();
        _reopenedBeforeLoad.Clear();
        _closedLoaded = _closedDirty = false;
    }

    // Clear history (the folder's files, closed.json included, are deleted on the I/O chain): nothing is closed any more,
    // and the (now empty) set is known, so later closes write at once.
    private void ClearClosedPeers()
    {
        _closedPeers.Clear();
        _reopenedBeforeLoad.Clear();
        _closedDirty = false;
        _closedLoaded = true;
    }
}
