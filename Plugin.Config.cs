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
    private const string CfgKeepWhispers   = "whisperHistory.keep";

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

        _sendTargetIndex = SavedSendTarget();
        _keepWhisperHistory = _cfg.Get(CfgKeepWhispers, true);   // .WhisperHistory.cs — on unless turned off
        LoadFavorites();   // .Favorites.cs — ordered favorite sticker ids ("stickerFavorites")
        LoadTranslateConfig();   // .Translate.cs — target language ("translate.target") + hover button ("translate.hoverButton")
        LoadTranslateOutConfig();   // .TranslateOut.Menu.cs — recent outgoing languages ("translate.outRecent")
        LoadCompactConfig();   // .Compact.cs — compact one-line messages ("display.compactMessages"), off by default
    }

    // The persisted default channel target, 0..3 only: Whisper (4) needs a picked conversation, so it is never a
    // default (older configs could hold it) — it, or anything out of range, falls back to 0 = Say (the Current tab).
    // Also the target when leaving the Whisper tab for All/System (.Composer.cs OnTabOpened).
    private int SavedSendTarget()
    {
        int i = _cfg.Get(CfgSendTarget, 0);
        return i >= 0 && i <= 3 ? i : 0;
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

    // Auto-reply text: saved on the field's submit, ~1 s after the last keystroke (debounced — not one file write per key)
    // and when the Settings window closes, so a typed reply is never lost to a forgotten Enter.
    private const float AutoReplySaveDelaySec = 1f;
    private float _autoReplySaveAt = -1f;   // TimeNow the pending typed text is due to save (-1 = nothing pending)

    private void OnAutoReplyTyped(string s)
    {
        _autoReplyText = s;
        _autoReplySaveAt = _services.Framework.TimeNow + AutoReplySaveDelaySec;
    }

    // From TickSettings, every framework tick (before its settings-open gate).
    private void TickAutoReplySave()
    {
        if (_autoReplySaveAt >= 0f && _services.Framework.TimeNow >= _autoReplySaveAt) SaveAutoReplyText();
    }

    private void FlushAutoReplyText() { if (_autoReplySaveAt >= 0f) SaveAutoReplyText(); }

    private void SaveAutoReplyText()
    {
        _autoReplySaveAt = -1f;
        _cfg.Set(CfgAutoReplyText, _autoReplyText ?? "");
        _cfg.Save();
    }

    private void SaveWhisperHistoryConfig()
    {
        _cfg.Set(CfgKeepWhispers, _keepWhisperHistory);
        _cfg.Save();
    }

    private void SaveSendTarget()
    {
        _cfg.Set(CfgSendTarget, _sendTargetIndex);
        _cfg.Save();
    }
}
