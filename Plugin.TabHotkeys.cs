using System;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// Previous / next read-tab hotkeys. Declared UNBOUND (no suggested default — the user binds them in Settings → Hotkeys).
/// They cycle All → World → Current → Party → Guild → Whisper → System and wrap, through the SAME path as a tab click
/// (<see cref="SelectTabFromUser"/>), so leaving / entering Whisper behaves identically. A third, "focus chat input",
/// puts the keyboard in the composer through the same path as the emoji / sticker pickers (<see cref="FocusComposer"/>,
/// a no-op on Whisper with no peer picked). Its triggering key is not typed into the field: the focus request only
/// activates the field on a later frame (UGuiTextInput.FocusRequest), after that key-down event has already been consumed.
///
/// Gated by <c>HotkeyAction.IsActive</c> (framework 2.26.0): while the chat window is not shown the action is dormant —
/// it neither fires nor is held back from the game, so a key bound here (say Tab) keeps its game meaning whenever the
/// chat is closed, even with "Don't pass hotkeys through to the game" on. While shown it behaves like F12: it fires even
/// while the composer has the keyboard (the framework hotkey poll reads raw Unity input and has no text-focus gate), so a
/// bare letter would also be typed — bind a modifier chord or a non-text key.
///
/// The labels are registered in the CURRENT language: a hotkey description can't be re-localized live.
/// </summary>
public sealed partial class Plugin
{
    private IHotkeyAction? _prevTabHotkey, _nextTabHotkey, _focusInputHotkey;

    private void InitTabHotkeys()
    {
        _prevTabHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("chattools.tab.prev", _loc.T("chat.hotkey.prevTab"), SuggestedDefault: null)
                { IsActive = TabHotkeysActive },
            () => CycleTab(-1));
        _nextTabHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("chattools.tab.next", _loc.T("chat.hotkey.nextTab"), SuggestedDefault: null)
                { IsActive = TabHotkeysActive },
            () => CycleTab(+1));
        _focusInputHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("chattools.input.focus", _loc.T("chat.hotkey.focusInput"), SuggestedDefault: null)
                { IsActive = TabHotkeysActive },
            FocusComposer);
    }

    // Polled by the framework every tick — keep it a plain property read. IsShown (not Visible): false while the window
    // is still mounting / suppressed by ShouldRender (loading screen, outside World), when the key should go to the game.
    private bool TabHotkeysActive() => _window.IsShown;

    private void CycleTab(int dir)
    {
        int i = Array.IndexOf(Tabs, _activeTab);
        if (i < 0) i = 0;
        SelectTabFromUser(Tabs[(i + dir + Tabs.Length) % Tabs.Length]);
    }

    private void DisposeTabHotkeys()
    {
        _prevTabHotkey?.Dispose();
        _nextTabHotkey?.Dispose();
        _focusInputHotkey?.Dispose();
    }
}
