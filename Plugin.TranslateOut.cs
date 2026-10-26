using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// OUTGOING translate (approved prototype): a 28×28 文A pill in the composer row between ☺ and the text box. Click → the
/// language menu (.TranslateOut.Menu.cs: up to 3 recent languages, then the full Settings list); picking one sends the
/// DRAFT (source <c>auto</c>) through the same request queue, cache and tag protection as the per-message translate
/// (.Translate.Net.cs). While it is on its way the box is dimmed and read-only (<c>InputElement.Locked</c>), the pill
/// shows the blue busy outline, Send is disabled and a status line above the row reads "Translating to Thai…".
/// Done → the box text is REPLACED by the translation (caret at the end, focused), the line reads
/// "文A Translated to Thai · Undo" and the pill stays lit until the user sends or edits. Undo restores the exact
/// original. Failed → the original stays, a red "Translation failed · click 文A to retry" line, red pill; a click
/// retries with the same language. The line hides on send, on the first edit and on Undo.
/// A translation longer than the 140 cap is still put in the box whole (never cut silently): the counter goes red and
/// Send waits until the user trims it (.Composer.cs SetInput / .SendCooldown.cs CanSendNow).
/// Privacy: only the draft is sent, and only on a language pick / retry; nothing about the text is logged.
/// </summary>
public sealed partial class Plugin
{
    private enum TrOutPhase { Idle, Pending, Done, Failed }

    private TrOutPhase _trOutPhase;
    private string _trOutLang = "";       // target of the current / last request (the retry language)
    private string _trOutOriginal = "";   // the user's own text before translating — what Undo puts back
    private string _trOutBoxText = "";    // the box text the last step left (Done: the translation; Failed: the source)
    private bool _trOutSame;              // Done, but Google says the draft already is in the target language
    private long _trOutKey;               // the live request's job key; a stale answer (other key) is dropped
    private long _trOutSeq;               // job keys for drafts: a fresh one per request, so the per-key cache never
                                          // hands an old draft's translation to a new draft

    private bool TrOutBusy() => _trOutPhase == TrOutPhase.Pending;

    // Something to translate: at least one readable glyph outside the tags (an emoji-only draft has nothing).
    private bool CanTranslateDraft() => HasReadableTextOutsideTags(_inputText);   // .Translate.cs

    // ── The 文A pill ──────────────────────────────────────────────────────────────────────────────────────────────
    // Same glyph / size / TMP path as the per-message pill (.Bubbles.cs BuildTranslateSlot) in the composer's square.
    private HudElement BuildTrOutButton() => BuildIconPill(
        new TextElement(() => TrIcon, TrOutGlyph, FontSize: TrButtonFontSize, Align: TextAlign.Center, NoWrap: true)
            { Bold = true },   // Bold = the TMP path, which honours FontSize on this surface (plain Text ignores it)
        TrOutFill, NoClick, () => TrOutBusy() ? TrBlue : null, onClickRect: OnTrOutClick);

    private ColorRgba? TrOutFill() => _trOutPhase switch
    {
        TrOutPhase.Failed => TrRed with { A = TrPillAlpha },
        TrOutPhase.Done => TrBlue with { A = TrPillAlpha },
        _ => ToolPillFill(_trOutMenuOpen),   // .Composer.cs — the ＋ / ☺ look: track tint, accent while its menu is open
    };

    private ColorRgba? TrOutGlyph() => _trOutPhase switch
    {
        TrOutPhase.Failed => TrRed,
        TrOutPhase.Pending or TrOutPhase.Done => TrBlue,
        _ => CanTranslateDraft() ? _services.Theme.Colors.MenuText : _services.Theme.Colors.MenuMuted with { A = TrOutDisabledAlpha },
    };

    private const float TrOutDisabledAlpha = 0.45f;

    // Pending: no-op (busy). Failed: retry with the same language at once. Otherwise open / close the language menu.
    // An empty (or emoji-only) draft greys the pill and ignores the click. `pill` = its screen rect (the menu's anchor).
    private void OnTrOutClick(WindowRect pill)
    {
        if (TrOutBusy() || !CanTranslateDraft()) return;
        if (_trOutPhase == TrOutPhase.Failed && _trOutLang.Length > 0) { StartOutgoingTranslation(_trOutLang); return; }
        ToggleTrOutMenu(pill);   // .TranslateOut.Menu.cs
    }

    // ── Request ───────────────────────────────────────────────────────────────────────────────────────────────────
    private void StartOutgoingTranslation(string code)
    {
        if (TrOutBusy() || !CanTranslateDraft() || !TrLangIndex.ContainsKey(code)) return;
        NoteRecentOutLang(code);   // .TranslateOut.Menu.cs
        // Re-translating the untouched result of an earlier step keeps the user's OWN text as the Undo target.
        bool chained = _trOutPhase is TrOutPhase.Done or TrOutPhase.Failed
                       && string.Equals(_inputText, _trOutBoxText, StringComparison.Ordinal);
        if (!chained) _trOutOriginal = _inputText;
        _trOutLang = code;
        _trOutSame = false;
        _trOutKey = ++_trOutSeq;
        _trOutPhase = TrOutPhase.Pending;   // before the cache check: a hit completes through the same guard
        var job = new TrJob(_trOutKey, _inputText, code, Outgoing: true);
        if (TryCachedTranslation(job.Key, job.Text, code, out var hit)) { ApplyOutgoingTranslation(job, hit); return; }
        EnqueueTranslation(job);   // .Translate.Net.cs
    }

    // Main thread (OnTranslationDone / a queued cache hit). Only the LIVE request lands.
    private void ApplyOutgoingTranslation(TrJob job, TrResult r)
    {
        if (job.Key != _trOutKey || _trOutPhase != TrOutPhase.Pending) return;
        _trOutSame = r.Same;
        if (!r.Same) _inputText = r.Text;   // never truncated — over 140 shows the red counter instead (.Composer.cs SetInput)
        _trOutBoxText = _inputText;
        _trOutPhase = TrOutPhase.Done;
        _composerFocusSeq++;   // composer FocusRequest: focus the box, caret at the end (applied after the unlock)
    }

    private void FailOutgoingTranslation(TrJob job)
    {
        if (job.Key != _trOutKey || _trOutPhase != TrOutPhase.Pending) return;
        _trOutBoxText = _inputText;
        _trOutPhase = TrOutPhase.Failed;
        _composerFocusSeq++;   // hand the keyboard back to the unlocked box
    }

    // The draft changed (typing, a picked emoji, a cleared box): the status line and the lit pill go away.
    private void NoteDraftEdited()
    {
        if (_trOutPhase is TrOutPhase.Done or TrOutPhase.Failed) _trOutPhase = TrOutPhase.Idle;
    }

    // A real send (.Composer.cs DoSend) ends the translate state.
    private void ResetTrOut() { if (_trOutPhase != TrOutPhase.Pending) _trOutPhase = TrOutPhase.Idle; }

    private void UndoTrOut()
    {
        if (_trOutPhase != TrOutPhase.Done) return;
        _inputText = _trOutOriginal;
        _trOutPhase = TrOutPhase.Idle;
        _composerFocusSeq++;
    }

    private bool TrOutUndoShown()
        => _trOutPhase == TrOutPhase.Done && !string.Equals(_trOutOriginal, _inputText, StringComparison.Ordinal);

    // ── Status line (above the composer row, only while a state is live) ──────────────────────────────────────────
    // Plain text, not TMP: a TMP text draws ONE face per string, so "文A" inside a Thai/Korean sentence would show boxes;
    // the plain menu Text has the OS font fallback. The leading 文A icon of the Done line is its own (TMP, CJK-face) text
    // like the per-message tag line. The row is outside the min-width composer row, so its width changing is harmless.
    private HudElement BuildTrOutStatus() => new ConditionalElement(() => _trOutPhase != TrOutPhase.Idle,
        new RowElement(new HudElement[]
        {
            new ConditionalElement(() => _trOutPhase == TrOutPhase.Done,
                // Default size (not the 11 tag size): it sits beside plain body-size text, not a small tag line.
                new TextElement(() => TrIcon, () => TrBlue, NoWrap: true) { Bold = true }),
            new TextElement(TrOutStatusText, TrOutStatusColor, NoWrap: true),
            // Link-style Undo: a Bare selectable (no box, tint or padding) around underlined text.
            new ConditionalElement(TrOutUndoShown,
                new SelectableElement(new TextElement(() => _loc.T("chat.translate.undo"), () => TrBlue, NoWrap: true) { Underline = true },
                    UndoTrOut) { Bare = true }),
        }, Gap: 6f));

    private ColorRgba? TrOutStatusColor() => _trOutPhase switch
    {
        TrOutPhase.Failed => TrRed,
        TrOutPhase.Done => TrBlue,
        _ => _services.Theme.Colors.MenuMuted,
    };

    // Cached per (phase, language, UI language): the line is polled every refresh. Pending cycles 0..3 ASCII dots
    // ("…" may be missing from the menu font); the width change is fine on this standalone row.
    private readonly string[] _trOutDots = new string[4];
    private string? _trOutTextLang, _trOutTextUi;
    private TrOutPhase _trOutTextPhase;
    private bool _trOutTextSame;
    private string _trOutText = "";

    private string TrOutStatusText()
    {
        var ui = _loc.Language;
        if (_trOutTextPhase != _trOutPhase || _trOutTextSame != _trOutSame
            || !string.Equals(_trOutTextLang, _trOutLang, StringComparison.Ordinal) || !string.Equals(_trOutTextUi, ui, StringComparison.Ordinal))
        {
            _trOutTextPhase = _trOutPhase; _trOutTextSame = _trOutSame; _trOutTextLang = _trOutLang; _trOutTextUi = ui;
            var lang = TrLangName(_trOutLang);
            switch (_trOutPhase)
            {
                case TrOutPhase.Pending:
                    var word = SafeFormat(_loc.T("chat.translate.outTranslating"), lang);
                    for (int n = 0; n < 4; n++) _trOutDots[n] = word + new string('.', n);
                    break;
                case TrOutPhase.Done:
                    _trOutText = SafeFormat(_loc.T(_trOutSame ? "chat.translate.already" : "chat.translate.outDone"), lang);
                    break;
                case TrOutPhase.Failed: _trOutText = _loc.T("chat.translate.outFailed"); break;
                default: _trOutText = ""; break;
            }
        }
        return _trOutPhase == TrOutPhase.Pending ? _trOutDots[(int)(_services.Framework.TimeNow * 3f) & 3] : _trOutText;
    }
}
