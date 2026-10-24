using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Window body layout and the settings drawer. Body is a ColumnElement: [tab bar] [log scroll that fills]
/// [composer] [settings drawer]. Only the scroll carries flexibleHeight (via ScrollElement), so it alone
/// absorbs the resize slack; the tab bar, composer and drawer stay content-sized.
/// </summary>
public sealed partial class Plugin
{
    private bool _settingsOpen;

    // NOTE: the log font-size control was removed — TextElement font size is ignored on the GlassMenu surface
    // (only HudOverlay honours it), so an A-/A+ stepper here was a confirmed no-op. Honouring a per-log font
    // size needs a framework change (per-TextElement size on the Menu text path); the old config key is simply
    // left dormant (no longer read or written).

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        BuildTabBar(),       // .Tabs.cs
        BuildLogArea(),      // .WhisperList.cs — the log (.Log.cs, the only flexible-height section; grows with the
                             // window), with the conversation list + header beside/above it while Whisper is open
        BuildNewBelowBar(),  // .NewBelow.cs — "↓ N new messages" pill, shown only while scrolled up with unseen rows
        BuildComposer(),     // .Composer.cs
        BuildSettingsDrawer(),
    }, Gap: 6f);

    private void ToggleSettings() => _settingsOpen = !_settingsOpen;

    private const string GearResource = "Stellar.ChatTools.Assets.settings_gear.png";
    private byte[]? _gearPng;   // embedded gear icon, read once
    private bool _gearTried;

    // Title-bar ⚙ (WindowRegistration.TitleTrailing — the chrome builds it right-aligned, just before the ✕). Same
    // icon + 15px-in-a-cell shape as the CooldownBar/StatInspector header gears; those windows are Borderless so their
    // gear sits in a self-drawn body header, while this GlassMenu window has a real title bar to host it. A PNG, not
    // the ⚙ glyph (no in-game font coverage). Row(Justify.Center) wrapper: the Selectable force-expands its child's
    // width, so a bare 15px image would be stretched to the cell's inner width (WindowBuilder-Patterns.md). Selected =
    // drawer open, so the gear reads as a toggle.
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

    // Settings drawer (title-bar gear-toggled). Migrates the auto-reply UI and adds a font-size control, the quick-phrase
    // editor, and a one-line pointer to where the channel colours are edited.
    private HudElement BuildSettingsDrawer() => new ConditionalElement(() => _settingsOpen, new ColumnElement(new HudElement[]
    {
        new SeparatorElement(),
        new TextElement(() => _loc.T("chat.settings.title"), Emphasis: true),

        // Auto-reply (migrated): capsule toggle + sibling label, then the reply text + cooldown readout.
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
                new InputElement(() => _autoReplyText, s => { _autoReplyText = s; SaveAutoReplyText(); }, 300f, OnChange: s => _autoReplyText = s),
            }, Gap: 4f),
            new TextElement(() => _loc.TFormat("chat.cooldown", AutoReplyCooldown.TotalSeconds), () => _services.Theme.Colors.MenuMuted),
        }, Gap: 4f)),

        new SeparatorElement(),

        // Translation (.Translate.cs): target language, hover-button toggle, and the privacy note.
        BuildTranslateSettings(),

        new SeparatorElement(),

        // Quick-phrase editor: list of phrase + remove, then an add row (Enter or the Add button).
        new TextElement(() => _loc.T("chat.quick.title"), Emphasis: true),
        new ConditionalElement(() => _quickPhrases.Count == 0,
            new TextElement(() => _loc.T("chat.quick.empty"), () => _services.Theme.Colors.MenuMuted)),
        BuildQuickEditor(),
        new RowElement(new HudElement[]
        {
            new InputElement(() => _quickEditText, s => { _quickEditText = s; AddQuickPhrase(); }, 260f, OnChange: s => _quickEditText = s),
            new ButtonElement(() => _loc.T("chat.quick.add"), AddQuickPhrase),
        }, Gap: 4f),

        new SeparatorElement(),

        // Channel colours: the tab strip itself is now the legend (each tab wears its channel's colour), so a
        // swatch list here would only repeat it. One hint pointing at where they are edited — the framework Theme
        // settings (IColorSlot is read-only to the plugin).
        new TextElement(() => _loc.T("chat.settings.colorsHint"), () => _services.Theme.Colors.MenuMuted),
    }, Gap: 4f));

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
