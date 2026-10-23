using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Per-channel send cooldown + the Send pill + the near-limit character counter. After a send, World and Current(Say)
/// each lock for a fixed number of seconds, tracked SEPARATELY (a World send never delays a Say send). Guild, Party and
/// Whisper have none. While the current target is cooling, every send path to it is refused with the draft kept: Enter /
/// Send (<see cref="DoSend"/>), quick phrases (<see cref="SendPhrase"/>) and stickers (<see cref="SendSticker"/>); the
/// Send pill greys out and shows the remaining seconds. Fixed defaults only (no learned timing — Session Rule 12).
/// </summary>
public sealed partial class Plugin
{
    // ⚙ The ONE place the cooldown lengths live (seconds). Kept as constants so they can be adjusted to the game's own
    // per-channel limits once confirmed from its tables.
    private const float WorldSendCooldownSec = 5f;
    private const float SaySendCooldownSec   = 5f;
    // Size of the cached "Ns" label table: the longer cooldown, rounded up (follows the two constants above).
    private const int   MaxCooldownLabelSec  = (int)(WorldSendCooldownSec > SaySendCooldownSec ? WorldSendCooldownSec : SaySendCooldownSec) + 1;

    private const int CounterShowAt   = 100;   // counter hidden below this many characters
    private const int CounterDangerAt = 130;   // orange (warning) from CounterShowAt, red from here up to the 140 cap
    private const float SendFillAlpha = 0.70f; // Send pill = the channel colour, stronger than the chip's 0.25 tint
    private const float DisabledFillAlpha = 0.16f;
    private static readonly ColorRgba CounterWarn   = new(1.00f, 0.62f, 0.20f);
    private static readonly ColorRgba CounterDanger = new(1.00f, 0.33f, 0.33f);

    // IFramework.TimeNow when each channel's cooldown ends (0 = never sent → already over). Same clock for write and
    // read, compared only as a difference (the epoch is arbitrary) — Framework-Interop-Foundation.md.
    private float _worldCdUntil, _sayCdUntil;

    // Cached "Ns" labels (index = whole seconds) for the current language; rebuilt only when the template changes.
    private string? _cdTemplate;
    private readonly string?[] _cdLabels = new string?[MaxCooldownLabelSec + 1];
    private readonly string?[] _counterLabels = new string?[MaxMessageLength + 1];   // "n/140", built on first show

    private static float CooldownSecFor(int target) => target switch
    {
        0 => SaySendCooldownSec,
        1 => WorldSendCooldownSec,
        _ => 0f,   // Party / Guild / Whisper: no cooldown
    };

    private float CooldownRemaining()
    {
        float until = _sendTargetIndex switch { 0 => _sayCdUntil, 1 => _worldCdUntil, _ => 0f };
        float left = until - _services.Framework.TimeNow;
        return left > 0f ? left : 0f;
    }

    private bool CoolingDown() => CooldownRemaining() > 0f;

    // Called right after a send was handed to the game. IChat.Send is fire-and-forget (no ack), so "sent" = dispatched.
    private void StartSendCooldown()
    {
        float sec = CooldownSecFor(_sendTargetIndex);
        if (sec <= 0f) return;
        float until = _services.Framework.TimeNow + sec;
        if (_sendTargetIndex == 0) _sayCdUntil = until; else _worldCdUntil = until;
    }

    // The Send pill is live only with something to send, a resolvable target and no cooldown. Allocation-free (polled
    // every refresh): the whisper check mirrors ResolveTarget without building a ChatTarget.
    private bool CanSendNow()
        => !string.IsNullOrWhiteSpace(_inputText)
           && (_sendTargetIndex != 4 || _replyTarget is { SenderId: not 0 })
           && !CoolingDown();

    private string SendLabel()
    {
        float left = CooldownRemaining();
        if (left <= 0f) return _loc.T("chat.send");
        var tpl = _loc.T("chat.composer.cooldown");
        if (!string.Equals(tpl, _cdTemplate, StringComparison.Ordinal))
        {
            _cdTemplate = tpl;
            Array.Clear(_cdLabels, 0, _cdLabels.Length);
        }
        int s = Clamp((int)MathF.Ceiling(left), 1, _cdLabels.Length - 1);
        return _cdLabels[s] ??= SafeFormat(tpl, s);
    }

    // Send pill colours: the target channel's colour as a strong fill with a contrast-floored label (lifted toward
    // white, or black on a light channel colour); greyed while it can't send.
    private ColorRgba? SendFill()
        => CanSendNow() ? TabBaseColor(TargetTab()) with { A = SendFillAlpha }
                        : _services.Theme.Colors.MenuMuted with { A = DisabledFillAlpha };

    private ColorRgba? SendText()
    {
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

    private string CharCounter()
    {
        int n = Clamp(_inputText.Length, 0, MaxMessageLength);
        return _counterLabels[n] ??= n + "/" + MaxMessageLength;
    }

    private ColorRgba? CounterColor()
    {
        var c = _inputText.Length >= CounterDangerAt ? CounterDanger : CounterWarn;
        return EnsureContrast(c, _services.Theme.Colors.MenuBackground with { A = 1f }, TabActiveMinContrast);
    }
}
