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
    // settings drawer shows a live swatch legend rather than an in-window picker. Resolved live via .Value.
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
        SeedUnread();       // .Tabs.cs  — seed last-seen from existing log so history doesn't light tabs

        _window = _services.Windows.Register(
            new WindowRegistration(
                new WindowSpec(
                    Id:          "chattools.main",
                    Title:       _loc.T("chat.window.title"),
                    // Compact bubble layout: a narrower default than the game's chat (favouring density), but wide
                    // enough for the 7 read-tabs; grip-resizable (below) so users can grow it.
                    DefaultRect: new WindowRect(21f, 586f, 440f, 430f),
                    Category:    WindowCategory.HUD,
                    Style:       WindowPanelStyle.GlassMenu)
                {
                    ShouldRender = () => _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                    Closable = true,
                    Draggable = true,
                    // Resizable turns OFF vertical content auto-fit; the body's ScrollElement (flexibleHeight=1)
                    // absorbs the freed height so the log grows with the window (WindowBuilder BuildScroll).
                    Resizable = true,
                    MinWidth = 360f, MinHeight = 260f,
                    MaxWidth = 1000f, MaxHeight = 1100f,
                },
                BuildRoot(),   // .Window.cs — element tree built ONCE; Funcs re-pull live state each refresh
                OnClose: () => _window!.SetVisible(false)),
            new HotkeyAction(
                Id:              "chattools.toggle",
                Description:     _loc.T("chat.hotkey.toggle"),
                SuggestedDefault: new KeyBinding(StellarKeyCode.F12)),
            _services.Hotkeys);

        // Always-on subscription: inbound messages drive BOTH per-tab unread tracking AND (when enabled)
        // whisper auto-reply. The handler early-outs when auto-reply is off, so the only cost of being
        // always-subscribed is the unread bookkeeping we need regardless of the window being open.
        _chat.MessageReceived += OnChatMessage;   // fires on the Unity main thread
    }

    public void Dispose()
    {
        try { _chat.MessageReceived -= OnChatMessage; } catch { /* swallow */ }
        _sayColor.Dispose();
        _worldColor.Dispose();
        _partyColor.Dispose();
        _guildColor.Dispose();
        _whisperColor.Dispose();
        _systemColor.Dispose();
        _window.Remove();
        DisposeAvatars();   // .Avatars.cs — free the LRU + default avatar textures (GPU memory)
    }
}
