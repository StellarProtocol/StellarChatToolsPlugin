using System;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Per-window opacity override for the MAIN chat window only (⚙ "Custom window opacity", off by default). Off, the chat
/// follows the framework's global window opacity (IChromeStyle.WindowOpacity, Theme settings) like every other window;
/// on, <c>WindowSpec.FrameOpacity</c> (Plugin.cs) returns the slider value instead. The framework polls that Func live and
/// only tints the frame/background Image alpha, so text, bubbles and buttons stay solid — the hint says exactly that.
/// The Settings window and the Borderless popups are untouched (they keep the framework value).
///
/// Persistence: the toggle saves at once; the slider applies live while dragging (the Func reads the field) but the
/// config write is debounced like the auto-reply text (.Config.cs) — one file write after the drag settles, not one per
/// drag frame — and flushed when the Settings window closes.
/// </summary>
public sealed partial class Plugin
{
    private const string CfgOpacityOn = "display.customOpacity.enabled";
    private const string CfgOpacity   = "display.customOpacity.value";
    private const float OpacityMin = 0.3f, OpacityMax = 1f;   // same range the framework clamps WindowOpacity to
    private const float OpacityDefault = 0.85f;
    private const float OpacitySaveDelaySec = 0.5f;

    private bool _customOpacityOn;
    private float _customOpacity = OpacityDefault;
    private float _opacitySaveAt = -1f;   // TimeNow the pending slider value is due to save (-1 = nothing pending)

    // From LoadConfig (.Config.cs).
    private void LoadOpacityConfig()
    {
        _customOpacityOn = _cfg.Get(CfgOpacityOn, false);
        _customOpacity = Mathf.Clamp(_cfg.Get(CfgOpacity, OpacityDefault), OpacityMin, OpacityMax);
    }

    // WindowSpec.FrameOpacity of chattools.main: null = follow the framework (IChromeStyle.WindowOpacity).
    private float? ChatFrameOpacity() => _customOpacityOn ? _customOpacity : null;

    private void SetCustomOpacityOn(bool on)
    {
        if (_customOpacityOn == on) return;
        _customOpacityOn = on;
        _cfg.Set(CfgOpacityOn, on);
        _cfg.Save();
    }

    private void SetCustomOpacity(float v)
    {
        _customOpacity = Mathf.Clamp(v, OpacityMin, OpacityMax);   // live: the next frame-alpha poll picks it up
        _opacitySaveAt = _services.Framework.TimeNow + OpacitySaveDelaySec;
    }

    // From TickSettings, every framework tick (before its settings-open gate).
    private void TickOpacitySave()
    {
        if (_opacitySaveAt >= 0f && _services.Framework.TimeNow >= _opacitySaveAt) SaveOpacity();
    }

    private void FlushOpacity() { if (_opacitySaveAt >= 0f) SaveOpacity(); }

    private void SaveOpacity()
    {
        _opacitySaveAt = -1f;
        _cfg.Set(CfgOpacity, _customOpacity);
        _cfg.Save();
    }

    // The framework's global value for the "follows" hint. IChromeStyle is not on IPluginServices, but the shared
    // INamedTheme instance is the framework theme service, which implements IChromeStyle too — so a cast reads it. If a
    // future framework splits them, the cast fails and the hint drops the percentage (OpacityHintText).
    private float? FrameworkOpacity() => (_services.NamedTheme as IChromeStyle)?.WindowOpacity;

    private static int Pct(float v) => Mathf.RoundToInt(v * 100f);

    // The "Custom window opacity" help text (Settings DISPLAY "?", .Settings.Layout.cs): while off it names the followed
    // framework value. Polled while the help popover shows → formatted only when the state, the followed percent or the
    // language changes.
    private bool _ohOn;
    private int _ohPct = int.MinValue;
    private string? _ohLang;
    private string _ohText = string.Empty;

    private string OpacityHintText()
    {
        var fw = FrameworkOpacity();
        int pct = fw.HasValue ? Pct(fw.Value) : -1;
        var lang = _loc.Language;
        if (_customOpacityOn == _ohOn && pct == _ohPct && string.Equals(lang, _ohLang, StringComparison.Ordinal))
            return _ohText;
        _ohOn = _customOpacityOn; _ohPct = pct; _ohLang = lang;
        _ohText = _customOpacityOn || pct < 0
            ? _loc.T("chat.settings.opacityHint")
            : _loc.TFormat("chat.settings.opacityHintFollow", pct);
        return _ohText;
    }
}
