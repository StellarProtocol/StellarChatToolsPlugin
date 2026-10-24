using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Avatar decode + rounded-corner bake helpers (split out of Plugin.Avatars.cs for the 500-LoC gate — a pure move). Every
/// avatar texture, per-player and the shared default, is made here; the cache / pipeline lives in Plugin.Avatars.cs.
/// </summary>
public sealed partial class Plugin
{
    // Decode a PNG and return a mipmap-free, rounded-corner RGBA32 texture. Null on any failure. Every avatar
    // (per-player AND the shared default, which also resolves through here) is decoded via this one method, so the
    // rounded-corner bake lives here once — covering all paths (chat rows, whisper list + header, friend picker,
    // store hit or fresh download) without double-baking.
    private Texture2D? DecodePng(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        Texture2D? src = null;
        try
        {
            src = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(src, bytes)) { SafeDestroy(src); return null; }   // stays readable
            var tex = BakeRoundedAvatar(src);
            if (!ReferenceEquals(tex, src)) SafeDestroy(src);
            return tex;
        }
        catch { if (src != null) SafeDestroy(src); return null; }
    }

    // Bake rounded-SQUARE corners into the ALPHA so no avatar needs a per-item stencil Mask (every live uGUI Mask is
    // a UI-batch break + extra stencil draw calls EVERY drawn frame — the at-rest FPS cost); the rounding is paid
    // once here, at decode time, inside the budgeted per-tick decode queue.
    //
    // ⚠️ LoadImage RE-FORMATS the texture to match the PNG: the RGBA32 we create is only a request. An RGB PNG (colour
    // type 2 — ~93% of the server's avatar photos, 300×300 RGB) comes back RGB24, which has NO alpha channel, so a
    // SetPixels32 bake on it is silently discarded and the corners stay square. Hence: if LoadImage didn't hand back
    // RGBA32, copy the pixels (GetPixels32 reports a=255 for RGB24) into an explicit RGBA32 texture and bake THAT.
    //
    // The radius is AvatarCornerFraction of the texture's short side, so it lands at the same fraction of whatever
    // size the element draws it at. A ~1px coverage ramp across the arc keeps the edge anti-aliased. The upload marks
    // the texture non-readable — nothing reads it back, and it frees the CPU-side copy of each LRU entry.
    private Texture2D BakeRoundedAvatar(Texture2D src)
    {
        int w = src.width, h = src.height;
        // ⚠️ MUST be an explicit managed Color32[]: the interop GetPixels32 returns Il2CppStructArray<Color32>, and
        // passing THAT to RoundCorner(Color32[]) runs Il2CppArrayBase<T>'s implicit T[] operator — a fresh COPY per
        // call. The corner alpha was written into four throwaway copies and SetPixels32 uploaded the untouched
        // original → square corners whatever the format. Convert once here; SetPixels32(px) copies it back in.
        Color32[] px = src.GetPixels32();
        var srcFormat = src.format;
        var dst = src;
        // Bake into an explicit RGBA32 texture whenever LoadImage re-formatted it (RGB24 has no alpha; ARGB32 does,
        // but a uniform RGBA32 keeps the bake + diagnostic one path).
        if (srcFormat != TextureFormat.RGBA32)
            dst = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        dst.filterMode = FilterMode.Bilinear;
        dst.wrapMode = TextureWrapMode.Clamp;

        float rPx = AvatarCornerFraction * Mathf.Min(w, h);
        if (w > 2 && h > 2 && rPx >= 0.5f)
        {
            int r = Mathf.Min(Mathf.CeilToInt(rPx), Mathf.Min(w, h) / 2);   // corner box side (clamped so boxes can't overlap)
            // Each corner's quarter-circle arc centre is inset rPx from the two edges it touches; only the r×r corner
            // boxes are visited (everything else keeps its alpha). (x,y) origin is bottom-left (GetPixels32 row order).
            RoundCorner(px, w, 0,     0,     rPx,           rPx,           rPx, r);   // bottom-left
            RoundCorner(px, w, w - r, 0,     (w - 1) - rPx, rPx,           rPx, r);   // bottom-right
            RoundCorner(px, w, 0,     h - r, rPx,           (h - 1) - rPx, rPx, r);   // top-left
            RoundCorner(px, w, w - r, h - r, (w - 1) - rPx, (h - 1) - rPx, rPx, r);   // top-right
        }
        dst.SetPixels32(px);
        if (!_loggedAvatarFormat)
        {
            // One-shot proof the bake reached the texture: read the corner back from the GPU-bound texture itself
            // (before Apply drops the CPU copy). Expect a=0 at (0,0) for any rounded avatar.
            _loggedAvatarFormat = true;
            byte cornerA = (byte)(dst.GetPixel(0, 0).a * 255f);
            byte centreA = (byte)(dst.GetPixel(w / 2, h / 2).a * 255f);
            _services.Log.Info($"[ChatTools] avatar bake: decoded {srcFormat} → displayed {dst.format} {w}x{h}, " +
                               $"corner r={AvatarCornerFraction * Mathf.Min(w, h):0.#}px, alpha corner(0,0)={cornerA} centre={centreA}");
        }
        dst.Apply(false, true);   // upload; no mipmaps (mip-free), drop the CPU copy
        return dst;
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
}
