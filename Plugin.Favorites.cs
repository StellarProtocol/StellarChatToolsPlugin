using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ChatTools;

/// <summary>
/// Favorites tab of the sticker picker (always the FIRST tab, <see cref="FavTab"/>). The user right-clicks any sticker
/// in a pack tab → "Add to favorites" (or "Remove from favorites" if it already is one); in this tab a right-click
/// offers "Remove from favorites", and the stickers reorder by drag and drop (framework <c>ListElement.OnReorder</c>).
/// Clicking a favorite sends it exactly like a click in its own pack tab (<see cref="SendStickerAndClose"/>).
/// The emoji tab gets no menu (its cells are inline &lt;sprite&gt; tags, not stickers).
///
/// Data: <see cref="_favorites"/> = an ordered, de-duplicated list of sticker configIds persisted in config. DISPLAY
/// skips ids that are unknown / no longer sendable (not in the filtered catalog — same free-or-owned filter as the pack
/// tabs) but KEEPS them in config, so a pack that is temporarily unowned or a pre-table-load open loses nothing. The
/// displayed list is cached (<see cref="_favVisible"/>) and rebuilt only on change / once the catalog arrives, so the
/// per-frame grid Funcs never allocate.
///
/// Context menu = a separate cursor-positioned popup window (Borderless + DismissOnOutsideClick, the CombatMeter row
/// menu pattern, CombatMeter-Plugin-Patterns.md §17): the framework dismisses it on Esc / outside click per render
/// frame; we only re-assert its rect once the (destroy-on-hide) window remounts.
/// </summary>
public sealed partial class Plugin
{
    private const int    FavTab        = 0;                    // tab index of Favorites (Emoji = 1, packs from FirstPackTab)
    private const string CfgFavorites  = "stickerFavorites";
    private const float  FavMenuW      = 200f;                 // menu width, canvas units
    private const float  FavMenuH      = 44f;                  // ≈ one item row + panel padding (edge-flip estimate only)
    private const string FavIconResource = "Stellar.ChatTools.Assets.favorite_star.png";

    private List<int> _favorites = new();                      // persisted order; may hold currently-unsendable ids
    private List<int> _favVisible = new();                     // displayed subset, in order (cached)
    private bool _favVisibleDirty = true;

    private IWindowControl _favMenuWindow = null!;
    private int  _favMenuId;                                   // sticker the open menu acts on
    private bool _favMenuOpen, _favMenuPlaced;
    private WindowRect _favMenuRect;

    private byte[]? _favIconPng;                               // embedded tab icon, read once
    private bool _favIconTried;

    private void LoadFavorites()
    {
        var ids = _cfg.Get<int[]>(CfgFavorites, null!);
        _favorites = new List<int>();
        if (ids != null)
            foreach (var id in ids) if (id != 0 && !_favorites.Contains(id)) _favorites.Add(id);   // de-dup on load
        _favVisibleDirty = true;
    }

    private void SaveFavorites()
    {
        _cfg.Set(CfgFavorites, _favorites.ToArray());
        _cfg.Save();
        _favVisibleDirty = true;
    }

    // The displayed favorites. Rebuilt only when dirty AND the catalog is loaded (before that it stays empty + dirty,
    // so the first open after the table loads fills it).
    private List<int> FavoriteStickers()
    {
        if (!_favVisibleDirty || !EnsureStickerCatalog() || _stickerCatalog is not { } cat) return _favVisible;
        var sendable = new HashSet<int>();
        foreach (var s in cat) sendable.Add(s.ConfigId);
        var list = new List<int>(_favorites.Count);
        foreach (var id in _favorites)
            if (sendable.Contains(id) && list.Count < MaxStickerSlots) list.Add(id);
        _favVisible = list;
        _favVisibleDirty = false;
        return _favVisible;
    }

    private int FavIdAt(int idx)
    {
        var list = FavoriteStickers();
        return idx >= 0 && idx < list.Count ? list[idx] : 0;
    }

    private void AddFavorite(int id)
    {
        if (id == 0 || _favorites.Contains(id) || _favorites.Count >= MaxStickerSlots) return;
        _favorites.Add(id);
        SaveFavorites();
    }

    private void RemoveFavorite(int id)
    {
        if (_favorites.Remove(id)) SaveFavorites();
    }

    // ListElement.OnReorder (remove-then-insert indices over the DISPLAYED list). Hidden (unsendable) ids keep their
    // stored slots: the reordered visible ids are written back into the positions visible ids occupied, in order.
    private void OnFavoritesReorder(int from, int to)
    {
        var vis = FavoriteStickers();
        if (from < 0 || from >= vis.Count || to < 0 || to >= vis.Count || from == to) return;
        var moved = new List<int>(vis);
        var id = moved[from];
        moved.RemoveAt(from);
        moved.Insert(to, id);
        int k = 0;
        for (int i = 0; i < _favorites.Count && k < moved.Count; i++)
            if (vis.Contains(_favorites[i])) _favorites[i] = moved[k++];
        SaveFavorites();
    }

    // Tab icon: the embedded star PNG (ImageElement), centred in a RowElement against the cell-width squish. Falls back
    // to a ★ glyph only if the resource failed to load.
    private HudElement BuildFavTabIcon() => new ConditionalElement(() => FavIconPng() != null,
        new RowElement(new HudElement[] { new ImageElement(FavIconPng, (int)StickerTabSize, (int)StickerTabSize) },
            Justify: RowJustify.Center),
        new RowElement(new HudElement[] { new TextElement(() => "★", FontSize: EmojiTabFontSize, NoWrap: true) },
            Justify: RowJustify.Center));

    private byte[]? FavIconPng()
    {
        if (_favIconTried) return _favIconPng;
        _favIconTried = true;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(FavIconResource);
            if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); _favIconPng = ms.ToArray(); }
        }
        catch (Exception ex) { _services.Log.Warning($"[ChatTools] favorites icon load failed: {ex.Message}"); }
        return _favIconPng;
    }

    // Favorites grid body: the empty-state hint (inside the fixed-height scroll so the picker keeps its size) + the
    // reorderable grid. Same cell geometry as the pack grid; click sends, right-click offers "Remove from favorites".
    private HudElement BuildFavoritesBody()
    {
        var slots = new HudElement[MaxStickerSlots];
        for (int i = 0; i < MaxStickerSlots; i++)
        {
            var idx = i;
            slots[idx] = new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new GameTextureElement(
                        () => { var id = FavIdAt(idx); return id == 0 ? null : _services.GameAssets.LoadStickerIcon(id, out _); },
                        (int)StickerThumbSize, (int)StickerThumbSize,
                        () => { var id = FavIdAt(idx); if (id != 0) { _services.GameAssets.LoadStickerIcon(id, out var uv); return uv; } return new UvRect(0f, 0f, 1f, 1f); }),
                }, Justify: RowJustify.Center),
                OnClick: () => { var id = FavIdAt(idx); if (id != 0) SendStickerAndClose(id); })
            {
                OnRightClick = () => OpenFavMenu(FavIdAt(idx)),
            };
        }
        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => FavoriteStickers().Count == 0,
                new TextElement(() => _loc.T("chat.sticker.fav.empty"), () => _services.Theme.Colors.MenuMuted)),
            new ListElement(() => FavoriteStickers().Count, slots,
                Columns: StickerGridColumns, CellWidth: StickerCellSize, CellHeight: StickerCellSize)
                { FillWidth = true, OnReorder = OnFavoritesReorder },
        }, Gap: 4f);
    }

    // ── Context menu ──────────────────────────────────────────────────────────────────────────────────────────────

    private IWindowControl RegisterFavMenuWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "chattools.stickermenu",
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, FavMenuW, FavMenuH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(new ColumnElement(new HudElement[]
            {
                // One item whose label/action follow the sticker's current favorite state.
                new SelectableElement(new TextElement(() => _loc.T(_favorites.Contains(_favMenuId)
                    ? "chat.sticker.fav.remove" : "chat.sticker.fav.add")), InvokeFavMenu),
            }, Gap: 2f), Padding: 8f),
            OnClose: CloseFavMenu));   // framework invokes this on Escape / click-outside (per-frame ticker)

    // Open at the cursor. Input.mousePosition is bottom-left SCREEN px; WindowRect is top-left CANVAS units, so scale
    // by CanvasWidth/ScreenWidth (k = 1 before the first measure). Opens down-right; flips at the canvas edges.
    private void OpenFavMenu(int stickerId)
    {
        if (stickerId == 0) { CloseFavMenu(); return; }
        _favMenuId = stickerId;
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 0 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        float cw = fw.CanvasWidth > 0 ? fw.CanvasWidth : Screen.width;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : Screen.height;
        var mp = Input.mousePosition;
        float x = mp.x * k, y = (Screen.height - mp.y) * k;
        if (x + FavMenuW > cw) x = Mathf.Max(0f, x - FavMenuW);
        if (y + FavMenuH > ch) y = Mathf.Max(0f, y - FavMenuH);
        _favMenuRect = new WindowRect(x, y, FavMenuW, FavMenuH);
        _favMenuPlaced = false;
        _favMenuOpen = true;
        _favMenuWindow.SetRect(_favMenuRect);   // no-op while still unmounted; re-asserted in TickFavMenuPlace
        _favMenuWindow.SetVisible(true);        // opens from hidden → remounts on top; no BringToFront (Rule 5)
    }

    private void InvokeFavMenu()
    {
        var id = _favMenuId;
        CloseFavMenu();
        if (_favorites.Contains(id)) RemoveFavorite(id); else AddFavorite(id);
    }

    // The menu window is destroyed while hidden and remounts a frame (or more) later, so the SetRect in OpenFavMenu
    // no-ops on the first open; re-assert once the mount lands (Framework.Update).
    // Also closes the menu with its owners, like the whisper popups (.WhisperList.cs TickWhisperPopups): the chat window
    // closing (✕, hotkey, logout) or the sticker picker it belongs to closing.
    private void TickFavMenuPlace(float _)
    {
        if (!_favMenuOpen) return;
        if (!_window.IsShown || !_stickerOpen) { CloseFavMenu(); return; }
        if (_favMenuPlaced) return;
        if (_favMenuWindow.IsShown) { _favMenuWindow.SetRect(_favMenuRect); _favMenuPlaced = true; }
    }

    private void CloseFavMenu()
    {
        if (!_favMenuOpen) return;
        _favMenuOpen = false;
        _favMenuPlaced = false;
        _favMenuWindow?.SetVisible(false);
    }
}
