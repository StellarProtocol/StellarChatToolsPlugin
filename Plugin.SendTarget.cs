using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// The composer's send-target CHIP and its picker, plus the shared composer "pill" control. The chip wears the target
/// channel's colours exactly like the read-tabs (.Tabs.cs): the channel tint as the fill and the contrast-floored full
/// colour as bold text, labelled with the tab's own name (so "Current" here = the Current tab). DropdownElement can't
/// be styled (fixed grey trigger), so the chip is a SelectableElement that opens a cursor-positioned popup menu — the
/// sticker right-click menu pattern (.Favorites.cs: Borderless + DismissOnOutsideClick, rect re-asserted once the
/// destroy-on-hide window remounts). Picking keeps the old dropdown semantics: <see cref="OnPickTarget"/> sets the target
/// and persists it as the default (<see cref="SaveSendTarget"/>); opening a read-tab still only changes it in memory.
/// </summary>
public sealed partial class Plugin
{
    // Every composer control (chip, ＋, ☺, Send) is exactly this tall, = the text field's 28-unit box; ＋ and ☺ are
    // also this wide (square). A pill is Bubble(padding 0, the visible fill) → Selectable(4 + 4 v-padding, the click +
    // hover wash) → content row whose height comes from two 20-tall struts, so the pill height doesn't depend on the
    // font's line box: 4 + 20 + 4 = 28 — as long as every label's line box is ≤ 20. ⚠️ A PLAIN (non-Emphasis) Text
    // holding a glyph the menu font lacks breaks that: the old "▾" fell back to a taller OS face and made the chip 31
    // tall, so the chevron is now an image. The bubble sits OUTSIDE the Selectable (it used to be inside its padding),
    // so the visible fill is the full 28-tall control instead of a 20-tall chip inset in an invisible 28 box.
    private const float ComposerControlSize = 28f;
    private const float PillStrutHeight = 20f;   // = ComposerControlSize − the Selectable's 4 + 4 padding
    private const float PillPadX        = 6f;    // strut width; + the Selectable's own 6 = 12 each side of a label
    private const float PillRadius      = 8f;    // same rounding as the tab pills
    private const float PillGap         = 2f;
    // Icon pills (＋ / ☺) pack their glyph with no gap: 28 − 12 Selectable padding leaves 16 for the glyph itself.
    // RowElement treats Gap 0 as "default gap" (8), so a near-zero value is the only way to ask for none.
    private const float IconPillGap     = 0.01f;
    private const int   ChevronW = 8, ChevronH = 5;
    private const string ChevronLightResource = "Stellar.ChatTools.Assets.chevron_light.png";
    private const string ChevronDarkResource  = "Stellar.ChatTools.Assets.chevron_dark.png";
    private byte[]? _chevronLight, _chevronDark;
    private bool _chevronTried;

    private const string TargetMenuId = "chattools.targetmenu";
    private const float  TargetMenuW  = 150f;    // canvas units
    // 5 pills × 28 + 4 column gaps × 2 + panel padding 2 × 6 — only used to open the menu ABOVE the cursor (the
    // composer sits at the bottom of the window) and for the edge flip, like FavMenuH.
    private const float  TargetMenuH  = 160f;

    // Composer target index (0=Say … 4=Whisper, see _sendTargetIndex) → the read-tab whose colours + label it wears.
    private static readonly ChatTab[] TargetTabs =
        { ChatTab.Current, ChatTab.World, ChatTab.Party, ChatTab.Guild, ChatTab.Whisper };

    private IWindowControl _targetMenuWindow = null!;
    private bool _targetMenuOpen, _targetMenuPlaced;
    private float _targetMenuClosedAt = -1f;      // IFramework.TimeNow of the last close (chip re-click debounce)
    // A click on the chip while the menu is open first DISMISSES it (outside-click fires on the press), then the release
    // lands on the chip as a click — which would reopen it at once. Ignore a chip click this soon after a close.
    private const float TargetMenuReopenGuardSec = 0.3f;
    private WindowRect _targetMenuRect;

    // Placeholder cache ("Message Guild…"): the input polls it every refresh, so the formatted string is rebuilt only
    // when the template or that target's label changes (language switch) — never allocated per poll.
    private string? _phTemplate;
    private readonly string?[] _phLabel = new string?[5];
    private readonly string?[] _phText  = new string?[5];

    private ChatTab TargetTab(int index) => TargetTabs[index >= 0 && index < TargetTabs.Length ? index : 0];
    private ChatTab TargetTab() => TargetTab(_sendTargetIndex);

    private string TargetLabel(int index) => _loc.T(TabLabelKey(TargetTab(index)));

    private string ComposerPlaceholder()
    {
        if (_sendTargetIndex == 4) return WhisperPlaceholder();
        int i = _sendTargetIndex >= 0 && _sendTargetIndex < TargetTabs.Length ? _sendTargetIndex : 0;
        var tpl = _loc.T("chat.composer.placeholder");
        var label = TargetLabel(i);
        if (!string.Equals(tpl, _phTemplate, StringComparison.Ordinal))
        {
            _phTemplate = tpl;
            Array.Clear(_phText, 0, _phText.Length);
        }
        if (_phText[i] == null || !string.Equals(label, _phLabel[i], StringComparison.Ordinal))
        {
            _phLabel[i] = label;
            _phText[i] = SafeFormat(tpl, label);
        }
        return _phText[i]!;
    }

    // "Whisper Alice…" for the open conversation, or the prompt to pick one (Send is disabled then — HasSendTarget).
    private string WhisperPlaceholder()
    {
        if (_selectedPeer == 0) return _loc.T("chat.whisper.pickPlaceholder");
        var tpl = _loc.T("chat.whisper.placeholder");
        var name = PeerName(_selectedPeer);
        if (_phPeer != _selectedPeer || !string.Equals(name, _phPeerName, StringComparison.Ordinal)
            || !string.Equals(tpl, _phWhisperTpl, StringComparison.Ordinal))
        {
            _phPeer = _selectedPeer; _phPeerName = name; _phWhisperTpl = tpl;
            _phWhisperText = SafeFormat(tpl, name);
        }
        return _phWhisperText;
    }

    // A malformed translated template must never throw out of a per-refresh Func; fall back to the bare template.
    private static string SafeFormat(string tpl, object arg)
    {
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl, arg); }
        catch (FormatException) { return tpl; }
    }

    // The shared composer pill: CellElement (natural width, or a fixed `width` for the square icon pills — its
    // LayoutElement's flexibleWidth 0 out-ranks the children's flexibleWidth 1, so the pill hugs instead of soaking up
    // the row's slack) → ONE Bubble (the visible fill; Funcs re-tint it, same single-structure rule as the tabs: state
    // changes colours only, never the tree) → Selectable (click + hover wash, the full pill) → the content row. Inside
    // the min-width composer row the bubble hugs its natural width; in a fixed-width cell it is stretched to the cell.
    // Text pills get fixed-width struts (horizontal padding); icon pills get flexible ones (Width 0 = flexible), which
    // centre the glyph in the fixed square. `outline` (optional) = a live border ring colour (null = none) — the translate
    // pill's busy state (.Translate.cs); it is an overlay, so it never changes the pill's size.
    private HudElement BuildPill(HudElement[] content, Func<ColorRgba?> fill, Action onClick, float width = 0f,
        Func<ColorRgba?>? outline = null)
    {
        bool icon = width > 0f;
        var row = new HudElement[content.Length + 2];
        row[0] = new SpacerElement(Width: icon ? 0f : PillPadX, Height: PillStrutHeight);
        Array.Copy(content, 0, row, 1, content.Length);
        row[row.Length - 1] = new SpacerElement(Width: icon ? 0f : PillPadX, Height: PillStrutHeight);
        return new CellElement(new BubbleElement(new SelectableElement(
                new RowElement(row, Gap: icon ? IconPillGap : PillGap, Justify: RowJustify.Center),
                OnClick: onClick))
            {
                // Transparent static fill (NOT null — null = the themed accent default); FillColorFn re-tints live.
                CornerRadius = PillRadius, PaddingX = 0f, PaddingY = 0f, FillColor = Transparent, FillColorFn = fill,
                OutlineColorFn = outline,
            },
            Width: width);
    }

    private HudElement BuildPill(Func<string> label, Func<ColorRgba?> text, Func<ColorRgba?> fill, Action onClick)
        => BuildPill(new HudElement[] { new TextElement(label, text, Emphasis: true, NoWrap: true) }, fill, onClick);

    // A square ComposerControlSize icon pill (＋ / ☺ / the translate 文A) around one centred glyph element.
    private HudElement BuildIconPill(HudElement glyph, Func<ColorRgba?> fill, Action onClick, Func<ColorRgba?>? outline = null)
        => BuildPill(new HudElement[] { glyph }, fill, onClick, ComposerControlSize, outline);

    // [Guild ▾] in the target channel's tab colours (active-tab look); while whispering, the RECIPIENT chip [→ Name ▾] in
    // the whisper colours (TargetTab() is Whisper then). Click → the picker menu (picking a channel leaves whisper —
    // .Composer.cs OnPickTarget). With no conversation picked it falls back to the plain "Whisper" label.
    private string ChipLabel()
    {
        if (_sendTargetIndex != 4 || _selectedPeer == 0) return TargetLabel(_sendTargetIndex);
        var name = PeerName(_selectedPeer);   // .Whisper.cs
        if (_chipPeer != _selectedPeer || !string.Equals(name, _chipName, StringComparison.Ordinal))
        {
            _chipPeer = _selectedPeer; _chipName = name;
            _chipText = name;   // no "→": the menu font has no U+2192 glyph (draws a blank gap); the whisper colour marks the chip
        }
        return _chipText;
    }

    private long _chipPeer;
    private string? _chipName;
    private string _chipText = "";
    // Whisper placeholder cache ("Whisper Alice…"), same rebuild-on-change rule as _phText.
    private long _phPeer;
    private string? _phPeerName, _phWhisperTpl;
    private string _phWhisperText = "";

    // The chip itself.
    private HudElement BuildTargetChip() => BuildPill(new HudElement[]
        {
            new TextElement(ChipLabel, () => TabActiveText(TargetTab()), Emphasis: true, NoWrap: true),
            BuildChevron(),
        },
        () => TabPillFill(TargetTab()), ToggleTargetMenu);

    // The ▾ as an 8×5 image (see ComposerControlSize for why not a glyph). ImageElement can't be tinted, so a light and a
    // dark copy are swapped by the label's own luminance (dark only on the Light preset, where labels are darkened). Both
    // branches are the same size and the fixed-width cell pins the slot, so the swap never changes the chip's width — it
    // sits inside the bubble's measured child (WindowBuilder-Patterns.md: keep variant content constant).
    private HudElement BuildChevron() => new CellElement(new ConditionalElement(
            () => Luminance(TabActiveText(TargetTab())) > 0.35f,
            new RowElement(new HudElement[] { new ImageElement(() => ChevronPng(light: true), ChevronW, ChevronH) }),
            new RowElement(new HudElement[] { new ImageElement(() => ChevronPng(light: false), ChevronW, ChevronH) })),
        Width: ChevronW);

    private byte[]? ChevronPng(bool light)
    {
        if (!_chevronTried)
        {
            _chevronTried = true;
            _chevronLight = ReadResource(ChevronLightResource);
            _chevronDark  = ReadResource(ChevronDarkResource);
        }
        return light ? _chevronLight : _chevronDark;
    }

    private byte[]? ReadResource(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] resource {name} load failed: {ex.Message}"); return null; }
    }

    // ── Picker menu ───────────────────────────────────────────────────────────────────────────────────────────────

    private IWindowControl RegisterTargetMenuWindow()
    {
        // One pill per target, styled like the tab strip: the current target = active look (tint + full colour), the
        // rest = muted colour on no fill. The column force-expands each cell, so all five pills share the menu width.
        var items = new HudElement[TargetTabs.Length];
        for (int i = 0; i < TargetTabs.Length; i++)
        {
            var idx = i;
            var tab = TargetTabs[idx];
            items[idx] = BuildPill(() => TargetLabel(idx),
                () => _sendTargetIndex == idx ? TabActiveText(tab) : TabMutedText(tab),
                () => _sendTargetIndex == idx ? TabPillFill(tab) : Transparent,
                () => { CloseTargetMenu(); OnPickTarget(idx); });
        }
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          TargetMenuId,
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, TargetMenuW, TargetMenuH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(items, Gap: 2f), Padding: 6f),
            OnClose: CloseTargetMenu));   // framework invokes this on Escape / click-outside (per-frame ticker)
    }

    private void ToggleTargetMenu()
    {
        if (_targetMenuOpen) { CloseTargetMenu(); return; }
        if (_targetMenuClosedAt >= 0f && _services.Framework.TimeNow - _targetMenuClosedAt < TargetMenuReopenGuardSec) return;
        // Cursor → canvas units (Input.mousePosition is bottom-left SCREEN px; WindowRect is top-left CANVAS units —
        // same conversion as OpenFavMenu). Opens ABOVE the cursor (the composer is the window's bottom edge, like the
        // game's own channel picker), flipping below if that would leave the top of the canvas; clamped horizontally.
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        var mp = Input.mousePosition;
        float x = mp.x * k - 12f, y = (Screen.height - mp.y) * k - TargetMenuH - 6f;
        if (y < 0f) y = Mathf.Min((Screen.height - mp.y) * k + 6f, Mathf.Max(0f, ch - TargetMenuH));
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, cw - TargetMenuW));
        _targetMenuRect = new WindowRect(x, y, TargetMenuW, TargetMenuH);
        _targetMenuPlaced = false;
        _targetMenuOpen = true;
        _targetMenuWindow.SetRect(_targetMenuRect);   // no-op while still unmounted; re-asserted in TickTargetMenu
        _targetMenuWindow.SetVisible(true);           // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    // Framework.Update: re-assert the rect once the remount lands (see TickFavMenuPlace), and close the menu if the
    // chat window itself went away (hotkey toggle / zone load) — the popup is its own window and would otherwise stay.
    // The chat window is already shown whenever the menu opens (its chip was clicked), so !IsShown here really means
    // it closed (not the one-Update first-open lag, WindowBuilder-Patterns.md "IsShown is FALSE…").
    private void TickTargetMenu(float _)
    {
        if (!_targetMenuOpen) return;
        if (!_window.IsShown) { CloseTargetMenu(); return; }
        if (!_targetMenuPlaced && _targetMenuWindow.IsShown) { _targetMenuWindow.SetRect(_targetMenuRect); _targetMenuPlaced = true; }
    }

    private void CloseTargetMenu()
    {
        if (!_targetMenuOpen) return;
        _targetMenuOpen = false;
        _targetMenuPlaced = false;
        _targetMenuClosedAt = _services.Framework.TimeNow;
        _targetMenuWindow?.SetVisible(false);
    }
}
