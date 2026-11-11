using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The logic behind the Settings window's whisper-history rows (the UI is in .Settings.Whispers.cs): the keep toggle,
/// this character's stats line, and "Clear saved whisper history" with the house destructive confirm — the click opens an
/// in-place confirm panel (bold question, muted consequence, Danger "Delete" + Neutral "Cancel"); Delete removes this
/// character's files on the I/O worker and drops the in-memory copy at once, then the stats line says "Cleared" for a
/// few seconds right above the button that was clicked. Nothing saved (0 conversations) → the Clear pill is recessed.
/// </summary>
public sealed partial class Plugin
{
    private const float HistClearedSec = 3f;

    private bool _histConfirming;      // the delete confirm panel is showing (Settings open only)
    private float _histClearedUntil;   // > now = show "Cleared" in the stats slot
    private int _histStatsBuiltRev = -1;
    private string? _histStatsTpl, _histStatsSub, _histStatsName;
    private string _histStatsText = "";
    private int _histStatsConvs;

    private void SetKeepWhisperHistory(bool on)
    {
        if (on == _keepWhisperHistory) return;
        _keepWhisperHistory = on;
        if (!on) _histPreKey.Clear();   // OFF = record nothing new; the saved files stay (and still show)
        SaveWhisperHistoryConfig();     // .Config.cs
    }

    // Clear pill → the confirm panel (only while there is something to delete — the pill is recessed otherwise).
    private void OpenClearHistoryConfirm() { if (HistHasSaved()) _histConfirming = true; }

    private void ConfirmClearHistory()
    {
        _histConfirming = false;
        ClearWhisperHistory();
    }

    private void CancelClearHistory() => _histConfirming = false;

    // From TickWhisperHistory: drop a stale confirm when the settings window closed some other way (chat hidden, zone
    // load), so it never reopens on the confirm panel. CloseSettings clears it too.
    private void TickHistoryConfirm(float now)
    {
        if (_histConfirming && !_settingsOpen) _histConfirming = false;
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

    private bool HistJustCleared() => _histClearedUntil > _services.Framework.TimeNow;

    // Something to delete: the folder is known and at least one conversation has saved messages. Polled by the Clear
    // pill's enabled Func — reads the cached count (EnsureHistStats walks the index only when it changed).
    private bool HistHasSaved()
    {
        if (_histKey == null) return false;
        EnsureHistStats();
        return _histStatsConvs > 0;
    }

    // "<character> · N conversation(s) · M message(s)" from the in-memory index; "Cleared" right after a delete.
    private string HistStatsText()
    {
        if (HistJustCleared()) return _loc.T("chat.history.cleared");
        EnsureHistStats();
        return _histStatsText;
    }

    // Rebuilt only when the index (rev), the language or the character name changes (cheap per poll otherwise).
    private void EnsureHistStats()
    {
        var tpl = _loc.T("chat.history.stats");
        // The "{0} · {1} · {2}" frame is the same in every locale, so it can't detect a language switch — a count
        // template (which differs per locale) is the cache key too.
        var sub = _loc.T("chat.history.msgs.other");
        var name = SelfName() ?? "";
        if (_histStatsBuiltRev == _histStatsRev && string.Equals(tpl, _histStatsTpl, StringComparison.Ordinal)
            && string.Equals(sub, _histStatsSub, StringComparison.Ordinal)
            && string.Equals(name, _histStatsName, StringComparison.Ordinal))
            return;
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
        _histStatsConvs = convs;
        _histStatsBuiltRev = _histStatsRev; _histStatsTpl = tpl; _histStatsSub = sub; _histStatsName = name;
    }

    // "<n> conversation(s)": every locale ships both .one and .other (identical where the language has no plural, e.g.
    // ja/ko/th/id/fil), so the choice stays a plain n == 1 here with no per-language rules.
    private string Counted(string key, int n)
        => string.Format(CultureInfo.InvariantCulture, _loc.T(key + (n == 1 ? ".one" : ".other")), n);
}
