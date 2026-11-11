using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// The settings window (<c>chattools.settings</c>), toggled by the title-bar ⚙ of the chat window. A SEPARATE window,
/// not a drawer inside <c>chattools.main</c>: the inline drawer squeezed the log and got messy in a short window, and
/// the chat layout must never change when settings open. House style (Stellar Design Language, user-approved "option A"
/// 2026-11-12): ToolHeader + two columns of SectionPanels (.Settings.Layout.cs: WHISPERS / TRANSLATION | ALL TAB /
/// DISPLAY), every setting row ending in a round "?" that opens the shared help popover (.SettingsHelp.cs). No title
/// bar — the header carries the title and the ✕, and the whole frame drags. The window height is CONSTANT across every
/// state (auto-reply on/off, the clear-history confirm, recessed controls), so nothing jumps. (Quick phrases are
/// managed in their own popover, .QuickPhrases.cs.)
///
/// Placement: re-centred over the chat window's CURRENT rect on EVERY open (clamped on-screen) — it reads as a sheet
/// over the chat, so it should open where the chat is now, not wherever it was last dragged. While open it can be
/// dragged freely. It is a child of the chat window: closing/hiding the chat (✕, hotkey, zone load) closes it too, and
/// it does not reopen by itself when the chat comes back.
///
/// Z-order: settings and the chat are both Category Tools (same ZCat), so the tie breaks on ZSeq. Settings only ever
/// opens from hidden (the ⚙ toggles it closed when open) → each open remounts it with a fresh, highest ZSeq, and
/// nothing ever BringToFront()s the chat, so settings always sorts above it — no BringToFront needed (Rule 5). The
/// translate-language DropdownElement popup lives on its own root canvas (32766) — above every window.
/// </summary>
public sealed partial class Plugin
{
    private const string SettingsId = "chattools.settings";
    // Fixed width (GlassMenu non-resizable: width = DefaultRect.Width, height content-fits). The column maths lives in
    // .Settings.Layout.cs; 720 holds the longest setting label of every shipped locale (house rule 18 — widen, don't
    // cram: e.g. fil "I-save ang whisper history sa PC" ≈ 200 px in a 223 px label slot).
    private const float SettingsW = 720f;
    // The open's first layout can still be settling (first mount, or a parked tree whose content changed while hidden),
    // so the centre is re-applied whenever the measured height changes during this short window after an open.
    private const float SettingsSettleSec = 0.6f;

    private IWindowControl _settingsWindow = null!;   // set in the ctor (RegisterSettingsWindow)
    private bool _settingsOpen;                        // user intent: gear / ✕ — drives the gear highlight
    private float _settingsSettleUntil;
    private float _settingsCentredH = -1f;

    private IWindowControl RegisterSettingsWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          SettingsId,
            Title:       _loc.T("chat.settings.windowTitle"),
            // Constant (never screen-derived, WindowBuilder-Patterns.md); the real position is set on every open.
            DefaultRect: new WindowRect(520f, 300f, SettingsW, 400f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        { StartVisible = false, Closable = true, Draggable = true,
          // No title bar: the ToolHeader carries the title and the ✕; the whole frame is the drag handle (controls and
          // lists are skipped by the drag, fw 2.29.1).
          ShowTitleBar = false,
          // Built in the background at start-up (Plugin-Performance-Rules §5): two columns of panels is a ~100-element
          // tree, so the first ⚙ click no longer builds it inline. Its value Funcs are safe while hidden (config fields,
          // the loc catalog, try-guarded PlayerState.Name).
          Prebuild = true,
          TitleProvider = () => _loc.T("chat.settings.windowTitle"),   // re-read on a live language switch (.Language.cs)
          ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                            && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
        BuildSettingsRoot(),
        OnClose: CloseSettings));   // the ✕ → gear highlight follows

    private void ToggleSettings() { if (_settingsOpen) CloseSettings(); else OpenSettings(); }

    private void OpenSettings()
    {
        _settingsOpen = true;
        _settingsSettleUntil = _services.Framework.TimeNow + SettingsSettleSec;
        _settingsCentredH = -1f;
        _settingsWindow.SetVisible(true);   // opens from hidden → fresh ZSeq above the chat (same ZCat); no BringToFront
        // A re-open finds the tree parked (still mounted, so IsShown is already true) → place it NOW so it never shows
        // a frame at the old spot. On the very first open it is unmounted (PlaceSettings bails); TickSettings centres it
        // once the mount lands.
        PlaceSettings();
    }

    private void CloseSettings()
    {
        FlushAutoReplyText();   // .Config.cs — a typed, un-submitted reply is saved on close
        FlushOpacity();         // .WindowOpacity.cs — a debounced slider value is saved on close
        CloseSettingsTip();     // .SettingsHelp.cs — the help popover belongs to this window
        _histConfirming = false;   // .WhisperHistory.Settings.cs — never reopen on the delete confirm
        _settingsOpen = false;
        _settingsWindow?.SetVisible(false);
    }

    // Framework.Update. The chat window is always shown when the ⚙ is clicked, so !_window.IsShown here really means
    // it closed (not the first-open mount lag, WindowBuilder-Patterns.md "IsShown is FALSE…") — close with it.
    private void TickSettings(float _)
    {
        TickAutoReplySave();    // .Config.cs — debounced save of a typed reply (runs while closed too)
        TickOpacitySave();      // .WindowOpacity.cs — debounced save of the opacity slider (runs while closed too)
        if (!_settingsOpen) return;
        TickSettingsTip();      // .SettingsHelp.cs — re-anchors a just-opened help popover for a few ticks
        if (!_window.IsShown) { CloseSettings(); return; }
        if (!_settingsWindow.IsShown) return;   // first open: mount lands in a later band
        var r = _settingsWindow.Rect;
        if (r.Width <= 0f || r.Height <= 0f) return;
        if (_services.Framework.TimeNow < _settingsSettleUntil)
        {
            if (Math.Abs(r.Height - _settingsCentredH) > 0.5f) PlaceSettings();
            return;
        }
        // The height is constant by design, but a live language switch can still re-measure it (a longer colour hint);
        // the window grows DOWN from its top edge, so keep the bottom on-screen. Only writes when actually off the
        // bottom (SetRect persists — no per-tick churn).
        float ch = CanvasH();
        if (r.Y + r.Height > ch + 0.5f && r.Y > 0.5f)
            _settingsWindow.SetRect(new WindowRect(r.X, Math.Max(0f, ch - r.Height), r.Width, r.Height));
    }

    // Centre over the chat window's current rect, clamped fully on-screen (canvas units, top-left origin). Unmounted,
    // Rect is only the spec default (and SetRect a no-op) — bail so _settingsCentredH isn't latched to a fake height.
    private void PlaceSettings()
    {
        if (!_settingsWindow.IsShown) return;
        var r = _settingsWindow.Rect;
        var m = _window.Rect;
        if (r.Width <= 0f || r.Height <= 0f || m.Width <= 0f) return;
        float x = m.X + (m.Width - r.Width) * 0.5f, y = m.Y + (m.Height - r.Height) * 0.5f;
        x = Math.Clamp(x, 0f, Math.Max(0f, CanvasW() - r.Width));
        y = Math.Clamp(y, 0f, Math.Max(0f, CanvasH() - r.Height));
        _settingsCentredH = r.Height;
        _settingsWindow.SetRect(new WindowRect(x, y, r.Width, r.Height));
    }

    private float CanvasW() => _services.Framework.CanvasWidth > 0 ? _services.Framework.CanvasWidth : Screen.width;
    private float CanvasH() => _services.Framework.CanvasHeight > 0 ? _services.Framework.CanvasHeight : Screen.height;

    // ── Title-bar gear ────────────────────────────────────────────────────────────────────────────────────────────
    private const string GearResource = "Stellar.ChatTools.Assets.settings_gear.png";
    private byte[]? _gearPng;   // embedded gear icon, read once
    private bool _gearTried;

    // Title-bar ⚙ (WindowRegistration.TitleTrailing — the chrome builds it right-aligned, just before the ✕). Same
    // icon + 15px-in-a-cell shape as the CooldownBar/StatInspector header gears; those windows are Borderless so their
    // gear sits in a self-drawn body header, while this GlassMenu window has a real title bar to host it. A PNG, not
    // the ⚙ glyph (no in-game font coverage). Row(Justify.Center) wrapper: the Selectable force-expands its child's
    // width, so a bare 15px image would be stretched to the cell's inner width (WindowBuilder-Patterns.md). Selected =
    // settings window open, so the gear reads as a toggle.
    private HudElement BuildTitleGear() => new CellElement(
        new SelectableElement(
            new RowElement(new HudElement[] { new ImageElement(GearPng, 15, 15) }, Justify: RowJustify.Center),
            OnClick: ToggleSettings, Selected: () => _settingsOpen),
        Width: 27f);

    private byte[]? GearPng()
    {
        if (_gearTried) return _gearPng;
        _gearTried = true;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(GearResource);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); _gearPng = ms.ToArray(); }
        }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] settings gear icon load failed: {ex.Message}"); }
        return _gearPng;
    }
}
