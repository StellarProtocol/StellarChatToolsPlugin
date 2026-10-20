using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The send composer: a send-target selector (Say/World/Party/Guild/Whisper) that defaults to the active
/// tab's channel but is overridable, a single-line input (Enter submits) + Send button, a reply indicator
/// for whisper, and a quick-phrases popover. No local echo — sends round-trip via MessageReceived.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxQuickPhrases = 12;

    // 0=Say, 1=World, 2=Party, 3=Guild, 4=Whisper. Whisper is reply-based this phase (needs _replyTarget).
    private int _sendTargetIndex;
    private ChatMessage? _replyTarget;   // the whisper being replied to (set by clicking a whisper row)

    private string _inputText = string.Empty;
    private bool _quickOpen;
    private string _quickEditText = string.Empty;
    private List<string> _quickPhrases = new();   // user-configurable; populated by LoadConfig

    // Send-target button keys (reuse the existing channel keys; whisper is new in this redesign).
    private static readonly string[] TargetKeys =
        { "chat.channel.say", "chat.channel.world", "chat.channel.party", "chat.channel.guild", "chat.channel.whisper" };

    // Default the composer target to the opened read-tab's channel (overridable afterwards). All/System don't
    // map to a send target, so they leave the current selection untouched. In-memory only (not persisted —
    // only an explicit target-button click persists the default).
    private void OnTabOpened(ChatTab tab)
    {
        switch (tab)
        {
            case ChatTab.Current: _sendTargetIndex = 0; break;
            case ChatTab.World:   _sendTargetIndex = 1; break;
            case ChatTab.Party:   _sendTargetIndex = 2; break;
            case ChatTab.Guild:   _sendTargetIndex = 3; break;
            case ChatTab.Whisper: _sendTargetIndex = 4; break;
        }
    }

    private HudElement BuildComposer()
    {
        var targets = new List<HudElement>(TargetKeys.Length);
        for (int i = 0; i < TargetKeys.Length; i++)
        {
            var idx = i;
            targets.Add(new ButtonElement(() => _loc.T(TargetKeys[idx]),
                () => { _sendTargetIndex = idx; SaveSendTarget(); },
                Active: () => _sendTargetIndex == idx));
        }

        return new ColumnElement(new HudElement[]
        {
            new RowElement(targets.ToArray(), Gap: 3f),

            // Whisper reply state: who we're replying to (+ cancel), or a hint to pick a whisper row.
            new ConditionalElement(() => _sendTargetIndex == 4 && _replyTarget != null, new RowElement(new HudElement[]
            {
                new TextElement(() => _loc.TFormat("chat.reply.to", _replyTarget?.SenderName ?? ""), () => _services.Theme.Colors.MenuMuted),
                new ButtonElement(() => _loc.T("chat.reply.cancel"), ClearReply),
            }, Gap: 6f)),
            new ConditionalElement(() => _sendTargetIndex == 4 && _replyTarget == null,
                new TextElement(() => _loc.T("chat.reply.hint"), () => _services.Theme.Colors.MenuMuted)),

            // Input (Enter submits -> DoSend) + Send button.
            new RowElement(new HudElement[]
            {
                new InputElement(() => _inputText, s => { _inputText = s; DoSend(); }, 300f, OnChange: s => _inputText = s),
                new ButtonElement(() => _loc.T("chat.send"), DoSend),
            }, Gap: 4f),

            // Composer toolbar. Quick-phrases button + the emoji SEAM (disabled — the emoji picker is the next
            // phase; this reserves its spot) + a right-aligned settings gear.
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T("chat.quick.button"), () => _quickOpen = !_quickOpen, Active: () => _quickOpen),
                new ButtonElement(() => _loc.T("chat.emoji.button"), () => { /* emoji picker: next phase */ }, Enabled: () => false),
                new SpacerElement(),
                new ButtonElement(() => _loc.T("chat.settings.button"), ToggleSettings, Active: () => _settingsOpen),
            }, Gap: 4f),

            // Quick-phrases popover: click a phrase to send it to the current target, then close.
            new ConditionalElement(() => _quickOpen, BuildQuickPopover()),
        }, Gap: 4f);
    }

    private HudElement BuildQuickPopover()
    {
        var slots = new HudElement[MaxQuickPhrases];
        for (int i = 0; i < MaxQuickPhrases; i++)
        {
            var idx = i;
            slots[idx] = new ButtonElement(() => QuickPhrase(idx), () => { SendPhrase(QuickPhrase(idx)); _quickOpen = false; });
        }
        return new PanelElement(new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _quickPhrases.Count == 0,
                new TextElement(() => _loc.T("chat.quick.empty"), () => _services.Theme.Colors.MenuMuted)),
            new ListElement(() => _quickPhrases.Count, slots),
        }, Gap: 2f));
    }

    private string QuickPhrase(int i) => i >= 0 && i < _quickPhrases.Count ? _quickPhrases[i] : "";

    // Resolve the current send target. Whisper requires a reply target (reply-based whisper this phase);
    // null means "cannot send" (the hint is already shown), so the caller just no-ops.
    private ChatTarget? ResolveTarget() => _sendTargetIndex switch
    {
        0 => ChatTarget.Say,
        1 => ChatTarget.World,
        2 => ChatTarget.Party,
        3 => ChatTarget.Guild,
        4 => (_replyTarget is { } r && r.SenderId != 0) ? ChatTarget.Reply(r) : null,
        _ => null,
    };

    private void DoSend()
    {
        if (string.IsNullOrWhiteSpace(_inputText)) return;
        if (ResolveTarget() is not { } target) return;
        _chat.Send(target, _inputText);   // game dispatcher — no packet construction, no local echo
        _inputText = string.Empty;        // FieldBinding clears the composer field on the next poll
    }

    private void SendPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return;
        if (ResolveTarget() is not { } target) return;
        _chat.Send(target, phrase);
    }

    private void SetReply(ChatMessage msg)
    {
        _replyTarget = msg;
        _sendTargetIndex = 4;   // switch the composer to Whisper so the reply goes to this sender
    }

    private void ClearReply() => _replyTarget = null;

    private void AddQuickPhrase()
    {
        var text = _quickEditText?.Trim();
        if (string.IsNullOrEmpty(text) || _quickPhrases.Count >= MaxQuickPhrases) return;
        _quickPhrases.Add(text);
        _quickEditText = string.Empty;
        SaveQuickPhrases();
    }

    private void RemoveQuickPhrase(int i)
    {
        if (i < 0 || i >= _quickPhrases.Count) return;
        _quickPhrases.RemoveAt(i);
        SaveQuickPhrases();
    }
}
