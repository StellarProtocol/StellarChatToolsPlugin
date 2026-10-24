using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Per-channel send cooldown + the Send pill + the near-limit character counter. The cooldown is SERVER-driven
/// (Chat-System-Architecture.md "SEND COOLDOWN"): each send reply carries the channel's cooldown end (World/Current ≈ 5 s,
/// Party/Guild none), which the framework exposes as <see cref="IChat.TryGetSendCooldown"/> — the single source of truth
/// for the countdown and the blocking, including cooldowns started by sends from the game's own chat box. Because the
/// reply takes a round trip, a send also starts a short local OPTIMISTIC hold on its channel so the pill greys at once;
/// the reply (<see cref="IChat.SendResult"/>) ends the hold and the server value takes over. A refused send (e.g.
/// <see cref="ChatSendResult.ErrorTooFrequent"/>) flashes a short notice on the Send pill and puts the cleared draft back.
/// While the current target is blocked, every send path to it is refused with the draft kept: Enter / Send
/// (<see cref="DoSend"/>), quick phrases (<see cref="SendPhrase"/>) and stickers (<see cref="SendSticker"/>). No learned
/// timing — the lengths come from the server, the local timeouts below are fixed (Session Rule 12).
/// </summary>
public sealed partial class Plugin
{
    // Longest the optimistic hold may last without a reply (lost reply / no reply for that channel): the pill re-enables
    // and the server value (if any arrived) governs again.
    private const float OptimisticHoldSec = 3f;
    // How long the "Too fast" / "Not sent" notice replaces the Send label after a refused send.
    private const float RefusedFlashSec   = 2f;
    // A refused reply restores the draft only for a send this recent (a late reply must not resurrect old text).
    private const float DraftRestoreWindowSec = 10f;
    private const int   MaxCachedCooldownSec  = 120;   // "Ns" labels cached up to this; longer ones are formatted per poll

    private const int CounterShowAt   = 100;   // counter hidden below this many characters
    private const int CounterDangerAt = 130;   // orange (warning) from CounterShowAt, red from here up to the 140 cap
    private const float SendFillAlpha = 0.70f; // Send pill = the channel colour, stronger than the chip's 0.25 tint
    private const float DisabledFillAlpha = 0.16f;
    private static readonly ColorRgba CounterWarn   = new(1.00f, 0.62f, 0.20f);
    private static readonly ColorRgba CounterDanger = new(1.00f, 0.33f, 0.33f);

    // IFramework.TimeNow at which each channel's optimistic hold ends (absent = no send awaiting its reply). Same clock for
    // write and read, compared only as a difference (the epoch is arbitrary) — Framework-Interop-Foundation.md.
    private readonly Dictionary<ChatChannel, float> _awaitingReplyUntil = new();

    // The last composer draft we cleared on send, kept until its reply says whether to put it back.
    private string? _sentDraft;
    private ChatChannel _sentDraftChannel;
    private float _sentDraftAt;

    private float _refusedUntil = -1f;   // TimeNow until which the Send pill shows the refusal notice
    private bool _refusedTooFast;        // which notice: "Too fast" (ErrorTooFrequent) or the generic "Not sent"

    // Cached "Ns" labels (index = whole seconds) for the current language; rebuilt only when the template changes.
    private string? _cdTemplate;
    private readonly string?[] _cdLabels = new string?[MaxCachedCooldownSec + 1];
    private readonly string?[] _counterLabels = new string?[MaxMessageLength + 1];   // "n/140", built on first show

    // Server-reported seconds left on the current target's channel (0 = free). Any failure reads as free — the server
    // still refuses an early send, which lands in OnSendResult.
    private float CooldownRemaining()
    {
        try { return _chat.TryGetSendCooldown(SendChannel(), out var left) && left > 0 ? (float)left : 0f; }
        catch { return 0f; }
    }

    private bool AwaitingReply()
        => _awaitingReplyUntil.TryGetValue(SendChannel(), out var until) && until > _services.Framework.TimeNow;

    private bool CoolingDown() => AwaitingReply() || CooldownRemaining() > 0f;

    // Called right after a send was handed to the game (IChat.Send is fire-and-forget; the verdict arrives as SendResult).
    // `draft` = the composer text this send cleared (null for phrases / stickers, which never touch the draft).
    private void NoteSendDispatched(string? draft)
    {
        var ch = SendChannel();
        var now = _services.Framework.TimeNow;
        _awaitingReplyUntil[ch] = now + OptimisticHoldSec;
        _sentDraft = draft;
        _sentDraftChannel = ch;
        _sentDraftAt = now;
    }

    // IChat.SendResult (main thread). Only replies to framework-issued sends matter here — a send from the game's own chat
    // box already shows the game's error tip, and its cooldown reaches us through TryGetSendCooldown anyway.
    private void OnSendResult(ChatSendResult r)
    {
        if (!r.ViaFramework) return;
        _awaitingReplyUntil.Remove(r.Channel);   // the server's verdict is in: its cooldown (if any) governs now
        bool ours = _sentDraft != null && r.Channel == _sentDraftChannel;
        if (r.Ok)
        {
            if (ours) _sentDraft = null;
            return;
        }
        var now = _services.Framework.TimeNow;
        _refusedUntil = now + RefusedFlashSec;
        _refusedTooFast = r.ErrorCode == ChatSendResult.ErrorTooFrequent;
        // Put the optimistically-cleared draft back — unless the user has already started typing a new one.
        if (ours && now - _sentDraftAt <= DraftRestoreWindowSec && _inputText.Length == 0)
        {
            _inputText = _sentDraft!;   // FieldBinding re-seeds the field from Get() on the next poll
            _composerFocusSeq++;        // and hand the keyboard back to it (caret at the end)
        }
        _sentDraft = null;
    }

    private bool RefusedFlashing() => _refusedUntil >= 0f && _services.Framework.TimeNow < _refusedUntil;

    // The Send pill is live only with something to send, a resolvable target and no cooldown / pending reply.
    // Allocation-free (polled every refresh). Also off while the draft is being translated or is over the 140 cap (a long
    // translation is kept whole, .TranslateOut.cs — the user trims it first).
    private bool CanSendNow()
        => !string.IsNullOrWhiteSpace(_inputText) && _inputText.Length <= MaxMessageLength && !TrOutBusy()
           && HasSendTarget() && !CoolingDown();

    private string SendLabel()
    {
        if (RefusedFlashing()) return _loc.T(_refusedTooFast ? "chat.composer.tooFast" : "chat.composer.notSent");
        float left = CooldownRemaining();
        // Waiting for the reply (optimistic hold) keeps the plain label, greyed: a channel without a cooldown (Party/Guild)
        // answers within a round trip, and flashing a "sending" label there would only flicker.
        if (left <= 0f) return _loc.T("chat.send");
        var tpl = _loc.T("chat.composer.cooldown");
        if (!string.Equals(tpl, _cdTemplate, StringComparison.Ordinal))
        {
            _cdTemplate = tpl;
            Array.Clear(_cdLabels, 0, _cdLabels.Length);
        }
        int s = Math.Max(1, (int)MathF.Ceiling(left));
        return s < _cdLabels.Length ? _cdLabels[s] ??= SafeFormat(tpl, s) : SafeFormat(tpl, s);
    }

    // Send pill colours: the target channel's colour as a strong fill with a contrast-floored label (lifted toward
    // white, or black on a light channel colour); greyed while it can't send; the refusal notice in the danger red.
    private ColorRgba? SendFill()
        => CanSendNow() ? TabBaseColor(TargetTab()) with { A = SendFillAlpha }
                        : _services.Theme.Colors.MenuMuted with { A = DisabledFillAlpha };

    private ColorRgba? SendText()
    {
        if (RefusedFlashing()) return EnsureContrast(CounterDanger, TabTrackColor(), TabActiveMinContrast);
        if (!CanSendNow()) return _services.Theme.Colors.MenuMuted;
        var fill = Over(TabBaseColor(TargetTab()) with { A = SendFillAlpha }, TabTrackColor());
        return EnsureContrast(new ColorRgba(1f, 1f, 1f), fill, TabActiveMinContrast);
    }

    // Clicks are always wired; a disabled pill is simply a no-op (DoSend re-checks everything).
    private HudElement BuildSendPill() => BuildPill(SendLabel, SendText, SendFill, DoSend);

    // ── Counter ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Hidden until the draft is near the cap; same raw-length count as before (a <sprite=N> tag counts its characters,
    // matching the game's own 140 limit on the raw text).
    private bool CounterVisible() => _inputText.Length >= CounterShowAt;

    // Over the cap (only a kept-whole translation gets there) the real count shows, e.g. "152/140", in the red.
    private string CharCounter()
    {
        int n = _inputText.Length;
        if (n <= MaxMessageLength) return _counterLabels[n] ??= n + "/" + MaxMessageLength;
        if (n != _counterOverN) { _counterOverN = n; _counterOver = n + "/" + MaxMessageLength; }
        return _counterOver;
    }

    private int _counterOverN = -1;
    private string _counterOver = "";

    private ColorRgba? CounterColor()
    {
        var c = _inputText.Length >= CounterDangerAt ? CounterDanger : CounterWarn;
        return EnsureContrast(c, _services.Theme.Colors.MenuBackground with { A = 1f }, TabActiveMinContrast);
    }
}
