using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// SYSTEM notices (<see cref="ChatChannel.System"/>): the game's client-generated "Obtained Guild Fund×40"-style lines,
/// which the framework captures from the game's own chat VM and delivers through <see cref="IChat.MessageReceived"/>
/// with <see cref="ChatMessage.SystemLabel"/> (e.g. "Obtained"), no sender and a negative MsgId (Chat-System-Architecture.md
/// "SYSTEM … NOTICES"). Server-side notices that the framework also maps to System (TopNotice / Play) arrive the same way
/// minus the label. In the System tab and in All they render as one COMPACT line instead of a chat bubble — no avatar, no
/// name: <c>12:03  System  Obtained  Guild Fund×40</c> (time + "System" muted, the label in the System colour, then the
/// text with the game's own item-quality colour tags intact).
///
/// The line is ONE wrapping TMP text (RichEmoji = the TMP path, so the game's &lt;color&gt; tags render and long lines
/// wrap at the log width) styled with inline colour tags — two TextElements side by side in a Row would squeeze each other
/// instead of wrapping (WindowBuilder-Patterns.md "Row layout"). The composed string is cached per pool slot and rebuilt
/// only when that slot shows a different message or the language/colours change, so the per-refresh Func doesn't allocate.
/// Notices never count toward the "↓ N new" bar (.NewBelow.cs); the System tab's unread dot works like any tab (.Tabs.cs).
/// </summary>
public sealed partial class Plugin
{
    private readonly long[] _noticeKey = new long[LogPoolSize];        // message identity each slot's cached line is for
    private readonly string?[] _noticeLine = new string?[LogPoolSize];
    // Language label + colours the cache was built with (a change drops it), and their pre-formatted forms.
    private string? _noticeLabel;
    private ColorRgba _noticeMutedC, _noticeSysC;
    private string _noticeMutedHex = "#888888", _noticeSysHex = "#FFCC73";

    private bool IsNoticeRow(int slot) => TryMsg(slot, out var m) && m.Channel == ChatChannel.System;

    // Width-filling notice line (the slot Column force-expands it, so the text wraps at the log width). Lives in its own
    // branch of the slot's outermost ConditionalElement (.Bubbles.cs BuildBubbleSlot), never inside a bubble.
    private HudElement BuildNoticeRow(int i) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => NoticeLine(i)) { RichEmoji = true },
    }) { Padding = 2 };

    private string NoticeLine(int slot)
    {
        if (!TryMsg(slot, out var m) || slot < 0 || slot >= LogPoolSize) return "";
        var label = _loc.T("chat.tab.system");   // the tab's own name — same word the user sees on the System tab
        var mutedC = _services.Theme.Colors.MenuMuted;
        var sysC = _systemColor.Value;
        if (!string.Equals(label, _noticeLabel, StringComparison.Ordinal) || !mutedC.Equals(_noticeMutedC) || !sysC.Equals(_noticeSysC))
        {
            _noticeLabel = label; _noticeMutedC = mutedC; _noticeSysC = sysC;
            _noticeMutedHex = Hex(mutedC); _noticeSysHex = Hex(sysC);
            Array.Clear(_noticeLine, 0, _noticeLine.Length);
        }
        string muted = _noticeMutedHex, sys = _noticeSysHex;
        long key = NoticeKey(m);
        if (_noticeLine[slot] is { } cached && _noticeKey[slot] == key) return cached;
        var time = m.Timestamp.ToLocalTime().ToString("HH:mm");
        var line = string.IsNullOrEmpty(m.SystemLabel)
            ? $"<color={muted}>{time}  {label}</color>  {m.Text}"
            : $"<color={muted}>{time}  {label}</color>  <color={sys}>{m.SystemLabel}</color>  {m.Text}";
        _noticeKey[slot] = key;
        _noticeLine[slot] = line;
        return line;
    }

    // Notices have no server id (the framework makes a unique negative MsgId per session); fold in the timestamp and
    // text so two slots never share a stale line.
    private static long NoticeKey(ChatMessage m)
        => (m.MsgId * 31L + m.Timestamp.Ticks) * 31L + (m.Text?.GetHashCode() ?? 0);

    private static string Hex(ColorRgba v)
    {
        static int B(float f) => (int)MathF.Round(Math.Clamp(f, 0f, 1f) * 255f);
        return $"#{B(v.R):X2}{B(v.G):X2}{B(v.B):X2}";
    }
}
