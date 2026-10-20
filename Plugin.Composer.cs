using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The send composer, restyled to the game's layout: a "Send to [channel ▾]" dropdown over a compact input
/// row (a ＋ button that opens quick phrases, the text field, a 0/140 char counter, and Send). The target
/// defaults to the active read-tab's channel but is overridable; whisper is reply-based. No local echo —
/// sends round-trip via MessageReceived (and teach us our own identity for right-aligning, see Bubbles).
/// </summary>
public sealed partial class Plugin
{
    private const int MaxQuickPhrases = 12;
    private const int MaxMessageLength = 140;   // mirrors the game's 0/140 counter; soft-capped on input

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
        return new ColumnElement(new HudElement[]
        {
            // "Send to [channel ▾]" — the game's target selector, as a dropdown. Settings gear parked on the right.
            new RowElement(new HudElement[]
            {
                new TextElement(() => _loc.T("chat.sendTo"), () => _services.Theme.Colors.MenuMuted, NoWrap: true),
                new DropdownElement(() => _sendTargetIndex, TargetOptions, OnPickTarget, Width: 120f),
                new SpacerElement(),
                new ButtonElement(() => _loc.T("chat.settings.button"), ToggleSettings, Active: () => _settingsOpen),
            }, Gap: 6f),

            // Whisper reply state: who we're replying to (+ cancel), or a hint to pick a whisper row.
            new ConditionalElement(() => _sendTargetIndex == 4 && _replyTarget != null, new RowElement(new HudElement[]
            {
                new TextElement(() => _loc.TFormat("chat.reply.to", _replyTarget?.SenderName ?? ""), () => _services.Theme.Colors.MenuMuted),
                new ButtonElement(() => _loc.T("chat.reply.cancel"), ClearReply),
            }, Gap: 6f)),
            new ConditionalElement(() => _sendTargetIndex == 4 && _replyTarget == null,
                new TextElement(() => _loc.T("chat.reply.hint"), () => _services.Theme.Colors.MenuMuted)),

            // Input row: ＋ (quick-phrase entry seam) · text field · 0/140 counter · Send. The field is wrapped
            // in a weighted cell so the dark rounded input stretches across the row like the game's composer.
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "＋", () => _quickOpen = !_quickOpen, Active: () => _quickOpen, Width: 30f),
                new CellElement(new InputElement(() => _inputText, s => { SetInput(s); DoSend(); }, 160f, OnChange: SetInput), Weight: 1f),
                new TextElement(() => CharCounter(), () => _services.Theme.Colors.MenuMuted, NoWrap: true),
                new ButtonElement(() => _loc.T("chat.send"), DoSend),
            }, Gap: 4f),

            // Quick-phrases popover: click a phrase to send it to the current target, then close.
            new ConditionalElement(() => _quickOpen, BuildQuickPopover()),
        }, Gap: 4f);
    }

    // Localized dropdown option list (Say/World/Party/Guild/Whisper), rebuilt each open so a language switch
    // is reflected. The dropdown is index-based, so the picked index maps straight to the send target.
    private IReadOnlyList<string> TargetOptions()
    {
        var opts = new string[TargetKeys.Length];
        for (int i = 0; i < TargetKeys.Length; i++) opts[i] = _loc.T(TargetKeys[i]);
        return opts;
    }

    private void OnPickTarget(int index)
    {
        if (index < 0 || index >= TargetKeys.Length) return;
        _sendTargetIndex = index;
        SaveSendTarget();
    }

    // Soft-cap the field to the game's 140 chars; the FieldBinding re-seeds the field from _inputText on the
    // next poll, so a truncation shows immediately.
    private void SetInput(string s) => _inputText = s.Length > MaxMessageLength ? s.Substring(0, MaxMessageLength) : s;

    private string CharCounter() => _inputText.Length + "/" + MaxMessageLength;

    // Index -> the channel the echo of a send will arrive on (for self-identity round-trip learning).
    private ChatChannel SendChannel() => _sendTargetIndex switch
    {
        0 => ChatChannel.Say,
        1 => ChatChannel.World,
        2 => ChatChannel.Party,
        3 => ChatChannel.Guild,
        _ => ChatChannel.Whisper,
    };

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
        NoteSelfSend(SendChannel(), _inputText);   // .Bubbles.cs — learn our identity from the echo
        _chat.Send(target, _inputText);            // game dispatcher — no packet construction, no local echo
        _inputText = string.Empty;                 // FieldBinding clears the composer field on the next poll
    }

    private void SendPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return;
        if (ResolveTarget() is not { } target) return;
        NoteSelfSend(SendChannel(), phrase);   // .Bubbles.cs
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
