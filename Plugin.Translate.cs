using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Per-message TRANSLATE (approved prototype). Hovering someone else's text bubble shows a small square 文A pill between
/// the bubble and its timestamp (.Bubbles.cs). Click → the bubble dims and shows "Translating…" with animated dots, the
/// pill gets a blue busy outline; done → the bubble shows the translation with a faint blue outline and a tag line
/// "文A Translated · Thai → English" (or "Already in English" when the source already is the target), the pill stays lit;
/// click again → back to the original (toggle; the translation is cached, so toggling is instant). Failure → the original
/// stays, a red "Translation failed · click to retry" line, red pill; click retries. The pill stays visible without hover
/// while a row is pending, translated or failed. Requests: .Translate.Net.cs; languages: .Translate.Langs.cs.
///
/// Layout rules this leans on (WindowBuilder-Patterns.md): the pill sits in a constant-width cell, so hover never moves
/// anything; every translate state is a WHOLE-bubble branch (a ConditionalElement around complete bubbles, never inside
/// a bubble's measured child); and each state change bumps the row's <c>VirtualListElement.RowRevision</c> so the
/// variable-height log re-measures that row in place (a latched height is otherwise final).
/// </summary>
public sealed partial class Plugin
{
    private const string CfgTrTarget = "translate.target";        // Google code; "" = follow the UI language
    private const string CfgTrHover  = "translate.hoverButton";

    private enum TrPhase { Pending, Done, Failed }

    // Per-message UI state, keyed by MessageKey (.Log.cs). Rev feeds RowRevision: bumped on EVERY change that can alter
    // the row's height (pending line on/off, translation shown/hidden, failure line).
    private sealed class TrEntry
    {
        public TrPhase Phase;
        public bool Showing;          // Done: the translation (or the "already in" tag) is shown, vs the plain original
        public long Rev;
        public string Target = "";
        public TrResult? Result;
        public string? Tag;           // cached tag line, for TagLang
        public string? TagLang;
    }

    private readonly Dictionary<long, TrEntry> _tr = new();
    private const int TrEntryCap = 1000;
    private string _trTargetCfg = "";
    private bool _trHoverButton = true;
    private int _trHoverSlot = -1;   // pool slot under the pointer (RowElement.OnHover), -1 = none

    // Fixed colours: "blue = translated / busy, red = failed" reads the same on every theme preset (not the theme accent).
    private static readonly ColorRgba TrBlue = new(0.40f, 0.68f, 1.00f);
    private static readonly ColorRgba TrRed  = new(1.00f, 0.42f, 0.42f);
    private const float TrPillAlpha = 0.25f, TrBubbleOutlineAlpha = 0.45f;
    private const int   TrButtonFontSize = 10;   // "文A" ≈ 16 wide: fits the 16-unit glyph slot of a 28 square pill
    private const int   TrTagFontSize = 11;
    private const string TrIcon = "文A";        // icon glyphs, not words — same in every language

    private void LoadTranslateConfig()
    {
        _trTargetCfg = _cfg.Get(CfgTrTarget, "") ?? "";
        _trHoverButton = _cfg.Get(CfgTrHover, true);
    }

    private void SaveTranslateConfig()
    {
        _cfg.Set(CfgTrTarget, _trTargetCfg);
        _cfg.Set(CfgTrHover, _trHoverButton);
        _cfg.Save();
    }

    // The target: the user's pick, else the plugin's current UI language (fil → tl, unknown → English).
    private string TranslateTarget()
    {
        if (_trTargetCfg.Length > 0) return _trTargetCfg;
        var ui = ToGoogleCode(_loc.Language);
        return TrLangIndex.ContainsKey(ui) ? ui : "en";
    }

    // ── Settings ⚙ section (.Settings.cs window) ───────────────────────────────────────────────────────────────────
    private HudElement BuildTranslateSettings() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("chat.translate.title"), Emphasis: true),
        new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("chat.translate.targetLabel")),
            // ~52 languages → a capped, scrollable popup (DropdownElement.MaxVisibleRows), not a screen-tall list.
            // Pill = house-style trigger + list (framework ≥ 2.28.0, same as MultiTool's Band Mute dropdown). The Pill list
            // is only as wide as its trigger, so a fixed Width keeps long names ("Bahasa Indonesia (…)") from being cut.
            new DropdownElement(TrTargetIndex, TrTargetOptions, OnPickTrTarget, Width: 220f) { MaxVisibleRows = 12, Style = DropdownStyle.Pill },
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => _trHoverButton, v => { _trHoverButton = v; SaveTranslateConfig(); }),
            new TextElement(() => _loc.T("chat.translate.hoverToggle")),
        }, Gap: 6f),
        new TextElement(() => _loc.T("chat.translate.privacy"), MutedCol),
    }, Gap: 4f);

    // ── Row state lookups (polled per refresh per slot — no allocation) ─────────────────────────────────────────────
    private TrEntry? TrEntryFor(int slot)
        => _tr.Count > 0 && TryMsg(slot, out var m) && _tr.TryGetValue(MessageKey(m), out var e) ? e : null;

    // RowRevision (.Log.cs) for a DATA index: 0 for a row with no translate state, else its entry's revision.
    private long TranslateRowRevision(int dataIndex)
    {
        if (_tr.Count == 0 || !TryDataMsg(dataIndex, out var m)) return 0L;
        return _tr.TryGetValue(MessageKey(m), out var e) ? e.Rev : 0L;
    }

    // Translatable = someone's ordinary text line with at least one readable glyph outside its tags (a message that
    // is only emoji has nothing to translate). Stickers / system notices / own messages never get the pill — the pill
    // only exists in the others' text layout (.Bubbles.cs), this guards the rest.
    private static bool CanTranslate(in ChatMessage m)
        => m.StickerId == 0 && m.Channel != ChatChannel.System && HasReadableTextOutsideTags(m.Text);

    private static bool HasReadableTextOutsideTags(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        bool inTag = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag && char.IsLetterOrDigit(c)) return true;
        }
        return false;
    }

    private static bool TrIsTranslatedShown(TrEntry? e) => e is { Phase: TrPhase.Done, Showing: true };

    // The translate bubble branch replaces the plain one while pending / failed / showing a translation.
    private bool TrBubbleShown(int slot) => TrEntryFor(slot) is { } e && (e.Phase != TrPhase.Done || e.Showing);

    private bool ShowTrButton(int slot)
    {
        if (!TryMsg(slot, out var m) || !CanTranslate(m)) return false;
        var e = _tr.Count > 0 && _tr.TryGetValue(MessageKey(m), out var x) ? x : null;
        if (e != null && (e.Phase != TrPhase.Done || e.Showing)) return true;   // keep it up while pending / translated / failed
        return _trHoverButton && _trHoverSlot == slot;
    }

    private void OnTrHover(int slot, bool over)
    {
        if (over) _trHoverSlot = slot;
        else if (_trHoverSlot == slot) _trHoverSlot = -1;
    }

    // Body text of the translate branch: the translation while shown (unless it's already in the target language).
    // Both are untrusted in TMP: the original is wire text, and a Google result can contain '<' too (ChatMarkup).
    private string TrBodyText(int slot)
    {
        if (!TryMsg(slot, out var m)) return "";
        var e = TrEntryFor(slot);
        var text = TrIsTranslatedShown(e) && e!.Result is { Same: false } r ? r.Text : m.Text;
        return SafeCached(_trBodyRaw, _trBodySafe, slot, text, trusted: false);   // .Log.cs — per-slot cache
    }

    private readonly string?[] _trBodyRaw = new string?[LogPoolSize], _trBodySafe = new string?[LogPoolSize];

    // Dim the original while it is being translated; otherwise the normal body colour.
    private ColorRgba? TrBodyColor(int slot)
        => TrEntryFor(slot) is { Phase: TrPhase.Pending } ? _services.Theme.Colors.MenuMuted : LogMsgColor(slot);

    private ColorRgba? TrStatusColor(int slot) => TrEntryFor(slot) switch
    {
        { Phase: TrPhase.Failed } => TrRed,
        { Phase: TrPhase.Done } => TrBlue,
        _ => _services.Theme.Colors.MenuMuted,
    };

    // Faint blue ring around a bubble that is showing a real translation (not for "already in", pending or failed).
    private ColorRgba? TrBubbleOutline(int slot)
    {
        var e = TrEntryFor(slot);
        return TrIsTranslatedShown(e) && e!.Result is { Same: false } ? TrBlue with { A = TrBubbleOutlineAlpha } : null;
    }

    // ── Pill colours ───────────────────────────────────────────────────────────────────────────────────────────────
    private ColorRgba? TrButtonFill(int slot) => TrEntryFor(slot) switch
    {
        { Phase: TrPhase.Failed } => TrRed with { A = TrPillAlpha },
        { Phase: TrPhase.Done, Showing: true } => TrBlue with { A = TrPillAlpha },
        _ => TabTrackFill,   // muted rest look = the composer icon pills' track tint
    };

    private ColorRgba? TrButtonGlyph(int slot) => TrEntryFor(slot) switch
    {
        { Phase: TrPhase.Failed } => TrRed,
        { Phase: TrPhase.Pending } or { Phase: TrPhase.Done, Showing: true } => TrBlue,
        _ => _services.Theme.Colors.MenuMuted,
    };

    private ColorRgba? TrButtonOutline(int slot) => TrEntryFor(slot) is { Phase: TrPhase.Pending } ? TrBlue : null;

    // ── Status line ────────────────────────────────────────────────────────────────────────────────────────────────
    // "Translating" + 0..3 dots. The missing dots are drawn fully transparent (TMP <alpha>), so the line keeps ONE
    // width while it animates — a width change would re-hug the bubble every tick (and could re-wrap a short message).
    private readonly string[] _trDots = new string[4];
    private string? _trDotsLang;

    private string TrStatus(int slot)
    {
        var e = TrEntryFor(slot);
        if (e == null) return "";
        switch (e.Phase)
        {
            case TrPhase.Failed: return _loc.T("chat.translate.failed");
            case TrPhase.Pending:
                if (!string.Equals(_trDotsLang, _loc.Language, StringComparison.Ordinal))
                {
                    _trDotsLang = _loc.Language;
                    var word = _loc.T("chat.translate.translating");
                    for (int n = 0; n < 4; n++) _trDots[n] = word + new string('.', n) + "<alpha=#00>" + new string('.', 3 - n);
                }
                return _trDots[(int)(_services.Framework.TimeNow * 3f) & 3];
            default: return TrTag(e);
        }
    }

    // "Translated from Thai to English" / "Already in English", cached per entry per UI language. No arrow glyph:
    // the game/menu fonts have no "→", so it rendered as a gap ("Translated · Thai  English").
    private string TrTag(TrEntry e)
    {
        if (e.Tag != null && string.Equals(e.TagLang, _loc.Language, StringComparison.Ordinal)) return e.Tag;
        var r = e.Result;
        var target = TrLangName(e.Target);
        e.Tag = r == null ? "" : r.Same
            ? SafeFormat(_loc.T("chat.translate.already"), target)
            : SafeFormat2(_loc.T("chat.translate.tag"), r.Src.Length > 0 ? TrLangName(r.Src) : "?", target);
        e.TagLang = _loc.Language;
        return e.Tag;
    }

    private static string SafeFormat2(string tpl, object a, object b)
    {
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl, a, b); }
        catch (FormatException) { return tpl; }
    }

    // ── Click ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnTranslateClick(int slot)
    {
        if (!TryMsg(slot, out var m) || !CanTranslate(m)) return;
        var key = MessageKey(m);
        _tr.TryGetValue(key, out var e);
        if (e is { Phase: TrPhase.Pending }) return;                                     // already on its way
        if (e is { Phase: TrPhase.Done, Showing: true }) { e.Showing = false; e.Rev++; return; }   // toggle back to the original
        if (e == null)
        {
            if (_tr.Count >= TrEntryCap) TrimTranslateEntries();
            e = new TrEntry();
            _tr[key] = e;
        }
        e.Target = TranslateTarget();
        e.Tag = null;
        // Translate what the bubble SHOWS (ChatMarkup): a player's <size>/<color> junk never goes to Google.
        var source = ChatMarkup.SanitizeWire(m.Text);
        if (TryCachedTranslation(key, source, e.Target, out var hit)) { e.Result = hit; e.Phase = TrPhase.Done; e.Showing = true; e.Rev++; return; }
        e.Phase = TrPhase.Pending;
        e.Rev++;
        EnqueueTranslation(key, source, e.Target);   // .Translate.Net.cs
    }

    // A finished request (or a queued duplicate answered from the cache). Only a row still waiting for THIS target
    // takes it — the user may have switched target and re-clicked meanwhile.
    private void ApplyTranslation(TrJob job, TrResult r)
    {
        if (job.Outgoing) { ApplyOutgoingTranslation(job, r); return; }   // a composer draft (.TranslateOut.cs)
        ApplyRowTranslation(job, r);
    }

    private void ApplyRowTranslation(TrJob job, TrResult r)
    {
        if (!_tr.TryGetValue(job.Key, out var e) || e.Phase != TrPhase.Pending || e.Target != job.Target) return;
        e.Result = r; e.Phase = TrPhase.Done; e.Showing = true; e.Tag = null; e.Rev++;
    }

    private void FailTranslation(TrJob job)
    {
        if (job.Outgoing) { FailOutgoingTranslation(job); return; }
        FailRowTranslation(job);
    }

    private void FailRowTranslation(TrJob job)
    {
        if (!_tr.TryGetValue(job.Key, out var e) || e.Phase != TrPhase.Pending || e.Target != job.Target) return;
        e.Phase = TrPhase.Failed; e.Rev++;
    }

    // Bounded: drop every settled row that isn't showing anything (their translations stay in the result cache).
    private void TrimTranslateEntries()
    {
        var drop = new List<long>();
        foreach (var kv in _tr) if (kv.Value.Phase == TrPhase.Done && !kv.Value.Showing) drop.Add(kv.Key);
        foreach (var k in drop) _tr.Remove(k);
        if (_tr.Count >= TrEntryCap) _tr.Clear();   // pathological: everything pending/shown — start over
    }
}
