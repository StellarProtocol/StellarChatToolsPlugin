using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette and the pill / panel /
// chip kit the Settings window and its help popover build from. Ported from the reference kits (StellarMultiToolPlugin /
// StellarMaestroPlugin Plugin.Style.cs, via .claude\skills\stellar-design\reference; the window-opacity fade and the
// clickable chip as in StatInspector's) and trimmed to what ChatTools uses. The colour maths it needs (Mix / Over /
// EnsureContrast / Luminance) already lives in Plugin.Tabs.cs and the recessed disabled look (DisabledShade /
// DisabledLabel) in Plugin.SendCooldown.cs — reused, not copied. Needs SDK >= 2.30.0 (theme status colours,
// TextElement.Height) — ChatTools is on 2.30.0.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so small captions are Bold+FontSize while hints stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh. Status green / red come from the theme too (IThemeStatusColors, fw 2.30.0).
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float NeutralMix       = 0.22f;   // neutral pill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float PillMinContrast  = 4.5f;    // tinted fills (Neutral, chips) keep 4.5:1 labels
    private const float SolidMinContrast = 3.0f;    // white on a SOLID fill (Danger, the accent tile) keeps white to 3:1
    private const float ChipWashAlpha    = 0.22f;   // "on" chip = bold coloured text on a 22% wash of its colour
    private const float ChipOffAlpha     = 0.15f;   // "off" chip = muted text on a faint muted wash
    private const float DangerFillAlpha  = 0.85f;   // the house Danger pill (#ff4d4f @ 0.85 on the dark presets)
    private const float HoverMix         = 0.10f;   // hover = 10% MenuText over the fill
    private static readonly ColorRgba SolidDarkLabel = new(0.12f, 0.12f, 0.12f);   // #1F1F1F: label when white < 3:1

    private IThemeColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);
    private ColorRgba DangerFill(ColorRgba surface) => Over(Menu.Danger with { A = DangerFillAlpha }, surface);

    // Solid-fill label rule (KB Stellar-Design-Language, fw 8df7f29): WHITE when white reaches 3:1 on the fill, otherwise
    // near-black — never a "darkened white" mid grey, which bold thin text can't carry.
    private static ColorRgba SolidLabel(ColorRgba fill)
        => ContrastRatio(1f, Luminance(fill)) >= SolidMinContrast ? new ColorRgba(1f, 1f, 1f) : SolidDarkLabel;

    // ── window opacity: EVERY coloured fill follows the window opacity, uniformly ──────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (Themes → Window Opacity); every bubble fill here is an opaque
    // pre-blended colour, so at a low opacity the panels, chips and pills would stay solid on a see-through frame. Every
    // fill therefore goes through Fade with the frame's factor; only text and pictures stay solid (house rule
    // 2026-11-07). Composed OPAQUE first, alpha last. The settings window follows the FRAMEWORK value — the
    // "Custom window opacity" override is for the chat window only (.WindowOpacity.cs). A cast + property read per
    // poll, no allocation.
    private float SettingsOpacity()
    {
        float? v;
        try { v = FrameworkOpacity(); } catch { v = null; }   // .WindowOpacity.cs
        return v is { } f ? Math.Clamp(f, OpacityMin, OpacityMax) : 1f;
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * SettingsOpacity() };

    // ── contrast cache ────────────────────────────────────────────────────────────────────────────────────────────
    // EnsureContrast walks up to 12 steps of pow() maths; the chip labels poll it every refresh with inputs that only
    // change with the theme, so each call site keeps its last answer.
    private sealed class ContrastCache
    {
        private ColorRgba _fg, _bg, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fg, ColorRgba bg, float ratio)
        {
            if (_valid && fg.Equals(_fg) && bg.Equals(_bg)) return _out;
            _fg = fg; _bg = bg; _out = EnsureContrast(fg, bg, ratio); _valid = true;
            return _out;
        }
    }

    // ── shape ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The corner radius for a ROUND (capsule / circle) bubble of side `side`: side / 2 − 1, NOT side / 2. The bubble's
    // 9-slice background reports a MIN size of its summed sprite borders (radius + 1 each side), so radius side/2 drew
    // every round control 2 px taller than its content (Maestro, measured in-game 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;

    private const float StylePillH    = 28f;
    private const float StylePillPadX = 12f;
    private const float StyleChipH    = 24f;
    private const float TileRadius    = 6f;    // panels, tiles

    // Hover that follows the shape: a Bare selectable (no square wash of its own) flips this flag and the surrounding
    // round Bubble's FillColorFn shows the hover.
    private sealed class HoverFlag { public bool On; }

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a Bare SelectableElement, 28 px tall (the 28 px
    // struts either side of a bold-13 label). The label box is FIXED (`labelW` ≥ the longest label in any locale, house
    // rule 10) so a language switch never resizes the pill. `surface` = what the pill sits on (panel or tile), for the
    // fill composition and the label contrast. `enabled` (optional): a disabled pill keeps its geometry, swallows the
    // click and is RECESSED (house rule 22: the sunken DisabledShade + a faint label, .SendCooldown.cs).
    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float labelW, Func<ColorRgba> surface, Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        var disabled = new DisabledLabelCache();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(StylePillPadX, StylePillH),
                DropBold(new TextElement(label, Color: () => En() ? text() : DisabledLabel(surface(), disabled),
                    Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                new SpacerElement(StylePillPadX, StylePillH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            // The recessed fill is drawn translucent (the renderer blends it over the real surface, so it already fades
            // with the window); the raised fills are opaque and faded here.
            FillColorFn = () => !En() ? DisabledShade : Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(StylePillH), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement NeutralButton(Func<string> label, Action onClick, float labelW, Func<ColorRgba> surface)
        => PillButton(label, NeutralFill, NeutralText, onClick, labelW, surface);

    private HudElement DangerButton(Func<string> label, Action onClick, float labelW, Func<ColorRgba> surface,
        Func<bool>? enabled = null)
        => PillButton(label, () => DangerFill(surface()), () => SolidLabel(DangerFill(surface())), onClick, labelW,
            surface, enabled);

    // Two pills side by side, equal widths (both cells fixed, so they split the row evenly).
    private static HudElement PillPair(HudElement left, HudElement right, float cellW) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Width: cellW),
        new CellElement(right, Width: cellW),
    }, Gap: 6f);

    // Bold (TMP) labels: centre the CAP-HEIGHT band — a centred bold-13 line sits ~1.5–2 px high (Maestro, measured on
    // "Rename"), so the label sits in a Column under a 2 px spacer, which moves the text down by 1 px.
    private const float BoldLabelDrop = 1f;

    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * BoldLabelDrop),
        label,
    }, Gap: 0.01f);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small bold UPPERCASE caption + right-aligned muted hint, then the
    // children. Directly in a Column it FILLS the width (a Bubble only hugs inside a Row). No trailing-control overload
    // on purpose (a second params overload silently swallows the first child — KB Stellar-Design-Language §4).
    private HudElement SectionPanel(Func<string> caption, Func<string>? hint, params HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        items[0] = new RowElement(new HudElement[]
        {
            new TextElement(UpperCached(caption), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new SpacerElement(0f),   // flexible: pushes the hint to the right edge
            new TextElement(hint ?? (() => string.Empty), Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // The UPPERCASE form of a localized string, re-made only when the source changes (a language switch), not per poll.
    private static Func<string> UpperCached(Func<string> src)
    {
        string? last = null, upper = string.Empty;
        return () =>
        {
            var s = src();
            if (!string.Equals(s, last, StringComparison.Ordinal)) { last = s; upper = s.ToUpperInvariant(); }
            return upper;
        };
    }

    // ── chip ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // A clickable 24 px chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. The caller swaps a complete "on" and "off" chip with a ConditionalElement (bold can't toggle live),
    // both in the same width, so the swap never moves the row. Pass the SAME HoverFlag to both states.
    private HudElement ChipButton(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, StyleChipH),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(StyleChipH), PaddingX = 0f, PaddingY = 0f,
        }, Width: width);

    // A coloured on/off pair: ON = bold `color` on a 22% wash of it, OFF = muted on a faint muted wash; same width,
    // swapped whole by `isOn`. Used by the settings toggles (Success colour, ON / OFF labels) and the All-tab channel
    // chips (channel colour, the channel name in both states).
    private HudElement ColourChip(Func<string> onLabel, Func<string> offLabel, Func<ColorRgba> color, Func<bool> isOn,
        Action onClick, float width)
    {
        var hover = new HoverFlag();
        var onText = new ContrastCache();
        var offText = new ContrastCache();
        ColorRgba OnFill()  => Over(color() with { A = ChipWashAlpha }, PanelColor());
        ColorRgba OffFill() => Over(MutedColor() with { A = ChipOffAlpha }, PanelColor());
        return new ConditionalElement(isOn,
            ChipButton(DropBold(new TextElement(onLabel, Color: () => onText.Get(color(), OnFill(), PillMinContrast),
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true }),
                OnFill, hover, onClick, width),
            ChipButton(new TextElement(offLabel, Color: () => offText.Get(MutedColor(), OffFill(), SolidMinContrast),
                    Align: TextAlign.Center, NoWrap: true),
                OffFill, hover, onClick, width));
    }
}
