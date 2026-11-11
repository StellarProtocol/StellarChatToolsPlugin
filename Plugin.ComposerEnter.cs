using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Enter in the composer, "like the game": with text, Enter SENDS and then leaves the input (the keyboard goes back to
/// the game); with an empty / whitespace-only box it just leaves; a refused send (cooling down, translating, over the
/// cap, no target, not delivered — <see cref="DoSend"/> keeps the draft) keeps the draft AND the focus for a retry.
///
/// One Enter press reaches TWO handlers, in no fixed order: the composer field's submit (<see cref="OnComposerSubmit"/>)
/// and the focus/unfocus hotkey, which defaults to Return (.TabHotkeys.cs <see cref="ToggleComposerFocus"/>).
/// Framework facts behind the ordering (2.30.0):
/// - The field submits from TMP's own key handling (UGuiTextInput default mode: Enter appends '\n', onValueChanged strips
///   it and submits; the field is NOT deactivated, so it keeps focus by design). That runs on the frame TMP pops the
///   KeyDown event — normally the key-down frame, later when the event is delayed (IME, streaming).
/// - The hotkey is NOT a GetKeyDown read: UnityInputGateway.TickPoll derives the edge from the key LEVEL on the throttled
///   framework tick (StellarTicker InvokeRepeating, ~30 Hz, after Update), so it fires on the first tick that sees the key
///   held — possibly BEFORE TMP pops the event (the old defocus then ate the Enter: text kept, box unfocused, the bug),
///   possibly AFTER, and never for a tap shorter than a tick (down+up in one streamed frame).
/// So whichever handler sees the press FIRST owns it, and the other one stands down:
/// - submit first → it opens the press (<see cref="_enterPressOpen"/>); a hotkey edge while it is open is the same press.
/// - hotkey first → it stamps <see cref="_enterHotkeyAt"/>; a submit inside <see cref="EnterDupWindowSec"/> is the same
///   press (already sent, or already refused) and is dropped BEFORE SetInput, so the field's stale pre-send text can't
///   come back into the draft and send twice.
/// The defocus itself waits for Enter to be RELEASED (<see cref="TickComposerEnter"/>): leaving while Enter is still down
/// would lift KeyboardInputGate (sampled on the same tick from the field's focus) with the key held, and the game would
/// then see an Enter press and open its own chat input. A rebound focus hotkey (not Return) never sees an Enter press, so
/// the submit path alone gives the same send-then-leave.
/// </summary>
public sealed partial class Plugin
{
    // A submit this soon after the hotkey handled an in-composer Enter is that same press reaching TMP late (IME/streamed
    // event delay). Generous vs. the ≤1-tick gap; a human can't land a second Enter AND have it reach only the field (the
    // hotkey would fire for it) inside it.
    private const float EnterDupWindowSec = 0.5f;
    // An open press older than this is stale for the hotkey's "same press?" test (its release was never seen, e.g. Enter
    // up and down again between two plugin ticks). The real gap is ≤ one tick plus a hitch.
    private const float EnterPressMaxSec = 0.5f;

    private float _enterHotkeyAt = -1f;   // unscaledTime the hotkey last handled an Enter in the focused composer
    private bool _enterPressOpen;         // an Enter press was handled and its release not seen yet
    private float _enterPressAt;          // unscaledTime that press was opened
    private bool _leaveOnRelease;         // ...and the composer should give the keyboard back once Enter is up

    private static bool EnterHeld() => Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

    // The composer InputElement's submit (Enter, or a pasted newline — both mean "send" in the field, and both now leave
    // after a successful send).
    private void OnComposerSubmit(string s)
    {
        if (_enterHotkeyAt >= 0f && Time.unscaledTime - _enterHotkeyAt < EnterDupWindowSec) return;   // the hotkey owned it
        SetInput(s);
        OpenEnterPress(SendOrLeave());
    }

    // The hotkey's half, called from ToggleComposerFocus with the composer focused and Enter down. True when the submit
    // already handled this press (the edge fired after it) — the caller then does nothing.
    private bool EnterAlreadyHandledBySubmit()
        => _enterPressOpen && Time.unscaledTime - _enterPressAt < EnterPressMaxSec;

    private void HandleEnterFromHotkey()
    {
        _enterHotkeyAt = Time.unscaledTime;
        OpenEnterPress(SendOrLeave());
    }

    // true = leave the input (sent, or nothing to send); false = keep the draft and the focus (a refused send).
    private bool SendOrLeave() => string.IsNullOrWhiteSpace(_inputText) || DoSend();

    private void OpenEnterPress(bool leave)
    {
        _enterPressOpen = true;
        _enterPressAt = Time.unscaledTime;
        _leaveOnRelease = leave;
    }

    // Framework.Update (the plugin tick: Band 2, ahead of the same beat's Band-3 hotkey dispatch, reading the same key
    // level). Idle = one bool test; no allocation. Closes the press once Enter is up and gives the keyboard back if it
    // was asked to.
    private void TickComposerEnter(float _)
    {
        if (!_enterPressOpen || EnterHeld()) return;
        _enterPressOpen = false;
        if (!_leaveOnRelease) return;
        _leaveOnRelease = false;
        DefocusComposer();   // .TabHotkeys.cs — no-op if the composer no longer has the keyboard; never throws
    }
}
