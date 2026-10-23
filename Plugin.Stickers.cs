using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Sticker (picture-emoji) send picker — a popover beside the composer's ＋ quick-phrase button. Opens an all-icon
/// grid (no localized category labels): representative-icon category tabs over a scrollable multi-column grid of the
/// active category's thumbnails; clicking a thumbnail sends that sticker and closes the picker. The FIRST tab is
/// Favorites (Plugin.Favorites.cs: user-picked stickers, drag-reorderable, right-click to add/remove); the SECOND is the
/// inline-emoji tab (Plugin.Emoji.cs): its cells insert a <c>&lt;sprite=N&gt;</c> tag into the draft instead of sending.
///
/// Catalog source: <see cref="IGameAssets.GetSendableStickers"/> (framework 2.26.0). Shows FREE + OWNED stickers:
/// a row is kept when it is free (<see cref="StickerInfo.DefaultUnlocked"/>) OR the account owns the gated pack
/// (<see cref="IGameAssets.IsStickerOwned"/>, the game's own <c>GetChatEmojiUnlock</c> predicate via Lua) —
/// unowned gated stickers are HIDDEN, since the server rejects sending them (nothing would appear for recipients).
/// Also EXCLUDES <c>Type == 5</c> (QuickMessage: canned text, covered by the plugin's own quick phrases) and the
/// blank GroupIds 1/2/4. Thumbnails load via <see cref="IGameAssets.LoadStickerIcon"/> (cached); sends go through
/// <see cref="IChat.SendSticker"/>.
///
/// Mutual exclusion: opening the sticker picker closes the quick-phrase popover and vice-versa (<see cref="_stickerOpen"/>
/// / <see cref="Plugin._quickOpen"/>).
///
/// ⚠️ PERF (v1): a whole category's thumbnails (up to ~98 textures) load at once when the picker opens — the
/// one-category-at-a-time grid is the mitigation. If a category visibly hitches on open, the follow-up is a
/// virtualized grid (<c>VirtualListElement</c>); not blocking v1.
/// </summary>
public sealed partial class Plugin
{
    private const int   MaxStickerSlots    = 128;   // pre-allocated grid cells (element tree is fixed at Register);
                                                    // must be >= the largest single group (per-GroupId now; was ~98 by Type)
    private const int   MaxStickerCats     = 16;    // one tab per GroupId (pack); ~13 distinct non-QuickMessage groups
    private const int   StickerGridColumns = 6;     // advisory min under the Flexible/FillWidth grid (ignored when flowing)
    private const float StickerThumbSize   = 64f;   // grid thumbnail px (bigger — Option A redesign)
    private const float StickerCellSize    = 72f;   // grid cell px (thumb + a little padding)
    private const float StickerTabSize     = 32f;   // category-tab icon px (bigger tabs — Option A redesign)
    private const float StickerTabCell     = 40f;   // category-tab CELL px. ⚠ REQUIRED: BuildList gates the cellSize
                                                    // override AND the FillWidth→Flexible flow on CellWidth>0. Without a
                                                    // CellWidth the tab ListElement falls back to the 120px default grid
                                                    // cell × MaxStickerCats cols → a ~1920px forced popover min-width.
    private const float StickerGridHeight  = 150f;  // scroll viewport height — ~2 rows of StickerCellSize cells; the
                                                    // recessed tab strip sits above it (Option A)

    // Open state. Mutually exclusive with _quickOpen (see ToggleStickerPicker / ToggleQuickPhrases in Plugin.Composer.cs).
    private bool _stickerOpen;
    private int  _stickerCat = EmojiTab;   // TAB index: FavTab (0) = favorites (.Favorites.cs); EmojiTab (1) = inline
                                           // emoji (.Emoji.cs); k >= FirstPackTab = StickerCategories()[k - FirstPackTab]
                                           // (NOT the raw GroupId). The first open picks Favorites-or-Emoji (ToggleStickerPicker).
    private bool _stickerTabChosen;        // the first-open default tab has been applied (later opens keep the last tab)
    private const int FirstPackTab = 2;    // tab index of the first GroupId (pack) tab

    // Filtered catalog (free + gated, non-QuickMessage), cached once non-empty, plus the distinct GroupIds present (sorted).
    // The game's chat emoji panel tabs by GroupId (pack) — each tab = one GroupId — so we group by GroupId, NOT by the
    // coarse Type (which lumped every pack of a Type into one giant tab). Both rebuild while empty so a pre-table-load
    // open retries on the next open (GetSendableStickers is itself framework-cached once populated). Active-category
    // list cached by GroupId so the per-frame slot Funcs don't alloc.
    private IReadOnlyList<StickerInfo>? _stickerCatalog;
    private List<int> _stickerCats = new();
    private int _activeCatGroup = -1;
    private List<StickerInfo> _activeCatList = new();

    private void ToggleStickerPicker()
    {
        _stickerOpen = !_stickerOpen;
        if (_stickerOpen) _quickOpen = false;   // mutually exclusive popovers
        // Default tab, applied on the session's first open only: Favorites when it has anything to show, else Emoji.
        // Later opens keep whatever tab the user left it on.
        if (_stickerOpen && !_stickerTabChosen)
        {
            _stickerTabChosen = true;
            _stickerCat = FavoriteStickers().Count > 0 ? FavTab : EmojiTab;
        }
        if (!_stickerOpen) CloseFavMenu();       // .Favorites.cs — the context menu never outlives the picker
    }

    // Populate _stickerCatalog + _stickerCats once the table has loaded. Returns false (no cache) while the catalog
    // is still empty so a later open retries. Shows FREE + OWNED stickers (unowned gated ones are hidden — the
    // server rejects sending them); drops QuickMessage (Type 5) and the blank GroupIds 1/2/4. Category tabs are the
    // distinct GroupIds (packs) present, sorted ascending — mirrors the game's chat emoji panel. A pack the account
    // owns none of simply gets no tab (so the owned filter needs no "nothing here" message).
    private bool EnsureStickerCatalog()
    {
        if (_stickerCatalog is { Count: > 0 }) return true;
        var all = _services.GameAssets.GetSendableStickers();
        if (all.Count == 0) return false;   // table not loaded yet
        var filtered = new List<StickerInfo>();
        var cats = new List<int>();
        foreach (var s in all)
        {
            // QuickMessage (Type 5) is canned text (covered by quick phrases). Drop GroupIds 1/2/4 — they render
            // BLANK in our Res-texture picker: 1 = StandardEmoji (inline <sprite> atlas glyphs, no standalone
            // texture), 2 = game-hidden "Emoji" atlas pack, 4 = game-hidden placeholder "Picture" pack (no usable
            // texture).
            if (s.Type == 5 || s.GroupId is 1 or 2 or 4) continue;
            // Keep only stickers we can actually SEND: free ones, or gated ones the account owns. Short-circuit on
            // DefaultUnlocked so the (Lua) ownership probe runs ONLY for the gated minority (fail-closed → hidden).
            if (!(s.DefaultUnlocked || _services.GameAssets.IsStickerOwned(s.ConfigId))) continue;
            filtered.Add(s);
            if (!cats.Contains(s.GroupId)) cats.Add(s.GroupId);   // one tab per pack (GroupId), not per coarse Type
        }
        if (filtered.Count == 0) return false;
        cats.Sort();
        _stickerCatalog = filtered;
        _stickerCats = cats;
        return true;
    }

    private IReadOnlyList<int> StickerCategories()
    {
        EnsureStickerCatalog();
        return _stickerCats;
    }

    // The stickers of the currently-selected category, cached by GroupId so the grid's per-frame Funcs don't allocate.
    // The favorites/emoji tabs (or an out-of-range tab) map to group -1 = no stickers (no GroupId is negative).
    private List<StickerInfo> ActiveCategoryStickers()
    {
        var cats = StickerCategories();
        int catIdx = _stickerCat - FirstPackTab;   // tabs 0/1 are favorites/emoji; sticker groups start at FirstPackTab
        int group = (catIdx >= 0 && catIdx < cats.Count) ? cats[catIdx] : -1;
        if (group != _activeCatGroup)
        {
            _activeCatGroup = group;
            var list = new List<StickerInfo>();
            if (_stickerCatalog is { } cat)
                foreach (var s in cat) if (s.GroupId == group) list.Add(s);
            _activeCatList = list;
        }
        return _activeCatList;
    }

    private int StickerIdAt(int idx)
    {
        var list = ActiveCategoryStickers();
        return idx >= 0 && idx < list.Count ? list[idx].ConfigId : 0;
    }

    // A representative icon id for a category tab = the first sticker of that GroupId (avoids any localized label text).
    private int CategoryIconId(int catIdx)
    {
        if (!EnsureStickerCatalog() || catIdx < 0 || catIdx >= _stickerCats.Count) return 0;
        int group = _stickerCats[catIdx];
        if (_stickerCatalog is { } cat)
            foreach (var s in cat) if (s.GroupId == group) return s.ConfigId;
        return 0;
    }

    private void OnPickCategory(int catIdx) => _stickerCat = catIdx;

    private void OnPickSticker(int idx)
    {
        int id = StickerIdAt(idx);
        if (id == 0) return;
        SendStickerAndClose(id);
    }

    // Shared by the pack grid and the Favorites grid, so a favorite sends exactly like a click in its own pack tab.
    private void SendStickerAndClose(int id)
    {
        // Cooling-down target (.SendCooldown.cs): refuse like text and keep the picker open, so the user can retry once
        // the Send pill's countdown ends.
        if (CoolingDown()) return;
        SendSticker(id);
        _stickerOpen = false;   // close on pick, like the quick-phrase popover
        CloseFavMenu();
    }

    // Resolve the current composer target and send the sticker. Null target (e.g. Whisper with no reply picked) is a
    // no-op, exactly like text (DoSend). We DO note the send (by sticker id, not text — a sticker carries none): the
    // echoed inbound copy carries back the same StickerId, so this teaches self-identity even on a fresh/solo session
    // that hasn't sent any text yet (otherwise our own sticker renders left until the first text send). See
    // NoteSelfSendSticker / LearnSelfFromEcho (Plugin.Bubbles.cs).
    private void SendSticker(int configId)
    {
        if (CoolingDown()) return;   // .SendCooldown.cs — stickers share the channel's cooldown with text
        if (ResolveTarget() is not { } target) return;
        NoteSelfSendSticker(SendChannel(), configId);   // .Bubbles.cs — learn our identity from the sticker echo
        _chat.SendSticker(target, configId);
        StartSendCooldown();   // .SendCooldown.cs
        JumpLogToBottom();   // .NewBelow.cs — own send: follow it to the bottom
    }

    private HudElement BuildStickerPopover() => new PanelElement(new ColumnElement(new HudElement[]
    {
        // Loading / not-yet-populated state (table loads lazily at boot). The ONLY user-facing text in the picker.
        new ConditionalElement(() => StickerCategories().Count == 0,
            new TextElement(() => _loc.T("chat.sticker.loading"), () => _services.Theme.Colors.MenuMuted)),
        // Option A — a recessed control strip on top, separated from the grid below:
        //  • the category tabs sit in a subtly-darker rounded BubbleElement so the strip reads apart from the grid
        //    (dropped straight into this Column, the bubble fills the full width rather than hugging — the
        //    documented Column-vs-Row behaviour);
        //  • a hairline SeparatorElement divides the strip from the grid (the core separation);
        //  • the active tab is marked by the SelectableElement pill + a thin accent underline bar (BuildCategoryTabs).
        new BubbleElement(BuildCategoryTabs())
            { FillColor = new ColorRgba(0f, 0f, 0f, 0.18f), CornerRadius = 8f, PaddingX = 4f, PaddingY = 4f },
        new SeparatorElement(),
        // FixedHeight: pin the grid at StickerGridHeight so it does NOT grow when the chat window is resized —
        // all slack height stays with the log's scroll instead (framework ScrollElement.FixedHeight).
        // Favorites (.Favorites.cs) and the emoji tab (.Emoji.cs) each swap in their own grid; every pack tab shows the
        // texture grid. Separate scrolls so each keeps its own scroll position.
        new ConditionalElement(() => _stickerCat == FavTab,
            new ScrollElement(BuildFavoritesBody(), Height: StickerGridHeight) { FixedHeight = true },
            new ConditionalElement(() => _stickerCat == EmojiTab,
                new ScrollElement(BuildEmojiGrid(), Height: StickerGridHeight) { FixedHeight = true },
                new ScrollElement(BuildStickerGrid(), Height: StickerGridHeight) { FixedHeight = true })),
    }, Gap: 4f));

    // Representative-icon category tabs (one per GroupId/pack, icon-only — no localized labels). A tab shows its
    // group's first sticker; SelectableElement.Selected highlights the active one with the accent pill, and
    // SelectedBar adds a thin accent underline bar under the active tab (Option A "active tab" indicator). The icon
    // is wrapped in a centred RowElement so the Selectable/grid cell's width force-expand doesn't squish the square
    // thumbnail (WindowBuilder-Patterns.md). FillWidth lets the ~13-tab strip flow/wrap in a narrow window.
    // Slot 0 is Favorites and slot 1 the inline-emoji tab (both always present, even before the sticker table loads);
    // slots FirstPackTab.. are the GroupId tabs, slot k reading sticker category k - FirstPackTab.
    private HudElement BuildCategoryTabs()
    {
        var slots = new HudElement[MaxStickerCats + FirstPackTab];
        slots[FavTab] = new SelectableElement(BuildFavTabIcon(),
            OnClick: () => OnPickCategory(FavTab),
            Selected: () => _stickerCat == FavTab) { SelectedBar = true };
        slots[EmojiTab] = new SelectableElement(BuildEmojiTabIcon(),
            OnClick: () => OnPickCategory(EmojiTab),
            Selected: () => _stickerCat == EmojiTab) { SelectedBar = true };
        for (int i = FirstPackTab; i < MaxStickerCats + FirstPackTab; i++)
        {
            var idx = i;
            var cat = i - FirstPackTab;
            slots[idx] = new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new GameTextureElement(
                        () => { var id = CategoryIconId(cat); return id == 0 ? null : _services.GameAssets.LoadStickerIcon(id, out _); },
                        (int)StickerTabSize, (int)StickerTabSize,
                        () => { var id = CategoryIconId(cat); if (id != 0) { _services.GameAssets.LoadStickerIcon(id, out var uv); return uv; } return new UvRect(0f, 0f, 1f, 1f); }),
                }, Justify: RowJustify.Center),
                OnClick: () => OnPickCategory(idx),
                Selected: () => _stickerCat == idx) { SelectedBar = true };
        }
        return new ListElement(() => FirstPackTab + Math.Min(StickerCategories().Count, MaxStickerCats), slots,
            Columns: MaxStickerCats + FirstPackTab,
            CellWidth: StickerTabCell, CellHeight: StickerTabCell) { FillWidth = true };
    }

    // Scrollable multi-column grid of the active category's thumbnails. Pre-allocated pool of MaxStickerSlots cells
    // (the element tree is fixed at Register); VisibleCount shows only the active category's rows, each slot reading
    // its sticker by index. Each thumbnail is centred in a RowElement to avoid the cell-width squish.
    private HudElement BuildStickerGrid()
    {
        var slots = new HudElement[MaxStickerSlots];
        for (int i = 0; i < MaxStickerSlots; i++)
        {
            var idx = i;
            slots[idx] = new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new GameTextureElement(
                        () => { var id = StickerIdAt(idx); return id == 0 ? null : _services.GameAssets.LoadStickerIcon(id, out _); },
                        (int)StickerThumbSize, (int)StickerThumbSize,
                        () => { var id = StickerIdAt(idx); if (id != 0) { _services.GameAssets.LoadStickerIcon(id, out var uv); return uv; } return new UvRect(0f, 0f, 1f, 1f); }),
                }, Justify: RowJustify.Center),
                OnClick: () => OnPickSticker(idx))
            {
                // Context menu: "Add to favorites" (or "Remove from favorites" if it already is one) — .Favorites.cs.
                OnRightClick = () => OpenFavMenu(StickerIdAt(idx)),
            };
        }
        // FillWidth flows the grid to fill the popover width (as many StickerCellSize cells per row as fit) and
        // re-wraps on window resize — a fixed column count left dead space on the right of a wide/resized window.
        // StickerGridColumns stays as an advisory min; the Flexible grid ignores it (harmless).
        return new ListElement(() => Math.Min(ActiveCategoryStickers().Count, MaxStickerSlots), slots,
            Columns: StickerGridColumns, CellWidth: StickerCellSize, CellHeight: StickerCellSize) { FillWidth = true };
    }
}
