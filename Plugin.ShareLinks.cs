using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// The game's SHARE messages (approved prototype _scratch\prototypes\chattools-links.html): position / item / player /
/// home / blueprint shares, the server's templated notices (loot rolls) and voice messages. Before framework 2.35 the
/// wire decode dropped their payload, so <see cref="IsUnrenderable"/> hid them; now <see cref="ChatMessage.Hypertext"/>
/// carries ordered text + link <see cref="ChatTextSegment"/>s (names already resolved and sanitised by the framework) and
/// <see cref="ChatMessage.Voice"/> the length + speech-to-text transcript (Chat-Hypertext-Links.md "Framework 2.35 API").
///
/// This file is the MODEL: each message is classified once into a <see cref="ShareView"/> (which row layout it needs, its
/// pre-composed TMP rich-text pieces), cached per pool slot and rebuilt only when the slot shows a different message or
/// the panel flips light/dark (the item / link colours are baked into the strings). The rows are .ShareLinks.Rows.cs,
/// the composer's "share my position" is .ShareLinks.Compose.cs.
///
/// Layouts: a position share gets its own bubble (the link is a CLICKABLE element, which a span inside wrapping text
/// can't be — see .ShareLinks.Rows.cs); a voice message a mic + "6s ·" + transcript bubble; a home share the home card;
/// a non-share notice (loot roll) one system-style line; every other share (item, player, blueprint…) is the ordinary
/// text bubble with the links styled inline (item = its quality colour, the rest the info blue, underlined).
/// </summary>
public sealed partial class Plugin
{
    private enum ShareRow { None, Inline, Position, Voice, Home, Notice }

    private sealed class ShareView
    {
        public ChatMessage Msg = null!;
        public bool Light;                 // panel brightness the colours were baked for
        public ShareRow Row;
        public string Body = "";           // Inline / Notice: the whole message as TMP rich text; Home: its card text
        // Position: the text before / after the link (rich text, "" = none), the link label and where it points.
        public string Before = "", After = "", LinkRich = "";
        public ChatPosition? Pos;
        // Voice: length + transcript; the "6s ·" head is composed lazily (language + muted colour, VoiceText).
        public int Seconds;
        public string Transcript = "";
        public string? VoiceTpl, VoiceMuted, VoiceRich;
        // Home: the "<Name>'s Home" line (language-dependent, composed lazily) and a notice line cache.
        public string? HomeTpl, HomeOwner;
        public string? NoticeLabel, NoticeMuted, NoticeLine;
        public ColorRgba NoticeChan;
    }

    private readonly ShareView?[] _shareViews = new ShareView?[LogPoolSize];

    // Game item-quality colours (E.ItemQualityColor, common\enum_define.lua:124) — the game colours a shared item's name
    // by its ItemTable Quality. On a light panel they are darkened for contrast (ItemQualityLightMix toward black).
    private static readonly ColorRgba[] ItemQualityDark =
    {
        new(0xA4 / 255f, 0xAA / 255f, 0xB2 / 255f), new(0x89 / 255f, 0xBD / 255f, 0x92 / 255f), new(0xAD / 255f, 0xCA / 255f, 0xFF / 255f),
        new(0xAB / 255f, 0x85 / 255f, 0xF6 / 255f), new(0xEE / 255f, 0xD3 / 255f, 0x35 / 255f), new(0xEF / 255f, 0x8E / 255f, 0x60 / 255f),
    };
    private const float ItemQualityLightMix = 0.40f;
    private string[]? _qualityHexDark, _qualityHexLight;

    // ── Classification ────────────────────────────────────────────────────────────────────────────────────────────

    // A share / voice message worth showing even with no readable msg_text (.Log.cs IsUnrenderable keeps it).
    private static bool HasShareContent(ChatMessage m) => m.Voice != null || m.Hypertext is { Segments.Count: > 0 };

    private bool LightPanel() => Luminance(_services.Theme.Colors.MenuBackground) >= 0.179f;

    // The slot's view, or null for an ordinary message (no allocation for those — the common case).
    private ShareView? ShareViewFor(int slot)
    {
        if ((uint)slot >= (uint)LogPoolSize || !TryMsg(slot, out var m) || !HasShareContent(m)) return null;
        bool light = LightPanel();
        var v = _shareViews[slot];
        if (v != null && ReferenceEquals(v.Msg, m) && v.Light == light) return v;
        return _shareViews[slot] = BuildShareView(m, light);
    }

    private ShareRow ShareRowOf(int slot) => ShareViewFor(slot)?.Row ?? ShareRow.None;

    private bool IsPosShareRow(int slot) => ShareRowOf(slot) == ShareRow.Position;
    private bool IsVoiceRow(int slot) => ShareRowOf(slot) == ShareRow.Voice;
    private bool IsHomeRow(int slot) => ShareRowOf(slot) == ShareRow.Home;
    // A templated notice outside the System channel (a loot roll on Party): the System line look, own channel tag. One
    // on the System channel already takes the System notice branch, whose text reads ShareBodyOr below.
    private bool IsHyperNoticeRow(int slot)
        => ShareViewFor(slot) is { Row: ShareRow.Notice } v && v.Msg.Channel != ChatChannel.System;

    // The text bubble / compact line / System notice body for a share: its rich composition, or null = the plain text.
    private string? ShareBodyOr(int slot)
        => ShareViewFor(slot) is { Row: ShareRow.Inline or ShareRow.Notice or ShareRow.Home } v ? v.Body : null;

    private ShareView BuildShareView(ChatMessage m, bool light)
    {
        var v = new ShareView { Msg = m, Light = light };
        if (m.Voice is { } voice)
        {
            v.Row = ShareRow.Voice;
            v.Seconds = Math.Max(0, voice.Seconds);
            v.Transcript = ChatMarkup.EscapeForRich(voice.Transcript);
            return v;
        }
        var segs = m.Hypertext!.Segments;
        if (!m.Hypertext.IsShare) { v.Row = ShareRow.Notice; v.Body = Compose(segs, 0, segs.Count, notice: true, light); return v; }
        if (m.Hypertext.Kind == ChatLinkKind.Home)
        {
            // "Come over to my place and hang out!" + the framework's trailing "[Visit My Home]" link (no action in v1):
            // the card shows the text only; its button is the "Home invite" label (.ShareLinks.Rows.cs).
            v.Row = ShareRow.Home;
            v.Body = ComposeTextOnly(segs);
            return v;
        }
        int pos = -1;
        for (int k = 0; k < segs.Count && pos < 0; k++)
            if (segs[k].Kind == ChatLinkKind.Position && segs[k].Position != null) pos = k;
        if (pos < 0) { v.Row = ShareRow.Inline; v.Body = Compose(segs, 0, segs.Count, notice: false, light); return v; }
        v.Row = ShareRow.Position;
        v.Pos = segs[pos].Position;
        v.LinkRich = "<u><noparse>" + segs[pos].Text + "</noparse></u>";
        v.Before = Compose(segs, 0, pos, notice: false, light).Trim();
        v.After = Compose(segs, pos + 1, segs.Count, notice: false, light).Trim();
        return v;
    }

    // ── Rich-text composition ─────────────────────────────────────────────────────────────────────────────────────
    // Segments [from, to) as ONE TMP rich-text string. Text runs keep their <sprite=N> emoji (EscapeForRich neutralises any
    // other '<' so our own tags can't be broken by it); the "[ " / " ]" the game's templates put around a link are taken
    // off the neighbouring runs and the link is drawn as "[Name]" itself — an item in its quality colour, a notice's
    // player name plain (the prototype's "Roru rolled…"), every other link in the info blue; links underlined.
    private string Compose(IReadOnlyList<ChatTextSegment> segs, int from, int to, bool notice, bool light)
    {
        var sb = new StringBuilder(64);
        for (int k = from; k < to; k++)
        {
            var s = segs[k];
            if (s.Kind == ChatLinkKind.Text)
            {
                var t = s.Text ?? "";
                bool linkBefore = k > 0 && segs[k - 1].Kind != ChatLinkKind.Text;
                bool linkAfter = k + 1 < segs.Count && segs[k + 1].Kind != ChatLinkKind.Text;
                if (linkBefore) t = TrimBracket(t, leading: true);
                if (linkAfter) t = TrimBracket(t, leading: false);
                if (t.Length == 0) continue;
                if (linkBefore && k > from && !char.IsWhiteSpace(t[0])) sb.Append(' ');
                sb.Append(ChatMarkup.EscapeForRich(t));
                if (linkAfter && k + 1 < to && !char.IsWhiteSpace(t[t.Length - 1])) sb.Append(' ');
                continue;
            }
            if (notice && s.Kind == ChatLinkKind.Player) { sb.Append("<noparse>").Append(s.Text).Append("</noparse>"); continue; }
            var hex = s.Kind == ChatLinkKind.Item ? QualityHex(s.ItemQuality, light) : LinkHex(light);
            sb.Append("<color=").Append(hex).Append("><u>[<noparse>").Append(s.Text).Append("</noparse>]</u></color>");
        }
        return sb.ToString();
    }

    // The plain text runs only (the home card's message), brackets around the dropped link removed.
    private static string ComposeTextOnly(IReadOnlyList<ChatTextSegment> segs)
    {
        var sb = new StringBuilder(64);
        for (int k = 0; k < segs.Count; k++)
        {
            if (segs[k].Kind != ChatLinkKind.Text) continue;
            var t = segs[k].Text ?? "";
            if (k > 0 && segs[k - 1].Kind != ChatLinkKind.Text) t = TrimBracket(t, leading: true);
            if (k + 1 < segs.Count && segs[k + 1].Kind != ChatLinkKind.Text) t = TrimBracket(t, leading: false);
            sb.Append(ChatMarkup.EscapeForRich(t));
        }
        return sb.ToString().Trim();
    }

    // "boss here[ " → "boss here" (before a link) / " ]!" → "!" (after it). Only a bracket right next to the link goes;
    // a run without one keeps its text, whitespace trimmed on the link side.
    private static string TrimBracket(string t, bool leading)
    {
        if (leading)
        {
            var s = t.TrimStart();
            return s.Length > 0 && s[0] == ']' ? s.Substring(1).TrimStart() : s;
        }
        var e = t.TrimEnd();
        return e.Length > 0 && e[e.Length - 1] == '[' ? e.Substring(0, e.Length - 1).TrimEnd() : e;
    }

    private string QualityHex(int quality, bool light)
    {
        var arr = light ? _qualityHexLight : _qualityHexDark;
        if (arr == null)
        {
            arr = new string[ItemQualityDark.Length];
            for (int q = 0; q < arr.Length; q++)
                arr[q] = Hex(light ? Mix(ItemQualityDark[q], new ColorRgba(0f, 0f, 0f), ItemQualityLightMix) : ItemQualityDark[q]);
            if (light) _qualityHexLight = arr; else _qualityHexDark = arr;
        }
        return arr[Math.Clamp(quality, 0, arr.Length - 1)];
    }

    // ── Link colours (info blue = the translate line's #9cc8ff; a darker blue on a light panel) ───────────────────────
    private const float LinkHoverMix = 0.45f;   // hover: this far toward white (dark panel) / black (light panel)
    // Lazy: a static initializer here could run before .Compact.cs's colour fields (partial-file order is unspecified).
    private static string? _linkHexDark, _linkHexLight;

    private static string LinkHex(bool light)
        => light ? _linkHexLight ??= Hex(CompactTrTextLight) : _linkHexDark ??= Hex(CompactTrText);

    private ColorRgba LinkColor() => LightPanel() ? CompactTrTextLight : CompactTrText;

    private ColorRgba LinkHoverColor()
    {
        bool light = LightPanel();
        return Mix(light ? CompactTrTextLight : CompactTrText, light ? new ColorRgba(0f, 0f, 0f) : new ColorRgba(1f, 1f, 1f), LinkHoverMix);
    }

    // ── Voice / home pieces that depend on the language (composed on first use + on a language / theme change) ──────

    // "<muted>6s ·</muted> transcript" — one wrapping TMP string, so the transcript wraps under itself next to the mic.
    private string VoiceText(int slot)
    {
        if (ShareViewFor(slot) is not { Row: ShareRow.Voice } v) return "";
        var tpl = _loc.T("chat.share.voiceSeconds");
        var muted = MutedHex();
        if (v.VoiceRich != null && string.Equals(tpl, v.VoiceTpl, StringComparison.Ordinal) && ReferenceEquals(muted, v.VoiceMuted))
            return v.VoiceRich;
        v.VoiceTpl = tpl; v.VoiceMuted = muted;
        var head = "<color=" + muted + ">" + SafeFormat(tpl, v.Seconds) + "</color>";
        return v.VoiceRich = v.Transcript.Length == 0 ? head : head + " " + v.Transcript;
    }

    // "Miyuki's Home" under the home card's text — the sender's name (the sharer is the home's owner).
    private string HomeOwnerLine(int slot)
    {
        if (ShareViewFor(slot) is not { Row: ShareRow.Home } v) return "";
        var tpl = _loc.T("chat.share.homeOf");
        if (v.HomeOwner != null && string.Equals(tpl, v.HomeTpl, StringComparison.Ordinal)) return v.HomeOwner;
        v.HomeTpl = tpl;
        return v.HomeOwner = SafeFormat(tpl, v.Msg.SenderName ?? "");
    }

    // Muted "#RRGGBB", re-formatted only when the theme's muted colour changes.
    private ColorRgba _mutedHexFor;
    private string? _mutedHex;

    private string MutedHex()
    {
        var c = _services.Theme.Colors.MenuMuted;
        if (_mutedHex == null || !c.Equals(_mutedHexFor)) { _mutedHexFor = c; _mutedHex = Hex(c); }
        return _mutedHex;
    }
}
