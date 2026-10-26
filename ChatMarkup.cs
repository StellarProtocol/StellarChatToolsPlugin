using System.Text;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>
/// Neutralizes TextMeshPro markup in text that came off the WIRE before it reaches a rich-text (RichEmoji / TMP) element.
/// Every bubble body is TMP rich text so the game's inline <c>&lt;sprite=N&gt;</c> emoji draw — which also means a
/// player's <c>&lt;size=300&gt;</c>, <c>&lt;alpha=#00&gt;</c>, <c>&lt;color&gt;</c>, <c>&lt;link&gt;</c>, <c>&lt;voffset&gt;</c>,
/// <c>&lt;mark&gt;</c>… would render for every ChatTools user. The game's own chat strips them.
///
/// Mirrors that strip (<c>RichTextHelper.RemoveTagsOtherThanEmojis</c>, rich_text_helper.lua:249, applied to EVERY wire
/// chat message in <c>ChatMsgHelper.GetMsg</c> and to every send in chat_main_vm.lua:977): any <c>&lt;…&gt;</c> run is
/// removed unless it is exactly <c>&lt;sprite=</c>digits<c>&gt;</c>, and line breaks + backslashes are dropped. Stripping
/// (not <c>&lt;noparse&gt;</c>) so we show exactly what the native chat shows, and nothing in the result can re-open a
/// tag: a <c>&lt;</c> survives only when no <c>&gt;</c> follows it at all. Backslashes go because TMP's
/// <c>parseCtrlCharacters</c> can turn a typed <c><</c> escape into a real <c>&lt;</c> that this pass never saw.
///
/// NOT for our own markup (name links, <c>&lt;alpha&gt;</c> pads, notice colours) — those are composed around text that
/// is already safe — and NOT for the client-generated System notices (<see cref="ChatMessage.SystemLabel"/> non-null),
/// whose item-quality <c>&lt;color&gt;</c> tags are the game's own and intended.
/// </summary>
internal static class ChatMarkup
{
    private static readonly char[] Unsafe = { '<', '\\', '\r', '\n' };

    /// <summary>The message's body as it may be shown in TMP: client notices verbatim, everything else sanitized.</summary>
    public static string SafeBody(ChatMessage m) => m.SystemLabel != null ? m.Text ?? "" : SanitizeWire(m.Text);

    /// <summary>Wire text → TMP-safe text (only <c>&lt;sprite=N&gt;</c> tags kept). Returns the SAME instance when there
    /// is nothing to neutralize, so the common case never allocates.</summary>
    public static string SanitizeWire(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(Unsafe) < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r' || c == '\\') continue;
            if (c == '\n') { sb.Append(' '); continue; }   // the game deletes it; a space keeps the two words apart
            if (c == '<')
            {
                // Lua "<[^>]+>": from this '<' to the FIRST following '>', at least one char between. Judged on the raw
                // text, so a tag can't be assembled out of the characters dropped above.
                int close = s.IndexOf('>', i + 1);
                if (close > i + 1)
                {
                    if (IsSpriteTag(s, i, close)) sb.Append(s, i, close - i + 1);
                    i = close;
                    continue;
                }
                // No closing '>' (or "<>"): TMP can't open a tag from it — keep it as the literal text it is ("<3").
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Exactly "<sprite=" + one or more ASCII digits + ">" (the game's Lua pattern "<sprite=%d+>", case-sensitive like it).
    private static bool IsSpriteTag(string s, int open, int close)
    {
        const string head = "<sprite=";
        int digits = close - open - head.Length;
        if (digits < 1 || string.CompareOrdinal(s, open, head, 0, head.Length) != 0) return false;
        for (int k = open + head.Length; k < close; k++)
            if (s[k] < '0' || s[k] > '9') return false;
        return true;
    }
}
