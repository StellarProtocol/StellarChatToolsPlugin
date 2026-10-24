using Stellar.Abstractions.Diagnostics;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Off-main-thread avatar preparation (2026-10-24). After the in-place bake fix the remaining main-thread cost was the
/// NATIVE PNG decode itself (<c>ImageConversion.LoadImage</c>) plus readback/upload of full-size photos: players'
/// custom photos reach 1254² / 2048², and single decodes measured 66–165 ms in game. Now the worker that already reads
/// or downloads the bytes also decodes, area-downscales to ≤ <see cref="AvatarTexMax"/>² and rounds the corners
/// (<see cref="AvatarPng"/>, pure managed); the main thread only uploads a ≤128² RGBA buffer (one memcpy +
/// <c>Apply</c>, well under 1 ms).
///
/// The disk cache keeps the SMALL copy: when the source was larger than <see cref="AvatarTexMax"/> the worker writes a
/// re-encoded ≤128² PNG over <c>avatar/&lt;id&gt;.png</c> (stored UNROUNDED, so every load path rounds exactly once). The
/// index / 24 h refresh / 500-file cap are unchanged — a refresh re-downloads the original and shrinks it again.
/// A PNG the managed decoder doesn't cover (16-bit, palette &lt; 8 bit, Adam7, corrupt) yields null → the old main-thread
/// <see cref="DecodePng"/> path, so coverage gaps cost speed, never correctness.
///
/// Visuals: 128 px ≥ 2× the largest avatar box (40), so it is downsampled once with a proper area filter instead of
/// being minified at draw time from a mip-less 1–2k texture (which aliased); corner radius stays
/// <see cref="AvatarCornerFraction"/> of the short side.
/// </summary>
public sealed partial class Plugin
{
    private const int AvatarTexMax = 128;

    // Worker → main thread hand-off: bottom-up RGBA32 rows, corners already rounded. SrcW/SrcH = the decoded original.
    private sealed class AvatarPixels
    {
        public byte[] Rgba = null!;
        public int W, H, SrcW, SrcH;
    }

    // WORKER thread (no Unity API). Null when the managed decoder can't read it → main-thread LoadImage fallback.
    // smallPng = a ≤AvatarTexMax² re-encode to store in place of an oversized original (null when it was small already).
    private static AvatarPixels? PrepareAvatar(byte[]? png, out byte[]? smallPng)
    {
        smallPng = null;
        if (png == null) return null;
        try
        {
            if (!AvatarPng.TryDecode(png, out var rgba, out int w, out int h)) return null;
            var px = AvatarPng.Downscale(rgba, w, h, AvatarTexMax, out int nw, out int nh);
            if (!ReferenceEquals(px, rgba)) smallPng = AvatarPng.Encode(px, nw, nh);   // before rounding (see summary)
            AvatarPng.RoundCorners(px, nw, nh, AvatarCornerFraction);
            AvatarPng.FlipRows(px, nw, nh);   // PNG rows are top-down; texture data is bottom row first
            return new AvatarPixels { Rgba = px, W = nw, H = nh, SrcW = w, SrcH = h };
        }
        catch { smallPng = null; return null; }
    }

    // MAIN thread. byte[] → Il2CppStructArray<byte> is one memcpy (no per-element interop); mip-free, non-readable after
    // upload (drops the CPU copy), same filter/wrap as the baked path.
    private Texture2D? UploadAvatar(AvatarPixels p)
    {
        var t = HitchProbe.Begin();
        Texture2D? tex = null;
        try
        {
            tex = new Texture2D(p.W, p.H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.LoadRawTextureData(p.Rgba);
            tex.Apply(false, true);
            return tex;
        }
        catch { if (tex != null) SafeDestroy(tex); return null; }
        finally { HitchProbe.End("ct:av.upload", t); }
    }

    // Decode one queued avatar: the prepared pixels when the worker managed it, else the main-thread LoadImage path.
    // Records what ran for the "slowest so far" line.
    private Texture2D? MakeAvatarTexture(byte[] bytes, AvatarPixels? px)
    {
        if (px != null && UploadAvatar(px) is { } fast)
        {
            _lastDecodeDesc = (px.SrcW, px.SrcH, false);
            return fast;
        }
        var tex = DecodePng(bytes);
        _lastDecodeDesc = (tex != null ? tex.width : 0, tex != null ? tex.height : 0, true);
        return tex;
    }

    private (int W, int H, bool Fallback) _lastDecodeDesc;
}
