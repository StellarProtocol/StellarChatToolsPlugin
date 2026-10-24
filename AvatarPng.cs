using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Stellar.ChatTools;

/// <summary>
/// Pure-managed PNG decode / area-downscale / corner-round / encode for avatars — NO Unity API, so all of it runs on a
/// worker thread. Why: <c>ImageConversion.LoadImage</c> decodes on the MAIN thread, and players' custom photos reach
/// 1254² / 2048² (in-game 2026-10-24: single decodes of 66–165 ms). The worker hands the main thread a ≤
/// <see cref="Plugin"/>.AvatarTexMax² RGBA buffer, which uploads in well under a millisecond (LoadRawTextureData).
///
/// Scope: 8-bit, non-interlaced PNGs of any colour type (gray, RGB, palette, gray+alpha, RGBA; tRNS honoured) — that
/// is every avatar in a 518-file cache sample (all 8-bit RGB/RGBA). Anything else (16-bit, sub-byte, Adam7, a corrupt
/// stream) returns false and the caller falls back to the main-thread LoadImage path, so coverage gaps cost speed,
/// never correctness. Ancillary chunks (gAMA, iCCP, …) are ignored — LoadImage ignores them too.
/// </summary>
internal static class AvatarPng
{
    private const int MaxSide = 4096;   // larger than any real avatar; bounds the inflate buffer

    // RGBA8, rows TOP-DOWN (PNG order).
    public static bool TryDecode(byte[] png, out byte[] rgba, out int w, out int h)
    {
        rgba = Array.Empty<byte>(); w = h = 0;
        try { return Decode(png, ref rgba, ref w, ref h); }
        catch { return false; }   // malformed → fallback decoder
    }

    private static bool Decode(byte[] png, ref byte[] rgba, ref int w, ref int h)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
        int depth = 0, type = -1, interlace = 0;
        byte[]? plte = null, trns = null;
        using var idat = new MemoryStream();
        for (int p = 8; p + 12 <= png.Length;)
        {
            int len = BE32(png, p);
            if (len < 0 || p + 12 + len > png.Length) return false;
            int t = p + 4, d = p + 8;
            if (Tag(png, t, "IHDR")) { w = BE32(png, d); h = BE32(png, d + 4); depth = png[d + 8]; type = png[d + 9]; interlace = png[d + 12]; }
            else if (Tag(png, t, "PLTE")) plte = Slice(png, d, len);
            else if (Tag(png, t, "tRNS")) trns = Slice(png, d, len);
            else if (Tag(png, t, "IDAT")) idat.Write(png, d, len);
            else if (Tag(png, t, "IEND")) break;
            p = d + len + 4;   // skip CRC
        }
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide || depth != 8 || interlace != 0) return false;
        int ch = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (ch == 0 || (type == 3 && plte == null)) return false;

        int stride = w * ch;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0, n;
            while (got < raw.Length && (n = z.Read(raw, got, raw.Length - got)) > 0) got += n;
            if (got < raw.Length) return false;   // truncated image data
        }
        if (!Unfilter(raw, stride, h, ch)) return false;
        rgba = ToRgba(raw, w, h, ch, type, plte, trns);
        return true;
    }

    // In place: row r's filtered bytes live at raw[r*(stride+1)+1 ..]; reconstructs them (PNG spec §9).
    private static bool Unfilter(byte[] raw, int stride, int h, int bpp)
    {
        for (int r = 0; r < h; r++)
        {
            int row = r * (stride + 1), cur = row + 1, prev = row - stride;   // prev row's data start (r>0)
            byte f = raw[row];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[cur + i - bpp] : 0;
                int b = r > 0 ? raw[prev + i] : 0;
                int c = r > 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                int add = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c), _ => -1 };
                if (add < 0) return false;
                raw[cur + i] = (byte)(raw[cur + i] + add);
            }
        }
        return true;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] ToRgba(byte[] raw, int w, int h, int ch, int type, byte[]? plte, byte[]? trns)
    {
        var o = new byte[w * h * 4];
        int stride = w * ch;
        for (int y = 0; y < h; y++)
        {
            int s = y * (stride + 1) + 1, d = y * w * 4;
            for (int x = 0; x < w; x++, s += ch, d += 4)
            {
                byte r, g, b, a = 255;
                switch (type)
                {
                    case 0: r = g = b = raw[s]; if (trns is { Length: >= 2 } && raw[s] == trns[1]) a = 0; break;
                    case 2:
                        r = raw[s]; g = raw[s + 1]; b = raw[s + 2];
                        if (trns is { Length: >= 6 } && r == trns[1] && g == trns[3] && b == trns[5]) a = 0;
                        break;
                    case 3:
                        int i = raw[s];
                        if (i * 3 + 2 >= plte!.Length) { r = g = b = 0; }
                        else { r = plte[i * 3]; g = plte[i * 3 + 1]; b = plte[i * 3 + 2]; }
                        if (trns != null && i < trns.Length) a = trns[i];
                        break;
                    case 4: r = g = b = raw[s]; a = raw[s + 1]; break;
                    default: r = raw[s]; g = raw[s + 1]; b = raw[s + 2]; a = raw[s + 3]; break;
                }
                o[d] = r; o[d + 1] = g; o[d + 2] = b; o[d + 3] = a;
            }
        }
        return o;
    }

    /// <summary>Area-average (box) downscale so the longer side is ≤ <paramref name="maxSide"/>; returns the input
    /// unchanged when already small enough. Alpha-weighted (premultiplied) so transparent pixels don't bleed their
    /// colour into the edge. Separable: horizontal pass into a w'×h float buffer, then vertical.</summary>
    public static byte[] Downscale(byte[] rgba, int w, int h, int maxSide, out int nw, out int nh)
    {
        nw = w; nh = h;
        if (Math.Max(w, h) <= maxSide) return rgba;
        double k = (double)maxSide / Math.Max(w, h);
        int ow = Math.Max(1, (int)Math.Round(w * k)), oh = Math.Max(1, (int)Math.Round(h * k));
        nw = ow; nh = oh;
        var wx = Weights(ow, w);
        var wy = Weights(oh, h);
        var tmp = new float[ow * h * 4];   // horizontal pass: premultiplied rgb (×a) + a, both on the 0..255 scale
        for (int y = 0; y < h; y++)
            foreach (var (dst, src, wt) in wx)
            {
                int si = (y * w + src) * 4, di = (y * ow + dst) * 4;
                float a = rgba[si + 3] * wt;
                tmp[di] += rgba[si] * a; tmp[di + 1] += rgba[si + 1] * a; tmp[di + 2] += rgba[si + 2] * a; tmp[di + 3] += a;
            }
        var acc = new float[ow * oh * 4];
        foreach (var (dst, src, wt) in wy)
            for (int x = 0; x < ow; x++)
            {
                int si = (src * ow + x) * 4, di = (dst * ow + x) * 4;
                acc[di] += tmp[si] * wt; acc[di + 1] += tmp[si + 1] * wt; acc[di + 2] += tmp[si + 2] * wt; acc[di + 3] += tmp[si + 3] * wt;
            }
        float norm = (float)(ow * (double)oh / ((double)w * h));   // each output pixel's total weight is 1/norm
        var o = new byte[ow * oh * 4];
        for (int i = 0; i < o.Length; i += 4)
        {
            float a = acc[i + 3];
            if (a <= 0f) continue;   // fully transparent → stays 0,0,0,0
            o[i]     = (byte)Math.Min(255f, acc[i] / a + 0.5f);
            o[i + 1] = (byte)Math.Min(255f, acc[i + 1] / a + 0.5f);
            o[i + 2] = (byte)Math.Min(255f, acc[i + 2] / a + 0.5f);
            o[i + 3] = (byte)Math.Min(255f, a * norm + 0.5f);
        }
        return o;
    }

    // 1-D area weights: output cell d covers source [d*s, (d+1)*s), s = srcLen/dstLen; each touched source cell j gets
    // the overlap length as its weight.
    private static List<(int Dst, int Src, float Wt)> Weights(int dstLen, int srcLen)
    {
        var list = new List<(int, int, float)>(srcLen + dstLen);
        double s = (double)srcLen / dstLen;
        for (int d = 0; d < dstLen; d++)
        {
            double a = d * s, b = Math.Min(srcLen, (d + 1) * s);
            for (int j = (int)a; j < b; j++)
            {
                float wt = (float)(Math.Min(b, j + 1) - Math.Max(a, j));
                if (wt > 0f) list.Add((d, j, wt));
            }
        }
        return list;
    }

    /// <summary>Rounded-square corners baked into the alpha — same geometry as the main-thread
    /// <c>BakeRoundedAvatar</c> (radius = <paramref name="fraction"/> × the short side, ~1 px coverage ramp). Symmetric,
    /// so row order doesn't matter.</summary>
    public static void RoundCorners(byte[] rgba, int w, int h, float fraction)
    {
        float rad = fraction * Math.Min(w, h);
        if (w <= 2 || h <= 2 || rad < 0.5f) return;
        int r = Math.Min((int)Math.Ceiling(rad), Math.Min(w, h) / 2);
        Corner(rgba, w, 0, 0, rad, rad, rad, r);
        Corner(rgba, w, w - r, 0, (w - 1) - rad, rad, rad, r);
        Corner(rgba, w, 0, h - r, rad, (h - 1) - rad, rad, r);
        Corner(rgba, w, w - r, h - r, (w - 1) - rad, (h - 1) - rad, rad, r);
    }

    private static void Corner(byte[] px, int w, int x0, int y0, float cx, float cy, float rad, int r)
    {
        for (int y = y0; y < y0 + r; y++)
        for (int x = x0; x < x0 + r; x++)
        {
            float dx = x - cx, dy = y - cy;
            float cov = Math.Clamp(rad + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
            if (cov >= 1f) continue;
            int a = (y * w + x) * 4 + 3;
            px[a] = (byte)(px[a] * cov);
        }
    }

    /// <summary>Reverse the row order in place (PNG is top-down; Unity texture data is bottom-row first).</summary>
    public static void FlipRows(byte[] rgba, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[stride];
        for (int top = 0, bot = h - 1; top < bot; top++, bot--)
        {
            Buffer.BlockCopy(rgba, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(rgba, bot * stride, rgba, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, rgba, bot * stride, stride);
        }
    }

    /// <summary>Minimal RGBA8 PNG (filter 0, zlib) — used to store the small copy in the avatar cache.</summary>
    public static byte[] Encode(byte[] rgba, int w, int h)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        using var o = new MemoryStream();
        o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE32(ihdr, 0, w); WriteBE32(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6;   // 8-bit RGBA, deflate, filter 0, no interlace
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", raw.ToArray());
        Chunk(o, "IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    private static void Chunk(Stream o, string tag, byte[] data)
    {
        var hdr = new byte[8];
        WriteBE32(hdr, 0, data.Length);
        for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)tag[i];
        o.Write(hdr);
        o.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, hdr, 4, 4), data, 0, data.Length) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        WriteBE32(c, 0, (int)crc);
        o.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    private static uint Crc(uint c, byte[] b, int off, int len)
    {
        for (int i = off; i < off + len; i++) c = CrcTable[(c ^ b[i]) & 0xFF] ^ (c >> 8);
        return c;
    }

    private static int BE32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static void WriteBE32(byte[] b, int i, int v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
    private static bool Tag(byte[] b, int i, string t) => b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3];
    private static byte[] Slice(byte[] b, int i, int len) { var s = new byte[len]; Buffer.BlockCopy(b, i, s, 0, len); return s; }
}
