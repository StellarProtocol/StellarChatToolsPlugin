using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Button-anchored popups: the send-target chip's channel menu (.SendTarget.cs), the 文A language menu
/// (.TranslateOut.Menu.cs) and the "+ New chat" friend picker (.FriendPicker.cs) open NEXT TO the control that opened
/// them — left edge on the control's left edge, a 4-unit gap, on the preferred side (above the composer controls,
/// below "+ New chat"), flipped to the other side when it doesn't fit, clamped on-screen. Right-click menus (sticker
/// favourites, message, conversation) stay at the cursor, which is right for a right-click.
/// The trigger's rect comes from <c>SelectableElement.OnClickWithRect</c> (fw 2.26.0) in SCREEN px; popups are placed
/// in CANVAS units (IWindowControl.SetRect), so it is converted once on open. The popups fit their content, so the
/// open-time size is only an estimate: for the first few SHOWN frames the popup is re-placed from its LIVE size, so
/// the gap holds exactly (e.g. the 文A menu's 0–3 recents) and an above-the-button popup's bottom edge never floats.
/// </summary>
public sealed partial class Plugin
{
    private const float PopupAnchorGap = 4f;      // canvas units between the trigger and the popup
    // Shown frames to re-place from the live size: the mount, the first layout pass and a spare (bounded — an endless
    // re-place could fight the framework's own on-screen clamp).
    private const int   PopupSettleFrames = 4;

    // One anchored popup's placement state, owned by its menu.
    private sealed class AnchoredPopup
    {
        public readonly float EstW, EstH;   // size estimate until the live size is measured
        public readonly bool PreferAbove;
        public WindowRect Trigger;          // the trigger's rect, CANVAS units, top-left origin
        public int SettleFrames;
        public AnchoredPopup(float estW, float estH, bool preferAbove) { EstW = estW; EstH = estH; PreferAbove = preferAbove; }
    }

    // Open-time placement: record the trigger, arm the settle frames, send the estimate-sized rect. Callers SetRect
    // BEFORE SetVisible(true) — a click-away popup applies a pre-mount SetRect when it is created (fw dcef900).
    private void PlaceAnchoredPopup(IWindowControl win, AnchoredPopup p, WindowRect triggerScreenPx)
    {
        // Screen px → canvas units (k = 1 before the first measure), same factor as the cursor popups.
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        p.Trigger = new WindowRect(triggerScreenPx.X * k, triggerScreenPx.Y * k,
            triggerScreenPx.Width * k, triggerScreenPx.Height * k);
        p.SettleFrames = PopupSettleFrames;
        win.SetRect(AnchoredRect(p, p.EstW, p.EstH));
    }

    // Framework.Update while the popup is open: re-place it from its live size for the first few shown frames (a
    // re-open of a parked popup is shown at once, a first open only after its mount lands — both count from then).
    private void SettleAnchoredPopup(IWindowControl win, AnchoredPopup p)
    {
        if (p.SettleFrames <= 0 || !win.IsShown) return;
        p.SettleFrames--;
        var cur = win.Rect;
        var want = AnchoredRect(p, cur.Width > 1f ? cur.Width : p.EstW, cur.Height > 1f ? cur.Height : p.EstH);
        if (Mathf.Abs(cur.X - want.X) > 0.5f || Mathf.Abs(cur.Y - want.Y) > 0.5f) win.SetRect(want);
    }

    // The w×h popup's rect beside the trigger: the preferred side if it fits, else the other side if THAT fits, else
    // whichever side has more room (then clamped, so it overlaps the trigger rather than leave the screen).
    private WindowRect AnchoredRect(AnchoredPopup p, float w, float h)
    {
        var fw = _services.Framework;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        var t = p.Trigger;
        float aboveY = t.Y - PopupAnchorGap - h, belowY = t.Y + t.Height + PopupAnchorGap;
        bool fitsAbove = aboveY >= 0f, fitsBelow = belowY + h <= ch;
        bool above = fitsAbove && fitsBelow ? p.PreferAbove
                   : fitsAbove || fitsBelow ? fitsAbove
                   : t.Y > ch - (t.Y + t.Height);
        float y = Mathf.Clamp(above ? aboveY : belowY, 0f, Mathf.Max(0f, ch - h));
        float x = Mathf.Clamp(t.X, 0f, Mathf.Max(0f, cw - w));
        return new WindowRect(x, y, w, h);
    }
}
