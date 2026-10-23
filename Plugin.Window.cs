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
        BuildLogScroll(),    // .Log.cs — the only flexible-height section; grows with the window
        BuildNewBelowBar(),  // .NewBelow.cs — "↓ N new messages" pill, shown only while scrolled up with unseen rows
        BuildComposer(),     // .Composer.cs
        BuildSettingsDrawer(),
    }, Gap: 6f);

    private void ToggleSettings() => _settingsOpen = !_settingsOpen;

    // Settings drawer (gear-toggled). Migrates the auto-reply UI and adds a font-size control, the quick-phrase
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
