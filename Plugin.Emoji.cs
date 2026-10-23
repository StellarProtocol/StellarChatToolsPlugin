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
/// Clicking a cell APPENDS the tag to <see cref="_inputText"/>; the composer's InputElement Get() binding re-seeds the
/// field on the next poll (framework FieldBinding diffs on Get(), so the external change shows without fighting live
/// typing). The picker stays open so several emoji can be inserted in a row. Each click also bumps
/// <see cref="_composerFocusSeq"/> (the composer's InputElement.FocusRequest), so the field regains focus with the caret at
/// the end — the click on the cell would otherwise leave the box unfocused. The send path (DoSend → IChat.Send) passes
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

    // Append the tag to the draft. Refuse (rather than truncate) when it would overflow the 140 cap: SetInput's
    // Substring would otherwise cut the tag in half and send a literal "<sprit" fragment. Focus goes back to the composer
    // either way (bumped before the cap check), so a refused pick still leaves the user typing/deleting in the box.
    private void OnPickEmoji(int idx)
    {
        if (idx < 0 || idx >= EmojiCount) return;
        _composerFocusSeq++;   // composer FocusRequest: hand the keyboard back to the field (caret at end) after the click
        var tag = EmojiTags[idx];
        if (_inputText.Length + tag.Length > MaxMessageLength) return;
        _inputText += tag;   // FieldBinding re-seeds the composer field from Get() on the next poll
    }

    // The emoji tab's icon: a RichEmoji <sprite=1>, centred like the texture tabs (icon-only, no localized label).
    private HudElement BuildEmojiTabIcon() => new RowElement(new HudElement[]
    {
        new TextElement(() => EmojiTags[0], FontSize: EmojiTabFontSize, NoWrap: true) { RichEmoji = true },
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
                    new TextElement(() => EmojiTags[idx], FontSize: EmojiFontSize, NoWrap: true) { RichEmoji = true },
                }, Justify: RowJustify.Center),
                OnClick: () => OnPickEmoji(idx));
        }
        return new ListElement(() => EmojiCount, slots,
            Columns: StickerGridColumns, CellWidth: EmojiCellSize, CellHeight: EmojiCellSize) { FillWidth = true };
    }
}
