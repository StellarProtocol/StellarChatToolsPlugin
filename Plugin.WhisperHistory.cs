using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// On-disk WHISPER history. The game server wipes private-chat history at maintenance, so every whisper we see (live
/// incoming, our own echoes, the per-peer history batches) is also kept here, per OWN character, and merged back into
/// the conversation view. Format + dedupe rules: WhisperHistoryFormat.cs. Settings section + clear: .WhisperHistory.Settings.cs.
///
/// Storage = the framework per-plugin data store (<c>stellar/plugindata/stellar.chattools.data/</c>, the avatar cache's
/// home — Chat-System-Architecture.md): <c>whisper.&lt;uid&gt;/peers.json</c> + <c>whisper.&lt;uid&gt;/&lt;peer&gt;.jsonl</c>.
/// A dot, not a nested folder: the store allows ONE subdirectory per name. The store has no append, so a flush rewrites
/// each dirty peer file whole (≤600 lines) — atomically, it writes a temp file and renames it.
///
/// Folder key = the in-game UID the HUD shows (<c>CharSerialize.charBase.showId</c>, one Lua read per login, accepted only
/// when that same record's charId is OURS — charBase can still hold the previous character during a switch). If it never
/// reads, the key falls back to <c>c&lt;charId&gt;</c> after <see cref="UidFallbackSec"/>. The next login that does read the UID merges
/// that fallback folder into the UID one (.WhisperHistory.Migrate.cs), so one character never keeps two histories.
///
/// Threading: all state below is main-thread. Every disk touch runs on ONE chained background task (<see cref="_histIo"/>,
/// so writes land in order and a clear can't race a flush), and results come back through IFramework.Post. Nothing here
/// runs inside a window apply: the merge is plain list work inside the log's EnsureFiltered (.Log.cs).
/// </summary>
public sealed partial class Plugin
{
    private const float HistFlushDelaySec = 2f;    // batch window: a flush runs this long after the first unsaved change
    private const float UidRetrySec       = 1f;
    private const float UidFallbackSec    = 30f;
    private const int   PreKeyCap         = 1000;  // whispers held while the folder key is still unknown
    private const int   PrewarmSavedRows  = 60;    // newest saved texts handed to the TMP glyph prewarm when a file loads
    private const string UidGlobal        = "__chattools_uid";

    private sealed class SavedPeer
    {
        public SavedPeerInfo Info = new();
        public List<SavedWhisper>? Msgs;       // null = the peer file isn't loaded yet
        public List<SavedWhisper>? Pending;    // recorded before the file loaded; merged in when it does
        public bool Loading, Dirty;
    }

    private bool _keepWhisperHistory = true;   // config "whisperHistory.keep" (.Config.cs)
    private readonly Dictionary<long, SavedPeer> _saved = new();
    private readonly List<ChatMessage> _histPreKey = new();
    private string? _histKey;                 // "whisper.<uid>" once known
    private long _histChar;                   // the charId the state belongs to
    private int _histGen;                     // bumped on clear / character change → late I/O results are dropped
    private bool _histIndexLoaded, _histIndexDirty, _histAnyDirty;
    private float _histFlushAt, _uidNextTry, _uidSince;
    private bool _uidLogged;                  // the first UID read result of this login is logged (diagnostics)
    private int _histStatsRev;                // bumped on any count change (Settings stats cache)
    private Task _histIo = Task.CompletedTask;

    private static string PeerFile(string key, long peer) => key + "/" + peer.ToString(CultureInfo.InvariantCulture) + ".jsonl";
    private static string IndexFile(string key) => key + "/peers.json";

    // Chain disk work onto the single I/O worker. Main thread only (the chain itself is the ordering).
    private void HistIo(Action work)
        => _histIo = _histIo.ContinueWith(_ =>
        {
            try { work(); }
            catch (Exception ex) { _services.Log.Warning($"[ChatTools] whisper history I/O failed: {ex.Message}"); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    // ── Character / folder key ──────────────────────────────────────────────────────────────────────────────────────
    // Framework.Update. A character change (logout = 0, or a switch) flushes the old character, then starts over.
    // Character + folder-key work runs ONLY in a stable logged-in world: after a logout from World the Phase can stay
    // World (no login view seen — Account Switcher) while the charId still reads the previous character and charBase is
    // already gone — that is what logged "UID read: nil:nil" at the title screen, and the 30 s fallback could then have
    // keyed a folder off it. Zone loads pause it too (harmless: the batch flush just waits).
    private void TickWhisperHistory(float _)
    {
        float now = _services.Framework.TimeNow;
        TickHistoryConfirm(now);   // .WhisperHistory.Settings.cs — the clear button's 5 s confirm timeout
        if (!InStableWorld()) { _uidSince = now; return; }
        long me = 0;
        try { me = _services.PlayerState.CharId; } catch { /* identity not ready */ }
        TickCharacterSwitch(me);   // .CharacterSwitch.cs — fallback reset when the charId changed with no logout edge
        if (me != _histChar)
        {
            FlushHistory();
            ResetHistoryState();
            _histChar = me;
            _uidSince = now;
            _uidNextTry = 0f;
            _uidLogged = false;
        }
        if (me == 0) return;
        if (_histKey == null) { TryResolveHistoryKey(me, now); return; }
        if (_histAnyDirty && now >= _histFlushAt) FlushHistory();
    }

    private void ResetHistoryState()
    {
        _histGen++;
        _histKey = null;
        _saved.Clear();
        _histIndexLoaded = _histIndexDirty = _histAnyDirty = false;
        _histStatsRev++;
        // Conversations that only existed because of the saved index belong to the previous character.
        RemoveSavedOnlyConversations();
        _logDirty = true;
    }

    private void TryResolveHistoryKey(long me, float now)
    {
        // CharId is known from the character select on, but charBase only describes us once in the world — so the
        // fallback clock only runs in the world (idling at the character select must not trip the charId fallback).
        bool canRead = false;
        try { canRead = _services.Lua.Ready && InStableWorld(); } catch { /* not ready */ }
        if (!canRead) { _uidSince = now; return; }
        if (now < _uidNextTry) return;
        _uidNextTry = now + UidRetrySec;
        string? uid = null;
        string? raw = null;
        try
        {
            // "<charId>:<showId>|<type>,<type>" from the record the game's own UID labels read (fighterbtns_view
            // refreshPlayerInfo: CharSerialize.charBase.showId); integers printed exactly. A failure parks "err|<msg>".
            // ⚠️ rawset, never `X=…`: the game's strict _G guard REJECTS writes to undeclared globals. The old plain
            // assignment (first statement inside the pcall) would error on every try, so nothing ever landed and the
            // key fell back to c<charId> — the likely cause of the failed read; the log line below confirms it in game
            // (Lua-Injection-from-CSharp.md "_G strict-global guard").
            _services.Lua.DoString(
                "rawset(_G,'" + UidGlobal + "',nil) " +
                "local ok,r=pcall(function() " +
                "local function s(v) if math.type and math.type(v)=='integer' then return tostring(v) " +
                "elseif type(v)=='number' then return string.format('%.0f',v) end return tostring(v) end " +
                "local b=Z.ContainerMgr.CharSerialize.charBase " +
                "return s(b.charId)..':'..s(b.showId)..'|'..type(b.charId)..','..type(b.showId) end) " +
                "rawset(_G,'" + UidGlobal + "',ok and r or ('err|'..tostring(r)))");
            raw = _services.Lua.ReadGlobalString(UidGlobal);
            uid = ParseUid(raw, me);
        }
        catch { /* Lua not usable yet — retry */ }
        // One line per login, so a failing read shows WHY (nil = the chunk never ran / the park was rejected).
        if (!_uidLogged) { _uidLogged = true; _services.Log.Info($"[ChatTools] whisper history UID read: {raw ?? "<nil>"}"); }
        if (uid == null && now - _uidSince >= UidFallbackSec)
        {
            uid = LegacyUid(me);
            _services.Log.Warning($"[ChatTools] whisper history UID never read (last: {raw ?? "<nil>"}); using the charId folder");
        }
        if (uid == null) return;
        _services.Log.Info($"[ChatTools] whisper history folder: whisper.{uid}");
        SetHistoryKey("whisper." + uid);
    }

    private void SetHistoryKey(string key)
    {
        _histKey = key;
        int gen = _histGen;
        // A UID key absorbs this character's charId-fallback folder first (.WhisperHistory.Migrate.cs), on the same
        // worker chain, so the index read below — and every peer load queued after it — already sees the merged files.
        var legacy = "whisper." + LegacyUid(_histChar);
        string charName = SelfName() ?? "";
        bool migrate = _histChar != 0 && !string.Equals(key, legacy, StringComparison.Ordinal);
        HistIo(() =>
        {
            if (migrate) MigrateLegacyFolder(legacy, key, charName);
            var list = WhisperHistoryFormat.ReadIndex(_services.Data.Read(IndexFile(key)));
            _services.Framework.Post(() => OnIndexLoaded(gen, list));
        });
        // Whispers that arrived before the key was known (login) are recorded now.
        if (_histPreKey.Count > 0)
        {
            foreach (var m in _histPreKey) RecordSaved(m);
            _histPreKey.Clear();
        }
    }

    private void OnIndexLoaded(int gen, List<SavedPeerInfo> list)
    {
        if (gen != _histGen) return;
        foreach (var p in list)
        {
            if (_saved.TryGetValue(p.CharId, out var sp))
            {
                // Recorded already this session: keep the live info, fill what it lacks.
                if (sp.Info.Name.Length == 0) sp.Info.Name = p.Name;
                if (sp.Info.Level == 0) sp.Info.Level = p.Level;
                if (p.LastMs > sp.Info.LastMs) { sp.Info.LastMs = p.LastMs; sp.Info.Preview = p.Preview; sp.Info.Kind = p.Kind; }
                if (sp.Msgs == null) sp.Info.Count = p.Count;   // until the file loads (its pending lines join on load)
            }
            else _saved[p.CharId] = new SavedPeer { Info = p };
            AddSavedConversation(p);   // .Whisper.cs
        }
        _histIndexLoaded = true;
        _histStatsRev++;
        if (_histIndexDirty) MarkHistDirty();
    }

    // ── Recording ───────────────────────────────────────────────────────────────────────────────────────────────────
    // From OnChatMessage for every message (main thread), AFTER NoteWhisper so the peer rules match the list's.
    private void NoteWhisperHistory(ChatMessage m)
    {
        if (!_keepWhisperHistory || m.Channel != ChatChannel.Whisper || IsUnrenderable(m)) return;
        // Park it while the key is unknown — or while the key belongs to ANOTHER character than the one now logged in (a
        // switch the logout edge missed: the new character's replay must never be written into the old one's folder).
        long cur = 0;
        try { cur = _services.PlayerState.CharId; } catch { /* identity not ready */ }
        if (_histKey == null || (cur != 0 && cur != _histChar)) { if (_histPreKey.Count < PreKeyCap) _histPreKey.Add(m); return; }
        RecordSaved(m);
    }

    private bool InStableWorld()
    {
        var cs = _services.ClientState;
        return cs.IsLoggedIn && cs.IsWorldActive && cs.Phase == GamePhase.World;
    }

    private void RecordSaved(ChatMessage m)
    {
        long me = SelfCharId();
        long peer = PeerOf(m);   // .Whisper.cs
        if (peer == 0 || peer == me) return;
        bool self = IsSelf(m);
        var w = new SavedWhisper
        {
            MsgId = m.MsgId, TsMs = new DateTimeOffset(DateTime.SpecifyKind(m.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            SenderId = m.SenderId != 0 ? m.SenderId : self ? me : peer, PeerId = peer, Self = self,
            SenderName = m.SenderName ?? "", Text = m.Text ?? "", StickerId = m.StickerId, Type = (int)m.Type,
        };
        var sp = GetOrCreateSaved(peer);
        var info = sp.Info;
        if (!self && w.SenderName.Length > 0) info.Name = w.SenderName;
        if (!self && m.Sender is { Level: > 0 } s) info.Level = s.Level;
        if (w.TsMs >= info.LastMs) { info.LastMs = w.TsMs; info.Preview = w.Text; info.Kind = w.StickerId != 0 ? 3 : 0; }
        _histIndexDirty = true;
        if (sp.Msgs == null)
        {
            (sp.Pending ??= new List<SavedWhisper>()).Add(w);
            BeginLoadPeer(peer, sp);
            MarkHistDirty();   // the index (name / preview) changed either way
            return;
        }
        if (!WhisperHistoryFormat.AddSorted(sp.Msgs, w)) return;
        WhisperHistoryFormat.Compact(sp.Msgs);
        info.Count = sp.Msgs.Count;
        sp.Dirty = true;
        _histStatsRev++;
        MarkHistDirty();
    }

    private SavedPeer GetOrCreateSaved(long peer)
    {
        if (!_saved.TryGetValue(peer, out var sp))
            _saved[peer] = sp = new SavedPeer { Info = new SavedPeerInfo { CharId = peer } };
        return sp;
    }

    private void MarkHistDirty()
    {
        if (_histAnyDirty) return;   // keep the first deadline: a steady stream still flushes every 2 s
        _histAnyDirty = true;
        _histFlushAt = _services.Framework.TimeNow + HistFlushDelaySec;
    }

    // ── Lazy peer-file load (conversation opened, or a whisper recorded for it) ─────────────────────────────────────
    private void EnsureSavedLoaded(long peer)
    {
        if (_histKey == null || peer == 0 || !_saved.TryGetValue(peer, out var sp)) return;   // unknown peer = no file
        BeginLoadPeer(peer, sp);
    }

    private void BeginLoadPeer(long peer, SavedPeer sp)
    {
        if (sp.Msgs != null || sp.Loading || _histKey == null) return;
        sp.Loading = true;
        var name = PeerFile(_histKey, peer);
        int gen = _histGen;
        HistIo(() =>
        {
            var list = WhisperHistoryFormat.ReadLines(_services.Data.Read(name), peer);
            _services.Framework.Post(() => OnPeerLoaded(gen, peer, sp, list));
        });
    }

    private void OnPeerLoaded(int gen, long peer, SavedPeer sp, List<SavedWhisper> list)
    {
        if (gen != _histGen) return;
        sp.Loading = false;
        sp.Msgs = list;
        bool changed = WhisperHistoryFormat.Compact(list);
        if (sp.Pending != null)
        {
            foreach (var w in sp.Pending) changed |= WhisperHistoryFormat.AddSorted(list, w);
            sp.Pending = null;
            changed |= WhisperHistoryFormat.Compact(list);
        }
        sp.Info.Count = list.Count;
        _histStatsRev++;
        if (changed) { sp.Dirty = true; _histIndexDirty = true; MarkHistDirty(); }
        for (int i = Math.Max(0, list.Count - PrewarmSavedRows); i < list.Count; i++)
            if (list[i].StickerId == 0 && list[i].Text.Length > 0) _services.Windows.PrewarmText(ChatMarkup.SanitizeWire(list[i].Text));
        if (peer == _selectedPeer) _logDirty = true;
    }

    // ── Flush (2 s batches, chat window close, character change, dispose) ───────────────────────────────────────────
    private void FlushHistory()
    {
        _histAnyDirty = false;
        if (_histKey == null) return;
        var key = _histKey;
        List<(string Name, SavedWhisper[] Msgs)>? files = null;
        foreach (var kv in _saved)
        {
            var sp = kv.Value;
            if (!sp.Dirty || sp.Msgs == null) continue;   // an unloaded file is never written (it would lose its old lines)
            sp.Dirty = false;
            (files ??= new()).Add((PeerFile(key, kv.Key), sp.Msgs.ToArray()));
        }
        SavedPeerInfo[]? index = null;
        string charName = SelfName() ?? "";
        // Never write the index before it has been read — a partial one would drop every peer not yet seen this session.
        if (_histIndexDirty && _histIndexLoaded)
        {
            _histIndexDirty = false;
            var tmp = new List<SavedPeerInfo>(_saved.Count);
            foreach (var sp in _saved.Values) if (sp.Info.LastMs > 0) tmp.Add(sp.Info.Clone());
            index = tmp.ToArray();
        }
        if (files == null && index == null) return;
        HistIo(() =>
        {
            if (files != null)
                foreach (var f in files) _services.Data.Write(f.Name, WhisperHistoryFormat.WriteLines(f.Msgs));
            if (index != null) _services.Data.Write(IndexFile(key), WhisperHistoryFormat.WriteIndex(charName, index));
        });
    }

    // Dispose: write what's pending and give the worker a moment (the game is closing or the plugin unloading).
    private void DisposeWhisperHistory()
    {
        try
        {
            FlushHistory();
            _histGen++;
            _histIo.Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* best effort */ }
    }

    // ── Display merge (from .Log.cs EnsureFiltered, Whisper tab with a peer open) ───────────────────────────────────
    // server = the peer's ring messages, sorted by Timestamp. Appends every saved message the server didn't already
    // provide, then returns the time-sorted union. A saved copy is hidden when a server message has the same server id,
    // or — for any pair — the same sender + content within ±2 s (an echo and its history copy can disagree on the id;
    // never double-show wins over keeping two identical lines sent within 2 s, which the server copy still shows).
    private List<ChatMessage> MergeSavedWhispers(long peer, List<ChatMessage> server)
    {
        if (!_saved.TryGetValue(peer, out var sp) || sp.Msgs is not { Count: > 0 } saved) return server;
        long me = SelfCharId();
        var ids = new HashSet<long>();
        var srv = new (long Ts, long From, int St, string Text)[server.Count];
        for (int i = 0; i < server.Count; i++)
        {
            var m = server[i];
            if (m.MsgId != 0) ids.Add(m.MsgId);
            long from = m.SenderId != 0 ? m.SenderId : IsSelf(m) ? me : peer;
            srv[i] = (new DateTimeOffset(DateTime.SpecifyKind(m.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), from, m.StickerId, m.Text ?? "");
        }
        var merged = new List<ChatMessage>(server.Count + saved.Count);
        int si = 0, lo = 0;
        foreach (var w in saved)
        {
            if (w.MsgId != 0 && ids.Contains(w.MsgId)) continue;
            while (lo < srv.Length && srv[lo].Ts < w.TsMs - WhisperHistoryFormat.DupWindowMs) lo++;
            bool dup = false;
            for (int j = lo; j < srv.Length && srv[j].Ts <= w.TsMs + WhisperHistoryFormat.DupWindowMs; j++)
                if (WhisperHistoryFormat.SameContent(srv[j].Ts, srv[j].From, srv[j].St, srv[j].Text, w.TsMs, w.SenderId, w.StickerId, w.Text))
                { dup = true; break; }
            if (dup) continue;
            var view = w.View ??= SavedView(w, me);
            while (si < server.Count && server[si].Timestamp <= view.Timestamp) merged.Add(server[si++]);
            merged.Add(view);
        }
        while (si < server.Count) merged.Add(server[si++]);
        return merged;
    }

    // A saved line as a display record. IsHistory = true: it never counts as unread or "new below".
    private static ChatMessage SavedView(SavedWhisper w, long me)
        => new(ChatChannel.Whisper, w.SenderName, w.SenderId, w.Text, w.Utc, (ChatMessageType)w.Type, default, null, w.MsgId, IsHistory: true)
        {
            StickerId = w.StickerId,
            PeerId = w.PeerId,
            TargetId = w.Self ? w.PeerId : me,
        };
}
