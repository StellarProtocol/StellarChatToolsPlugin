using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Stellar.ChatTools;

/// <summary>
/// Avatar cache upkeep (the pipeline itself is Plugin.Avatars.cs): failed-load retries, day-old photo refresh and the
/// disk-cache cap. All state is main-thread only except <see cref="_avatarTouched"/> (read by the startup prune task).
///
///   • <b>Retry with backoff.</b> A failed load (no URL — the profile RPC returns null for BOTH "no custom photo" and a
///     failed request — a failed download, an undecodable file) used to stick for the whole session. It now shows the
///     default face and is tried again after <see cref="AvatarRetryDelaysSec"/> (30 s, 2 min, 10 min): three retries per
///     session, then it is given up (the negative cache) until the next game start. Fixed delays (Session Rule 12).
///   • <b>Refresh.</b> A photo read from the disk cache whose last download is older than <see cref="AvatarMaxAgeMs"/>
///     (~24 h) — or that predates this index — is shown at once and re-downloaded in the background, once per session;
///     the new one replaces it when its decode lands (LruInsert frees the old texture). A failed refresh keeps the old.
///   • <b>Cap.</b> At startup a background task trims <c>avatar/</c> to the <see cref="AvatarDiskCap"/> most recently
///     used / downloaded files (files the index doesn't know rank oldest), skipping any id this session already touched.
///
/// The ages come from a small index, <c>avatar/_meta.json</c> (charId → last download + last use, Unix ms): the plugin
/// data store has no file times. It is written whole, at most every <see cref="AvatarMetaSaveDelaySec"/>, off the main
/// thread, and once more (synchronously) on dispose — never before the startup read has landed (it would drop the rest).
/// </summary>
public sealed partial class Plugin
{
    private static readonly float[] AvatarRetryDelaysSec = { 30f, 120f, 600f };
    private const long AvatarMaxAgeMs = 24L * 60 * 60 * 1000;
    private const int AvatarDiskCap = 500;
    private const float AvatarMetaSaveDelaySec = 30f;
    private const string AvatarMetaStoreName = "avatar/_meta.json";

    private readonly Dictionary<long, int> _avatarFails = new();      // failed loads this session
    private readonly Dictionary<long, float> _avatarRetryAt = new();  // IFramework.TimeNow at which the next try is due
    private readonly HashSet<long> _avatarRefreshed = new();         // refresh already started this session
    private readonly HashSet<long> _avatarStaleCheck = new();        // read from disk before the index loaded
    private readonly Dictionary<long, (long Fetched, long Used)> _avatarMeta = new();
    private readonly ConcurrentDictionary<long, byte> _avatarTouched = new();   // ids in use this session (prune skips them)
    private bool _avatarMetaLoaded, _avatarMetaDirty, _avatarCacheStarted;
    private float _avatarMetaSaveAt;

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Retry ────────────────────────────────────────────────────────────────────────────────────────────────────────
    private void NoteAvatarFailure(long charId)
    {
        _inFlight.Remove(charId);
        int n = _avatarFails.TryGetValue(charId, out var f) ? f + 1 : 1;
        _avatarFails[charId] = n;
        if (n > AvatarRetryDelaysSec.Length) { _avatarRetryAt.Remove(charId); _negative.Add(charId); return; }
        _avatarRetryAt[charId] = _services.Framework.TimeNow + AvatarRetryDelaysSec[n - 1];
    }

    // From the render poll: true while a failed load waits out its delay. Due → forget the wait (the caller starts over).
    private bool AvatarRetryPending(long charId)
    {
        if (!_avatarRetryAt.TryGetValue(charId, out var at)) return false;
        if (_services.Framework.TimeNow < at) return true;
        _avatarRetryAt.Remove(charId);
        return false;
    }

    // ── Refresh ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // From OnStoreRead on a disk hit (the cached photo is being decoded for display meanwhile).
    private void MaybeRefreshAvatar(long charId)
    {
        if (_avatarRefreshed.Contains(charId)) return;
        if (!_avatarMetaLoaded) { _avatarStaleCheck.Add(charId); return; }   // decided when the index lands
        if (_avatarMeta.TryGetValue(charId, out var m) && NowUnixMs() - m.Fetched < AvatarMaxAgeMs) return;
        _avatarRefreshed.Add(charId);
        try
        {
            _services.PlayerProfile.RequestAvatarUrl(charId, url =>
            {
                // No URL now: no photo any more, or the request failed — keep the cached one either way.
                if (!_avatarsDisposed && !string.IsNullOrEmpty(url)) StartAvatarDownload(charId, url!, refresh: true);
            });
        }
        catch { /* keep the cached photo */ }
    }

    // ── Index (avatar/_meta.json) ────────────────────────────────────────────────────────────────────────────────────
    private void NoteAvatarFetched(long charId)
    {
        long now = NowUnixMs();
        _avatarMeta[charId] = (now, now);
        MarkAvatarMetaDirty();
    }

    private void NoteAvatarUsed(long charId)
    {
        _avatarMeta[charId] = (_avatarMeta.TryGetValue(charId, out var m) ? m.Fetched : 0L, NowUnixMs());
        MarkAvatarMetaDirty();
    }

    private void MarkAvatarMetaDirty()
    {
        if (_avatarMetaDirty) return;   // keep the first deadline: a steady stream still writes every 30 s
        _avatarMetaDirty = true;
        _avatarMetaSaveAt = _services.Framework.TimeNow + AvatarMetaSaveDelaySec;
    }

    // From TickAvatarDecode (every framework tick). Starts the one-time load + prune, then writes the index when due.
    private void TickAvatarMetaSave()
    {
        if (!_avatarCacheStarted) { _avatarCacheStarted = true; StartAvatarCacheMaintenance(); }
        if (!_avatarMetaDirty || !_avatarMetaLoaded || _services.Framework.TimeNow < _avatarMetaSaveAt) return;
        _avatarMetaDirty = false;
        var bytes = WriteAvatarMeta(_avatarMeta);
        _ = Task.Run(() => _services.Data.Write(AvatarMetaStoreName, bytes));
    }

    // Dispose: the last write, synchronously (a background task could be cut off by the shutdown).
    private void FlushAvatarMeta()
    {
        if (!_avatarMetaDirty || !_avatarMetaLoaded) return;
        _avatarMetaDirty = false;
        try { _services.Data.Write(AvatarMetaStoreName, WriteAvatarMeta(_avatarMeta)); } catch { /* best effort */ }
    }

    // Background: read the index, cap the folder, post the (pruned) index back.
    private void StartAvatarCacheMaintenance()
        => _ = Task.Run(() =>
        {
            Dictionary<long, (long Fetched, long Used)> meta = new();
            int files = 0, pruned = 0;
            try
            {
                meta = ReadAvatarMeta(_services.Data.Read(AvatarMetaStoreName));
                var present = new List<(long Id, string Name, long Rank)>();
                foreach (var name in _services.Data.List("avatar/"))
                    if (TryAvatarId(name, out var id))
                        present.Add((id, name, meta.TryGetValue(id, out var m) ? Math.Max(m.Fetched, m.Used) : 0L));
                files = present.Count;
                present.Sort((a, b) => b.Rank.CompareTo(a.Rank));   // newest first; unknown (0) last
                for (int i = AvatarDiskCap; i < present.Count; i++)
                {
                    if (_avatarTouched.ContainsKey(present[i].Id)) continue;   // in use since boot — keep
                    _services.Data.Delete(present[i].Name);
                    meta.Remove(present[i].Id);
                    pruned++;
                }
                var keep = new HashSet<long>();
                foreach (var p in present) keep.Add(p.Id);
                foreach (var id in new List<long>(meta.Keys)) if (!keep.Contains(id)) meta.Remove(id);   // file gone
            }
            catch { /* best effort: an unreadable index just means everything refreshes once */ }
            _services.Framework.Post(() => OnAvatarMetaLoaded(meta, files, pruned));
        });

    private void OnAvatarMetaLoaded(Dictionary<long, (long Fetched, long Used)> loaded, int files, int pruned)
    {
        if (_avatarsDisposed) return;
        foreach (var kv in loaded) if (!_avatarMeta.ContainsKey(kv.Key)) _avatarMeta[kv.Key] = kv.Value;   // this session's wins
        _avatarMetaLoaded = true;
        if (pruned > 0) MarkAvatarMetaDirty();
        _services.Log.Info($"[ChatTools] avatar cache: {files} cached photos, {pruned} pruned (cap {AvatarDiskCap})");
        foreach (var id in _avatarStaleCheck) MaybeRefreshAvatar(id);
        _avatarStaleCheck.Clear();
    }

    // "avatar/<digits>.png" → the charId (skips _default.png / _meta.json).
    private static bool TryAvatarId(string name, out long id)
    {
        id = 0;
        const string pre = "avatar/", ext = ".png";
        if (!name.StartsWith(pre, StringComparison.Ordinal) || !name.EndsWith(ext, StringComparison.Ordinal)) return false;
        return long.TryParse(name.AsSpan(pre.Length, name.Length - pre.Length - ext.Length), NumberStyles.None,
                             CultureInfo.InvariantCulture, out id) && id != 0;
    }

    // { "v":1, "a":[ { "id":…, "f":<last download ms>, "u":<last use ms> }, … ] }
    private static byte[] WriteAvatarMeta(Dictionary<long, (long Fetched, long Used)> meta)
    {
        using var ms = new MemoryStream(32 + meta.Count * 48);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteStartArray("a");
            foreach (var kv in meta)
            {
                w.WriteStartObject();
                w.WriteNumber("id", kv.Key);
                w.WriteNumber("f", kv.Value.Fetched);
                w.WriteNumber("u", kv.Value.Used);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static Dictionary<long, (long Fetched, long Used)> ReadAvatarMeta(byte[]? bytes)
    {
        var map = new Dictionary<long, (long, long)>();
        if (bytes == null || bytes.Length == 0) return map;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("a", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
            foreach (var e in arr.EnumerateArray())
            {
                long id = MetaLong(e, "id");
                if (id != 0) map[id] = (MetaLong(e, "f"), MetaLong(e, "u"));
            }
        }
        catch { /* unreadable → empty (every cached photo refreshes once) */ }
        return map;
    }

    private static long MetaLong(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;
}
