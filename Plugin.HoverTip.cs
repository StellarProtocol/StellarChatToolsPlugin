using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// A small HOVER tooltip (the approved share-links prototype's "Click: track on map" / "Share my position" / "Can't share
/// your position on this map" tips). The toolkit has no tooltip (Stellar-Design-Language R12), so this is one shared
/// borderless window: an element's <c>OnHover</c> reports enter / leave with a key and a text Func; after a short delay the
/// tip opens next to the CURSOR (the hovered element's rect isn't reported on hover — only on click), flipped at the
/// screen edges, and closes on leave. <c>Passive</c>: no click blocker and no pointer-over-UI — it never steals the hover
/// from the element under it, and it sits beside the cursor anyway. It opens from hidden, so it remounts on top of the
/// chat (Session Rule 5: no BringToFront). Ticked from Framework.Update; idle (no work) while nothing is hovered.
///
/// ⚠️ A Borderless window is always its DefaultRect WIDTH (the framework pins the root's preferredWidth to it; only the
/// height follows the content), so the window is a fixed HoverTipW strip and the visible tip is a bubble HUGGING one end
/// of it — the left end normally, the right end when flipped left at the screen edge, so it still ends beside the cursor.
/// </summary>
public sealed partial class Plugin
{
    private const string HoverTipId = "chattools.hovertip";
    private const float  HoverTipDelaySec = 0.35f;
    private const float  HoverTipW = 380f, HoverTipEstH = 26f;      // fixed window width (see the class note); height until measured
    private const float  HoverTipOffX = 14f, HoverTipOffY = 20f;    // canvas units right of / below the cursor tip
    private const int    HoverTipFontSize = 12;

    private IWindowControl _hoverTipWindow = null!;
    private int _tipKey;                 // 0 = nothing hovered
    private Func<string>? _tipFn;
    private string _tipText = "";
    private float _tipSince;
    private bool _tipShown;
    private int _tipSettle;
    private float _tipCursorX, _tipCursorY;   // CANVAS units, top-left origin, captured when the tip opens

    private IWindowControl RegisterHoverTipWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          HoverTipId,
            Title:       "",
            DefaultRect: new WindowRect(100f, 100f, HoverTipW, HoverTipEstH),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.Borderless)
        { StartVisible = false, Passive = true,
          ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                            && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
        new ConditionalElement(() => _tipFlipLeft,
            new RowElement(new HudElement[] { new SpacerElement(), BuildHoverTipBubble() }, Gap: 0.01f),
            new RowElement(new HudElement[] { BuildHoverTipBubble(), new SpacerElement() }, Gap: 0.01f))));

    // RichEmoji = the TMP path (honours FontSize); the text is ours or already sanitised (a game reason string). Wraps past
    // the window width (a long translated reason) instead of spilling out of it.
    private HudElement BuildHoverTipBubble() => new BubbleElement(
        new TextElement(() => _tipText, () => TextColor(), FontSize: HoverTipFontSize) { RichEmoji = true })
    {
        FillColorFn = () => TabTrackColor(), OutlineColorFn = () => MutedColor() with { A = 0.45f },
        CornerRadius = 6f, PaddingX = 8f, PaddingY = 4f, MaxWidth = HoverTipW,
    };

    private bool _tipFlipLeft;   // the tip hugs the window's right end (opened left of the cursor)

    // From an element's OnHover. A leave only clears the tip it owns (a fast move can report the next enter first).
    private void HoverTip(int key, bool on, Func<string> text)
    {
        if (on)
        {
            if (_tipKey == key) return;
            _tipKey = key; _tipFn = text; _tipSince = _services.Framework.TimeNow;
            if (_tipShown) HideHoverTipWindow();   // a new target waits its own delay
            return;
        }
        if (_tipKey == key) HideHoverTip();
    }

    private void HideHoverTip()
    {
        _tipKey = 0; _tipFn = null;
        HideHoverTipWindow();
    }

    private void HideHoverTipWindow()
    {
        if (!_tipShown) return;
        _tipShown = false;
        _hoverTipWindow?.SetVisible(false);
    }

    private void TickHoverTip(float _)
    {
        if (_tipKey == 0) return;
        if (!_window.IsShown) { HideHoverTip(); return; }
        var fn = _tipFn;
        if (fn == null) return;
        if (!_tipShown)
        {
            if (_services.Framework.TimeNow - _tipSince < HoverTipDelaySec) return;
            _tipText = SafeTip(fn());
            CaptureTipCursor();
            _tipSettle = PopupSettleFrames;   // .PopupAnchor.cs — re-place from the live size once it has mounted
            _hoverTipWindow.SetRect(HoverTipRect(HoverTipEstH));   // before SetVisible: applied at mount
            _hoverTipWindow.SetVisible(true);
            _tipShown = true;
            return;
        }
        var t = fn();   // e.g. the pin button's tip switches when the map stops allowing shares
        if (!string.Equals(t, _tipText, StringComparison.Ordinal)) _tipText = SafeTip(t);
        if (_tipSettle > 0 && _hoverTipWindow.IsShown)
        {
            _tipSettle--;
            var cur = _hoverTipWindow.Rect;
            var want = HoverTipRect(cur.Height > 1f ? cur.Height : HoverTipEstH);
            if (Mathf.Abs(cur.X - want.X) > 0.5f || Mathf.Abs(cur.Y - want.Y) > 0.5f) _hoverTipWindow.SetRect(want);
        }
    }

    // A game-supplied reason may carry rich tags; ours never do. Same instance when clean.
    private static string SafeTip(string? s) => ChatMarkup.SanitizeWire(s);

    // Input.mousePosition is bottom-left SCREEN px; WindowRect is top-left CANVAS units (k = 1 before the first measure).
    private void CaptureTipCursor()
    {
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        var mp = Input.mousePosition;
        _tipCursorX = mp.x * k;
        _tipCursorY = (Screen.height - mp.y) * k;
    }

    // Below-right of the cursor; flipped left / above when it would leave the canvas (then fully clear of the cursor).
    private WindowRect HoverTipRect(float h)
    {
        const float w = HoverTipW;
        var fw = _services.Framework;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        float x = _tipCursorX + HoverTipOffX, y = _tipCursorY + HoverTipOffY;
        _tipFlipLeft = x + w > cw;
        if (_tipFlipLeft) x = _tipCursorX - w - 4f;
        if (y + h > ch) y = _tipCursorY - h - 4f;
        return new WindowRect(Mathf.Clamp(x, 0f, Mathf.Max(0f, cw - w)), Mathf.Clamp(y, 0f, Mathf.Max(0f, ch - h)), w, h);
    }
}
