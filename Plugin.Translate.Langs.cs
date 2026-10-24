using System;
using System.Collections.Generic;

namespace Stellar.ChatTools;

/// <summary>
/// The translate-target language list (Settings ⚙ → Translation → "Translate to"). Order (approved):
/// 1. ChatTools' own UI locales, DISCOVERED from the embedded <c>Lang.&lt;code&gt;.json</c> catalogs (so a new locale file
///    joins the top group without a code change, as long as the master table below knows its Google code);
/// 2. the other game client languages (the game's <c>LocalizationMgr</c> language words — Bundle-Pkg-Asset-Pipeline.md:
///    chinese/traditionalchinese/german/french/spanish/portuguese);
/// 3. a divider, then ~40 common Google languages A–Z by English name.
/// Each option reads "native name (localized name)", e.g. "ภาษาไทย (Thai)". Native is null for right-to-left and
/// complex-shaping scripts (Arabic, Hebrew, Persian, Urdu, Devanagari, Bengali, Tamil, Burmese, Khmer, Lao): the menu's
/// uGUI Text does no RTL/shaping, so those names would render reversed or broken — they show the localized name only.
/// Codes are Google's (Filipino = <c>tl</c>, Hebrew = <c>iw</c>, Chinese = <c>zh-CN</c>/<c>zh-TW</c>).
/// </summary>
public sealed partial class Plugin
{
    private readonly record struct TrLang(string Code, string? Native);

    // Master table, in group-3 (English-name A–Z) order for the tail. Lang key of each display name = "chat.lang.<Code>".
    private static readonly TrLang[] TrAllLangs =
    {
        new("en", "English"), new("ja", "日本語"), new("th", "ภาษาไทย"), new("id", "Bahasa Indonesia"),
        new("tl", "Filipino"), new("ko", "한국어"),
        new("zh-CN", "简体中文"), new("zh-TW", "繁體中文"), new("de", "Deutsch"), new("fr", "Français"),
        new("es", "Español"), new("pt", "Português"),
        new("af", "Afrikaans"), new("ar", null), new("bn", null), new("bg", "Български"), new("my", null),
        new("ca", "Català"), new("hr", "Hrvatski"), new("cs", "Čeština"), new("da", "Dansk"), new("nl", "Nederlands"),
        new("et", "Eesti"), new("fi", "Suomi"), new("el", "Ελληνικά"), new("iw", null), new("hi", null),
        new("hu", "Magyar"), new("is", "Íslenska"), new("it", "Italiano"), new("km", null), new("lo", null),
        new("lv", "Latviešu"), new("lt", "Lietuvių"), new("ms", "Bahasa Melayu"), new("mn", "Монгол"), new("ne", null),
        new("no", "Norsk"), new("fa", null), new("pl", "Polski"), new("ro", "Română"), new("ru", "Русский"),
        new("sr", "Српски"), new("sk", "Slovenčina"), new("sl", "Slovenščina"), new("sw", "Kiswahili"),
        new("sv", "Svenska"), new("ta", null), new("tr", "Türkçe"), new("uk", "Українська"), new("ur", null),
        new("vi", "Tiếng Việt"),
    };

    // Group 2: the game client languages ChatTools has no UI locale for (skipped if a Lang file is added for one later).
    private static readonly string[] TrGameLangs = { "zh-CN", "zh-TW", "de", "fr", "es", "pt" };
    private const int TrTailStart = 12;   // TrAllLangs index where the A–Z tail (group 3) begins

    // Built once: option codes in menu order, null = the divider row. Labels are rebuilt per UI language (_trOptLang).
    private string?[]? _trOptCodes;
    private readonly List<string> _trOptLabels = new();
    private string? _trOptLang;
    private const string TrDividerLabel = "· · · · · · · ·";   // Latin-1 middle dots: every menu face has them (no taller fallback font)

    private string?[] TrOptionCodes()
    {
        if (_trOptCodes != null) return _trOptCodes;
        var codes = new List<string?>();
        foreach (var ui in UiLocaleCodes())
            if (TrLangIndex.ContainsKey(ui) && !codes.Contains(ui)) codes.Add(ui);
        foreach (var g in TrGameLangs) if (!codes.Contains(g)) codes.Add(g);
        codes.Add(null);
        for (int i = TrTailStart; i < TrAllLangs.Length; i++) if (!codes.Contains(TrAllLangs[i].Code)) codes.Add(TrAllLangs[i].Code);
        return _trOptCodes = codes.ToArray();
    }

    // The plugin's UI locales, in csproj/catalog order, mapped to Google codes (fil → tl).
    private static IEnumerable<string> UiLocaleCodes()
    {
        const string Prefix = "Stellar.ChatTools.Lang.", Suffix = ".json";
        foreach (var n in typeof(Plugin).Assembly.GetManifestResourceNames())
            if (n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
                yield return ToGoogleCode(n.Substring(Prefix.Length, n.Length - Prefix.Length - Suffix.Length));
    }

    // Dropdown captions, cached per UI language (the Func is polled every refresh).
    private IReadOnlyList<string> TrTargetOptions()
    {
        var lang = _loc.Language;
        if (_trOptLabels.Count > 0 && string.Equals(lang, _trOptLang, StringComparison.Ordinal)) return _trOptLabels;
        _trOptLang = lang;
        _trOptLabels.Clear();
        foreach (var code in TrOptionCodes())
        {
            if (code == null) { _trOptLabels.Add(TrDividerLabel); continue; }
            var name = TrLangName(code);
            var native = TrNative(code);
            _trOptLabels.Add(native == null || string.Equals(native, name, StringComparison.Ordinal) ? name : $"{native} ({name})");
        }
        return _trOptLabels;
    }

    private int TrTargetIndex() => Array.IndexOf(TrOptionCodes(), TranslateTarget());

    private void OnPickTrTarget(int index)
    {
        var codes = TrOptionCodes();
        if (index < 0 || index >= codes.Length || codes[index] is not { } code) return;   // the divider is inert
        _trTargetCfg = code;
        SaveTranslateConfig();   // .Translate.cs
    }

    // Code → TrAllLangs index (ordinal), so lookups never scan or allocate a closure.
    private static readonly Dictionary<string, int> TrLangIndex = BuildTrLangIndex();
    private static Dictionary<string, int> BuildTrLangIndex()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < TrAllLangs.Length; i++) d[TrAllLangs[i].Code] = i;
        return d;
    }

    private static string? TrNative(string code) => TrLangIndex.TryGetValue(code, out var i) ? TrAllLangs[i].Native : null;

    // Localized display name of a Google code; an unknown code (a detected source we don't list) shows as the raw code.
    private string TrLangName(string code) => TrLangIndex.ContainsKey(code) ? _loc.T("chat.lang." + code) : code;

    // Normalize a locale or a Google-detected source code to the codes in TrAllLangs (case, Google's legacy aliases).
    private static string ToGoogleCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var c = code.Trim().Replace('_', '-');
        var lower = c.ToLowerInvariant();
        if (lower is "fil" || lower.StartsWith("fil-", StringComparison.Ordinal)) return "tl";
        if (lower is "he" || lower.StartsWith("he-", StringComparison.Ordinal)) return "iw";
        if (lower is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" || lower.StartsWith("zh-hant", StringComparison.Ordinal)) return "zh-TW";
        if (lower is "zh" || lower.StartsWith("zh-", StringComparison.Ordinal)) return "zh-CN";
        if (lower is "nb" or "nn") return "no";
        var dash = lower.IndexOf('-');
        return dash > 0 ? lower.Substring(0, dash) : lower;
    }
}
