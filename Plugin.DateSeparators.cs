using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// DATE SEPARATORS in the chat log (approved design "option A"): a centred row — thin line · pill · thin line — reading
/// "Today", "Yesterday" or a short localized date ("Thu, 22 Oct"). One goes between two consecutive displayed rows on
/// different LOCAL PC days (<see cref="ChatMessage.Timestamp"/> is UTC → ToLocalTime), and one at the top, above the oldest
/// displayed row, when that row is not from today (as the native chat does). Every view gets them — All, each channel
/// tab and a whisper conversation — because they are computed from the view's own filtered list (.Log.cs).
///
/// A separator is a synthetic local-only row IN the filtered list, like the "not sent" notices (.LocalNotice.cs): a
/// System-channel <see cref="ChatMessage"/> with no sender and a MsgId in a reserved range that encodes the local day
/// (<see cref="DateSepIdBase"/>). It never enters the framework ring or <c>MessageReceived</c>, so nothing sends, saves,
/// auto-replies to, unread-counts or "new below"-counts it; its slot branch has no avatar, name, translate slot or
/// right-click target, so translate / copy-message / name card never see it. The id is derived from the day, so a
/// rebuild re-creates an IDENTICAL row identity (<c>MessageKey</c>) and the list's height cache keeps its latch.
///
/// Inserted at the three places the filtered list changes: the full rebuild, the incremental append (an append that
/// crosses a day boundary adds the separator + the message — two slots, StableSlots unaffected) and the chunked front
/// trim (which keeps its pool-multiple shift — see <see cref="TrimLogFront"/>). Labels are cached per pool slot; the
/// local date is re-read every <see cref="DateSepClockCheckSec"/> s and a day change rebuilds the view once, so "Today"
/// turns into "Yesterday" and the top separator appears/re-labels across midnight.
/// </summary>
public sealed partial class Plugin
{
    // Far from the framework's small negative notice ids and the local notices' long.MinValue/2 range. MsgId =
    // base − (local day number), so the range spans ~3.65M ids (DateTime.MaxValue's day count).
    private const long DateSepIdBase = long.MinValue / 4;
    private const long DateSepIdSpan = 4_000_000;
    private const double DateSepClockCheckSec = 30;

    // Approved prototype colours (fixed, not themed): line #4a3b52, pill #261d2b, text #9b8aa5.
    private static readonly ColorRgba DateSepLineColor = new(0x4a / 255f, 0x3b / 255f, 0x52 / 255f);
    private static readonly ColorRgba DateSepPillColor = new(0x26 / 255f, 0x1d / 255f, 0x2b / 255f);
    private static readonly ColorRgba DateSepTextColor = new(0x9b / 255f, 0x8a / 255f, 0xa5 / 255f);
    private const int   DateSepFontSize    = 11;      // ≈ the timestamp size; Bold = the TMP path, which honours FontSize
    private const float DateSepPillRadius  = 9f;
    private const float DateSepPillPadX    = 10f, DateSepPillPadY = 2f;
    private const float DateSepPillMaxW    = 180f;    // explicit cap: never rely on the parent-width cap (ratchet note)
    private const float DateSepGap         = 8f;      // line ↔ pill

    private DateTime _sepToday;          // local date as of the last clock check (default = not read yet)
    private double _sepNextClockCheck;

    private readonly long[] _sepLabelKey = new long[LogPoolSize];
    private readonly string?[] _sepLabelText = new string?[LogPoolSize];
    private string? _sepLabelLang;

    private static bool IsDateSep(ChatMessage m)
        => m.MsgId <= DateSepIdBase && m.MsgId > DateSepIdBase - DateSepIdSpan && m.SenderId == 0 && m.Channel == ChatChannel.System;

    private bool IsDateSepRow(int slot) => TryMsg(slot, out var m) && IsDateSep(m);

    private static DateTime LocalDay(ChatMessage m)
        => IsDateSep(m) ? new DateTime((DateSepIdBase - m.MsgId) * TimeSpan.TicksPerDay) : m.Timestamp.ToLocalTime().Date;

    private static ChatMessage MakeDateSep(DateTime localDay)
    {
        // Timestamp = that local midnight (UTC), so the row sorts at/above the day's first message if anything ever
        // compares it. Text is empty: the label is composed per slot from the day (DateSepLabel).
        var ts = DateTime.SpecifyKind(localDay, DateTimeKind.Local).ToUniversalTime();
        return new ChatMessage(ChatChannel.System, string.Empty, 0L, string.Empty, ts, ChatMessageType.System,
            ReadOnlyMemory<byte>.Empty, null, MsgId: DateSepIdBase - localDay.Ticks / TimeSpan.TicksPerDay);
    }

    // From EnsureFiltered, BEFORE its dirty check: a local-date change marks the view dirty so this same call rebuilds
    // it (top separator + every label re-evaluated against the new today). Re-reads the PC clock at most every 30 s.
    private void TickDateSepClock()
    {
        var now = _services.Framework.TimeNow;
        if (now < _sepNextClockCheck && _sepToday != default) return;
        _sepNextClockCheck = now + DateSepClockCheckSec;
        var today = DateTime.Now.Date;
        if (today == _sepToday) return;
        if (_sepToday != default) _logDirty = true;
        _sepToday = today;
    }

    // Full rebuild: the time-sorted view (local notices merged) → _filtered with separators in place.
    private void AddWithDateSeparators(List<ChatMessage> rows)
    {
        DateTime prev = default;
        for (int n = 0; n < rows.Count; n++)
        {
            var day = LocalDay(rows[n]);
            if (n == 0 ? day != _sepToday : day != prev) _filtered.Add(MakeDateSep(day));
            prev = day;
            _filtered.Add(rows[n]);
        }
    }

    // Incremental append (TryAppendPending): a message on a new local day brings its separator with it.
    private void AppendWithDateSeparator(ChatMessage cm)
    {
        var day = LocalDay(cm);
        if (_filtered.Count == 0 ? day != _sepToday : day != LocalDay(_filtered[^1])) _filtered.Add(MakeDateSep(day));
        _filtered.Add(cm);
    }

    // Chunked front trim (.Log.cs): still removes exactly LogTrimChunk rows (a pool multiple, so every remaining row keeps
    // its StableSlots slot), but the new oldest row may need a header — the last row being dropped is overwritten with it
    // instead of inserting one (an insert would shift every row by one and re-bind the whole pool).
    private void TrimLogFront()
    {
        _filtered.RemoveRange(0, LogTrimChunk - 1);
        var top = _filtered[1];
        if (!IsDateSep(top) && LocalDay(top) != _sepToday) _filtered[0] = MakeDateSep(LocalDay(top));
        else _filtered.RemoveAt(0);
    }

    // line · pill · line. The two lines are flex-width flat bars (SeparatorElement's colour is the theme border, not the
    // approved #4a3b52), so they share the row's leftover width equally and the hugging pill sits centred. Constant
    // structure (no Cond) and a one-line NoWrap label → the row height never changes. No click / hover target.
    private HudElement BuildDateSepRow(int i) => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            DateSepLine(),
            new BubbleElement(new TextElement(() => DateSepLabel(i), () => DateSepTextColor, FontSize: DateSepFontSize, NoWrap: true)
                { Bold = true })
            {
                FillColor = DateSepPillColor, CornerRadius = DateSepPillRadius,
                PaddingX = DateSepPillPadX, PaddingY = DateSepPillPadY, MaxWidth = DateSepPillMaxW,
            },
            DateSepLine(),
        }, Gap: DateSepGap),
    }) { Padding = 2 };

    // A 1 px flat line: the Modern bar is a sprite-less track + fill; at fraction 1 the opaque fill covers the track.
    private static HudElement DateSepLine()
        => new BarElement(static () => 1f, DateSepLineColor) { Style = BarStyle.Modern, Height = 1f, FillWidth = true };

    // "Today" / "Yesterday" / a short localized date. Polled every refresh → cached per slot, keyed on (day, kind), and
    // dropped on a language switch.
    private string DateSepLabel(int slot)
    {
        if (!TryMsg(slot, out var m) || !IsDateSep(m)) return "";
        var lang = _loc.Language;
        if (!string.Equals(lang, _sepLabelLang, StringComparison.Ordinal))
        {
            _sepLabelLang = lang;
            Array.Clear(_sepLabelText, 0, _sepLabelText.Length);
        }
        var day = LocalDay(m);
        int kind = day == _sepToday ? 1 : day == _sepToday.AddDays(-1) ? 2 : 3;
        long key = (day.Ticks / TimeSpan.TicksPerDay) * 4 + kind;
        if ((uint)slot < (uint)LogPoolSize && _sepLabelKey[slot] == key && _sepLabelText[slot] is { } hit) return hit;
        var text = kind switch
        {
            1 => _loc.T("chat.date.today"),
            2 => _loc.T("chat.date.yesterday"),
            _ => FormatSepDate(day, lang),
        };
        if ((uint)slot < (uint)LogPoolSize) { _sepLabelKey[slot] = key; _sepLabelText[slot] = text; }
        return text;
    }

    // UI language → culture + a short day pattern in that language's natural order. Unknown codes try the code itself;
    // a culture the runtime can't create falls back to InvariantCulture (English names, "Thu, 22 Oct").
    private static string FormatSepDate(DateTime day, string? lang)
    {
        var (name, pattern) = (lang ?? "").ToLowerInvariant() switch
        {
            "ja"  => ("ja-JP", "M月d日(ddd)"),
            "ko"  => ("ko-KR", "M월 d일 (ddd)"),
            "th"  => ("th-TH", "ddd d MMM"),
            "id"  => ("id-ID", "ddd, d MMM"),
            "fil" => ("fil-PH", "ddd, MMM d"),
            "en"  => ("en-GB", "ddd, d MMM"),
            var other => (other, "ddd, d MMM"),
        };
        CultureInfo culture;
        try { culture = name.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        try { return day.ToString(pattern, culture); }
        catch (FormatException) { return day.ToString("ddd, d MMM", CultureInfo.InvariantCulture); }
    }
}
