namespace VStudioCraft.Game
{
    // Inventory screen layout. Mirrors PauseMenu — the renderer reads slot
    // rectangles from here, and the host hit-tests clicks against the same
    // rects so the two threads can never disagree about where a slot lives.
    //
    // The on-screen layout matches Alpha 1.1.2_01 inventory geometry, plus
    // the project's "+1 row, +20% larger" tweak:
    //   ┌──────────── INVENTORY ────────────┐
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 0 (slots 0..8)
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 1 (slots 9..17)
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 2 (slots 18..26)
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 3 (slots 27..35)
    //   │                                    │   <- HotbarGap separator
    //   │  □ □ □ □ □ □ □ □ □                │   <- hotbar row  (slots 36..44)
    //   └────────────────────────────────────┘
    //
    // In creative mode the renderer replaces the main 4×9 grid with a search
    // bar + scrollable catalog of every placeable BlockType — see
    // CreativeCatalog. The hotbar row stays in place either way so the
    // player's loadout is always visible. Click handling lives in the
    // renderer: GameRenderer.HandleInventoryClick branches on game mode and
    // dispatches to either the survival slot rules or the creative catalog
    // pick.
    //
    // All pixel sizes are *base* values — the renderer pipes them through
    // UiScale.For(viewW, viewH) so the panel grows with the viewport and
    // looks consistent at fullscreen / 1080p / 1440p instead of stranded in
    // the centre at its tool-window design size. Hit-tests use the same
    // scaled values, so a click on a row-3 slot in a fullscreen window
    // hits the same slot it visually covers.
    internal static class InventoryScreen
    {
        public const int Cols = 9;
        public const int MainRows = 4;

        // ---- base (scale=1) pixel sizes ---------------------------------
        // These are NOT used directly by callers — they're the inputs to the
        // scaled accessors below. The "20% larger than the hotbar" sizing
        // and the original 46/37 hotbar metrics it was derived from both
        // live here in one place so any future global resize is a single
        // edit.
        private const int SlotPxBase       = 55;
        private const int IconPxBase       = 44;
        private const int SlotBorderPxBase = 2;
        private const int HotbarGapBase    = 10;
        private const int PanelPadXBase    = 17;
        private const int PanelPadYBase    = 17;
        private const int TitleScaleBase   = 2;
        private const int TitleGapBase     = 14;
        private const int SearchBarHeightBase = 26;
        private const int SearchBarGapBase    = 8;
        // Gap between the inventory panel's bottom edge and the on-screen
        // hotbar's top edge. The panel anchors to the hotbar (not the
        // viewport centre) so the spatial relationship between the
        // inventory-row icons and the bar stays consistent at every
        // window size — this used to drift because the panel was screen-
        // centred while the hotbar floated at a fixed margin from the
        // bottom edge.
        private const int HotbarGapAboveBase = 40;

        // ---- non-pixel layout constants (no scaling) --------------------
        public const int MainSlotCount = Cols * MainRows;        // 36
        public const int HotbarSlotCount = Cols;                 // 9
        public const int TotalSlots = MainSlotCount + HotbarSlotCount; // 45
        public const int CatalogRows = MainRows;                 // catalog covers main-grid region

        public const string Title = "INVENTORY";

        // ---- scaled pixel accessors -------------------------------------
        // Every layout decision below funnels through these so we never
        // mix a base value with a scaled one and produce sub-pixel drift
        // between the panel chrome and a slot's hit-test rect.
        public static int SlotPx(int viewW, int viewH)        => UiScale.S(SlotPxBase, viewW, viewH);
        public static int IconPx(int viewW, int viewH)        => UiScale.S(IconPxBase, viewW, viewH);
        public static int SlotBorderPx(int viewW, int viewH)  => UiScale.S(SlotBorderPxBase, viewW, viewH);
        public static int HotbarGap(int viewW, int viewH)     => UiScale.S(HotbarGapBase, viewW, viewH);
        public static int PanelPadX(int viewW, int viewH)     => UiScale.S(PanelPadXBase, viewW, viewH);
        public static int PanelPadY(int viewW, int viewH)     => UiScale.S(PanelPadYBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)      => UiScale.S(TitleGapBase, viewW, viewH);
        public static int SearchBarHeight(int viewW, int viewH) => UiScale.S(SearchBarHeightBase, viewW, viewH);
        public static int SearchBarGap(int viewW, int viewH)  => UiScale.S(SearchBarGapBase, viewW, viewH);

        // Title glyph multiplier — the bitmap font ships at 8 px per cell;
        // base scale 2 keeps it readable, growing with the viewport so a
        // 4K panel doesn't have a postage-stamp title bar. Rounded so we
        // never hit a 0× scale on tiny viewports.
        public static int TitleScale(int viewW, int viewH)
        {
            int s = (int)(TitleScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }
        public static int TitleHeight(int viewW, int viewH)
            => HotbarTextures.GlyphCellH * TitleScale(viewW, viewH);

        // ---- panel geometry ----------------------------------------------

        private static int GridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * Cols;
        private static int GridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * MainRows + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);

        public static int PanelWidth(int viewW, int viewH)
            => GridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
            => PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
             + GridHeightPx(viewW, viewH) + PanelPadY(viewW, viewH);

        // Top-left corner of the panel. Horizontally screen-centred,
        // vertically anchored so the panel's bottom edge sits a fixed
        // (UiScale-aware) gap above the on-screen hotbar's top edge.
        // This ties the inventory's spatial position to the hotbar so
        // the two never drift apart when the viewport resizes.
        public static void GetPanelRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            w = PanelWidth(screenW, screenH);
            h = PanelHeight(screenW, screenH);
            x = (screenW - w) / 2;
            int hotbarTop = HotbarLayout.BarTopY(screenW, screenH);
            int gap = UiScale.S(HotbarGapAboveBase, screenW, screenH);
            y = hotbarTop - gap - h;
            // Tiny-window safety: if the panel is taller than the space
            // above the hotbar, fall back to the top of the viewport so
            // we don't render off-screen with negative y.
            if (y < 0) y = 0;
        }

        // Title is centred horizontally; this returns the Y of its top edge.
        public static int TitleY(int screenW, int screenH)
        {
            GetPanelRect(screenW, screenH, out _, out int py, out _, out _);
            return py + PanelPadY(screenW, screenH);
        }

        // Slot rect for slotIndex 0..(TotalSlots-1).
        //   0..(MainSlotCount-1) — main inventory, row-major top-down, 9 per row.
        //   MainSlotCount..(TotalSlots-1) — hotbar (left → right), drawn
        //     HotbarGap below the main grid.
        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            int gap  = HotbarGap(screenW, screenH);
            int gridX0 = px + padX;
            int gridY0 = py + padY + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);

            int col = slotIndex % Cols;
            if (slotIndex < MainSlotCount)
            {
                int row = slotIndex / Cols;
                x = gridX0 + col * slot;
                y = gridY0 + row * slot;
            }
            else
            {
                x = gridX0 + col * slot;
                y = gridY0 + MainRows * slot + gap;
            }
            w = slot;
            h = slot;
        }

        public static bool IsHotbarSlot(int slotIndex) => slotIndex >= MainSlotCount;
        public static int HotbarColumn(int slotIndex) => slotIndex - MainSlotCount;

        // Return the slot index under (mx, my), or -1 if none.
        public static int HitTest(int screenW, int screenH, int mx, int my)
        {
            for (int i = 0; i < TotalSlots; i++)
            {
                GetSlotRect(i, screenW, screenH, out int sx, out int sy, out int sw, out int sh);
                if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) return i;
            }
            return -1;
        }

        // ---- creative layout helpers ------------------------------------

        // Search bar rect — fills the grid-width region just below the title,
        // where the first row of the survival main grid would normally be.
        public static void GetSearchBarRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            x = px + PanelPadX(screenW, screenH);
            y = py + PanelPadY(screenW, screenH) + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);
            w = GridWidthPx(screenW, screenH);
            h = SearchBarHeight(screenW, screenH);
        }

        // Catalog grid rect — sits below the search bar, fills the rest of
        // the main-grid region down to the hotbar gap. The renderer places
        // catalog tiles on the same SlotPx grid so icons line up with the
        // hotbar columns visually.
        public static void GetCatalogRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int gridY0 = py + PanelPadY(screenW, screenH) + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);
            int catalogTop = gridY0 + SearchBarHeight(screenW, screenH) + SearchBarGap(screenW, screenH);
            x = px + PanelPadX(screenW, screenH);
            y = catalogTop;
            w = GridWidthPx(screenW, screenH);
            // Bottom of catalog = top of hotbar row - HotbarGap. The catalog
            // therefore takes up the rows it has, minus the search-bar's
            // intrusion. Compute from the survival main-grid height instead
            // of redeclaring it.
            int mainBottom = gridY0 + MainRows * SlotPx(screenW, screenH);
            h = mainBottom - catalogTop;
        }

        // Hit-test the catalog tile under (mx, my). Returns the visible-row
        // index (0..CatalogRows*Cols-1) the cursor is on, or -1 if outside.
        // The caller adds the scroll offset to map this to a CreativeCatalog
        // index.
        public static int HitTestCatalogTile(int screenW, int screenH, int mx, int my)
        {
            GetCatalogRect(screenW, screenH, out int cx, out int cy, out int cw, out int ch);
            if (mx < cx || mx >= cx + cw || my < cy || my >= cy + ch) return -1;
            int slot = SlotPx(screenW, screenH);
            int col = (mx - cx) / slot;
            int row = (my - cy) / slot;
            if (col < 0 || col >= Cols || row < 0 || row >= CatalogRows) return -1;
            return row * Cols + col;
        }

        // Hit-test the hotbar slot under (mx, my). Returns the slot index
        // in Inventory.Slots (HotbarStart..HotbarStart+HotbarCount-1) or -1.
        // Used by creative click routing — only the hotbar row is a real
        // slot in creative mode; the catalog area handles its own hits.
        public static int HitTestHotbar(int screenW, int screenH, int mx, int my)
        {
            for (int i = MainSlotCount; i < TotalSlots; i++)
            {
                GetSlotRect(i, screenW, screenH, out int sx, out int sy, out int sw, out int sh);
                if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) return i;
            }
            return -1;
        }

        // Hit-test the search bar (returns true if (mx,my) is inside it).
        public static bool HitTestSearchBar(int screenW, int screenH, int mx, int my)
        {
            GetSearchBarRect(screenW, screenH, out int x, out int y, out int w, out int h);
            return mx >= x && mx < x + w && my >= y && my < y + h;
        }
    }
}
