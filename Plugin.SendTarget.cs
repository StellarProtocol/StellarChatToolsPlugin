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
    // Every composer control (chip, ＋, ☺, Send) is this tall, = the text field's 28-unit box. A pill is
    // Selectable(4 + 4 v-padding) → Bubble(PaddingY 0) → content row whose height comes from two height-only struts
    // (SpacerElement(Height) has no width), so the pill height doesn't depend on the font's line box: 4 + 20 + 4 = 28.
    // The label is vertically centred by the row (MiddleLeft) as long as its line box is ≤ 20.
    private const float PillStrutHeight = 20f;   // = ComposerControlHeight (28) − the Selectable's 4 + 4 padding
    private const float PillPadX        = 8f;
    private const float PillRadius      = 8f;    // same rounding as the tab pills

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

    // A malformed translated template must never throw out of a per-refresh Func; fall back to the bare template.
    private static string SafeFormat(string tpl, object arg)
    {
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl, arg); }
        catch (FormatException) { return tpl; }
    }

    // The shared composer pill: CellElement (natural width — its LayoutElement's flexibleWidth 0 out-ranks the
    // Selectable's flexibleWidth 1, so the pill hugs its label instead of soaking up the row's slack) → Selectable
    // (click + hover wash) → ONE Bubble whose fill/text colours are Funcs (same single-structure rule as the tabs: state
    // changes colours only, never the tree). Inside the min-width composer row the bubble hugs its natural width.
    private HudElement BuildPill(HudElement[] content, Func<ColorRgba?> fill, Action onClick)
    {
        var row = new HudElement[content.Length + 2];
        row[0] = new SpacerElement(Height: PillStrutHeight);
        Array.Copy(content, 0, row, 1, content.Length);
        row[row.Length - 1] = new SpacerElement(Height: PillStrutHeight);
        return new CellElement(new SelectableElement(
            new BubbleElement(new RowElement(row, Gap: 2f, Justify: RowJustify.Center))
            {
                // Transparent static fill (NOT null — null = the themed accent default); FillColorFn re-tints live.
                CornerRadius = PillRadius, PaddingX = PillPadX, PaddingY = 0f, FillColor = Transparent, FillColorFn = fill,
            },
            OnClick: onClick));
    }

    private HudElement BuildPill(Func<string> label, Func<ColorRgba?> text, Func<ColorRgba?> fill, Action onClick)
        => BuildPill(new HudElement[] { new TextElement(label, text, Emphasis: true, NoWrap: true) }, fill, onClick);

    // [Guild ▾] in the target channel's tab colours (active-tab look). Click → the picker menu.
    private HudElement BuildTargetChip() => BuildPill(new HudElement[]
        {
            new TextElement(() => TargetLabel(_sendTargetIndex), () => TabActiveText(TargetTab()), Emphasis: true, NoWrap: true),
            new TextElement(() => "▾", () => TabActiveText(TargetTab()), NoWrap: true),
        },
        () => TabPillFill(TargetTab()), ToggleTargetMenu);

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
