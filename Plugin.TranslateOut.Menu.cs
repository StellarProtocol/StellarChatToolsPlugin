using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// The outgoing-translate language menu behind the composer 文A pill (.TranslateOut.cs). Same cursor-popup pattern as
/// the send-target menu (.SendTarget.cs: Borderless + DismissOnOutsideClick, opened ABOVE the cursor because the
/// composer is the window's bottom edge, rect re-asserted once the destroy-on-hide window remounts, a pill re-click
/// within 0.3 s of a close ignored). Content: the title "Translate my message to", up to 3 RECENT outgoing languages
/// (persisted, most recent first; none yet = the incoming translate target), the divider, then the full Settings list
/// (.Translate.Langs.cs — same order and "native (localized)" labels) in a scrollable virtual list.
/// </summary>
public sealed partial class Plugin
{
    private const string CfgTrOutRecent = "translate.outRecent";   // string[] of Google codes, most recent first
    private const int    TrOutRecentMax = 3;

    private const string TrOutMenuId      = "chattools.trmenu";
    private const float  TrOutMenuW       = 250f;
    private const float  TrOutRowHeight   = 28f;    // uniform VirtualList pitch = one Selectable text row + gap
    private const float  TrOutListHeight  = 224f;   // 8 rows visible, the rest scroll
    private const int    TrOutPoolSize    = 11;     // 224 ÷ 28 + buffer
    // Title + 3 recents + divider + the list + panel padding / gaps — only used to open the menu above the cursor and
    // for the edge flip (the window itself fits its content).
    private const float  TrOutMenuH       = 26f + TrOutRecentMax * TrOutRowHeight + 22f + TrOutListHeight + 40f;

    private readonly List<string> _trOutRecent = new();
    private IWindowControl _trOutMenuWindow = null!;
    private bool _trOutMenuOpen, _trOutMenuPlaced;
    private float _trOutMenuClosedAt = -1f;
    private WindowRect _trOutMenuRect;
    private int _trOutListFirst;

    private void LoadTranslateOutConfig()
    {
        _trOutRecent.Clear();
        var saved = _cfg.Get<string[]>(CfgTrOutRecent, null!);
        if (saved == null) return;
        foreach (var c in saved)
            if (c != null && TrLangIndex.ContainsKey(c) && !_trOutRecent.Contains(c) && _trOutRecent.Count < TrOutRecentMax)
                _trOutRecent.Add(c);
    }

    // Most recent first, deduplicated, at most 3; saved on each pick.
    private void NoteRecentOutLang(string code)
    {
        if (_trOutRecent.Count > 0 && _trOutRecent[0] == code) return;
        _trOutRecent.Remove(code);
        _trOutRecent.Insert(0, code);
        if (_trOutRecent.Count > TrOutRecentMax) _trOutRecent.RemoveRange(TrOutRecentMax, _trOutRecent.Count - TrOutRecentMax);
        _cfg.Set(CfgTrOutRecent, _trOutRecent.ToArray());
        _cfg.Save();
    }

    // Recent slot k → code. With no history the single slot is the incoming translate target (.Translate.cs).
    private string? TrOutRecentCode(int k)
    {
        if (_trOutRecent.Count == 0) return k == 0 ? TranslateTarget() : null;
        return k < _trOutRecent.Count ? _trOutRecent[k] : null;
    }

    // The Settings dropdown's label for a code (cached per UI language there), so both menus read the same.
    private string TrOutCodeLabel(string? code)
    {
        if (code == null) return "";
        int i = Array.IndexOf(TrOptionCodes(), code);
        var labels = TrTargetOptions();
        return i >= 0 && i < labels.Count ? labels[i] : TrLangName(code);
    }

    private string? TrOutListCode(int slot)
    {
        var codes = TrOptionCodes();
        int i = _trOutListFirst + slot;
        return i >= 0 && i < codes.Length ? codes[i] : null;
    }

    private string TrOutListLabel(int slot)
    {
        int i = _trOutListFirst + slot;
        var labels = TrTargetOptions();
        return i >= 0 && i < labels.Count ? labels[i] : "";
    }

    private IWindowControl RegisterTrOutMenuWindow()
    {
        var recents = new HudElement[TrOutRecentMax];
        for (int n = 0; n < TrOutRecentMax; n++)
        {
            var k = n;
            recents[k] = new ConditionalElement(() => TrOutRecentCode(k) != null,
                new SelectableElement(new TextElement(() => TrOutCodeLabel(TrOutRecentCode(k)), NoWrap: true),
                    () => PickTrOutLang(TrOutRecentCode(k))));
        }
        var pool = new HudElement[TrOutPoolSize];
        for (int n = 0; n < TrOutPoolSize; n++)
        {
            var k = n;
            // The divider row (null code) of the shared list is muted and inert, as in the Settings dropdown.
            pool[k] = new SelectableElement(new TextElement(() => TrOutListLabel(k),
                    () => TrOutListCode(k) == null ? _services.Theme.Colors.MenuMuted : null, NoWrap: true),
                () => PickTrOutLang(TrOutListCode(k)));
        }
        var body = new List<HudElement> { new TextElement(() => _loc.T("chat.translate.outMenuTitle"), Emphasis: true) };
        body.AddRange(recents);
        body.Add(new TextElement(() => TrDividerLabel, MutedCol, Align: TextAlign.Center, NoWrap: true));
        body.Add(new VirtualListElement(
            Count:     () => TrOptionCodes().Length,
            RowHeight: TrOutRowHeight,
            Pool:      pool,
            OnWindow:  first => _trOutListFirst = first,
            Height:    TrOutListHeight));
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          TrOutMenuId,
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, TrOutMenuW, TrOutMenuH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(body.ToArray(), Gap: 2f), Padding: 8f),
            OnClose: CloseTrOutMenu));   // framework invokes this on Escape / click-outside
    }

    private void PickTrOutLang(string? code)
    {
        if (code == null) return;   // the divider
        CloseTrOutMenu();
        StartOutgoingTranslation(code);   // .TranslateOut.cs
    }

    private void ToggleTrOutMenu()
    {
        if (_trOutMenuOpen) { CloseTrOutMenu(); return; }
        if (_trOutMenuClosedAt >= 0f && _services.Framework.TimeNow - _trOutMenuClosedAt < TargetMenuReopenGuardSec) return;
        // Cursor → canvas units, opened ABOVE the cursor and flipped below at the top edge — as ToggleTargetMenu.
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        var mp = Input.mousePosition;
        float x = mp.x * k - 12f, y = (Screen.height - mp.y) * k - TrOutMenuH - 6f;
        if (y < 0f) y = Mathf.Min((Screen.height - mp.y) * k + 6f, Mathf.Max(0f, ch - TrOutMenuH));
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, cw - TrOutMenuW));
        _trOutMenuRect = new WindowRect(x, y, TrOutMenuW, TrOutMenuH);
        _trOutMenuPlaced = false;
        _trOutMenuOpen = true;
        _trOutMenuWindow.SetRect(_trOutMenuRect);   // no-op while still unmounted; re-asserted in TickTrOutMenu
        _trOutMenuWindow.SetVisible(true);          // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    // Framework.Update: re-assert the rect once the remount lands; close with the chat window (see TickTargetMenu).
    private void TickTrOutMenu(float _)
    {
        if (!_trOutMenuOpen) return;
        if (!_window.IsShown) { CloseTrOutMenu(); return; }
        if (!_trOutMenuPlaced && _trOutMenuWindow.IsShown) { _trOutMenuWindow.SetRect(_trOutMenuRect); _trOutMenuPlaced = true; }
    }

    private void CloseTrOutMenu()
    {
        if (!_trOutMenuOpen) return;
        _trOutMenuOpen = false;
        _trOutMenuPlaced = false;
        _trOutMenuClosedAt = _services.Framework.TimeNow;
        _trOutMenuWindow?.SetVisible(false);
    }
}
