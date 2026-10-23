using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The "↓ N new messages" bar: while the reader is scrolled UP in the log, messages that land in the active tab
/// are counted, and a centred accent pill directly below the log shows the count; clicking it jumps to the newest
/// message. The bar hides as soon as the view is back at the bottom (click OR a manual scroll down), on a tab
/// switch, and on the ResetScroll re-pin. Our OWN sends never count — they jump to the bottom instead, the common
/// chat-app behaviour. "At the bottom" comes from the framework list itself
/// (<see cref="VirtualListElement.OnAtBottomChanged"/>, the SAME decision its StickToBottom pin makes), so the bar
/// can never disagree with the auto-follow.
/// </summary>
public sealed partial class Plugin
{
    private bool _atBottom = true;     // last at-bottom state reported by the log list (it starts pinned to bottom)
    private int _newBelow;             // messages shown in the active tab that arrived while scrolled up
    private int _scrollToBottomSeq;    // VirtualListElement.ScrollToBottomRequest token — bump to jump + re-pin

    // Label cache: the bar's text Func is polled every window refresh, so format only when the count (or the
    // language) actually changes instead of allocating a fresh string each poll.
    private int _newBelowLabelN = -1;
    private string? _newBelowLabelLang;
    private string _newBelowLabel = string.Empty;

    // VirtualListElement.OnAtBottomChanged (.Log.cs): back at the bottom → everything below has been seen.
    private void OnLogAtBottomChanged(bool atBottom)
    {
        _atBottom = atBottom;
        if (atBottom) _newBelow = 0;
    }

    // Bar click + our own sends. Optimistically mark at-bottom now: the list honours the request on its next apply
    // (and reports true then), but a message — e.g. our own echo, before self-identity is learned — can arrive
    // in between and must not re-raise the bar we just dismissed.
    private void JumpLogToBottom()
    {
        _scrollToBottomSeq++;
        _atBottom = true;
        _newBelow = 0;
    }

    // From NoteIncoming (.Tabs.cs), AFTER its history/IsUnrenderable bail — so only rows the log will actually SHOW
    // reach here. Count it only if it lands in the active tab's filter (same test EnsureFiltered uses: All = no
    // filter) and isn't our own message.
    private void NoteNewBelow(ChatMessage msg)
    {
        if (_atBottom) return;
        if (msg.Channel == ChatChannel.System) return;   // notices ("Obtained …") are ambient: they never raise the bar
        if (TabChannel(_activeTab) is { } ch && ch != msg.Channel) return;
        if (_activeTab == ChatTab.Whisper && PeerOf(msg) != _selectedPeer) return;   // only the open conversation is shown
        if (IsSelf(msg)) return;   // .Bubbles.cs — own sends jump to bottom (JumpLogToBottom), never count
        _newBelow++;
    }

    private void ResetNewBelow() => _newBelow = 0;

    private string NewBelowLabel()
    {
        var lang = _loc.Language;
        if (_newBelow == _newBelowLabelN && lang == _newBelowLabelLang) return _newBelowLabel;
        _newBelowLabelN = _newBelow; _newBelowLabelLang = lang;
        _newBelowLabel = _loc.TFormat(_newBelow == 1 ? "chat.newBelow.one" : "chat.newBelow.many", _newBelow);
        return _newBelowLabel;
    }

    // Sits in BuildRoot's column BETWEEN the log and the composer (.Window.cs). No space is reserved for it: showing
    // it shrinks the flex log viewport from the BOTTOM edge only — the list's content is top-anchored, so the rows
    // on screen stay put (the reader is scrolled up, so the list's sticky at-bottom latch is false and preserves
    // their position across the height change). It only ever hides once the view is at the bottom (or on a tab
    // switch, which re-pins to bottom), where the latch holds the bottom pin while the viewport grows back.
    // Pill styling mirrors the active tab pill (.Tabs.cs): accent BubbleElement fill + bright text, no Selected arg
    // (the Selectable's own selected fill would double the pill). The Row centres it at its content width.
    private HudElement BuildNewBelowBar() => new ConditionalElement(() => _newBelow > 0 && !_atBottom,
        new RowElement(new HudElement[]
        {
            new SelectableElement(
                new BubbleElement(
                    new TextElement(NewBelowLabel, () => _services.Theme.Colors.MenuText, Align: TextAlign.Center))
                {
                    CornerRadius = 8f, PaddingX = 12f, PaddingY = 4f,
                    FillColorFn  = () => _services.Theme.Colors.MenuAccent,
                },
                OnClick: JumpLogToBottom),
        }, Justify: RowJustify.Center));
}
