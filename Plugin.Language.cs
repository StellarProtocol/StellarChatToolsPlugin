namespace Stellar.ChatTools;

/// <summary>
/// Live language switch (<see cref="Stellar.Abstractions.Services.ILocalization.LanguageChanged"/>, main thread). Almost
/// every label is a draw-time <c>Func</c> or a cache keyed on the language / template (NewBelow, friend rows, the whisper
/// header and tags, the translate menu, the cooldown label), so it re-localizes by itself. What was resolved ONCE:
///   • the two window titles → <c>WindowSpec.TitleProvider</c> (the framework chrome re-reads it on the switch);
///   • the six channel-colour slot labels shown in the framework Theme editor → re-registered under the same keys (the
///     user's colour overrides are keyed by slot key, so they survive);
///   • the default auto-reply text, while the user never saved one of their own → swapped to the new language.
/// Not covered: the toggle hotkey's description in the framework Hotkeys panel — <c>HotkeyAction</c> is declared once
/// with a plain string and the framework has no relabel path for it (it re-localizes on the next game start).
/// </summary>
public sealed partial class Plugin
{
    private void InitLanguage() => _loc.LanguageChanged += OnLanguageChanged;

    private void DisposeLanguage()
    {
        try { _loc.LanguageChanged -= OnLanguageChanged; } catch { /* swallow */ }
    }

    private void OnLanguageChanged()
    {
        try
        {
            // Unregister (Dispose) first: Register throws on a key that is still registered.
            DisposeColorSlots();
            RegisterColors();   // .Log.cs
        }
        catch (System.Exception ex) { _services.Log.Warning($"[ChatTools] colour relabel failed: {ex.Message}"); }
        // Nothing of the user's own saved and nothing typed pending (.Config.cs) → the shown text is our default.
        if (string.IsNullOrEmpty(_cfg.Get(CfgAutoReplyText, "")) && _autoReplySaveAt < 0f)
            _autoReplyText = _loc.T("chat.autoReply.default");
    }

    private void DisposeColorSlots()
    {
        _sayColor?.Dispose();
        _worldColor?.Dispose();
        _partyColor?.Dispose();
        _guildColor?.Dispose();
        _whisperColor?.Dispose();
        _systemColor?.Dispose();
    }
}
