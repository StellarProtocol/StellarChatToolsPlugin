using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The Settings window's "Whisper history" section (after Translation): the keep toggle, the where/how-much note, and a
/// two-step "Clear saved whisper history" button with this character's stats beside it. First click arms it (solid red,
/// "Click again to delete", a Cancel pill + a scope note; reverts by itself after <see cref="HistConfirmSec"/>); the
/// second deletes this character's files on the I/O worker and drops the in-memory copy at once.
/// </summary>
public sealed partial class Plugin
{
    private const float HistConfirmSec = 5f;
    private const float HistClearedSec = 3f;
    private static readonly ColorRgba DangerRed = new(0.90f, 0.30f, 0.30f);
    private const float DangerIdleFillAlpha = 0.10f;

    private float _histConfirmUntil;   // > now = armed
    private float _histClearedUntil;   // > now = show "Cleared" in the stats slot
    private int _histStatsBuiltRev = -1;
    private string? _histStatsTpl, _histStatsName;
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
            BuildPill(new HudElement[]
                {
                    new TextElement(() => _loc.T(HistConfirming() ? "chat.history.confirm" : "chat.history.clear"),
                        () => HistConfirming() ? new ColorRgba(1f, 1f, 1f) : EnsureContrast(DangerRed, _services.Theme.Colors.MenuBackground, 3f),
                        Emphasis: true, NoWrap: true),
                },
                () => HistConfirming() ? DangerRed : DangerRed with { A = DangerIdleFillAlpha },
                OnClearHistoryClick, 0f, () => DangerRed),
            new ConditionalElement(HistConfirming,
                BuildPill(() => _loc.T("chat.history.cancel"), () => _services.Theme.Colors.MenuText,
                    () => _services.Theme.Colors.MenuText with { A = 0.08f }, CancelClearHistory)),
            // Weight cell (min 0) + ellipsis: the armed row's extra Cancel pill squeezes the stats instead of the window.
            new CellElement(new TextElement(HistStatsText, MutedCol, NoWrap: true) { Ellipsize = true }, Weight: 1f),
        }, Gap: 6f),
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
        _logDirty = true;
        HistIo(() => { foreach (var name in _services.Data.List(prefix)) _services.Data.Delete(name); });
        _histClearedUntil = _services.Framework.TimeNow + HistClearedSec;
        _services.Log.Info($"[ChatTools] whisper history cleared ({_histKey})");
    }

    // "<character> · N conversations · M messages" from the in-memory index (cheap; rebuilt only when it changes).
    private string HistStatsText()
    {
        if (_histClearedUntil > _services.Framework.TimeNow) return _loc.T("chat.history.cleared");
        var tpl = _loc.T("chat.history.stats");
        var name = SelfName() ?? "";
        if (_histStatsBuiltRev == _histStatsRev && string.Equals(tpl, _histStatsTpl, StringComparison.Ordinal)
            && string.Equals(name, _histStatsName, StringComparison.Ordinal))
            return _histStatsText;
        int convs = 0, msgs = 0;
        foreach (var sp in _saved.Values)
        {
            int n = sp.Msgs?.Count ?? sp.Info.Count;
            if (n > 0) { convs++; msgs += n; }
        }
        try { _histStatsText = string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl, name, convs, msgs); }
        catch (FormatException) { _histStatsText = tpl; }
        _histStatsBuiltRev = _histStatsRev; _histStatsTpl = tpl; _histStatsName = name;
        return _histStatsText;
    }
}
