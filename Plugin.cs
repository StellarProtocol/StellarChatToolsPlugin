using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// In-game chat client: a rolling, channel-filtered log, a send composer with per-channel targets +
/// quick phrases, whisper reply/auto-reply, and a settings drawer. Reads/sends via the framework
/// <see cref="IChat"/> surface only (no packet construction) and never echoes locally — sent messages
/// round-trip through <see cref="IChat.MessageReceived"/> like any other.
///
/// Phase 1 CORE of the redesign (resizable window, read-tabs, composer, quick phrases, settings drawer,
/// config persistence). Emoji picker / inline sprite rendering is a deferred follow-up; a seam (the
/// disabled emoji button in the composer toolbar) is left for it — see <see cref="BuildComposer"/>.
///
/// Laid out as partials (500-LoC CI gate): Plugin.cs (lifecycle), Plugin.Window.cs (layout + settings
/// drawer), Plugin.Tabs.cs (read-tabs + unread), Plugin.Log.cs (rows/filter/colours), Plugin.Composer.cs
/// (send + quick phrases), Plugin.AutoReply.cs (inbound handler + auto-reply), Plugin.Config.cs (persist).
///
/// Hotkey: F12 toggles visibility (user can rebind).
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "ChatTools";

    // Initial capacity hint for the filtered-index list (_filtered); the framework RecentMessages ring is itself
    // bounded. No longer a display cap: VirtualListElement windows the FULL filtered set and materializes only
    // LogPoolSize (.Log.cs) recycled bubble GameObjects, so this does NOT bound live rows.
    private const int MaxLogRows = 200;

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly IChat _chat;
    private readonly IWindowControl _window;
    private IConfigSection _cfg = null!;   // set in LoadConfig() (ctor), before any save

    // Per-channel sender-name colours. ChatTools-owned, user-editable slots: edited through the framework
    // Theme settings (the registry persists overrides) — IColorSlot is read-only to the plugin, so the
    // channel tabs wear these colours as the legend; there is no in-window picker. Resolved live via .Value.
    private IColorSlot _sayColor = null!;      // Say == the game's "Current"/Scene channel
    private IColorSlot _worldColor = null!;
    private IColorSlot _partyColor = null!;
    private IColorSlot _guildColor = null!;
    private IColorSlot _whisperColor = null!;
    private IColorSlot _systemColor = null!;

    public Plugin(IPluginServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _loc = services.Localization;
        _chat = services.Chat;
        _services.Log.Info("[ChatTools] plugin constructed");

        RegisterColors();   // .Log.cs — must precede BuildRoot's colour Funcs resolving at draw time
        LoadConfig();       // .Config.cs — quick phrases, auto-reply, font size, default send target

        _window = _services.Windows.Register(
            new WindowRegistration(
                new WindowSpec(
                    Id:          "chattools.main",
                    Title:       _loc.T("chat.window.title"),
                    // Compact bubble layout: a narrower default than the game's chat (favouring density), but wide
                    // enough for the 7 read-tabs; grip-resizable (below) so users can grow it.
                    DefaultRect: new WindowRect(21f, 586f, 480f, 430f),
                    Category:    WindowCategory.HUD,
                    Style:       WindowPanelStyle.GlassMenu)
                {
                    ShouldRender = () => _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                    // Render on our OWN nested canvas (a uGUI batching boundary). The log is a ScrollRect: scrolling
                    // moves its content every frame, which dirties + rebatches whatever canvas it lives on. On the
                    // SHARED overlay canvas that re-batches EVERY window's graphics per render frame → an FPS drop
                    // that scales with how many windows are open. Isolated, a scroll rebuilds only THIS window's
                    // batch. overrideSorting stays false, so BringToFront / layout z-order still work normally.
                    IsolateCanvas = true,
                    Closable = true,
                    Draggable = true,
                    // Resizable turns OFF vertical content auto-fit; the body's ScrollElement (flexibleHeight=1)
                    // absorbs the freed height so the log grows with the window (WindowBuilder BuildScroll).
                    Resizable = true,
                    // 480 is only a FLOOR: the real minimum width comes from content — the tab row and composer rows
                    // are RowElement.ConstrainsWindowMinWidth, so it follows the current language's natural widths.
                    MinWidth = 480f, MinHeight = 260f,
                    MaxWidth = 1000f, MaxHeight = 1100f,
                },
                BuildRoot(),   // .Window.cs — element tree built ONCE; Funcs re-pull live state each refresh
                TitleTrailing: BuildTitleGear(),   // .Window.cs — settings ⚙ in the title bar, before the ✕
                // Don't strand the popups (.SendTarget.cs / .WhisperList.cs / .FriendPicker.cs).
                OnClose: () => { CloseTargetMenu(); CloseConvMenu(); CloseFriendPicker(); _window!.SetVisible(false); }),
            new HotkeyAction(
                Id:              "chattools.toggle",
                Description:     _loc.T("chat.hotkey.toggle"),
                SuggestedDefault: new KeyBinding(StellarKeyCode.F12)),
            _services.Hotkeys);

        // Always-on subscription: inbound messages drive BOTH per-tab unread tracking AND (when enabled)
        // whisper auto-reply. The handler early-outs when auto-reply is off, so the only cost of being
        // always-subscribed is the unread bookkeeping we need regardless of the window being open.
        _chat.MessageReceived += OnChatMessage;   // fires on the Unity main thread
        // The server's verdict on each send (.SendCooldown.cs): ends the optimistic Send hold, flashes a refusal, and
        // restores a refused draft. The cooldown length itself is polled from IChat.TryGetSendCooldown.
        _chat.SendResult += OnSendResult;

        // Sticker right-click menu (.Favorites.cs): a cursor-positioned click-away popup; the Update hook only
        // re-asserts its rect once the window has remounted after SetVisible(true).
        _favMenuWindow = RegisterFavMenuWindow();
        _services.Framework.Update += TickFavMenuPlace;
        // Send-target picker (.SendTarget.cs): same cursor-popup pattern as the sticker menu.
        _targetMenuWindow = RegisterTargetMenuWindow();
        _services.Framework.Update += TickTargetMenu;
        // Avatar decodes (.Avatars.cs) run here under a fixed per-tick budget — never inside the window apply.
        _services.Framework.Update += TickAvatarDecode;
        // Per-person whispers (.Whisper.cs): conversation list from IFriends + live whispers; the conversation right-click
        // menu (.WhisperList.cs) and the "+ New chat" friend picker (.FriendPicker.cs) are cursor popups like the above.
        InitWhisper();
        _convMenuWindow = RegisterConvMenuWindow();
        _friendPickerWindow = RegisterFriendPickerWindow();
        _services.Framework.Update += TickWhisper;
    }

    public void Dispose()
    {
        try { _chat.MessageReceived -= OnChatMessage; } catch { /* swallow */ }
        try { _chat.SendResult -= OnSendResult; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickFavMenuPlace; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickTargetMenu; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickAvatarDecode; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickWhisper; } catch { /* swallow */ }
        DisposeWhisper();
        DisposeTranslate();   // .Translate.Net.cs — cancel in-flight requests; late results are dropped
        _sayColor.Dispose();
        _worldColor.Dispose();
        _partyColor.Dispose();
        _guildColor.Dispose();
        _whisperColor.Dispose();
        _systemColor.Dispose();
        _window.Remove();
        _favMenuWindow?.Remove();
        _targetMenuWindow?.Remove();
        _convMenuWindow?.Remove();
        _friendPickerWindow?.Remove();
        DisposeAvatars();   // .Avatars.cs — free the LRU + default avatar textures (GPU memory)
    }
}
