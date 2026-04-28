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
        // Tier 4 #19 — Four armor slots (Helmet/Chestplate/Leggings/
        // Boots), painted as a vertical column LEFT of the main grid.
        // Slot indices match Inventory.ArmorStart..ArmorStart+3 so a
        // hit-test or render rect translates directly via Slots[i].
        public const int ArmorSlotCount = 4;
        public const int TotalSlots = MainSlotCount + HotbarSlotCount + ArmorSlotCount; // 49
        public const int CatalogRows = MainRows;                 // catalog covers main-grid region

        // Tier 4 #19 — Horizontal gap between the armor column and the
        // main grid. Matches the visual rhythm of the rest of the
        // panel (one slot's worth of breathing room is too tight; half
        // a slot leaves the column hugging the grid in a way that
        // reads as broken). Scales with the rest of the panel via
        // UiScale.
        private const int ArmorGapBase = 12;

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
        public static int ArmorGap(int viewW, int viewH)      => UiScale.S(ArmorGapBase, viewW, viewH);
        // Tier 4 #19 — Total horizontal extent of the armor column
        // including its gap to the main grid (one slot wide + the
        // gap). Returned as a single helper so the panel-width and
        // grid-origin computations reference one expression instead
        // of recomposing the same arithmetic at each site.
        public static int ArmorColumnWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) + ArmorGap(viewW, viewH);

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

        // Tier 4 #19 — Panel width grows by one armor column + its gap
        // so the four equipment slots fit on the LEFT of the main grid
        // without overlapping the panel chrome. Same widening applies
        // to creative mode (the catalog grid still anchors at the same
        // column-1 X via gridX0 below) so the search-bar and catalog
        // hit-rects continue to line up at the right edge.
        public static int PanelWidth(int viewW, int viewH)
            => GridWidthPx(viewW, viewH) + ArmorColumnWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
            => PanelHeight(viewW, viewH, /*creative*/false);

        // Creative panels are taller than survival by the height of the
        // search-bar zone (the bar itself + the gap below it). The survival
        // panel reserves exactly MainRows*SlotPx for the main grid; in
        // creative we want the *catalog* to get that much space, with the
        // search bar above it as a separate band — otherwise the catalog
        // ends up ~half a row short and the bottom row is clipped /
        // partially-hidden behind the hotbar gap.
        public static int PanelHeight(int viewW, int viewH, bool creative)
        {
            int h = PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                  + GridHeightPx(viewW, viewH) + PanelPadY(viewW, viewH);
            if (creative)
                h += SearchBarHeight(viewW, viewH) + SearchBarGap(viewW, viewH);
            return h;
        }

        // Top-left corner of the panel. Horizontally screen-centred,
        // vertically screen-centred too — but with a floor: the panel's
        // bottom edge must never get closer than HotbarGapAboveBase
        // (UiScale-aware) to the on-screen hotbar's top edge. On normal
        // viewports the screen centre sits well above the hotbar so the
        // panel just centres; on short viewports the clamp kicks in and
        // pushes the panel up so it never collides with the bar.
        public static void GetPanelRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, /*creative*/false, out x, out y, out w, out h);
        }

        public static void GetPanelRect(int screenW, int screenH, bool creative,
            out int x, out int y, out int w, out int h)
        {
            w = PanelWidth(screenW, screenH);
            h = PanelHeight(screenW, screenH, creative);
            x = (screenW - w) / 2;

            // Preferred: screen-centred.
            int centeredY = (screenH - h) / 2;
            // Floor: bottom edge no lower than (hotbarTop - gap).
            int hotbarTop = HotbarLayout.BarTopY(screenW, screenH);
            int gap = UiScale.S(HotbarGapAboveBase, screenW, screenH);
            int maxY = hotbarTop - gap - h;

            y = centeredY < maxY ? centeredY : maxY;
            // Tiny-window safety: if the panel is taller than the space
            // above the hotbar, fall back to the top of the viewport so
            // we don't render off-screen with negative y.
            if (y < 0) y = 0;
        }

        // Title is centred horizontally; this returns the Y of its top edge.
        public static int TitleY(int screenW, int screenH)
            => TitleY(screenW, screenH, /*creative*/false);

        public static int TitleY(int screenW, int screenH, bool creative)
        {
            GetPanelRect(screenW, screenH, creative, out _, out int py, out _, out _);
            return py + PanelPadY(screenW, screenH);
        }

        // Slot rect for slotIndex 0..(TotalSlots-1).
        //   0..(MainSlotCount-1) — main inventory, row-major top-down, 9 per row.
        //   MainSlotCount..(TotalSlots-1) — hotbar (left → right), drawn
        //     HotbarGap below the main grid.
        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetSlotRect(slotIndex, screenW, screenH, /*creative*/false, out x, out y, out w, out h);
        }

        // Creative variant. The main grid (slots 0..35) doesn't actually
        // render in creative — the catalog takes that space — but we still
        // compute slot rects there so the existing hit-test loops keep
        // working harmlessly. Hotbar slots get bumped down by the search-
        // bar zone so they line up with the (taller) creative panel's
        // bottom edge.
        public static void GetSlotRect(int slotIndex, int screenW, int screenH, bool creative,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, creative, out int px, out int py, out _, out _);
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            int gap  = HotbarGap(screenW, screenH);
            // Tier 4 #19 — Armor column lives at the panel's left edge
            // (just inside the pad); the main grid's column-0 X is
            // pushed RIGHT by the armor-column width + its gap so the
            // two grids don't collide.
            int armorX = px + padX;
            int gridX0 = armorX + ArmorColumnWidthPx(screenW, screenH);
            int gridY0 = py + padY + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);

            int col = slotIndex % Cols;
            if (slotIndex < MainSlotCount)
            {
                int row = slotIndex / Cols;
                x = gridX0 + col * slot;
                y = gridY0 + row * slot;
            }
            else if (slotIndex < MainSlotCount + HotbarSlotCount)
            {
                x = gridX0 + col * slot;
                int hotbarY = gridY0 + MainRows * slot + gap;
                if (creative) hotbarY += SearchBarHeight(screenW, screenH) + SearchBarGap(screenW, screenH);
                y = hotbarY;
            }
            else
            {
                // Tier 4 #19 — Armor slot. Index 0..3 maps to the
                // four-tall column at the panel's left edge, top-down
                // in the canonical Helmet→Chestplate→Leggings→Boots
                // order (matches the per-slot index returned by
                // BlockData.GetArmorSlot). Vertically aligned with
                // the main grid's first four rows so the column reads
                // as a "doll" silhouette flanking the grid.
                int armorIndex = slotIndex - MainSlotCount - HotbarSlotCount;
                x = armorX;
                y = gridY0 + armorIndex * slot;
            }
            w = slot;
            h = slot;
        }

        // Tier 4 #19 — Hotbar slot range is now [MainSlotCount,
        // MainSlotCount+HotbarSlotCount); armor slots past that are NOT
        // hotbar slots so callers (numeric-key bindings, label
        // overlays, drop-on-quit logic) don't accidentally treat an
        // armor piece as a quickbar item.
        public static bool IsHotbarSlot(int slotIndex)
            => slotIndex >= MainSlotCount && slotIndex < MainSlotCount + HotbarSlotCount;
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

        // Search bar rect — fills the grid-width region just below the title.
        // Sits in its own band above the catalog grid; the panel grows by
        // the bar's height in creative mode so it doesn't displace any
        // catalog rows.
        public static void GetSearchBarRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, /*creative*/true, out int px, out int py, out _, out _);
            // Tier 4 #19 — Search bar anchors to the right of the
            // armor column (same X as gridX0 in GetSlotRect) so the
            // bar lines up with the catalog beneath it instead of
            // overflowing into the new armor area.
            x = px + PanelPadX(screenW, screenH) + ArmorColumnWidthPx(screenW, screenH);
            y = py + PanelPadY(screenW, screenH) + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);
            w = GridWidthPx(screenW, screenH);
            h = SearchBarHeight(screenW, screenH);
        }

        // Catalog grid rect — sits below the search bar and is exactly
        // CatalogRows*SlotPx tall, so all 4 rows are fully visible and a
        // tile-snapped scroll always shows complete rows. The taller
        // creative panel makes room for this without crowding the hotbar.
        public static void GetCatalogRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, /*creative*/true, out int px, out int py, out _, out _);
            int gridY0 = py + PanelPadY(screenW, screenH) + TitleHeight(screenW, screenH) + TitleGap(screenW, screenH);
            int catalogTop = gridY0 + SearchBarHeight(screenW, screenH) + SearchBarGap(screenW, screenH);
            // Tier 4 #19 — Catalog grid sits to the right of the armor
            // column so it doesn't overlap the helmet/chest/leg/boot
            // slots.
            x = px + PanelPadX(screenW, screenH) + ArmorColumnWidthPx(screenW, screenH);
            y = catalogTop;
            w = GridWidthPx(screenW, screenH);
            h = CatalogRows * SlotPx(screenW, screenH);
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
        // Pass creative=true when called from creative-mode handlers so the
        // hit rects line up with the (taller) creative panel.
        public static int HitTestHotbar(int screenW, int screenH, int mx, int my)
            => HitTestHotbar(screenW, screenH, mx, my, /*creative*/false);

        public static int HitTestHotbar(int screenW, int screenH, int mx, int my, bool creative)
        {
            // Tier 4 #19 — Iterate only the hotbar range; armor slots
            // sit past the hotbar in the slot index space but aren't
            // a quickbar target. Limiting the range to MainSlotCount
            // .. MainSlotCount+HotbarSlotCount keeps creative-mode
            // hotbar drops from accidentally filling the helmet slot.
            for (int i = MainSlotCount; i < MainSlotCount + HotbarSlotCount; i++)
            {
                GetSlotRect(i, screenW, screenH, creative, out int sx, out int sy, out int sw, out int sh);
                if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) return i;
            }
            return -1;
        }

        // Tier 4 #19 — Hit-test the armor column. Returns the absolute
        // slot index in Inventory.Slots (ArmorStart..ArmorStart+ArmorCount-1)
        // or -1 if (mx,my) isn't on an armor cell. Used by the creative
        // click router so the player can equip armor in the GUI without
        // dropping into survival mode — without this pass the four armor
        // slots fell through to the "outside everything" cursor-toss
        // branch and looked broken.
        public static int HitTestArmor(int screenW, int screenH, int mx, int my, bool creative)
        {
            int armorStart = MainSlotCount + HotbarSlotCount;
            int armorEnd   = armorStart + ArmorSlotCount;
            for (int i = armorStart; i < armorEnd; i++)
            {
                GetSlotRect(i, screenW, screenH, creative, out int sx, out int sy, out int sw, out int sh);
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
