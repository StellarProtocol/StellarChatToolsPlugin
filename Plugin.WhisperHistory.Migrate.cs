using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.ChatTools;

/// <summary>
/// Folder-key stability for the on-disk whisper history (.WhisperHistory.cs). The key is the HUD UID
/// (<c>whisper.&lt;showId&gt;</c>); only when that read fails for <see cref="UidFallbackSec"/> does a login use
/// <c>whisper.c&lt;charId&gt;</c>. So one character can own BOTH folders. Rather than letting history split, every login
/// that DOES get the UID first folds that character's charId folder into the UID folder (merge-dedupe per peer file +
/// index), then deletes the old files. It runs on the history I/O worker ahead of the index read, so it is invisible
/// to the rest of the code, and it self-heals: a later fallback login only adds a fresh charId folder that the next
/// UID login absorbs again. The store has no rename/append and one folder level, so it is read → merge → whole-file
/// write → read-back check → delete; nothing is deleted unless every write verified.
/// </summary>
public sealed partial class Plugin
{
    private static string LegacyUid(long charId) => "c" + charId.ToString(CultureInfo.InvariantCulture);

    // The UID read result for the LOG, without the ids: "<charId>:<showId>|<types>" → "<ids>|<types>" (the Lua types
    // still show a nil/odd read); "err|<lua message>" and nil verbatim — that is what diagnoses a failed read.
    private static string UidReadForLog(string? raw)
    {
        if (raw == null) return "<nil>";
        if (raw.StartsWith("err|", System.StringComparison.Ordinal)) return raw;
        int bar = raw.IndexOf('|');
        return bar >= 0 ? "<ids>" + raw.Substring(bar) : "<ids>";
    }

    // "<charId>:<showId>|<types>" → the showId, accepted only when that same record's charId is OURS (charBase can
    // still describe the previous character during a switch). "err|…", nil, or a mismatch → null (retry).
    private static string? ParseUid(string? raw, long me)
    {
        if (raw == null) return null;
        int bar = raw.IndexOf('|');
        var body = bar >= 0 ? raw.AsSpan(0, bar) : raw.AsSpan();
        int colon = body.IndexOf(':');
        if (colon <= 0) return null;
        if (!long.TryParse(body[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var cid) || cid != me) return null;
        if (!long.TryParse(body[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var show) || show <= 0) return null;
        return show.ToString(CultureInfo.InvariantCulture);
    }

    // I/O worker only. from/to are folder keys ("whisper.c123" → "whisper.456").
    private void MigrateLegacyFolder(string from, string to, string charName)
    {
        var names = _services.Data.List(from + "/");
        if (names.Count == 0) return;   // the common case: no fallback folder for this character

        var index = new Dictionary<long, SavedPeerInfo>();
        foreach (var p in WhisperHistoryFormat.ReadIndex(_services.Data.Read(IndexFile(to)))) index[p.CharId] = p;
        foreach (var p in WhisperHistoryFormat.ReadIndex(_services.Data.Read(IndexFile(from)))) MergeInfo(index, p);

        var moved = new List<string>(names.Count);
        bool allWritten = true;
        int peers = 0, added = 0;
        string prefix = from + "/";
        foreach (var name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)
                || !long.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 6), NumberStyles.None,
                                  CultureInfo.InvariantCulture, out var peer) || peer == 0)
                continue;   // the index (handled below) or something we don't own → left alone
            var dstName = PeerFile(to, peer);
            var dst = WhisperHistoryFormat.ReadLines(_services.Data.Read(dstName), peer);
            int before = dst.Count;
            foreach (var w in WhisperHistoryFormat.ReadLines(_services.Data.Read(name), peer))
                WhisperHistoryFormat.AddSorted(dst, w);   // the same dedupe the live recorder uses
            WhisperHistoryFormat.Compact(dst);
            if (dst.Count != before)
            {
                var bytes = WhisperHistoryFormat.WriteLines(dst);
                _services.Data.Write(dstName, bytes);
                // Write is a silent no-op on an IO failure → verify before the source may be deleted.
                if (_services.Data.Read(dstName)?.Length != bytes.Length) { allWritten = false; continue; }
                added += dst.Count - before;
            }
            if (dst.Count > 0)
            {
                if (!index.TryGetValue(peer, out var info)) index[peer] = info = InfoFromLines(peer, dst);
                info.Count = dst.Count;
            }
            moved.Add(name);
            peers++;
        }

        var list = new List<SavedPeerInfo>(index.Count);
        foreach (var p in index.Values) if (p.LastMs > 0) list.Add(p);
        var idxBytes = WhisperHistoryFormat.WriteIndex(charName, list);
        _services.Data.Write(IndexFile(to), idxBytes);
        if (_services.Data.Read(IndexFile(to))?.Length != idxBytes.Length) allWritten = false;
        if (!MergeClosedFile(from, to)) allWritten = false;   // .WhisperClosed.cs — closed conversations move too

        if (!allWritten)
        {
            _services.Log.Warning($"[ChatTools] whisper history: merging {from} into {to} hit a write failure; kept {from} for the next login");
            return;
        }
        foreach (var name in moved) _services.Data.Delete(name);
        _services.Data.Delete(ClosedFile(from));
        _services.Data.Delete(IndexFile(from));   // the (now empty) directory itself stays; the store can't remove it
        _services.Log.Info($"[ChatTools] whisper history: merged {from} into {to} ({peers} conversations, {added} new messages)");
    }

    // Index merge: the newer entry's last-message fields win; name / level fill in when one side lacks them.
    private static void MergeInfo(Dictionary<long, SavedPeerInfo> index, SavedPeerInfo p)
    {
        if (!index.TryGetValue(p.CharId, out var cur)) { index[p.CharId] = p; return; }
        if (p.LastMs > cur.LastMs)
        {
            cur.LastMs = p.LastMs; cur.Preview = p.Preview; cur.Kind = p.Kind;
            if (p.Name.Length > 0) cur.Name = p.Name;
            if (p.Level > 0) cur.Level = p.Level;
        }
        if (cur.Name.Length == 0) cur.Name = p.Name;
        if (cur.Level == 0) cur.Level = p.Level;
    }

    // A peer file with no index entry on either side: rebuild the entry from its lines.
    private static SavedPeerInfo InfoFromLines(long peer, List<SavedWhisper> lines)
    {
        var last = lines[lines.Count - 1];
        var info = new SavedPeerInfo { CharId = peer, LastMs = last.TsMs, Preview = last.Text, Kind = last.StickerId != 0 ? 3 : 0 };
        for (int i = lines.Count - 1; i >= 0; i--)
            if (!lines[i].Self && lines[i].SenderName.Length > 0) { info.Name = lines[i].SenderName; break; }
        return info;
    }
}
