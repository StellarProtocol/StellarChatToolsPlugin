using System;
using System.Collections.Generic;

namespace Stellar.ChatTools;

/// <summary>
/// Config persistence via the framework config service (stored in stellar.chattools.config.json). Persists
/// the quick phrases, auto-reply enabled/text, log font size, and the default send-target. Window size/position
/// persist through the framework (Resizable/Draggable); colour-slot overrides persist via the Theme registry.
/// Loaded once on construct; each setter saves on the user-driven change.
/// </summary>
public sealed partial class Plugin
{
    private const string CfgQuickPhrases   = "quickPhrases";
    private const string CfgAutoReplyOn    = "autoReply.enabled";
    private const string CfgAutoReplyText  = "autoReply.text";
    private const string CfgSendTarget     = "sendTarget";

    private void LoadConfig()
    {
        _cfg = _services.Config.GetSection("chattools");

        var phrases = _cfg.Get<string[]>(CfgQuickPhrases, null!);
        _quickPhrases = phrases != null ? new List<string>(phrases) : new List<string>();

        _autoReplyEnabled = _cfg.Get(CfgAutoReplyOn, false);
        _autoReplyText = _cfg.Get(CfgAutoReplyText, "") ?? "";
        // Displayed default must route through _loc (Rule 10) and be set in the ctor body (field initializers
        // run before _loc is assigned). Only fall back when nothing was persisted.
        if (string.IsNullOrEmpty(_autoReplyText)) _autoReplyText = _loc.T("chat.autoReply.default");

        _sendTargetIndex = Clamp(_cfg.Get(CfgSendTarget, 0), 0, 4);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private void SaveQuickPhrases()
    {
        _cfg.Set(CfgQuickPhrases, _quickPhrases.ToArray());
        _cfg.Save();
    }

    private void SaveAutoReplyEnabled()
    {
        _cfg.Set(CfgAutoReplyOn, _autoReplyEnabled);
        _cfg.Save();
    }

    // Called on the auto-reply text field's submit (not per keystroke — avoids a file write per key).
    private void SaveAutoReplyText()
    {
        _cfg.Set(CfgAutoReplyText, _autoReplyText ?? "");
        _cfg.Save();
    }

    private void SaveSendTarget()
    {
        _cfg.Set(CfgSendTarget, _sendTargetIndex);
        _cfg.Save();
    }
}
