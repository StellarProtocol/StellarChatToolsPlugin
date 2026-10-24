using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The send composer: ONE row of equal-height (28) controls — a channel-coloured target chip (.SendTarget.cs), ＋ (quick
/// phrases), ☺ (stickers/emoji), the text field (placeholder follows the target), a counter shown only near the 140 cap,
/// and a channel-coloured Send that greys out while empty or cooling down (.SendCooldown.cs). Settings live in their own
/// window behind the title-bar gear (.Settings.cs). The target defaults to the active read-tab's channel but is overridable. Whisper is per
/// PERSON: target 4 whispers the conversation selected in the Whisper tab (.Whisper.cs) — the chip then reads "→ Name".
/// No local echo — sends round-trip via MessageReceived (and teach us our own identity, see Bubbles).
/// </summary>
public sealed partial class Plugin
{
    private const int MaxQuickPhrases = 12;
    private const int MaxMessageLength = 140;   // mirrors the game's 0/140 counter; soft-capped on input
    private const int ComposerEmojiFontSize = 14;   // ☺ pill sprite: ≈14 wide, line box < the 20 pill strut

    // 0=Say, 1=World, 2=Party, 3=Guild, 4=Whisper (to _selectedPeer, .Whisper.cs).
    private int _sendTargetIndex;

    private string _inputText = string.Empty;
    // Bumped per emoji-picker click; the composer InputElement.FocusRequest polls it and refocuses the field (caret at
    // the end) on each change, so the user keeps typing after picking. Stickers send at once — they don't bump it.
    private int _composerFocusSeq;
    private bool _quickOpen;
    private string _quickEditText = string.Empty;
    private List<string> _quickPhrases = new();   // user-configurable; populated by LoadConfig

    // Default the composer target to the opened read-tab's channel (overridable afterwards). In-memory only (not
    // persisted — only an explicit target-button click persists the default). Whisper mode lives ONLY in the Whisper
    // tab: a whisper that kept going after switching to All was an accidental-whisper trap, so leaving Whisper always
    // lands on a channel. All/System have no channel of their own, so leaving Whisper for them falls back to the SAVED
    // default (never overwritten here); any other selection is left untouched there. _selectedPeer is kept, so coming
    // back to Whisper re-targets the same conversation.
    private void OnTabOpened(ChatTab tab)
    {
        switch (tab)
        {
            case ChatTab.Current: _sendTargetIndex = 0; break;
            case ChatTab.World:   _sendTargetIndex = 1; break;
            case ChatTab.Party:   _sendTargetIndex = 2; break;
            case ChatTab.Guild:   _sendTargetIndex = 3; break;
            case ChatTab.Whisper: _sendTargetIndex = 4; break;
            default: if (_sendTargetIndex == 4) _sendTargetIndex = SavedSendTarget(); break;   // .Config.cs
        }
    }

    private HudElement BuildComposer()
    {
        return new ColumnElement(new HudElement[]
        {
            // ONE composer row: [target chip ▾] [＋] [☺] [text field …] [n/140 near the cap] [Send]. Every control is
            // a 28-tall pill (.SendTarget.cs BuildPill) = the field's own 28 box, and the Row centres them vertically.
            // The field sits in a weighted cell so the dark rounded input takes all the row's leftover width.
            new RowElement(new HudElement[]
            {
                BuildTargetChip(),   // .SendTarget.cs — channel-coloured chip, click = target picker menu
                // ＋ / ☺ are 28×28 squares (.SendTarget.cs BuildIconPill). ASCII "+" (bold, centred) — the fullwidth "＋"
                // was ~19 wide and made the pill 47; it can't fit the 16-unit glyph slot of a 28 square.
                BuildIconPill(new TextElement(() => "+", () => _services.Theme.Colors.MenuText, Emphasis: true, Align: TextAlign.Center, NoWrap: true),
                    () => ToolPillFill(_quickOpen), ToggleQuickPhrases),
                // Sticker (picture-emoji) picker toggle — all-icon popover, mutually exclusive with ＋ (.Stickers.cs). The
                // old "☺" glyph is missing from every in-game face (it drew an EMPTY pill), so the icon is the game's own
                // chat emoji <sprite=1> through the RichEmoji TMP path — the same tag + path as the picker's emoji tab icon
                // (.Emoji.cs), colour by nature, so it reads on the dark pill. FontSize sizes the sprite to the 16 slot and
                // keeps its line box under the 20 strut (TMP honours FontSize on this surface, unlike plain Text).
                BuildIconPill(new TextElement(() => EmojiTags[0], FontSize: ComposerEmojiFontSize, Align: TextAlign.Center, NoWrap: true) { RichEmoji = true },
                    () => ToolPillFill(_stickerOpen), ToggleStickerPicker),
                new CellElement(new InputElement(() => _inputText, s => { SetInput(s); DoSend(); }, 160f, OnChange: SetInput)
                    { AutoFocus = true, RichEmoji = true, FocusRequest = () => _composerFocusSeq, Placeholder = ComposerPlaceholder }, Weight: 1f),
                new ConditionalElement(CounterVisible, new TextElement(CharCounter, CounterColor, NoWrap: true)),   // .SendCooldown.cs
                BuildSendPill(),     // .SendCooldown.cs — channel-coloured; greyed + "Ns" while empty / cooling down
            // Min-width row (any language): the input is pinned at 160 (typing never grows it); the chip's label, the
            // Send/"Ns" label and the near-cap counter move its natural width by a few tens of units — still well
            // below the tab row's requirement, which wins.
            }, Gap: 4f) { ConstrainsWindowMinWidth = true },

            // Quick-phrases popover: click a phrase to send it to the current target, then close.
            new ConditionalElement(() => _quickOpen, BuildQuickPopover()),
            // Sticker picker popover (.Stickers.cs) — sibling of the quick-phrase one; only one is open at a time.
            new ConditionalElement(() => _stickerOpen, BuildStickerPopover()),
        }, Gap: 4f);
    }

    // ＋ quick-phrase toggle — closes the sticker picker when it opens (mutually exclusive popovers).
    private void ToggleQuickPhrases()
    {
        _quickOpen = !_quickOpen;
        if (_quickOpen) _stickerOpen = false;
    }

    // ＋ / ☺ pill fill: the recessed track tint at rest (the tab strip's track), the accent pill while its popover is open.
    private ColorRgba? ToolPillFill(bool open) => open ? _services.Theme.Colors.MenuAccent with { A = TabPillAlpha } : TabTrackFill;

    // A pick from the target menu (.SendTarget.cs) — index-based like the old dropdown, and persisted as the default.
    // Whisper needs a PERSON, so picking it opens the Whisper tab (pick or start a conversation there) and is never the
    // persisted default. Picking a channel while the Whisper tab is open leaves it for that channel's tab — the composer
    // and the log never disagree about where you are talking.
    private void OnPickTarget(int index)
    {
        if (index < 0 || index >= TargetTabs.Length) return;
        if (index == 4) { SetActiveTab(ChatTab.Whisper); _sendTargetIndex = 4; return; }
        if (_activeTab == ChatTab.Whisper) SetActiveTab(TargetTabs[index]);
        _sendTargetIndex = index;
        SaveSendTarget();
    }

    // Soft-cap the field to the game's 140 chars; the FieldBinding re-seeds the field from _inputText on the
    // next poll, so a truncation shows immediately.
    private void SetInput(string s) => _inputText = s.Length > MaxMessageLength ? s.Substring(0, MaxMessageLength) : s;

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
            // Close only on a real send — a phrase refused by the cooldown leaves the popover open to retry.
            slots[idx] = new ButtonElement(() => QuickPhrase(idx), () => { if (SendPhrase(QuickPhrase(idx))) _quickOpen = false; });
        }
        return new PanelElement(new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _quickPhrases.Count == 0,
                new TextElement(() => _loc.T("chat.quick.empty"), () => _services.Theme.Colors.MenuMuted)),
            new ListElement(() => _quickPhrases.Count, slots),
        }, Gap: 2f));
    }

    private string QuickPhrase(int i) => i >= 0 && i < _quickPhrases.Count ? _quickPhrases[i] : "";

    // Resolve the current send target. Whisper goes to the selected conversation (.Whisper.cs); null means "cannot send"
    // (no conversation picked — the placeholder already says so), so the caller just no-ops.
    private ChatTarget? ResolveTarget() => _sendTargetIndex switch
    {
        0 => ChatTarget.Say,
        1 => ChatTarget.World,
        2 => ChatTarget.Party,
        3 => ChatTarget.Guild,
        4 => _selectedPeer != 0 ? ChatTarget.Whisper(_selectedPeer) : null,
        _ => null,
    };

    // Allocation-free "is there someone to send to" (polled every refresh by CanSendNow); mirrors ResolveTarget.
    private bool HasSendTarget() => _sendTargetIndex != 4 || _selectedPeer != 0;

    // Enter and the Send pill. A cooling-down target (.SendCooldown.cs) refuses the send and KEEPS the draft: the field's
    // Enter strip already left the text in place, and _inputText is only cleared on a real send.
    private void DoSend()
    {
        if (string.IsNullOrWhiteSpace(_inputText)) return;
        if (CoolingDown()) return;                 // .SendCooldown.cs
        if (ResolveTarget() is not { } target) return;
        NoteSelfSend(SendChannel(), _inputText);   // .Bubbles.cs — learn our identity from the echo
        _chat.Send(target, _inputText);            // game dispatcher — no packet construction, no local echo
        NoteSendDispatched(_inputText);            // .SendCooldown.cs — grey Send until the reply; a refusal restores the draft
        JumpLogToBottom();                         // .NewBelow.cs — own send: follow it to the bottom, like chat apps
        _inputText = string.Empty;                 // FieldBinding clears the composer field on the next poll
    }

    // True when the phrase was actually sent (false: blank, no target, or the target is cooling down).
    private bool SendPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        if (CoolingDown()) return false;       // .SendCooldown.cs
        if (ResolveTarget() is not { } target) return false;
        NoteSelfSend(SendChannel(), phrase);   // .Bubbles.cs
        _chat.Send(target, phrase);
        NoteSendDispatched(null);              // .SendCooldown.cs — a phrase never touches the draft
        JumpLogToBottom();   // .NewBelow.cs
        return true;
    }

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
