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

    private string OpacityHintText()
    {
        if (_customOpacityOn) return _loc.T("chat.settings.opacityHint");
        var fw = FrameworkOpacity();
        return fw.HasValue ? _loc.TFormat("chat.settings.opacityHintFollow", Pct(fw.Value)) : _loc.T("chat.settings.opacityHint");
    }

    // ⚙ settings (.Settings.cs): capsule toggle + sibling label; when on, label + slider + "{n}%" readout (the framework
    // ThemesPanel opacity-row idiom); then the muted hint, which names the followed framework value while off.
    private HudElement BuildOpacitySettings() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => _customOpacityOn, SetCustomOpacityOn),
            new TextElement(() => _loc.T("chat.settings.opacity")),
        }, Gap: 6f),
        new ConditionalElement(() => _customOpacityOn, new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("chat.settings.opacityLabel"), Width: 56f),
            new SliderElement(() => _customOpacity, SetCustomOpacity, OpacityMin, OpacityMax),
            new TextElement(() => $"{Pct(_customOpacity)}%"),
        }, Gap: 4f)),
        new TextElement(OpacityHintText, MutedCol),
    }, Gap: 4f);
}
