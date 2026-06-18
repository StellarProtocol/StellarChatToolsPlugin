using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// In-game chat log, composer, and whisper auto-reply. Subscribes to <see cref="IChat"/> events,
/// maintains a rolling log of the last <see cref="MaxMessages"/> messages, and lets the user type
/// and send chat via the game's own dispatcher (<see cref="IChat.Send"/>) — no packet construction.
/// Demonstrates a multi-section uGUI window (log + composer + whisper rule list) using ScrollElement
/// and InputElement.
///
/// Hotkey: F12 toggles visibility (user can rebind).
/// </summary>
public sealed class Plugin : IStellarPlugin
{
    public string Name => "ChatTools";

    private const int MaxMessages = 60;   // log rows built; the newest MaxMessages are shown

    private readonly IPluginServices _services;
    private readonly IChat _chat;
    private readonly IWindowControl _window;

    // Party/Whisper are ChatTools-owned, user-editable colour slots (per-preset defaults carry the
    // light/dark adaptation). Resolved live via slot.Value in MsgColor.
    private readonly IColorSlot _partySlot;
    private readonly IColorSlot _whisperSlot;

    // composer state
    private string _inputText = string.Empty;
    private int _selectedChannelIndex;   // 0=Say, 1=World, 2=Party, 3=Guild

    private static readonly string[] ChannelLabels = { "Say", "World", "Party", "Guild" };
    private static readonly ChatTarget[] ChannelTargets = { ChatTarget.Say, ChatTarget.World, ChatTarget.Party, ChatTarget.Guild };

    // auto-reply state
    private bool _autoReplyEnabled;
    private string _autoReplyText = "I'm away — auto-reply from Stellar ChatTools.";
    private readonly Dictionary<string, DateTime> _autoReplyCooldown = new(StringComparer.Ordinal);
    private static readonly TimeSpan AutoReplyCooldown = TimeSpan.FromSeconds(30);
    private bool _autoReplySubscribed;

    public Plugin(IPluginServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _chat = services.Chat;
        services.Log.Info("[ChatTools] plugin constructed");

        var registry = _services.Theme.ColorRegistry;
        _partySlot = registry.Register("ChatTools.Channel.Party", "Party text", new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = new ColorRgba(0.40f, 0.85f, 1.00f),
            [ThemePreset.Dark]    = new ColorRgba(0.40f, 0.85f, 1.00f),
            [ThemePreset.Crimson] = new ColorRgba(0.40f, 0.85f, 1.00f),
            [ThemePreset.Light]   = new ColorRgba(0.07f, 0.42f, 0.66f),
        });
        _whisperSlot = registry.Register("ChatTools.Channel.Whisper", "Whisper text", new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = new ColorRgba(0.85f, 0.50f, 1.00f),
            [ThemePreset.Dark]    = new ColorRgba(0.85f, 0.50f, 1.00f),
            [ThemePreset.Crimson] = new ColorRgba(0.85f, 0.50f, 1.00f),
            [ThemePreset.Light]   = new ColorRgba(0.52f, 0.18f, 0.72f),
        });

        _window = _services.Windows.Register(
            new WindowRegistration(
                new WindowSpec(
                    Id:          "chattools.main",
                    Title:       "ChatTools",
                    DefaultRect: new WindowRect(21f, 596f, 480f, 0f),
                    Category:    WindowCategory.HUD,
                    Style:       WindowPanelStyle.GlassMenu)
                { HideUntilInWorld = true, Closable = true, Draggable = true },
                BuildRoot(),
                OnClose: () => _window!.SetVisible(false)),
            new HotkeyAction(
                Id:              "chattools.toggle",
                Description:     "Toggle ChatTools window",
                SuggestedDefault: new KeyBinding(StellarKeyCode.F12)),
            _services.Hotkeys);
    }

    public void Dispose()
    {
        if (_autoReplySubscribed)
        {
            try { _chat.MessageReceived -= OnMessageForAutoReply; } catch { /* swallow */ }
            _autoReplySubscribed = false;
        }
        _partySlot.Dispose();
        _whisperSlot.Dispose();
        _window.Remove();
    }

    // The window element tree, built once. Funcs re-pull the live chat log / state on the framework refresh.
    private HudElement BuildRoot()
    {
        var slots = new HudElement[MaxMessages];
        for (int i = 0; i < MaxMessages; i++) { var idx = i; slots[i] = BuildMessageRow(idx); }

        var channelButtons = new List<HudElement>();
        for (int i = 0; i < ChannelLabels.Length; i++)
        {
            var idx = i;
            channelButtons.Add(new ButtonElement(() => ChannelLabels[idx], () => _selectedChannelIndex = idx,
                Active: () => _selectedChannelIndex == idx));
        }
        channelButtons.Add(new InputElement(() => _inputText, s => { _inputText = s; DoSend(); }, 240f, OnChange: s => _inputText = s));
        channelButtons.Add(new ButtonElement(() => "Send", DoSend));

        return new ColumnElement(new HudElement[]
        {
            new ScrollElement(new ListElement(() => Math.Min(_chat.RecentMessages.Count, MaxMessages), slots), 150f),
            new RowElement(channelButtons.ToArray(), Gap: 4f),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", () => _autoReplyEnabled, SetAutoReply),
                new TextElement(() => "Auto-reply to whispers"),
            }, Gap: 6f),
            new ConditionalElement(() => _autoReplyEnabled, new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "Reply:", Width: 56f),
                    new InputElement(() => _autoReplyText, s => _autoReplyText = s, 320f, OnChange: s => _autoReplyText = s),
                }, Gap: 4f),
                new TextElement(() => $"Cooldown: {AutoReplyCooldown.TotalSeconds:0}s per sender", () => _services.Theme.Colors.TextMuted),
            }, Gap: 4f)),
        });
    }

    // One chat-log row: timestamp | channel prefix | "sender: text", coloured by channel (Party/Whisper from
    // their owned slots; System muted; others framework default).
    private HudElement BuildMessageRow(int i) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(() => MsgTime(i), () => MsgColor(i)), Width: 64f),
        new CellElement(new TextElement(() => MsgPrefix(i), () => MsgColor(i)), Width: 56f),
        new TextElement(() => MsgText(i), () => MsgColor(i)),
    }, Gap: 4f);

    // Map a slot index to the windowed message index — the slots show the LAST MaxMessages messages (newest
    // at the bottom), so slot 0 = the oldest still-shown message, not RecentMessages[0].
    private int MsgIndex(int slot) => Math.Max(0, _chat.RecentMessages.Count - MaxMessages) + slot;

    private string MsgTime(int slot)
    {
        var m = _chat.RecentMessages; var i = MsgIndex(slot);
        return i < m.Count ? m[i].Timestamp.ToLocalTime().ToString("HH:mm:ss") : "";
    }

    private string MsgPrefix(int slot)
    {
        var m = _chat.RecentMessages; var i = MsgIndex(slot);
        return i < m.Count ? PrefixFor(m[i].Channel) : "";
    }

    private string MsgText(int slot)
    {
        var m = _chat.RecentMessages; var i = MsgIndex(slot);
        return i < m.Count ? $"{m[i].SenderName}: {m[i].Text}" : "";
    }

    private ColorRgba? MsgColor(int slot)
    {
        var m = _chat.RecentMessages; var i = MsgIndex(slot);
        if (i >= m.Count) return null;
        return m[i].Channel switch
        {
            ChatChannel.Party   => _partySlot.Value,
            ChatChannel.Whisper => _whisperSlot.Value,
            ChatChannel.System  => _services.Theme.Colors.MenuMuted,
            _                   => (ColorRgba?)null,   // framework default (MenuText)
        };
    }

    private void SetAutoReply(bool enabled)
    {
        if (enabled == _autoReplyEnabled) return;
        _autoReplyEnabled = enabled;
        if (_autoReplyEnabled && !_autoReplySubscribed)
        {
            _chat.MessageReceived += OnMessageForAutoReply;
            _autoReplySubscribed = true;
        }
        else if (!_autoReplyEnabled && _autoReplySubscribed)
        {
            _chat.MessageReceived -= OnMessageForAutoReply;
            _autoReplySubscribed = false;
        }
    }

    private void DoSend()
    {
        if (string.IsNullOrWhiteSpace(_inputText)) return;
        var target = ChannelTargets[_selectedChannelIndex];
        _chat.Send(target, _inputText);   // game dispatcher — no packet construction
        _inputText = string.Empty;        // FieldBinding clears the composer field on the next poll
        // No local echo — the message round-trips via MessageReceived and shows in the log.
    }

    private void OnMessageForAutoReply(ChatMessage msg)
    {
        if (msg.Channel != ChatChannel.Whisper) return;
        if (msg.IsHistory) return;                   // skip server-replayed history dump on login
        if (msg.SenderId == 0) return;               // Reply() requires a routable SenderId
        if (string.IsNullOrWhiteSpace(_autoReplyText)) return;

        var key = msg.SenderName + "|" + msg.SenderId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_autoReplyCooldown.TryGetValue(key, out var last) && DateTime.UtcNow - last < AutoReplyCooldown)
            return;

        _autoReplyCooldown[key] = DateTime.UtcNow;
        _chat.Send(ChatTarget.Reply(msg), _autoReplyText);
    }

    private static string PrefixFor(ChatChannel ch) => ch switch
    {
        ChatChannel.Say     => "[Say]",
        ChatChannel.World   => "[World]",
        ChatChannel.Party   => "[Party]",
        ChatChannel.Guild   => "[Guild]",
        ChatChannel.Whisper => "[Whisp]",
        ChatChannel.System  => "[Sys]",
        _                   => "[??]",
    };
}
