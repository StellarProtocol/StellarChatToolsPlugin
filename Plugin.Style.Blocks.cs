using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

// More house-kit building blocks (Stellar Design Language §4; reference Maestro / StatInspector Plugin.Style.Blocks.cs):
// embedded art, the accent icon tile, the ToolHeader with its round ✕, and a fixed-height text line.
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.ChatTools.Resources.<file>") and kept:
    // ImageElement Funcs are polled. A missing resource caches null (the element then draws nothing) instead of
    // re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.ChatTools.Resources." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // ── icon tile ─────────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with a flat white glyph centred on it (art can't follow the theme, so the
    // colour lives in the tile). The glyph is PRE-DRAWN at the exact size it is shown (Resources\chattools-settings-24.png,
    // hinted to whole pixels by tools\make-settings-icon.py) — never the 128 px launcher icon scaled down, whose thin
    // strokes turn soft grey at 24. 40 px tile → the 24 px glyph with 8 px all round. Radius ≈ 22 % of the side (9 at 40).
    // The fixed-width Cell + centred Row stop the image being stretched (the icon-squish gotcha).
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;
    private const string SettingsGlyphPng = "chattools-settings-24.png";

    private HudElement IconBox(string glyphPng, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(() => CachedPng(glyphPng), glyph, glyph) },
                Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: 40 px icon + bold title over a live muted status line, then the round ✕ at the far right.
    private HudElement ToolHeader(string glyphPng, Func<string> title, Func<string> status, Action onClose) => new RowElement(
        new HudElement[]
        {
            IconBox(glyphPng, ToolIconSize, ToolGlyphSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                new TextElement(status, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
            CloseIcon(onClose, IconButtonSize),
        }, Gap: 10f);

    // ── round icon-only ✕ ─────────────────────────────────────────────────────────────────────────────────────────
    // House "remove hover": a muted glyph at rest; on hover a red glyph on a soft round red wash. Exactly `side` square
    // (one Row[strut of the side, a side-wide glyph box], house rule 4). Red = the theme's Danger colour: the wash is
    // Danger @ 0.22, the glyph Danger lifted 35 % toward white (#ff4d4f → #ff8a8b on the dark presets).
    private const float IconButtonSize = 28f;
    private const float RemoveWashAlpha = 0.22f;
    private const float RemoveGlyphLift = 0.35f;

    private HudElement CloseIcon(Action onClick, float side)
    {
        var hover = new HoverFlag();
        var glyph = new ContrastCache();
        ColorRgba Wash() => Over(Menu.Danger with { A = RemoveWashAlpha }, Menu.MenuBackground);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, side),
                new TextElement(() => "✕", Width: side, Align: TextAlign.Center, NoWrap: true,
                    Color: () => hover.On
                        ? glyph.Get(Mix(Menu.Danger, new ColorRgba(1f, 1f, 1f), RemoveGlyphLift), Wash(), SolidMinContrast)
                        : MutedColor()),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            // Transparent at rest — explicit, NOT null (null = the themed accent default, WindowBuilder-Patterns.md).
            FillColorFn = () => hover.On ? Fade(Wash()) : Transparent,
            CornerRadius = RoundRadius(side), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── fixed-height line ─────────────────────────────────────────────────────────────────────────────────────────
    // One line of text in a box of exactly `height` (TextElement.Height, fw 2.30.0): bold/empty/longer text can't
    // change the panel height; NoWrap + Ellipsize keeps it on one line.
    private const float LineH = 20f;

    private static TextElement FixedLine(Func<string> text, Func<ColorRgba?> color)
        => new(text, color, NoWrap: true) { Ellipsize = true, Height = LineH };
}
