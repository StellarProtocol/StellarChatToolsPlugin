using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// "Share my position" in the composer (approved prototype chattools-links.html §3, Option A): a 28×28 PIN button right
/// after ☺. A click ATTACHES the current position — a faint blue strip above the composer (pin + the position text in the
/// info blue + an icon-only ✕ that removes it) and an "on" outline on the button. With an attachment, Send / Enter calls
/// <see cref="IChatPositionShare.SharePosition"/> with the typed text (the text shown in front of the link, may be empty)
/// to the composer's current target — Current / World / Party / Guild / the open whisper — instead of a normal send, then
/// clears the box and the attachment. The server builds the link message; it comes back like any other message.
///
/// Where the map forbids sharing (dungeons, instances — <see cref="ChatPositionPreview.CanShare"/>) the button is RECESSED
/// (house rule: disabled = sunken, not raised) and its tip says why (the game's own reason when the framework has it).
/// Leaving for such a map, or for another map, while attached drops the attachment. The preview is a Lua read, so it is
/// polled only while the chat is open: 2 Hz for the button's state, 4 Hz while attached (for the strip text).
/// The server's verdict arrives on SendResult (IsPositionShare): a refusal restores the draft AND the attachment
/// (.SendCooldown.cs OnSendResult).
/// </summary>
public sealed partial class Plugin
{
    private const float PosPollIdleSec = 0.5f, PosPollAttachedSec = 0.25f;
    private const float PinIconSize = 14f;          // pre-drawn 14 px pin in the 28 px square
    private const float PosStripWashAlpha = 0.12f;  // the strip's faint blue
    private const int   TipKeyPinButton = 1, TipKeyStripRemove = 2;
    private static readonly ColorRgba RemoveWash  = new(0xFF / 255f, 0x4D / 255f, 0x4F / 255f, 0.22f);   // house icon-only ✕ hover
    private static readonly ColorRgba RemoveGlyph = new(0xFF / 255f, 0x8A / 255f, 0x8B / 255f);

    private ChatPositionPreview? _posPreview;
    private float _posPolledAt = -1000f;
    private bool _posAttached;
    private int _posAttachedScene;
    private bool _pinHover, _stripRemoveHover;
    private readonly DisabledLabelCache _pinDisabledTint = new();
    // The strip's position text, re-formatted only when the position / scene name / template changes.
    private int _posTextScene = int.MinValue, _posTextLine, _posTextX, _posTextY, _posTextZ;
    private string? _posTextName, _posTextTpl;
    private string _posText = "";

    private bool CanSharePosition() => _posPreview is { Available: true, CanShare: true };

    // ── Poll ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private void TickPosShare(float _)
    {
        if (!_window.IsShown) return;
        var now = _services.Framework.TimeNow;
        if (now - _posPolledAt < (_posAttached ? PosPollAttachedSec : PosPollIdleSec)) return;
        RefreshPosPreview(now);
    }

    private void RefreshPosPreview(float now)
    {
        _posPolledAt = now;
        try { _posPreview = _chat.GetSharePosition(); }
        catch (Exception ex)
        {
            _posPreview = null;
            _services.Log.Warning($"[ChatTools] share preview failed: {ex.GetType().Name}: {ex.Message}");
        }
        // The map no longer allows it, or it is a different map than the one attached: the link would be wrong — drop it.
        if (_posAttached && (!CanSharePosition() || _posPreview!.Position?.SceneId != _posAttachedScene)) DetachPosition();
    }

    private void AttachPosition()
    {
        RefreshPosPreview(_services.Framework.TimeNow);   // the freshest position, not up to 0.5 s old
        if (!CanSharePosition() || _posPreview!.Position is not { } p) return;
        _posAttached = true;
        _posAttachedScene = p.SceneId;
    }

    private void DetachPosition()
    {
        _posAttached = false;
        if (_tipKey == TipKeyStripRemove) HideHoverTip();   // the ✕ under the cursor just went away
    }

    private void OnPinClick()
    {
        if (_posAttached) DetachPosition();
        else if (CanSharePosition()) AttachPosition();
        else return;   // recessed: no-op (the tip says why)
        _composerFocusSeq++;   // back to typing the text that goes in front of the link
    }

    // "Asterleeds · Line 4 (312, 18, -945)" (localized template) for the strip.
    private string PosPreviewText()
    {
        if (_posPreview?.Position is not { } p) return _posText;
        var tpl = _loc.T("chat.share.positionFormat");
        if (p.SceneId == _posTextScene && p.LineId == _posTextLine && p.X == _posTextX && p.Y == _posTextY && p.Z == _posTextZ
            && ReferenceEquals(p.SceneName, _posTextName) && string.Equals(tpl, _posTextTpl, StringComparison.Ordinal)) return _posText;
        _posTextScene = p.SceneId; _posTextLine = p.LineId; _posTextX = p.X; _posTextY = p.Y; _posTextZ = p.Z;
        _posTextName = p.SceneName; _posTextTpl = tpl;
        try
        {
            _posText = string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl,
                ChatMarkup.SanitizeWire(p.SceneName), p.LineId, p.X, p.Y, p.Z);
        }
        catch (FormatException) { _posText = tpl; }
        return _posText;
    }

    // ── The pin button ────────────────────────────────────────────────────────────────────────────────────────────
    // The ＋ / ☺ pill shape (.SendTarget.cs PillBubble: 28×28, PillRadius), but a BARE selectable with its own hover fill:
    // the recessed state must not light a hover wash (a lit wash reads as "pressable"). Bare has no 4+4 padding, so the
    // struts are the full 28.
    private HudElement BuildPinButton() => new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(Width: 0f, Height: ComposerControlSize),
                new CellElement(new ImageElement(Pin14, (int)PinIconSize, (int)PinIconSize) { Tint = PinTint }, Width: PinIconSize),
                new SpacerElement(Width: 0f, Height: ComposerControlSize),
            }, Gap: IconPillGap, Justify: RowJustify.Center),
            OnClick: OnPinClick)
        { Bare = true, OnHover = OnPinHover })
    {
        CornerRadius = PillRadius, PaddingX = 0f, PaddingY = 0f, FillColor = Transparent,
        FillColorFn = PinFill, OutlineColorFn = () => _posAttached ? LinkColor() : null,
    }, Width: ComposerControlSize);

    private ColorRgba? PinFill()
    {
        if (!CanSharePosition() && !_posAttached) return DisabledShade;   // .SendCooldown.cs — recessed
        return _pinHover ? _services.Theme.Colors.MenuAccent with { A = TabPillAlpha } : TabTrackFill;
    }

    private ColorRgba? PinTint()
        => CanSharePosition() || _posAttached ? LinkColor()
            : DisabledLabel(_services.Theme.Colors.MenuBackground with { A = 1f }, _pinDisabledTint);

    private void OnPinHover(bool on)
    {
        _pinHover = on;
        HoverTip(TipKeyPinButton, on, PinTipText);
    }

    private string PinTipText()
    {
        if (CanSharePosition() || _posAttached) return _loc.T("chat.share.pinTip");
        var reason = _posPreview?.Reason;
        return !string.IsNullOrEmpty(reason) ? reason! : _loc.T("chat.share.cantShare");
    }

    // ── The attachment strip (above the composer row) ─────────────────────────────────────────────────────────────
    // Directly in the composer Column, so it fills the width; the text ellipsizes in a weighted cell before the ✕.
    private HudElement BuildPosStrip() => new BubbleElement(new RowElement(new HudElement[]
        {
            LinkIcon(Pin12, () => LinkColor()),   // .ShareLinks.Rows.cs
            new CellElement(new TextElement(PosPreviewText, () => LinkColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            // House icon-only ✕ (Stellar-Design-Language): transparent at rest, the red wash + light-red glyph on hover.
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(Width: 3f, Height: 20f),
                    new TextElement(() => "✕", () => _stripRemoveHover ? RemoveGlyph : MutedColor(), NoWrap: true),
                    new SpacerElement(Width: 3f, Height: 20f),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => { DetachPosition(); _composerFocusSeq++; })
                { Bare = true, OnHover = OnStripRemoveHover })
            {
                FillColor = Transparent, FillColorFn = () => _stripRemoveHover ? RemoveWash : Transparent,
                CornerRadius = 4f, PaddingX = 2f, PaddingY = 2f, MaxWidth = 40f,
            },
        }, Gap: 6f))
    {
        FillColor = Transparent, FillColorFn = () => LinkColor() with { A = PosStripWashAlpha },
        CornerRadius = 6f, PaddingX = 8f, PaddingY = 1f,
    };

    private void OnStripRemoveHover(bool on)
    {
        _stripRemoveHover = on;
        HoverTip(TipKeyStripRemove, on, StripRemoveTip);
    }

    private string StripRemoveTip() => _loc.T("chat.share.removeTip");

    // ── Send ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool _sentPosShare;   // the draft awaiting a reply went out as a position share (a refusal re-attaches)

    // From DoSend (.Composer.cs) while attached. Same gates as a text send, except an empty box is allowed (link only).
    private bool DoSharePosition()
    {
        if (TrOutBusy() || _inputText.Length > MaxMessageLength) return false;
        if (CoolingDown()) return false;            // .SendCooldown.cs
        if (ResolveTarget() is not { } target) return false;
        var text = _inputText;
        ChatShareResult r;
        try { r = _chat.SharePosition(target, string.IsNullOrWhiteSpace(text) ? null : text); }
        catch (Exception ex)
        {
            _services.Log.Warning($"[ChatTools] share position failed: {ex.GetType().Name}: {ex.Message}");
            r = new ChatShareResult { Status = ChatShareStatus.Failed };
        }
        if (!r.Ok)
        {
            // Nothing left the client: keep the draft + attachment; the Send pill flashes "Not sent" (.SendCooldown.cs).
            _refusedUntil = _services.Framework.TimeNow + RefusedFlashSec;
            _refusedTooFast = false;
            if (r.Status == ChatShareStatus.Refused) DetachPosition();   // the map forbids it after all
            return false;
        }
        NoteSendDispatched(text.Length > 0 ? text : null);   // grey Send until the reply; a refusal restores the draft
        _sentPosShare = true;
        JumpLogToBottom();                                    // .NewBelow.cs
        _inputText = string.Empty;
        ResetTrOut();                                         // .TranslateOut.cs
        DetachPosition();
        return true;
    }

    // From OnSendResult (.SendCooldown.cs) for a refused share: the draft comes back there; bring the link back too.
    private void OnPositionShareRefused()
    {
        if (!_sentPosShare) return;
        _sentPosShare = false;
        if (!_posAttached && CanSharePosition()) AttachPosition();
    }
}
