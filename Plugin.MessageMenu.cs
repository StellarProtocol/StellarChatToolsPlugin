using System;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Right-click a message (any chat bubble — others' and our own, every tab incl. the Whisper conversation pane — or a
/// System notice line) → a cursor popup with message actions. Same cursor-popup pattern as the sticker / conversation
/// menus (.Favorites.cs / .WhisperList.cs): a separate content-sized borderless window, placed at the cursor in canvas
/// units, closed on Escape / click-outside by the framework.
///
/// The hook is <see cref="SelectableElement.OnRightClick"/> on a <see cref="SelectableElement.Bare"/> target wrapped
/// AROUND the whole bubble (never inside its measured child — see BuildBubbleRow) or around the notice line. Bare adds no
/// visuals or padding, so nothing moves at rest. The framework ticker fires it per render frame (a throttled plugin
/// Update could miss the one-frame edge), applies FrontWindowBlocks + scroll-clip, and suppresses the click-away dismiss
/// on the press that opened the menu. Nested buttons (name links, avatar name card, translate pill) sit deeper in the
/// hierarchy and keep receiving their left clicks; the Bare target's own left click is a no-op.
///
/// The message is captured when the menu OPENS (pool slots are recycled while scrolling, so the slot may show another
/// message by the time an item is clicked). Items are a table (<see cref="MsgMenuItems"/>) so more can be added later.
/// </summary>
public sealed partial class Plugin
{
    private const string MsgMenuId       = "chattools.msgmenu";
    private const float  MsgMenuW        = 200f;
    private const float  MsgMenuItemH    = 28f;    // ≈ one Selectable text row + its gap (edge-flip estimate only)
    private const float  MsgMenuPad      = 16f;    // panel padding top + bottom
    private const double MsgCopiedFlash  = 1.5;    // seconds the "Copied" label shows before the menu closes

    // One menu entry. Label/Enabled are polled per refresh; Run gets the captured message.
    private readonly record struct MsgMenuItem(Func<string> Label, Func<bool> Enabled, Action Run);

    private IWindowControl _msgMenuWindow = null!;
    private MsgMenuItem[] _msgMenuItems = Array.Empty<MsgMenuItem>();
    private bool _msgMenuOpen;
    private string _msgMenuText = "";          // the captured message's copyable text ("" = nothing to copy)
    private bool _msgMenuSticker;              // captured message is a sticker (no text to copy)
    private double _msgCopiedUntil = double.NegativeInfinity;   // TimeNow until which "Copied" shows

    // TMP rich-text tags to drop from copied text. An explicit tag-NAME list, not "anything in <>": TMP renders an unknown
    // tag (a player's literal "<hi>") as plain text, so a blanket strip would delete real message text. <sprite=N> is
    // deliberately absent — it round-trips into chat as the emoji. <#rrggbb> is TMP's colour shorthand.
    private static readonly Regex RichTagRx = new(
        @"</?(?:color|b|i|u|s|size|mark|link|font|font-weight|material|alpha|align|cspace|indent|line-height|line-indent|" +
        @"lowercase|uppercase|allcaps|smallcaps|margin|margin-left|margin-right|mspace|noparse|nobr|page|pos|rotate|" +
        @"strikethrough|underline|style|sub|sup|voffset|width|gradient|space|br)(?:[=\s][^<>]*)?>|<#[0-9a-fA-F]{3,8}>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private MsgMenuItem[] BuildMsgMenuItems() => new[]
    {
        new MsgMenuItem(
            () => _loc.T(_services.Framework.TimeNow < _msgCopiedUntil ? "chat.menu.copied" : "chat.menu.copyMessage"),
            () => !_msgMenuSticker && _msgMenuText.Length > 0,
            CopyMenuMessage),
    };

    private IWindowControl RegisterMsgMenuWindow()
    {
        _msgMenuItems = BuildMsgMenuItems();
        var rows = new HudElement[_msgMenuItems.Length];
        for (int n = 0; n < rows.Length; n++)
        {
            var item = _msgMenuItems[n];
            // A disabled item (e.g. Copy on a sticker) greys its label and ignores the click.
            rows[n] = new SelectableElement(new TextElement(item.Label, () => item.Enabled() ? null : _services.Theme.Colors.MenuMuted),
                () => { if (item.Enabled()) item.Run(); });
        }
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          MsgMenuId,
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, MsgMenuW, MsgMenuHeight()),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(rows, Gap: 2f), Padding: 8f),
            OnClose: CloseMsgMenu));   // framework invokes this on Escape / click-outside
    }

    private float MsgMenuHeight() => MsgMenuPad + MsgMenuItemH * Math.Max(1, _msgMenuItems.Length);

    // Wrap a whole bubble (or a notice line) in the zero-visual right-click target. OnClick is required by the record
    // and deliberately does nothing — the bubble had no left-click action of its own.
    private HudElement MessageRightClickTarget(HudElement child, int slot)
        => new SelectableElement(child, OnClick: static () => { }) { Bare = true, OnRightClick = () => OpenMsgMenu(slot) };

    private void OpenMsgMenu(int slot)
    {
        if (!TryMsg(slot, out var m)) { CloseMsgMenu(); return; }
        _msgMenuSticker = m.StickerId != 0;
        // ORIGINAL text, never the translation shown in a translated bubble (m.Text is the wire text; the translation
        // lives in the .Translate.cs cache). A notice copies its label + text, as the line reads, minus the time.
        // Copy the human-readable text the row shows: wire text as sanitized for display (ChatMarkup — <sprite=N> kept, it
        // round-trips into chat as the emoji), a client notice minus the game's colour tags.
        var raw = m.Channel == ChatChannel.System && !string.IsNullOrEmpty(m.SystemLabel) ? m.SystemLabel + " " + m.Text : m.Text;
        _msgMenuText = _msgMenuSticker ? "" : StripRichTags(m.SystemLabel != null ? raw ?? "" : ChatMarkup.SanitizeWire(raw));
        _msgCopiedUntil = double.NegativeInfinity;
        _msgMenuOpen = true;
        // Set BEFORE showing: the framework applies a pre-mount SetRect of a click-away popup at its mount (fw dcef900).
        // The height comes from the item list, so it is known up front: nothing to re-place once mounted.
        _msgMenuWindow.SetRect(CursorPopupRect(MsgMenuW, MsgMenuHeight()));   // .WhisperList.cs
        _msgMenuWindow.SetVisible(true);        // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    private static string StripRichTags(string s) => s.IndexOf('<') < 0 ? s : RichTagRx.Replace(s, "").Trim();

    // Copy, then flash "Copied" on the item and close after MsgCopiedFlash (TickMsgMenu). Outside-click still closes early.
    private void CopyMenuMessage()
    {
        CopyToClipboard(_msgMenuText);   // .WhisperList.cs — GUIUtility.systemCopyBuffer, failure logged
        _msgCopiedUntil = _services.Framework.TimeNow + MsgCopiedFlash;
    }

    // Framework.Update: end the "Copied" flash, and close with the chat window.
    private void TickMsgMenu(float _)
    {
        if (!_msgMenuOpen) return;
        if (!_window.IsShown) { CloseMsgMenu(); return; }
        if (_msgCopiedUntil != double.NegativeInfinity && _services.Framework.TimeNow >= _msgCopiedUntil) CloseMsgMenu();
    }

    private void CloseMsgMenu()
    {
        if (!_msgMenuOpen) return;
        _msgMenuOpen = false;
        _msgCopiedUntil = double.NegativeInfinity;
        _msgMenuWindow?.SetVisible(false);
    }
}
