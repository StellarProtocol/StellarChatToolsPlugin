using System;
using Stellar.Abstractions.Domain;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.ChatTools;

/// <summary>
/// Previous / next read-tab hotkeys. Declared UNBOUND (no suggested default — the user binds them in Settings → Hotkeys).
/// They cycle All → World → Current → Party → Guild → Whisper → System and wrap, through the SAME path as a tab click
/// (<see cref="SelectTabFromUser"/>), so leaving / entering Whisper behaves identically. A third, "focus/unfocus chat
/// input", is a TOGGLE: with the composer holding the keyboard it DEFOCUSES it exactly like a click-away / Esc (see
/// <see cref="TryDefocusComposer"/>); otherwise it puts the keyboard in the composer through the same path as the emoji /
/// sticker pickers (<see cref="FocusComposer"/>, a no-op on Whisper with no peer picked). Its triggering key is not typed
/// into the field: the focus request only activates the field on a later frame (UGuiTextInput.FocusRequest), after that
/// key-down event has already been consumed.
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
    private const string MainWindowId = "chattools.main";   // = the WindowSpec Id (Plugin.cs) = the window root's GameObject name

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
            ToggleComposerFocus);
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

    private void ToggleComposerFocus()
    {
        if (!TryDefocusComposer()) FocusComposer();   // .Composer.cs
    }

    // The framework's InputElement has no focus readback or defocus API for plugins, so ask Unity directly: the composer
    // has the keyboard iff the EventSystem's selected object is a FOCUSED input field (TMP for the RichEmoji composer;
    // legacy InputField if the framework fell back from TMP) inside our main window. The composer is the only text field
    // in chattools.main (the friend-picker search and settings fields live in their own windows), so "a focused field
    // under chattools.main" is the composer — no handle to it is needed (the framework exposes none).
    // Defocus = the framework's own Esc path (UGuiTextInput.Defocus): DeactivateInputField + clear the selection. It
    // sticks: the field's UGuiTextInput.Tick sees the focus-lost edge and restores the cursor, KeyboardInputGate
    // releases the game keyboard (IsFocused only stays true through a SUBMIT grace, which a deactivation doesn't arm),
    // and nothing refocuses — FocusRequest fires only when _composerFocusSeq changes (we don't bump it here) and the
    // composer has no AutoFocus (opening the window never grabs the keyboard). Never throws out of the hotkey: any
    // Unity/interop failure just falls through to the focus branch.
    private bool TryDefocusComposer()
    {
        try
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var go = es.currentSelectedGameObject;
            if (go == null || !IsUnderMainWindow(go.transform)) return false;

            var tmp = go.GetComponent<TMP_InputField>();
            if (tmp != null && tmp.isFocused)
            {
                tmp.DeactivateInputField(false);
                es.SetSelectedGameObject(null);
                return true;
            }
            var legacy = go.GetComponent<InputField>();
            if (legacy != null && legacy.isFocused)
            {
                legacy.DeactivateInputField();
                es.SetSelectedGameObject(null);
                return true;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[ChatTools] focus hotkey: defocus failed: {ex.Message}");
        }
        return false;
    }

    // The window root is named after its WindowSpec Id (StellarWindowCanvas/chattools.main/…), so walk up to it.
    private static bool IsUnderMainWindow(Transform? t)
    {
        for (; t != null; t = t.parent)
            if (t.name == MainWindowId) return true;
        return false;
    }

    private void DisposeTabHotkeys()
    {
        _prevTabHotkey?.Dispose();
        _nextTabHotkey?.Dispose();
        _focusInputHotkey?.Dispose();
    }
}
