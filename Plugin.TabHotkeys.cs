using System;
using Stellar.Abstractions.Domain;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.ChatTools;

/// <summary>
/// Previous / next read-tab hotkeys. Declared UNBOUND (no suggested default — the user binds them in Settings → Hotkeys).
/// The focus/unfocus toggle is the exception: it defaults to Return (owner request 2026-10-27), like the game's own chat
/// key. Being IsActive-gated, Return keeps its game meaning whenever the chat window is closed.
/// They cycle All → World → Current → Party → Guild → Whisper → System and wrap, through the SAME path as a tab click
/// (<see cref="SelectTabFromUser"/>), so leaving / entering Whisper behaves identically. A third, "focus/unfocus chat
/// input", is a TOGGLE: without the keyboard it puts it in the composer through the same path as the emoji / sticker
/// pickers (<see cref="FocusComposer"/>, a no-op on Whisper with no peer picked) — the triggering key is not typed into
/// the field: the focus request only activates it on a later frame (UGuiTextInput.FocusRequest), after that key-down
/// event has already been consumed. With the composer holding the keyboard it leaves it like a click-away / Esc
/// (<see cref="DefocusComposer"/>) — but on Enter (the default binding) it shares the press with the field's own submit,
/// so it follows the game's Enter rules instead: send the draft, then leave on release; exactly one owner per press
/// (.ComposerEnter.cs).
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
            new HotkeyAction("chattools.input.focus", _loc.T("chat.hotkey.focusInput"), SuggestedDefault: new KeyBinding(StellarKeyCode.Return))
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

    // One press, one action. Not focused → focus the composer (a no-op on Whisper with no peer picked). Focused and the
    // press is Enter (the default binding) → the Enter rules in .ComposerEnter.cs: unless the field's submit already
    // handled this press, send the draft (or nothing, if empty) and leave once Enter is released — a refused send keeps
    // the draft and the focus. Focused and bound to some other key → a plain toggle: leave at once.
    private void ToggleComposerFocus()
    {
        if (!ComposerHasKeyboard()) { FocusComposer(); return; }   // .Composer.cs
        if (!EnterHeld()) { DefocusComposer(); return; }
        if (EnterAlreadyHandledBySubmit()) return;                  // .ComposerEnter.cs
        HandleEnterFromHotkey();
    }

    // The framework's InputElement has no focus readback or defocus API for plugins, so ask Unity directly: the composer
    // has the keyboard iff the EventSystem's selected object is a FOCUSED input field (TMP for the RichEmoji composer;
    // legacy InputField if the framework fell back from TMP) inside our main window. The composer is the only text field
    // in chattools.main (the friend-picker search and settings fields live in their own windows), so "a focused field
    // under chattools.main" is the composer — no handle to it is needed (the framework exposes none). Never throws out of
    // the hotkey / tick: any Unity/interop failure reads as "not focused".
    private bool ComposerHasKeyboard() => FindFocusedComposer(out _, out _, out _);

    // Defocus = the framework's own Esc path (UGuiTextInput.Defocus): DeactivateInputField + clear the selection. It
    // sticks: the field's UGuiTextInput.Tick sees the focus-lost edge and restores the cursor, KeyboardInputGate
    // releases the game keyboard (IsFocused only stays true through a SUBMIT grace, which a deactivation doesn't arm),
    // and nothing refocuses — FocusRequest fires only when _composerFocusSeq changes (we don't bump it here) and the
    // composer has no AutoFocus (opening the window never grabs the keyboard). For an Enter press this is deferred to the
    // key's release (.ComposerEnter.cs), so the lifted gate never meets a held Enter. No-op when not focused; never throws.
    private void DefocusComposer()
    {
        try
        {
            if (!FindFocusedComposer(out var es, out var tmp, out var legacy)) return;
            if (tmp != null) tmp.DeactivateInputField(false);
            else legacy!.DeactivateInputField();
            es!.SetSelectedGameObject(null);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[ChatTools] focus hotkey: defocus failed: {ex.Message}");
        }
    }

    private bool FindFocusedComposer(out EventSystem? es, out TMP_InputField? tmp, out InputField? legacy)
    {
        es = null; tmp = null; legacy = null;
        try
        {
            es = EventSystem.current;
            if (es == null) return false;
            var go = es.currentSelectedGameObject;
            if (go == null || !IsUnderMainWindow(go.transform)) return false;
            tmp = go.GetComponent<TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            tmp = null;
            legacy = go.GetComponent<InputField>();
            if (legacy != null && legacy.isFocused) return true;
            legacy = null;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[ChatTools] focus hotkey: focus check failed: {ex.Message}");
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
