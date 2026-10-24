using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Click a player's AVATAR → the GAME's own name card (idcard popup) for that charId — chat bubble rows (others and
/// self), the Whisper conversation list and the conversation header. The game opens a card for ANY charId, nearby or
/// not, through its idcard VM (KB Agent-Game-Input-Automation.md "Player name card + Inspect"):
/// <c>Z.VMMgr.GetVM("idcard").AsyncGetCardData(charId, token)</c>. It is an Async* VM call, so it runs inside the
/// game's own coroutine wrapper (<c>Z.CoroUtil.create_coro_xpcall</c>), one-way via <c>ILua.DoString</c> — never a
/// Lua→C# callback (those crash the game).
///
/// The click target is a <see cref="SelectableElement.Bare"/> (no visuals, hugs its child) around a zero-padding,
/// transparent <see cref="BubbleElement"/> whose 1-px outline ring is the hover cue — so the avatar's layout and
/// rest look are byte-for-byte unchanged. The framework ticker already applies FrontWindowBlocks + scroll-clip to the
/// Selectable's hover and the EventSystem click path honours the RectMask2D.
/// </summary>
public sealed partial class Plugin
{
    private const double NameCardClickCooldown = 0.5;   // seconds — a double-click opens the card once, not twice

    // Hover-owner keys (one hovered avatar at a time). Bubble rows key by their pool slot, conversation rows by
    // AvatarKeyConvBase + slot, the header by AvatarKeyHeader — distinct ranges, so no per-frame allocation.
    private const int AvatarKeyConvBase = 100_000;
    private const int AvatarKeyHeader   = 200_000;

    private int    _avatarHover = -1;           // key of the hovered avatar (-1 = none)
    private double _lastNameCardAt = double.NegativeInfinity;
    private bool   _nameCardLogged;             // "open namecard" logged once per session (first use)

    // Wrap an avatar in the click-to-open-name-card target. charId is read at CLICK time (pool slots are recycled),
    // and 0 (unknown sender / no selection) does nothing. size = the avatar's box: the ring takes its baked corner
    // (AvatarCornerFraction, so it hugs the photo) and it is the bubble's explicit MaxWidth — with MaxWidth 0 a bubble caps
    // at its PARENT's width, and here the parent (the hugging Bare Selectable) is sized FROM the bubble: a circular cap.
    private HudElement ClickableAvatar(HudElement avatar, int key, float size, Func<long> charId)
        => new SelectableElement(
            new BubbleElement(avatar)
            {
                // Transparent static fill (NOT null — null = the themed accent default); zero padding → the bubble IS
                // the avatar's box. The ring is drawn over the avatar's edge, never measured → hover can't resize.
                FillColor = Transparent, PaddingX = 0f, PaddingY = 0f, MaxWidth = size, CornerRadius = size * AvatarCornerFraction,
                OutlineColorFn = () => _avatarHover == key ? AvatarHoverRing() : null,
            },
            OnClick: () => OpenNameCard(charId()))
        {
            Bare = true,
            OnHover = on => { if (on) _avatarHover = key; else if (_avatarHover == key) _avatarHover = -1; },
        };

    // Subtle hover cue: the menu text colour, part-transparent, as a 1-px ring.
    private ColorRgba AvatarHoverRing() => _services.Theme.Colors.MenuText with { A = 0.7f };

    private void OpenNameCard(long charId)
    {
        if (charId == 0 || !_services.Lua.Ready) return;
        var now = (double)_services.Framework.TimeNow;
        if (now - _lastNameCardAt < NameCardClickCooldown) return;
        _lastNameCardAt = now;
        if (!_nameCardLogged) { _nameCardLogged = true; _services.Log.Info("[ChatTools] open namecard (first this session)"); }
        // Invariant-culture integer literal: no float conversion, no locale digit grouping. charIds sit far below 2^53,
        // so the Lua number is exact. pcall keeps a Lua-side error from crossing the interop trampoline.
        var id = charId.ToString(CultureInfo.InvariantCulture);
        _services.Lua.DoString(
            "pcall(function() local cs=Z.CancelSource.Rent(); Z.CoroUtil.create_coro_xpcall(function() " +
            "Z.VMMgr.GetVM(\"idcard\").AsyncGetCardData(" + id + ", cs:CreateToken()) end)() end)");
    }
}
