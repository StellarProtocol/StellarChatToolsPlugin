using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// The settings window (<c>chattools.settings</c>), toggled by the title-bar ⚙ of the chat window. A SEPARATE window,
/// not a drawer inside <c>chattools.main</c>: the inline drawer squeezed the log and got messy in a short window, and
/// the chat layout must never change when settings open. Content: auto-reply, translation (.Translate.cs), the
/// quick-phrase editor, and the channel-colour hint.
///
/// Placement: re-centred over the chat window's CURRENT rect on EVERY open (clamped on-screen) — it reads as a sheet
/// over the chat, so it should open where the chat is now, not wherever it was last dragged. While open it can be
/// dragged freely. It is a child of the chat window: closing/hiding the chat (✕, hotkey, zone load) closes it too, and
/// it does not reopen by itself when the chat comes back.
///
/// Z-order: Category Tools (ZCat 1) vs the chat's HUD (ZCat 0), and nothing ever BringToFront()s the chat, so settings
/// always sorts above it. It also only ever opens from hidden (the ⚙ toggles it closed when open), so no BringToFront
/// (Rule 5). The translate-language DropdownElement popup lives on its own root canvas (32766) — above every window.
/// </summary>
public sealed partial class Plugin
{
    private const string SettingsId = "chattools.settings";
    // Fixed width (GlassMenu non-resizable: width = DefaultRect.Width, height content-fits). 380 leaves the auto-reply
    // row (56 label + 4 + 280 field) and the quick-phrase add row inside the 12+12 body padding.
    private const float SettingsW = 380f;
    // Quick phrases are the only unbounded section (up to MaxQuickPhrases rows). Up to this many the list is plain
    // (window height fits content); past it the list moves into a fixed-height ScrollElement, which caps the whole
    // window at ~650 units so it stays on-screen. ScrollElement has no "fit content up to a max" mode (its Height is a
    // preferred/fixed viewport), so the cap is done by swapping branches instead of a framework change.
    private const int QuickPlainMax = 6;
    private const float QuickScrollH = 6 * 32f;   // ≈6 rows of 28 + 4 gap
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
        _settingsWindow.SetVisible(true);   // opens from hidden, ZCat Tools > the chat's HUD → on top; no BringToFront
        // A re-open finds the tree parked (still mounted, so IsShown is already true) → place it NOW so it never shows
        // a frame at the old spot. On the very first open it is unmounted (PlaceSettings bails); TickSettings centres it
        // once the mount lands.
        PlaceSettings();
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        _settingsWindow?.SetVisible(false);
    }

    // Framework.Update. The chat window is always shown when the ⚙ is clicked, so !_window.IsShown here really means
    // it closed (not the first-open mount lag, WindowBuilder-Patterns.md "IsShown is FALSE…") — close with it.
    private void TickSettings(float _)
    {
        if (!_settingsOpen) return;
        if (!_window.IsShown) { CloseSettings(); return; }
        if (!_settingsWindow.IsShown) return;   // first open: mount lands in a later band
        var r = _settingsWindow.Rect;
        if (r.Width <= 0f || r.Height <= 0f) return;
        if (_services.Framework.TimeNow < _settingsSettleUntil)
        {
            if (Math.Abs(r.Height - _settingsCentredH) > 0.5f) PlaceSettings();
            return;
        }
        // Content grew while open (auto-reply on, a phrase added): the window grows DOWN from its top edge, so keep the
        // bottom on-screen. Only writes when actually off the bottom (SetRect persists — no per-tick churn).
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

    // ── Settings content (unchanged from the old inline drawer; the window title replaces its "Settings" header) ──
    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        // Auto-reply: capsule toggle + sibling label, then the reply text + cooldown readout.
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => _autoReplyEnabled, SetAutoReply),
            new TextElement(() => _loc.T("chat.autoReply.label")),
        }, Gap: 6f),
        new ConditionalElement(() => _autoReplyEnabled, new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => _loc.T("chat.reply"), Width: 56f),
                new InputElement(() => _autoReplyText, s => { _autoReplyText = s; SaveAutoReplyText(); }, 280f, OnChange: s => _autoReplyText = s),
            }, Gap: 4f),
            new TextElement(() => _loc.TFormat("chat.cooldown", AutoReplyCooldown.TotalSeconds), () => _services.Theme.Colors.MenuMuted),
        }, Gap: 4f)),

        new SeparatorElement(),

        // Translation (.Translate.cs): target language, hover-button toggle, and the privacy note.
        BuildTranslateSettings(),

        new SeparatorElement(),

        // Quick-phrase editor: list of phrase + remove, then an add row (Enter or the Add button). Plain while short,
        // scrolled past QuickPlainMax (see the const) — two list instances, one per branch, over the same state.
        new TextElement(() => _loc.T("chat.quick.title"), Emphasis: true),
        new ConditionalElement(() => _quickPhrases.Count == 0,
            new TextElement(() => _loc.T("chat.quick.empty"), () => _services.Theme.Colors.MenuMuted)),
        new ConditionalElement(() => _quickPhrases.Count <= QuickPlainMax, BuildQuickEditor(),
            new ScrollElement(BuildQuickEditor(), QuickScrollH) { FixedHeight = true }),
        new RowElement(new HudElement[]
        {
            new InputElement(() => _quickEditText, s => { _quickEditText = s; AddQuickPhrase(); }, 260f, OnChange: s => _quickEditText = s),
            new ButtonElement(() => _loc.T("chat.quick.add"), AddQuickPhrase),
        }, Gap: 4f),

        new SeparatorElement(),

        // Channel colours: the tab strip itself is the legend (each tab wears its channel's colour), so a swatch list
        // here would only repeat it. One hint pointing at where they are edited — the framework Theme settings
        // (IColorSlot is read-only to the plugin).
        new TextElement(() => _loc.T("chat.settings.colorsHint"), () => _services.Theme.Colors.MenuMuted),
    }, Gap: 4f);

    private HudElement BuildQuickEditor()
    {
        var slots = new HudElement[MaxQuickPhrases];
        for (int i = 0; i < MaxQuickPhrases; i++)
        {
            var idx = i;
            slots[idx] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => QuickPhrase(idx)), Weight: 1f),
                new ButtonElement(() => _loc.T("chat.quick.remove"), () => RemoveQuickPhrase(idx)),
            }, Gap: 4f);
        }
        return new ListElement(() => _quickPhrases.Count, slots);
    }
}
