using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Real player-photo avatars for the chat bubbles (replacing the old name-hashed colour chips). Two-tier cache,
/// built to keep GC + GPU memory flat across a long session:
///   • <b>In-memory LRU</b> of decoded <see cref="Texture2D"/> keyed by charId, bounded (<see cref="AvatarLruCap"/>);
///     the evicted texture is <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>'d to free GPU memory.
///   • <b>Persistent store</b> (survives relogs/offline): the framework per-plugin data store, rooted at
///     <c>stellar/plugindata/stellar.chattools.data/avatar/&lt;charId&gt;.png</c> — NOT the plugin's own folder, so the
///     files aren't swept by <c>PluginHost</c>'s recursive DLL scan. A person's photo is downloaded once, then
///     always re-read from the store.
///   • <b>Negative cache</b> + <b>in-flight guard</b>: a charId the RPC reports as having no custom photo never
///     re-RPCs (resolves to the shared default face); a charId currently resolving never issues a second request.
///
/// The single shared <see cref="_defaultAvatar"/> (the game's default-avatar.png) is handed out BY REFERENCE for
/// everyone with no custom photo and as the placeholder while a real photo loads — never copied.
///
/// Threading: all cache state (LRU / negative / in-flight / default / decode queue) is touched ONLY on the Unity main
/// thread — <see cref="AvatarTextureFor"/> runs in the render poll, <see cref="IPlayerProfile"/>'s callback fires on
/// the main thread, and every background completion hops back via <c>IFramework.Post</c>. The only off-thread work is
/// the HTTP GET + the store READ/WRITE (pure byte IO, touching no shared collection and no Unity API), so no locking
/// is needed. Every Unity / IO / network call is wrapped in try/catch — a failed avatar must only fall back to the
/// default, never throw into the window render path.
///
/// Off the apply path (login-freeze fix, 2026-10-23): the render poll NEVER reads disk or decodes. It used to read +
/// PNG-decode + bake the rounded corners synchronously for each uncached sender (~24 ms each; 22 of them = 522 ms
/// inside ONE window apply at login). Now a miss starts a background store read; the bytes come back to the main
/// thread and join <see cref="_decodeQueue"/>, which <see cref="TickAvatarDecode"/> drains under a fixed per-tick
/// budget (<see cref="AvatarDecodesPerTick"/> / <see cref="AvatarDecodeBudgetMs"/>) — fresh downloads go through the
/// same queue. Until its texture is ready a row shows the shared default face (or nothing, the first frames before
/// the default itself is decoded) and swaps to the photo on the next apply after it decodes.
/// </summary>
public sealed partial class Plugin
{
    // The game's server-returned default face (same URL the game's own snapshot regex validates and the MultiTool
    // profile card uses). Downloaded + decoded ONCE, cached to disk as _default.png, shared by every row.
    private const string DefaultAvatarUrl = "https://bpm-sea-photo.17996cdn.net/avatar/default-avatar.png";

    private const int AvatarLruCap = 64;         // decoded textures kept live; the rest fall back to disk/default
    private const int PngMaxBytes  = 5_000_000;  // reject an absurd payload — avatars are tiny

    // One client for all avatar downloads (socket reuse); short timeout so a dead url can't pile up. Mirrors the
    // sibling MultiTool InviteProfileResolver posture.
    private static readonly HttpClient AvatarHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    // One LRU node = a charId + its decoded texture. Class (not struct) so node.Value fields can be mutated in place.
    private sealed class AvatarEntry
    {
        public long Id;
        public Texture2D Tex = null!;
    }

    // LRU: most-recently-used at First, eviction victim at Last. _lruIndex gives O(1) lookup → O(1) move-to-front.
    private readonly LinkedList<AvatarEntry> _lru = new();
    private readonly Dictionary<long, LinkedListNode<AvatarEntry>> _lruIndex = new();

    private readonly HashSet<long> _negative = new();   // no custom photo / terminal failure → default, never re-RPC
    private readonly HashSet<long> _inFlight = new();   // resolve/download running → never issue a second request

    private Texture2D? _defaultAvatar;   // shared default face (null until downloaded/decoded)
    private bool _defaultStarted;        // guard: kick the default download/decode exactly once
    private bool _defaultRetried;        // the one re-fetch after an undecodable default has been spent

    // Main-thread decode budget (fixed — no adaptive tuning): at most this many avatar decodes per framework tick, and
    // none started once this many ms of the tick have gone to them. One decode + corner bake is ~1-24 ms, so in
    // practice the count cap wins: one avatar per tick, ~a frame each, instead of every uncached sender in one apply.
    private const int AvatarDecodesPerTick = 1;
    private const double AvatarDecodeBudgetMs = 4.0;

    // Bytes waiting to become textures (main thread only). FromDisk picks the failure path: a corrupt STORE file is
    // deleted + re-downloaded; a corrupt download is negative-cached. The charId stays in _inFlight while queued, so
    // the poll keeps returning the placeholder and never issues a second read/request.
    private readonly Queue<(long Id, byte[] Bytes, bool FromDisk)> _decodeQueue = new();
    private byte[]? _defaultBytes;   // the default face's bytes awaiting decode — drained before any player avatar
    private bool _avatarsDisposed;   // late background completions after Dispose are dropped

    // Data-store names (IPluginDataStore: one subdir + file, no rooted/backslash/.. parts). Pure string ops → safe
    // to build from the download thread too. The store roots these under stellar/plugindata/stellar.chattools.data/.
    private const string DefaultStoreName = "avatar/_default.png";
    private static string AvatarStoreName(long charId) =>
        "avatar/" + charId.ToString(CultureInfo.InvariantCulture) + ".png";

    // ---- Render entry point ---------------------------------------------------------------------------------
    // Cheap + non-blocking: called every frame per visible bubble row (the GameTextureElement poll Func). Returns a
    // boxed UnityEngine.Texture (GameTextureElement only rebinds when the reference changes), or null if even the
    // default face isn't decoded yet. Never throws — any failure falls back to the default.
    private object? AvatarTextureFor(long charId)
    {
        try
        {
            EnsureDefaultAvatar();   // idempotent — kicks the shared default download/decode the first time

            if (charId == 0) return _defaultAvatar;   // unknown sender → default

            // 1. LRU hit → move-to-front, return. (Unity's fake-null == catches a destroyed texture.)
            if (_lruIndex.TryGetValue(charId, out var node))
            {
                if (node.Value.Tex != null)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Tex;
                }
                _lru.Remove(node);   // destroyed out from under us — drop and fall through to re-resolve
                _lruIndex.Remove(charId);
            }

            // 2/3. Terminal-no-photo, or a store read / queued decode / resolve already running → default placeholder.
            if (_negative.Contains(charId) || _inFlight.Contains(charId)) return _defaultAvatar;

            // 4. Persistent store → read it in the BACKGROUND, decode later under the per-tick budget (never here: this
            //    is the window apply). A store miss continues into the RPC resolve from OnStoreRead. Default meanwhile.
            BeginStoreRead(charId);
            return _defaultAvatar;
        }
        catch { return _defaultAvatar; }
    }

    // ---- Store read + budgeted decode ------------------------------------------------------------------------

    // Main thread. Mark in-flight (dedupes: the poll returns the placeholder and never starts a second read), then
    // read the store file on a worker — bytes only, no Unity API off the main thread.
    private void BeginStoreRead(long charId)
    {
        _inFlight.Add(charId);
        var name = AvatarStoreName(charId);
        _ = Task.Run(() =>
        {
            byte[]? bytes = null;
            try { bytes = _services.Data.Read(name); } catch { /* treated as a miss → resolve */ }
            _services.Framework.Post(() => OnStoreRead(charId, bytes));
        });
    }

    // Main thread (via Post). Hit → queue the decode (stays in-flight until it lands). Miss → hand over to the RPC
    // resolve, which takes over the in-flight guard itself (BeginResolve refuses an id that is already in flight).
    private void OnStoreRead(long charId, byte[]? bytes)
    {
        if (_avatarsDisposed) return;
        if (bytes != null) { _decodeQueue.Enqueue((charId, bytes, true)); return; }
        _inFlight.Remove(charId);
        BeginResolve(charId);
    }

    // Framework Update (main thread). Drain the decode queue under the fixed budget — the default face first (every
    // row's placeholder), then player avatars in arrival order. Rows pick the texture up on their next poll.
    private void TickAvatarDecode(float _)
    {
        if (_defaultBytes == null && _decodeQueue.Count == 0) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var done = 0;
        if (_defaultBytes != null)
        {
            var b = _defaultBytes;
            _defaultBytes = null;
            if (_defaultAvatar == null)
            {
                var tex = DecodePng(b);
                if (tex != null) _defaultAvatar = tex;
                // Undecodable (a corrupt store copy, or a bad download): drop the store copy and let the next poll's
                // EnsureDefaultAvatar re-fetch it — the old sync path likewise fell through to a download. Once only, so
                // a server that keeps serving an undecodable PNG can't loop downloads.
                else if (!_defaultRetried) { _defaultRetried = true; _services.Data.Delete(DefaultStoreName); _defaultStarted = false; }
            }
            done++;
        }
        while (done < AvatarDecodesPerTick && _decodeQueue.Count > 0
               && (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < AvatarDecodeBudgetMs)
        {
            var (id, bytes, fromDisk) = _decodeQueue.Dequeue();
            DecodeQueued(id, bytes, fromDisk);
            done++;
        }
    }

    // One budgeted decode. Success → LRU + clear in-flight. A corrupt STORE file is deleted so a fresh download can
    // replace it, then re-resolved; a corrupt DOWNLOAD is negative-cached (falls back to the default, never spins).
    private void DecodeQueued(long charId, byte[] bytes, bool fromDisk)
    {
        try
        {
            var hT = HitchProbe.Begin();   // diag: [Hitch] budgeted PNG decode + corner bake (now OUTSIDE the window apply)
            var tex = DecodePng(bytes);
            HitchProbe.End(fromDisk ? "ct:avatarDisk" : "ct:avatarDecodePost", hT);
            _inFlight.Remove(charId);
            if (tex != null) { LruInsert(charId, tex); return; }
            if (fromDisk) { _services.Data.Delete(AvatarStoreName(charId)); BeginResolve(charId); }
            else _negative.Add(charId);
        }
        catch (Exception ex)
        {
            _inFlight.Remove(charId);
            _negative.Add(charId);
            _services.Log.Warning($"[ChatTools] avatar {charId}: decode failed: {ex.Message}");
        }
    }

    // ---- Resolve pipeline -----------------------------------------------------------------------------------

    // Main thread. Ask the framework for the avatar URL; the callback fires exactly once, also on the main thread.
    private void BeginResolve(long charId)
    {
        if (charId == 0 || _inFlight.Contains(charId) || _negative.Contains(charId)) return;
        _inFlight.Add(charId);
        try
        {
            _services.PlayerProfile.RequestAvatarUrl(charId, url => OnAvatarUrl(charId, url));
        }
        catch (Exception ex)
        {
            _inFlight.Remove(charId);
            _services.Log.Warning($"[ChatTools] avatar resolve request failed {charId}: {ex.Message}");
        }
    }

    // Main thread (IPlayerProfile contract). null/empty url = the player has no custom photo → negative-cache it.
    private void OnAvatarUrl(long charId, string? url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) { _negative.Add(charId); _inFlight.Remove(charId); return; }
            StartAvatarDownload(charId, url!);   // keeps in-flight set until the download+decode completes
        }
        catch (Exception ex)
        {
            _inFlight.Remove(charId);
            _services.Log.Warning($"[ChatTools] avatar url handling failed {charId}: {ex.Message}");
        }
    }

    // Download the png off the game thread; validate it's really a PNG under the size cap; write it to disk (bg OK),
    // then hop to the main thread to decode + insert into the LRU. Any failure marks it negative so it falls back to
    // the default and never spins. Never throws out of the Task.
    private void StartAvatarDownload(long charId, string url)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await AvatarHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                if (!IsPng(bytes))
                {
                    _services.Log.Warning($"[ChatTools] avatar {charId}: not a PNG or too large ({bytes?.Length ?? 0} bytes)");
                    FailAvatar(charId);
                    return;
                }
                _services.Data.Write(AvatarStoreName(charId), bytes);   // persist for next session (bg thread; store never throws)
                // Texture2D creation is main-thread-only → hop back and join the BUDGETED decode queue (a burst of
                // downloads landing in one tick must not decode all at once either). Stays in-flight until decoded.
                _services.Framework.Post(() => { if (!_avatarsDisposed) _decodeQueue.Enqueue((charId, bytes, false)); });
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[ChatTools] avatar {charId}: download failed: {ex.Message}");
                FailAvatar(charId);
            }
        });
    }

    // Called from the download Task on a failure → mutate the main-thread-only state back on the main thread.
    private void FailAvatar(long charId)
        => _services.Framework.Post(() => { _inFlight.Remove(charId); _negative.Add(charId); });

    // ---- Default avatar -------------------------------------------------------------------------------------

    // Idempotent. Store first (survives offline), else download once + cache to the store — both on a worker. The
    // bytes come back to the main thread as _defaultBytes, which TickAvatarDecode decodes ahead of any player avatar
    // (never in the render poll that calls this). An undecodable result re-arms this (TickAvatarDecode).
    private void EnsureDefaultAvatar()
    {
        if (_defaultStarted) return;
        _defaultStarted = true;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[]? bytes = null;
                try { bytes = _services.Data.Read(DefaultStoreName); } catch { /* fall through to download */ }
                if (!IsPng(bytes))
                {
                    bytes = await AvatarHttp.GetByteArrayAsync(DefaultAvatarUrl).ConfigureAwait(false);
                    if (!IsPng(bytes))
                    {
                        _services.Log.Warning($"[ChatTools] default avatar: not a PNG or too large ({bytes?.Length ?? 0} bytes)");
                        return;
                    }
                    _services.Data.Write(DefaultStoreName, bytes);   // persist for next session (bg thread; store never throws)
                }
                _services.Framework.Post(() => { if (!_avatarsDisposed && _defaultAvatar == null) _defaultBytes = bytes; });
            }
            catch (Exception ex) { _services.Log.Warning($"[ChatTools] default avatar: download failed: {ex.Message}"); }
        });
    }

    // ---- LRU ------------------------------------------------------------------------------------------------

    // Insert a freshly-decoded texture at the front and evict the least-recently-used over the cap (destroying the
    // evicted texture's GPU memory). The "already present" branch is defensive — the callers only reach here on a
    // miss — but it still frees the old texture to avoid a leak.
    private void LruInsert(long charId, Texture2D tex)
    {
        if (_lruIndex.TryGetValue(charId, out var existing))
        {
            var old = existing.Value.Tex;
            existing.Value.Tex = tex;
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            if (old != null && !ReferenceEquals(old, tex)) SafeDestroy(old);
            return;
        }

        var node = _lru.AddFirst(new AvatarEntry { Id = charId, Tex = tex });
        _lruIndex[charId] = node;

        while (_lruIndex.Count > AvatarLruCap)
        {
            var victim = _lru.Last;
            if (victim == null) break;
            _lru.RemoveLast();
            _lruIndex.Remove(victim.Value.Id);
            SafeDestroy(victim.Value.Tex);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    // Decode a PNG into a mipmap-free RGBA32 texture (resizes to the PNG on LoadImage). Null on any failure.
    // Every avatar (per-player AND the shared default, which also resolves through here) is decoded via this one
    // method, so the rounded-corner bake lives here once — covering all paths without double-baking.
    private static Texture2D? DecodePng(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        Texture2D? tex = null;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (ImageConversion.LoadImage(tex, bytes))   // LoadImage uploads + leaves the texture readable
            {
                RoundCornersInPlace(tex);   // bake rounded-square corners into alpha → no per-row stencil Mask needed
                return tex;
            }
            SafeDestroy(tex);
            return null;
        }
        catch { if (tex != null) SafeDestroy(tex); return null; }
    }

    // Bake rounded-SQUARE corners into the texture's ALPHA so the chat bubble avatar needs NO per-item stencil Mask.
    // Every live uGUI Mask is a UI-batch break + extra stencil draw calls EVERY drawn frame (the at-rest residual
    // FPS cost); a baked alpha has none — the rounding is paid once here, on a tiny texture, at decode time. rPx
    // scales the on-screen corner radius (AvatarCorner px at an AvatarSize-px display) into THIS texture's pixel
    // space, so the curve matches the old stencil at any source PNG resolution. A ~1px coverage ramp across the arc
    // keeps the edge anti-aliased (no hard stair-step), mirroring the stencil mask's smoothing. Texture stays RGBA32.
    private static void RoundCornersInPlace(Texture2D tex)
    {
        int w = tex.width, h = tex.height;
        if (w <= 2 || h <= 2) return;
        float rPx = AvatarCorner * (Mathf.Min(w, h) / AvatarSize);
        if (rPx < 0.5f) return;                               // radius rounds to nothing at this size — leave it square
        int r = Mathf.Min(Mathf.CeilToInt(rPx), Mathf.Min(w, h) / 2);   // corner box side (clamped so boxes can't overlap)

        var px = tex.GetPixels32();
        // Each corner's quarter-circle arc centre is inset rPx from the two edges it touches; only the r×r corner
        // boxes are visited (everything else keeps full alpha). (x,y) origin is bottom-left (GetPixels32 row order).
        RoundCorner(px, w, 0,     0,     rPx,           rPx,           rPx, r);   // bottom-left
        RoundCorner(px, w, w - r, 0,     (w - 1) - rPx, rPx,           rPx, r);   // bottom-right
        RoundCorner(px, w, 0,     h - r, rPx,           (h - 1) - rPx, rPx, r);   // top-left
        RoundCorner(px, w, w - r, h - r, (w - 1) - rPx, (h - 1) - rPx, rPx, r);   // top-right
        tex.SetPixels32(px);
        tex.Apply(false);   // re-upload; no mipmap regen (texture is mip-free)
    }

    // Multiply alpha by the quarter-circle coverage over one r×r corner box whose arc centre is (cx,cy), radius rad.
    // Fully inside the arc → alpha untouched; fully outside → alpha 0; a ~1px linear ramp spans the boundary.
    private static void RoundCorner(Color32[] px, int w, int x0, int y0, float cx, float cy, float rad, int r)
    {
        for (int y = y0; y < y0 + r; y++)
        for (int x = x0; x < x0 + r; x++)
        {
            float dx = x - cx, dy = y - cy;
            float cov = Mathf.Clamp01(rad + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
            if (cov >= 1f) continue;                          // inside the arc → keep
            int idx = y * w + x;
            px[idx].a = (byte)(px[idx].a * cov);
        }
    }

    private static bool IsPng(byte[]? b) =>
        b != null && b.Length >= 4 && b.Length < PngMaxBytes
        && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static void SafeDestroy(UnityEngine.Object? o) { try { if (o != null) UnityEngine.Object.Destroy(o); } catch { } }

    // Teardown (called from Plugin.Dispose, after TickAvatarDecode is unsubscribed): free every live texture — the LRU
    // entries and the shared default — and drop pending decodes; late background completions see _avatarsDisposed.
    private void DisposeAvatars()
    {
        _avatarsDisposed = true;
        _decodeQueue.Clear();
        _defaultBytes = null;
        try
        {
            foreach (var node in _lru) SafeDestroy(node.Tex);
            _lru.Clear();
            _lruIndex.Clear();
            SafeDestroy(_defaultAvatar);
            _defaultAvatar = null;
        }
        catch { /* teardown — swallow */ }
    }
}
