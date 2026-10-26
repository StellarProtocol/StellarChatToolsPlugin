using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The Settings window's "Whisper history" section (after Translation): the keep toggle, the where/how-much note, and a
/// two-step "Clear saved whisper history" button with this character's stats on the line below it. First click arms it (solid red,
/// "Click again to delete", a Cancel pill + a scope note; reverts by itself after <see cref="HistConfirmSec"/>); the
/// second deletes this character's files on the I/O worker and drops the in-memory copy at once.
/// </summary>
public sealed partial class Plugin
{
    private const float HistConfirmSec = 5f;
    private const float HistClearedSec = 3f;
    private static readonly ColorRgba DangerRed = new(0.90f, 0.30f, 0.30f);
    private const float DangerIdleFillAlpha = 0.10f;
    private const float CancelPillMaxW = 160f;   // well above the widest locale's "Cancel" (fil "Kanselahin"); a cap, not a size

    private float _histConfirmUntil;   // > now = armed
    private float _histClearedUntil;   // > now = show "Cleared" in the stats slot
    private int _histStatsBuiltRev = -1;
    private string? _histStatsTpl, _histStatsSub, _histStatsName;
    private string _histStatsText = "";

    private bool HistConfirming() => _histConfirmUntil > _services.Framework.TimeNow;

    private HudElement BuildWhisperHistorySettings() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("chat.history.title"), Emphasis: true),
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => _keepWhisperHistory, SetKeepWhisperHistory),
            new TextElement(() => _loc.T("chat.history.keep")),
        }, Gap: 6f),
        new TextElement(() => _loc.T("chat.history.note"), MutedCol),
        new RowElement(new HudElement[]
        {
            // Danger pill (28 tall, the composer pill shape): red outline + faint red wash at rest, solid red when armed.
            // The bare Bubble sits DIRECTLY in this Column-stretched Row — NOT in BuildPill's Cell. Its label changes
            // (idle ↔ armed, language), and in a natural Cell the parent-width cap can only shrink: "Click again to
            // delete" pinned it narrow and "Clear saved whisper history" then spilled out of the box (.SendTarget.cs
            // PillBubble). Here the cap is the full row, so it hugs any label and only wraps (grows taller, hence no
            // NoWrap) if a translation is wider than the window.
            PillBubble(new HudElement[]
                {
                    new TextElement(() => _loc.T(HistConfirming() ? "chat.history.confirm" : "chat.history.clear"),
                        () => HistConfirming() ? new ColorRgba(1f, 1f, 1f) : EnsureContrast(DangerRed, _services.Theme.Colors.MenuBackground, 3f),
                        Emphasis: true),
                },
                () => HistConfirming() ? DangerRed : DangerRed with { A = DangerIdleFillAlpha },
                OnClearHistoryClick, outline: () => DangerRed),
            // Its parent is the Conditional (sized from it), so it gets a static cap instead — same shrink-only trap.
            new ConditionalElement(HistConfirming,
                PillBubble(new HudElement[]
                    {
                        new TextElement(() => _loc.T("chat.history.cancel"), () => _services.Theme.Colors.MenuText,
                            Emphasis: true, NoWrap: true),
                    },
                    () => _services.Theme.Colors.MenuText with { A = 0.08f }, CancelClearHistory, maxWidth: CancelPillMaxW)),
        }, Gap: 6f),
        // The stats get their OWN line. Beside the pill, the stats cell pushed the row's preferred width past the window,
        // and the layout then shrank EVERY child toward its min — the pill too, so its NoWrap label spilled across the
        // stats. The pills alone fit (idle or armed), so each keeps its label width; a long name here just wraps.
        new TextElement(HistStatsText, MutedCol),
        new ConditionalElement(HistConfirming, new TextElement(() => _loc.T("chat.history.confirmNote"), MutedCol)),
    }, Gap: 4f);

    private void SetKeepWhisperHistory(bool on)
    {
        if (on == _keepWhisperHistory) return;
        _keepWhisperHistory = on;
        if (!on) _histPreKey.Clear();   // OFF = record nothing new; the saved files stay (and still show)
        SaveWhisperHistoryConfig();     // .Config.cs
    }

    private void OnClearHistoryClick()
    {
        if (!HistConfirming()) { _histConfirmUntil = _services.Framework.TimeNow + HistConfirmSec; return; }
        _histConfirmUntil = 0f;
        ClearWhisperHistory();
    }

    private void CancelClearHistory() => _histConfirmUntil = 0f;

    // From TickWhisperHistory: nothing to do per tick — HistConfirming() compares the clock itself — except dropping a
    // stale arm when the settings window closes, so it never reopens armed.
    private void TickHistoryConfirm(float now)
    {
        if (_histConfirmUntil > 0f && (!_settingsOpen || now >= _histConfirmUntil)) _histConfirmUntil = 0f;
    }

    // Delete this character's saved history: in-memory now, files on the worker (queued AFTER any pending flush, so
    // nothing written earlier survives). Messages the server still holds keep showing — they're in the chat ring.
    private void ClearWhisperHistory()
    {
        if (_histKey == null) return;   // folder not known yet → nothing saved to clear
        var prefix = _histKey + "/";
        _histGen++;                      // in-flight loads for the old data are dropped
        _saved.Clear();
        _histPreKey.Clear();
        _histIndexLoaded = true;         // the (now empty) index is known — new whispers may write it again
        _histIndexDirty = _histAnyDirty = false;
        _histStatsRev++;
        RemoveSavedOnlyConversations();  // .Whisper.cs
        ClearClosedPeers();              // .WhisperClosed.cs — closed.json goes with the folder below
        _logDirty = true;
        // This character's charId-fallback folder too (.WhisperHistory.Migrate.cs): left behind when a merge hit a write
        // failure, or written while the UID never read — it would otherwise merge the "cleared" history back in at the
        // next login. closed.json is deleted with the rest of each folder.
        var legacy = "whisper." + LegacyUid(_histChar) + "/";
        bool alsoLegacy = _histChar != 0 && !string.Equals(legacy, prefix, StringComparison.Ordinal);
        HistIo(() =>
        {
            foreach (var name in _services.Data.List(prefix)) _services.Data.Delete(name);
            if (alsoLegacy) foreach (var name in _services.Data.List(legacy)) _services.Data.Delete(name);
        });
        _histClearedUntil = _services.Framework.TimeNow + HistClearedSec;
        _services.Log.Info($"[ChatTools] whisper history cleared ({_histKey})");
    }

    // "<character> · N conversation(s) · M message(s)" from the in-memory index (cheap; rebuilt only when it changes).
    private string HistStatsText()
    {
        if (_histClearedUntil > _services.Framework.TimeNow) return _loc.T("chat.history.cleared");
        var tpl = _loc.T("chat.history.stats");
        // The "{0} · {1} · {2}" frame is the same in every locale, so it can't detect a language switch — a count
        // template (which differs per locale) is the cache key too.
        var sub = _loc.T("chat.history.msgs.other");
        var name = SelfName() ?? "";
        if (_histStatsBuiltRev == _histStatsRev && string.Equals(tpl, _histStatsTpl, StringComparison.Ordinal)
            && string.Equals(sub, _histStatsSub, StringComparison.Ordinal)
            && string.Equals(name, _histStatsName, StringComparison.Ordinal))
            return _histStatsText;
        int convs = 0, msgs = 0;
        foreach (var sp in _saved.Values)
        {
            int n = sp.Msgs?.Count ?? sp.Info.Count;
            if (n > 0) { convs++; msgs += n; }
        }
        try
        {
            _histStatsText = string.Format(CultureInfo.InvariantCulture, tpl, name,
                Counted("chat.history.convs", convs), Counted("chat.history.msgs", msgs));
        }
        catch (FormatException) { _histStatsText = tpl; }
        _histStatsBuiltRev = _histStatsRev; _histStatsTpl = tpl; _histStatsSub = sub; _histStatsName = name;
        return _histStatsText;
    }

    // "<n> conversation(s)": every locale ships both .one and .other (identical where the language has no plural, e.g.
    // ja/ko/th/id/fil), so the choice stays a plain n == 1 here with no per-language rules.
    private string Counted(string key, int n)
        => string.Format(CultureInfo.InvariantCulture, _loc.T(key + (n == 1 ? ".one" : ".other")), n);
}
