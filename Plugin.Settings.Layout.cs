using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The Settings window's content, house style (user-approved "option A", 2026-11-12): ToolHeader (accent icon tile, title,
/// live status line, round ✕), then two top-aligned columns of SectionPanels — WHISPERS (.Settings.Whispers.cs) +
/// TRANSLATION on the left, ALL TAB + DISPLAY on the right. Every setting row is [label (flex)] [control] [round "?"],
/// so the "?" column lines up (house rule 17); the long notes moved behind the "?"s (.SettingsHelp.cs).
///
/// Geometry is FIXED (the column maths below) so the window never changes size: on/off chips swap whole same-size
/// variants, recessed controls keep their size, the status line ellipsizes, and every state of a panel occupies the same
/// height. Settings and behaviour are unchanged — the same setters, debounced saves and config keys as before.
/// </summary>
public sealed partial class Plugin
{
    // ── column maths (canvas units) ───────────────────────────────────────────────────────────────────────────────
    private const float SetBodyPad  = 12f;   // GlassMenu body padding, each side
    private const float SetColGap   = 10f;
    private const float SetPanelPad = 10f;   // SectionPanel PaddingX
    private const float SetGap      = 6f;    // between a row's children and a panel's rows
    private const float SetColW     = (SettingsW - 2f * SetBodyPad - SetColGap) / 2f;   // 343
    private const float SetInnerW   = SetColW - 2f * SetPanelPad;                      // 323: a panel's content width
    private const float OnOffChipW  = 64f;
    private const float TrDropdownW = 170f;  // leaves 106+ for "Translate to" in every locale (id "Terjemahkan ke" ≈ 96)
    private const float OpacityLabelW = 64f; // "Opacity" — ja/ko ≈ 56
    private const float OpacityValueW = 40f; // "100%"
    private static readonly float ChannelChipW = MathF.Floor((SetInnerW - 2f * SetGap) / 3f);   // 3 per row

    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(SettingsGlyphPng, () => _loc.T("chat.settings.windowTitle"), SettingsStatus, CloseSettings),
        new RowElement(new HudElement[]
        {
            SettingsColumn(BuildWhispersPanel(), BuildTranslationPanel()),   // .Settings.Whispers.cs
            SettingsColumn(BuildAllTabPanel(), BuildDisplayPanel()),
        }, Gap: SetColGap),
    }, Gap: 10f);

    // A fixed-width, TOP-aligned column (a Row centres its children vertically, and the two columns differ in height):
    // an always-true Conditional with Fill makes the column take the row's height and lay out from the top
    // (WindowBuilder-Patterns.md "Top-align inside a row", ✅ in-game in the compact rows).
    private static HudElement SettingsColumn(params HudElement[] panels) => new CellElement(
        new ConditionalElement(() => true, new ColumnElement(panels, Gap: 8f), Fill: true), Width: SetColW);

    // [label (flex, ellipsized)] [control] [?] — 24 tall with a chip, 28 with a dropdown.
    private HudElement SettingRow(Func<string> label, HudElement control, HelpTip tip) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(label, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
        control,
        HelpDot(tip),   // .SettingsHelp.cs
    }, Gap: SetGap);

    // The house on/off toggle chip: ON = bold Success text on a 22% Success wash, OFF = muted; same 64 × 24 both ways.
    private HudElement OnOffChip(Func<bool> isOn, Action<bool> set) => ColourChip(
        () => _loc.T("chat.settings.on"), () => _loc.T("chat.settings.off"),
        () => Menu.Success, isOn, () => set(!isOn()), OnOffChipW);

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    // "Auto-reply off · All tab shows 5 of 6 channels · Translate to English", rebuilt only when an input changes (the
    // Func is polled every refresh). The translate target follows the UI language while unset, so the language is a key.
    private bool _stAuto;
    private int _stShown = -1;
    private string? _stTarget, _stLang;
    private string _stText = string.Empty;

    private string SettingsStatus()
    {
        bool auto = _autoReplyEnabled;
        int shown = AllTabChips.Length - _allTabHidden.Count;
        var lang = _loc.Language;
        if (auto == _stAuto && shown == _stShown && ReferenceEquals(_trTargetCfg, _stTarget)
            && string.Equals(lang, _stLang, StringComparison.Ordinal))
            return _stText;
        _stAuto = auto; _stShown = shown; _stTarget = _trTargetCfg; _stLang = lang;
        _stText = _loc.T(auto ? "chat.settings.status.autoReplyOn" : "chat.settings.status.autoReplyOff")
            + " · " + SafeFormat2(_loc.T("chat.settings.status.allTab"), shown, AllTabChips.Length)
            + " · " + SafeFormat(_loc.T("chat.settings.status.translate"), TrLangName(TranslateTarget()));
        return _stText;
    }

    // ── TRANSLATION ───────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildTranslationPanel()
    {
        Func<string> section = () => _loc.T("chat.translate.title");
        return SectionPanel(section, null,
            SettingRow(() => _loc.T("chat.translate.targetLabel"),
                // ~52 languages → a capped, scrollable popup (MaxVisibleRows). Pill = the house trigger + list; the list
                // is only as wide as its trigger, so a long name ("Bahasa Indonesia (…)") ellipsizes. Capsule = 13 on
                // the 28 trigger (fw 2.29.0); the fill is faded with the window like every other fill (fw 2.30.0).
                new DropdownElement(TrTargetIndex, TrTargetOptions, OnPickTrTarget, Width: TrDropdownW)
                {
                    MaxVisibleRows = 12, Style = DropdownStyle.Pill, CornerRadius = RoundRadius(StylePillH),
                    FillColorFn = () => Fade(NeutralFill()),
                },
                new HelpTip("chat.translate.targetLabel", "chat.translate.title",
                    HelpBody2("chat.settings.help.translateTo", "chat.translate.privacy"))),
            SettingRow(() => _loc.T("chat.translate.hoverToggle"),
                OnOffChip(() => _trHoverButton, v => { _trHoverButton = v; SaveTranslateConfig(); }),
                new HelpTip("chat.translate.hoverToggle", "chat.translate.title",
                    HelpBody2("chat.settings.help.hoverButton", "chat.translate.privacy"))));
    }

    // ── ALL TAB ───────────────────────────────────────────────────────────────────────────────────────────────────
    // The six channel chips (.AllTabFilter.cs logic: the last chip that is on can't be turned off), two rows of three
    // (the toolkit has no wrapping row), each in its channel's tab colour.
    private HudElement BuildAllTabPanel() => SectionPanel(() => _loc.T("chat.settings.allTab.title"),
        () => _loc.T("chat.settings.allTab.hint"),
        new RowElement(new[] { AllTabChip(AllTabChips[0]), AllTabChip(AllTabChips[1]), AllTabChip(AllTabChips[2]) }, Gap: SetGap),
        new RowElement(new[] { AllTabChip(AllTabChips[3]), AllTabChip(AllTabChips[4]), AllTabChip(AllTabChips[5]) }, Gap: SetGap));

    private HudElement AllTabChip(ChatTab tab)
    {
        var ch = TabChannel(tab)!.Value;
        var key = TabLabelKey(tab);
        Func<string> label = () => _loc.T(key);
        return ColourChip(label, label, () => TabBaseColor(tab), () => AllTabShows(ch), () => ToggleAllTabChannel(ch),
            ChannelChipW);
    }

    // ── DISPLAY ───────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildDisplayPanel()
    {
        const string Section = "chat.settings.section.display";
        return SectionPanel(() => _loc.T(Section), null,
            SettingRow(() => _loc.T("chat.settings.compact"), OnOffChip(CompactOn, SetCompactMessages),
                new HelpTip("chat.settings.compact", Section, () => _loc.T("chat.settings.compactHint"))),
            SettingRow(() => _loc.T("chat.settings.opacity"), OnOffChip(() => _customOpacityOn, SetCustomOpacityOn),
                new HelpTip("chat.settings.opacity", Section, OpacityHintText)),
            BuildOpacitySliderRow(Section),
            // Channel colours: the tab strip itself is the legend, so one hint pointing at where they are edited (the
            // framework theme settings — IColorSlot is read-only to the plugin).
            new TextElement(() => _loc.T("chat.settings.colorsHint"), Color: () => MutedColor()));
    }

    // ON: [muted "Opacity"] [round-knob slider] [78%] [?]. OFF: [muted "Uses Stellar opacity · 90%"] [?] — the slider
    // has no visible disabled state (Interactable=false still draws a pink track + white knob, KB WindowBuilder-Patterns
    // "Two-column settings window"), so it is swapped out rather than greyed. Both states sit in one Row pinned by a
    // 28 px strut (the Row centres them), so the row — and the window — keep their height; the "?" stays outside the
    // swap so the column still lines up (house rule 17).
    private HudElement BuildOpacitySliderRow(string section) => new RowElement(new HudElement[]
    {
        new CellElement(new RowElement(new HudElement[]
        {
            new SpacerElement(1f, StylePillH),
            new CellElement(new ConditionalElement(() => _customOpacityOn, new RowElement(new HudElement[]
            {
                new TextElement(() => _loc.T("chat.settings.opacityLabel"), Color: () => MutedColor(), Width: OpacityLabelW,
                    NoWrap: true) { Ellipsize = true },
                new CellElement(new SliderElement(() => _customOpacity, SetCustomOpacity, OpacityMin, OpacityMax)
                    { SquareHandle = true, HandleSize = 14f }, Weight: 1f),
                new TextElement(OpacityValueText, Color: () => TextColor(), Width: OpacityValueW,
                    Align: TextAlign.Right, NoWrap: true),
            }, Gap: SetGap),
                new TextElement(OpacityFollowText, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }), Weight: 1f),
        }, Gap: 0.01f), Weight: 1f),
        HelpDot(new HelpTip("chat.settings.opacityLabel", section, () => _loc.T("chat.settings.help.opacityLevel"))),
    }, Gap: SetGap);

    // "78%", re-made only when the shown percent changes (polled every refresh; dragging changes it ~per frame).
    private int _opValuePct = -1;
    private string _opValueText = string.Empty;

    private string OpacityValueText()
    {
        int p = Pct(_customOpacity);
        if (p != _opValuePct) { _opValuePct = p; _opValueText = p + "%"; }
        return _opValueText;
    }
}
