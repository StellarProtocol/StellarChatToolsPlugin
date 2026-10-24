using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// In-game chat client: a rolling, channel-filtered log, a send composer with per-channel targets +
/// quick phrases, whisper reply/auto-reply, and a separate settings window. Reads/sends via the framework
/// <see cref="IChat"/> surface only (no packet construction) and never echoes locally — sent messages
/// round-trip through <see cref="IChat.MessageReceived"/> like any other.
///
/// Phase 1 CORE of the redesign (resizable window, read-tabs, composer, quick phrases, settings window,
/// config persistence). Emoji picker / inline sprite rendering is a deferred follow-up; a seam (the
/// disabled emoji button in the composer toolbar) is left for it — see <see cref="BuildComposer"/>.
///
/// Laid out as partials (500-LoC CI gate): Plugin.cs (lifecycle), Plugin.Window.cs (layout), Plugin.Settings.cs (settings
/// window + title gear), Plugin.Tabs.cs (read-tabs + unread), Plugin.Log.cs (rows/filter/colours), Plugin.Composer.cs
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
    private readonly IDisposable _launcher;   // Stellar menu tile; disposed with the plugin
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
        InitLanguage();     // .Language.cs — live language switch (titles, colour labels, default auto-reply)
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
                    // Starts HIDDEN: opened only by the hotkey or the Stellar menu tile. StartVisible covers boot;
                    // OnPhaseChanged re-hides on every later world entry (Visible is session state, so a window left
                    // open at logout would otherwise come back up on the next character's login).
                    // Live-localized: the chrome re-reads it on a language switch (Title stays the build-time fallback).
                    TitleProvider = () => _loc.T("chat.window.title"),
                    StartVisible = false,
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
                TitleTrailing: BuildTitleGear(),   // .Settings.cs — settings ⚙ in the title bar, before the ✕
                OnClose: CloseChat),   // closes popups + settings and flushes whisper history (below)
            new HotkeyAction(
                Id:              "chattools.toggle",
                Description:     _loc.T("chat.hotkey.toggle"),
                SuggestedDefault: new KeyBinding(StellarKeyCode.F12)),
            _services.Hotkeys);

        // Always-on subscription: inbound messages drive BOTH per-tab unread tracking AND (when enabled)
        // whisper auto-reply. The handler early-outs when auto-reply is off, so the only cost of being
        // always-subscribed is the unread bookkeeping we need regardless of the window being open.
        _chat.MessageReceived += OnChatMessage;   // fires on the Unity main thread
        // Re-hide on each world entry (see StartVisible above). Phase stays World through in-world zone loads, so this
        // fires once per character login, never on a teleport/dungeon load mid-session.
        _services.ClientState.PhaseChanged += OnPhaseChanged;
        // Per-character reset (.CharacterSwitch.cs): raised from the game's OnLogout before the player record is cleared.
        _services.ClientState.Logout += OnClientLogout;
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
        // On-disk whisper history (.WhisperHistory.cs): per-character folder key, 2 s batched writes, logout flush.
        _services.Framework.Update += TickWhisperHistory;
        // Settings (.Settings.cs): its own window, toggled by the title ⚙; centred over the chat on each open and
        // closed with it.
        _settingsWindow = RegisterSettingsWindow();
        _services.Framework.Update += TickSettings;
        // Message right-click menu (.MessageMenu.cs): Copy message; same cursor-popup pattern.
        _msgMenuWindow = RegisterMsgMenuWindow();
        _services.Framework.Update += TickMsgMenu;
        // Outgoing-translate language menu (.TranslateOut.Menu.cs) behind the composer 文A pill; same cursor-popup pattern.
        _trOutMenuWindow = RegisterTrOutMenuWindow();
        _services.Framework.Update += TickTrOutMenu;

        // Stellar menu tile — the second way to open the chat besides the hotkey. "ChatTools" is the product name (also
        // the stable pin identity), so it stays unlocalized like the other plugins' product-name tiles. Show, not
        // toggle: a tile click on an already-open chat is a no-op rather than a surprise close.
        _launcher = _services.Launcher.Register(new LauncherEntry(
            Title:   "ChatTools",
            IconPng: LoadLauncherIcon(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });
    }

    private const string LauncherIconResource = "Stellar.ChatTools.Resources.chattools-icon.png";

    private static byte[]? LoadLauncherIcon()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(LauncherIconResource);
            if (s == null) return null;   // tile still shows, just without an icon
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ✕ close: also write any pending whisper history now (.WhisperHistory.cs) instead of waiting out its 2 s batch.
    private void CloseChat()
    {
        FlushHistory();
        HideChat();
    }

    // Hide the chat and everything hanging off it — don't strand the popups (.SendTarget.cs / .WhisperList.cs /
    // .FriendPicker.cs) or the settings window. Session-only SetVisible (the framework hotkey toggle uses it too):
    // visibility is deliberately NOT persisted, so the window never restores itself open. Position/size persist
    // independently of visibility.
    private void HideChat()
    {
        CloseTargetMenu(); CloseConvMenu(); CloseMsgMenu(); CloseTrOutMenu(); CloseFriendPicker(); CloseFavMenu(); CloseSettings();
        _window.SetVisible(false);
    }

    // World entry → start hidden. Leaving World also resets per-character state (.CharacterSwitch.cs) — but only as a
    // second trigger: a logout from World usually raises NO PhaseChanged (Phase stays World until the login view shows,
    // which an Account-Switcher auto-login skips), so the primary edge is IClientState.Logout (OnClientLogout). No
    // FlushHistory at world entry: the logout edge already flushed, and SelfName() may already be the new character.
    private void OnPhaseChanged(PhaseChange change)
    {
        _services.Log.Info($"[ChatTools] phase {change.From} → {change.To}");
        if (change.From == GamePhase.World && change.To != GamePhase.World) ResetCharacterState(fallback: false);
        if (change.To == GamePhase.World && change.From != GamePhase.World) HideChat();
    }

    public void Dispose()
    {
        try { _launcher.Dispose(); } catch { /* swallow */ }
        try { _services.ClientState.PhaseChanged -= OnPhaseChanged; } catch { /* swallow */ }
        try { _services.ClientState.Logout -= OnClientLogout; } catch { /* swallow */ }
        try { _chat.MessageReceived -= OnChatMessage; } catch { /* swallow */ }
        try { _chat.SendResult -= OnSendResult; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickFavMenuPlace; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickTargetMenu; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickAvatarDecode; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickWhisper; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickWhisperHistory; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickSettings; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickMsgMenu; } catch { /* swallow */ }
        try { _services.Framework.Update -= TickTrOutMenu; } catch { /* swallow */ }
        // Every remaining step in its own guard: one throwing (a half-torn-down service, a destroyed window) must not
        // skip the rest — above all the history flush and the GPU texture release.
        Safe(FlushAutoReplyText);      // .Config.cs — a typed reply not yet saved
        Safe(DisposeWhisper);
        Safe(DisposeWhisperHistory);   // .WhisperHistory.cs — write pending lines, wait briefly for the worker
        Safe(DisposeTranslate);        // .Translate.Net.cs — cancel in-flight requests; late results are dropped
        Safe(DisposeLanguage);
        Safe(DisposeColorSlots);       // .Language.cs
        Safe(() => _window.Remove());
        Safe(() => _favMenuWindow?.Remove());
        Safe(() => _targetMenuWindow?.Remove());
        Safe(() => _convMenuWindow?.Remove());
        Safe(() => _friendPickerWindow?.Remove());
        Safe(() => _settingsWindow?.Remove());
        Safe(() => _msgMenuWindow?.Remove());
        Safe(() => _trOutMenuWindow?.Remove());
        Safe(DisposeAvatars);          // .Avatars.cs — free the LRU + default avatar textures (GPU memory)
    }

    private void Safe(Action step)
    {
        try { step(); }
        catch (Exception ex) { try { _services.Log.Warning($"[ChatTools] dispose step failed: {ex.GetType().Name}: {ex.Message}"); } catch { } }
    }
}
