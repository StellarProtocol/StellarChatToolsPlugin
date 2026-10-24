using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Inline-emoji tab of the sticker picker (always the SECOND tab, after Favorites — <see cref="EmojiTab"/>). Mirrors the game's own chat
/// emoji panel: it hardcodes 63 inline emoji and writes <c>&lt;sprite=N&gt;</c> (N = 1..63, 1-based, sent verbatim) into
/// the draft — it does NOT send (<c>chat_emoji_container_popup_view.lua:31,588-596</c>). Those are the sticker table's
/// GroupId 1 rows, which the texture grid (.Stickers.cs) deliberately drops because they have no standalone texture;
/// here each cell is instead a <see cref="TextElement.RichEmoji"/> text holding the tag, which the framework draws via
/// TMP with the game's default "emoji" sprite asset — the same path the chat bubbles use (Plugin.Bubbles.cs).
///
/// Clicking a cell INSERTS the tag at the composer's caret (replacing a selection) through the field's
/// InputElement.InsertRequest; the framework refocuses the field with the caret right after the tag, and the new draft
/// comes back through OnChange like typing. The picker stays open so several emoji can be inserted in a row. The send path (DoSend → IChat.Send) passes
/// the text through unescaped, so the tag goes out exactly as if typed by hand.
/// </summary>
public sealed partial class Plugin
{
    private const int   EmojiTab        = 1;     // tab index of the emoji tab (after Favorites = 0); GroupId tabs follow
    private const int   EmojiCount      = 63;    // the game's hardcoded emojiCount_ (sprite 64 exists but is unreachable)
    private const float EmojiCellSize   = 44f;   // grid cell px — smaller than a sticker cell, like the game's panel
    private const int   EmojiFontSize   = 30;    // TMP sprites scale with font size → this sets the emoji's drawn size
    private const int   EmojiTabFontSize = 24;   // tab icon (<sprite=1>) sized to sit inside a StickerTabCell

    // The 63 tags, built once — the per-frame cell Funcs return these cached strings (no per-frame string alloc).
    private static readonly string[] EmojiTags = BuildEmojiTags();

    private static string[] BuildEmojiTags()
    {
        var tags = new string[EmojiCount];
        for (int i = 0; i < EmojiCount; i++) tags[i] = "<sprite=" + (i + 1) + ">";   // 1-based, matches the game
        return tags;
    }

    // Insert the tag AT THE CARET (the composer's InputElement.InsertRequest: the framework remembers where the caret was
    // before the click took focus, replaces a selection, never splits another tag, refocuses with the caret after the
    // tag; the result comes back through OnChange → SetInput like typing). Refuse (rather than truncate) when it would
    // overflow the 140 cap — counted as if appended, which is the worst case — since SetInput's Substring would otherwise
    // cut a tag in half. A refused pick (or a locked box) still hands the keyboard back via FocusRequest.
    private void OnPickEmoji(int idx)
    {
        if (idx < 0 || idx >= EmojiCount) return;
        var tag = EmojiTags[idx];
        if (TrOutBusy() || _inputText.Length + tag.Length > MaxMessageLength) { _composerFocusSeq++; return; }
        _emojiInsertText = tag;
        _emojiInsertSeq++;
    }

    // The emoji tab's icon: a RichEmoji <sprite=1>, centred like the texture tabs (icon-only, no localized label).
    // CenterGlyphs (TMP Midline) centres the drawn picture: a sprite sits on the text BASELINE, so default line-box
    // centring drew it visibly higher than the PNG tab icons. Same on the grid cells and the composer ☺ button —
    // NOT on chat bubbles, where emoji share the baseline with the surrounding text.
    private HudElement BuildEmojiTabIcon() => new RowElement(new HudElement[]
    {
        new TextElement(() => EmojiTags[0], FontSize: EmojiTabFontSize, NoWrap: true) { RichEmoji = true, CenterGlyphs = true },
    }, Justify: RowJustify.Center);

    // Fixed 63-cell grid of RichEmoji texts. Each tag is centred in a RowElement (same anti-squish wrapper as the sticker
    // thumbnails) inside a SelectableElement for the click + hover tint. FillWidth flows it to the popover width.
    private HudElement BuildEmojiGrid()
    {
        var slots = new HudElement[EmojiCount];
        for (int i = 0; i < EmojiCount; i++)
        {
            var idx = i;
            slots[idx] = new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new TextElement(() => EmojiTags[idx], FontSize: EmojiFontSize, NoWrap: true) { RichEmoji = true, CenterGlyphs = true },
                }, Justify: RowJustify.Center),
                OnClick: () => OnPickEmoji(idx));
        }
        return new ListElement(() => EmojiCount, slots,
            Columns: StickerGridColumns, CellWidth: EmojiCellSize, CellHeight: EmojiCellSize) { FillWidth = true };
    }
}
