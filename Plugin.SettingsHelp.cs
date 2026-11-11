using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The round "?" at the end of every Settings row and the ONE help popover they share, house style (Maestro
/// Plugin.Help.cs / StatInspector Plugin.Settings.Help.cs): a 24 px Neutral circle (lighter on hover, accent-tinted while
/// its tip is open) whose click opens a title-bar-less GlassMenu popover anchored under it — 32 px accent "?" tile + bold
/// setting name + muted "SECTION · HELP" + round ✕, then the help text in one Tile box, 360 wide. Re-clicking the same "?"
/// closes it; another "?" swaps the text and re-anchors it. The popover belongs to the Settings window: it closes with it.
///
/// Z-order: no BringToFront (Rule 5). The tip and Settings are both Category Tools; the tip only ever opens from hidden
/// while Settings is already open (each open remounts it with a fresh, highest ZSeq), and nothing in ChatTools fronts any
/// window, so it always sorts above Settings — also when switching tips while it is visible.
/// </summary>
public sealed partial class Plugin
{
    private const string SettingsTipId = "chattools.settings.help";
    private const float HelpSize    = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;
    private const float HelpOpenAccentAlpha = 0.35f;

    // One "?": the setting's title + section loc keys and its body text Func (cached by the caller where it composes).
    private sealed class HelpTip
    {
        public readonly string TitleKey, SectionKey;
        public readonly Func<string> Body;
        public readonly HoverFlag Hover = new();
        public HelpTip(string titleKey, string sectionKey, Func<string> body)
        { TitleKey = titleKey; SectionKey = sectionKey; Body = body; }
    }

    private IWindowControl? _settingsTipWindow;
    private HelpTip? _openTip;
    private WindowRect _tipRect;              // where the popover should sit (re-applied for a few ticks after opening)
    private int _tipRepositionTicks;
    private readonly HoverFlag _tipCloseHover = new();

    private bool TipOpen(HelpTip tip) => ReferenceEquals(_openTip, tip) && _settingsTipWindow?.IsShown == true;

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box.
    private HudElement HelpDot(HelpTip tip) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: NoClick)                         // the real work is in OnClickWithRect (needs the button's rect)
        {
            Bare = true,
            OnHover = over => tip.Hover.On = over,
            OnClickWithRect = r => ToggleSettingsTip(r, tip),
        })
    {
        FillColorFn = () => Fade(TipOpen(tip) ? Over(AccentColor() with { A = HelpOpenAccentAlpha }, NeutralFill())
                                 : tip.Hover.On ? HoverFill(NeutralFill())
                                 : NeutralFill()),
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Registered BEFORE the Settings window (Plugin.cs): Settings is prebuilt, and its "?" fills poll TipOpen.
    private IWindowControl RegisterSettingsTipWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          SettingsTipId,
            Title:       _loc.T("chat.settings.help"),
            DefaultRect: new WindowRect(800f, 80f, TipWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => _loc.T("chat.settings.help"),
            Draggable = true, Closable = true, StartVisible = false,
            // No title bar: the header carries the title and a close; the whole frame is the drag handle.
            ShowTitleBar = false,
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildTipRoot(),
        OnClose: CloseSettingsTip));

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            TipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _openTip != null ? _loc.T(_openTip.TitleKey) : string.Empty, Color: () => TextColor(),
                    NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            TipCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(() => _openTip?.Body() ?? string.Empty, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // "WHISPERS · HELP" — composed only when the section or the language changes.
    private string? _tipSubSection, _tipSubHelp;
    private string _tipSub = string.Empty;

    private string TipSubtitle()
    {
        if (_openTip == null) return string.Empty;
        string section = _loc.T(_openTip.SectionKey), help = _loc.T("chat.settings.help");
        if (!string.Equals(section, _tipSubSection, StringComparison.Ordinal) || !string.Equals(help, _tipSubHelp, StringComparison.Ordinal))
        {
            _tipSubSection = section; _tipSubHelp = help;
            _tipSub = (section + " · " + help).ToUpperInvariant();
        }
        return _tipSub;
    }

    // A help body made of two catalog texts (the setting's own note + a shared note, e.g. the privacy line), joined by a
    // blank line and re-made only when either text changes (a language switch).
    private Func<string> HelpBody2(string key1, string key2)
    {
        string? a = null, b = null, joined = string.Empty;
        return () =>
        {
            string x = _loc.T(key1), y = _loc.T(key2);
            if (!string.Equals(x, a, StringComparison.Ordinal) || !string.Equals(y, b, StringComparison.Ordinal))
            {
                a = x; b = y; joined = x + "\n\n" + y;
            }
            return joined;
        };
    }

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement TipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => SolidLabel(AccentColor()),
                Width: TipIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Round 24 px ✕ that closes the tip: muted glyph at rest, a soft round wash + text colour on hover.
    private HudElement TipCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseSettingsTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
    {
        FillColorFn = () => _tipCloseHover.On ? Fade(HoverFill(PanelColor())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Re-clicking the same "?" closes it; otherwise (closed, or another "?") show this tip anchored under the "?".
    private void ToggleSettingsTip(WindowRect r, HelpTip tip)
    {
        if (_settingsTipWindow == null) return;
        if (TipOpen(tip)) { CloseSettingsTip(); return; }
        _openTip = tip;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the row instead of off the window.
        float x = Math.Clamp(r.X + r.Width - TipWidth, 0f, Math.Max(0f, CanvasW() - TipWidth));
        float y = Math.Min(r.Y + r.Height + 4f, CanvasH() - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _settingsTipWindow.SetVisible(true);   // see the class summary: no BringToFront (Rule 5)
        _settingsTipWindow.SetRect(_tipRect);
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _tipRepositionTicks = 4;
    }

    private void CloseSettingsTip()
    {
        _tipRepositionTicks = 0;
        _openTip = null;
        // Unconditional: on the very first open IsShown is still false while the mount lands (WindowBuilder-Patterns.md).
        _settingsTipWindow?.SetVisible(false);
    }

    // From TickSettings while Settings is open: re-assert the tip position while the reposition window is open.
    private void TickSettingsTip()
    {
        if (_tipRepositionTicks <= 0) return;
        _tipRepositionTicks--;
        if (_settingsTipWindow?.IsShown == true) _settingsTipWindow.SetRect(_tipRect);
    }
}
