using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Sticker (picture-emoji) send picker — a popover beside the composer's ＋ quick-phrase button. Opens an all-icon
/// grid (no localized category labels): representative-icon category tabs over a scrollable multi-column grid of the
/// active category's thumbnails; clicking a thumbnail sends that sticker and closes the picker.
///
/// Catalog source: <see cref="IGameAssets.GetSendableStickers"/> (framework 2.26.0), filtered to FREE
/// (<see cref="StickerInfo.DefaultUnlocked"/>) stickers only — v1 sidesteps the server rejecting unowned gated
/// stickers — and EXCLUDING <c>Type == 5</c> (QuickMessage: canned text, covered by the plugin's own quick phrases).
/// Types 1–4 (StandardEmoji / Emoji / Picture / HeadPicture) are kept. Thumbnails load via
/// <see cref="IGameAssets.LoadStickerIcon"/> (cached); sends go through <see cref="IChat.SendSticker"/>.
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
    private const int   MaxStickerCats     = 16;    // one tab per GroupId (pack); ~13 distinct free non-QuickMessage groups
    private const int   StickerGridColumns = 6;     // advisory min under the Flexible/FillWidth grid (ignored when flowing)
    private const float StickerThumbSize   = 56f;   // grid thumbnail px
    private const float StickerCellSize    = 64f;   // grid cell px (thumb + a little padding)
    private const float StickerTabSize     = 26f;   // category-tab icon px (small so ~13 tabs fit a row)
    private const float StickerTabCell     = 32f;   // category-tab CELL px. ⚠ REQUIRED: BuildList gates the cellSize
                                                    // override AND the FillWidth→Flexible flow on CellWidth>0. Without a
                                                    // CellWidth the tab ListElement falls back to the 120px default grid
                                                    // cell × MaxStickerCats cols → a ~1920px forced popover min-width.
    private const float StickerGridHeight  = 200f;  // scroll viewport height

    // Open state. Mutually exclusive with _quickOpen (see ToggleStickerPicker / ToggleQuickPhrases in Plugin.Composer.cs).
    private bool _stickerOpen;
    private int  _stickerCat;   // index into StickerCategories() (NOT the raw GroupId)

    // Filtered catalog (free, non-QuickMessage), cached once non-empty, plus the distinct GroupIds present (sorted).
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
    }

    // Populate _stickerCatalog + _stickerCats once the table has loaded. Returns false (no cache) while the catalog
    // is still empty so a later open retries. Filters to FREE stickers and drops QuickMessage (Type 5). Category tabs
    // are the distinct GroupIds (packs) present, sorted ascending — mirrors the game's chat emoji panel.
    private bool EnsureStickerCatalog()
    {
        if (_stickerCatalog is { Count: > 0 }) return true;
        var all = _services.GameAssets.GetSendableStickers();
        if (all.Count == 0) return false;   // table not loaded yet
        var filtered = new List<StickerInfo>();
        var cats = new List<int>();
        foreach (var s in all)
        {
            // Free only; QuickMessage (Type 5) is canned text. Drop GroupIds 1/2/4 — they render BLANK in our
            // Res-texture picker: 1 = StandardEmoji (inline <sprite> atlas glyphs, no standalone texture),
            // 2 = game-hidden "Emoji" atlas pack, 4 = game-hidden placeholder "Picture" pack (no usable texture).
            if (!s.DefaultUnlocked || s.Type == 5 || s.GroupId is 1 or 2 or 4) continue;
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
    private List<StickerInfo> ActiveCategoryStickers()
    {
        var cats = StickerCategories();
        int group = (_stickerCat >= 0 && _stickerCat < cats.Count) ? cats[_stickerCat]
                  : (cats.Count > 0 ? cats[0] : 0);
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
        SendSticker(id);
        _stickerOpen = false;   // close on pick, like the quick-phrase popover
    }

    // Resolve the current composer target and send the sticker. Null target (e.g. Whisper with no reply picked) is a
    // no-op, exactly like text (DoSend). We DO note the send (by sticker id, not text — a sticker carries none): the
    // echoed inbound copy carries back the same StickerId, so this teaches self-identity even on a fresh/solo session
    // that hasn't sent any text yet (otherwise our own sticker renders left until the first text send). See
    // NoteSelfSendSticker / LearnSelfFromEcho (Plugin.Bubbles.cs).
    private void SendSticker(int configId)
    {
        if (ResolveTarget() is not { } target) return;
        NoteSelfSendSticker(SendChannel(), configId);   // .Bubbles.cs — learn our identity from the sticker echo
        _chat.SendSticker(target, configId);
    }

    private HudElement BuildStickerPopover() => new PanelElement(new ColumnElement(new HudElement[]
    {
        // Loading / not-yet-populated state (table loads lazily at boot). The ONLY user-facing text in the picker.
        new ConditionalElement(() => StickerCategories().Count == 0,
            new TextElement(() => _loc.T("chat.sticker.loading"), () => _services.Theme.Colors.MenuMuted)),
        BuildCategoryTabs(),
        new ScrollElement(BuildStickerGrid(), Height: StickerGridHeight),
    }, Gap: 4f));

    // Representative-icon category tabs (one per GroupId/pack, icon-only — no localized labels). A tab shows its
    // group's first sticker; SelectableElement.Selected highlights the active one. The icon is wrapped in a centred
    // RowElement so the Selectable/grid cell's width force-expand doesn't squish the square thumbnail
    // (WindowBuilder-Patterns.md). FillWidth lets the ~13-tab strip flow/wrap in a narrow window instead of overflowing.
    private HudElement BuildCategoryTabs()
    {
        var slots = new HudElement[MaxStickerCats];
        for (int i = 0; i < MaxStickerCats; i++)
        {
            var idx = i;
            slots[idx] = new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new GameTextureElement(
                        () => { var id = CategoryIconId(idx); return id == 0 ? null : _services.GameAssets.LoadStickerIcon(id, out _); },
                        (int)StickerTabSize, (int)StickerTabSize,
                        () => { var id = CategoryIconId(idx); if (id != 0) { _services.GameAssets.LoadStickerIcon(id, out var uv); return uv; } return new UvRect(0f, 0f, 1f, 1f); }),
                }, Justify: RowJustify.Center),
                OnClick: () => OnPickCategory(idx),
                Selected: () => _stickerCat == idx);
        }
        return new ListElement(() => StickerCategories().Count, slots, Columns: MaxStickerCats,
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
                OnClick: () => OnPickSticker(idx));
        }
        // FillWidth flows the grid to fill the popover width (as many StickerCellSize cells per row as fit) and
        // re-wraps on window resize — a fixed column count left dead space on the right of a wide/resized window.
        // StickerGridColumns stays as an advisory min; the Flexible grid ignores it (harmless).
        return new ListElement(() => Math.Min(ActiveCategoryStickers().Count, MaxStickerSlots), slots,
            Columns: StickerGridColumns, CellWidth: StickerCellSize, CellHeight: StickerCellSize) { FillWidth = true };
    }
}
