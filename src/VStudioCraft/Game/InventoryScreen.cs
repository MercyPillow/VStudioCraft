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
    internal static class InventoryScreen
    {
        public const int Cols = 9;
        public const int MainRows = 4;

        // Slot pixel size — bumped from the previous 46-px well by ~20% so
        // the larger panel reads as one tier up from the hotbar without
        // making icons feel cramped. Icon size keeps the same ratio so a
        // block icon in the inventory and on the hotbar render at
        // proportional sizes.
        public const int SlotPx = 55;
        public const int IconPx = 44;
        public const int SlotBorderPx = 2;

        // Vertical breathing room between the bottom of the main grid and
        // the top of the hotbar row. Reads as "those 9 slots are the
        // hotbar" without needing a separator line. Scaled with SlotPx.
        public const int HotbarGap = 10;

        // Outer panel padding around the slot grid (excluding the title row).
        public const int PanelPadX = 17;
        public const int PanelPadY = 17;

        // Title region above the grid: scale-2 glyph (16 px) plus 14 px
        // breathing room below it before the first slot row.
        public const int TitleScale = 2;
        public const int TitleHeight = HotbarTextures.GlyphCellH * TitleScale; // 16
        public const int TitleGap = 14;

        public const int MainSlotCount = Cols * MainRows;        // 36
        public const int HotbarSlotCount = Cols;                 // 9
        public const int TotalSlots = MainSlotCount + HotbarSlotCount; // 45

        public const string Title = "INVENTORY";

        // ---- creative-mode extras ---------------------------------------
        // Search bar sits where the title gap would normally be — replaces
        // the main grid entirely. CatalogRows defines how many rows of
        // catalog tiles render before the rest scrolls; each tile is the
        // same SlotPx as a real slot so icons line up visually.
        public const int SearchBarHeight = 26;
        public const int SearchBarGap   = 8;   // gap below search bar before catalog grid
        public const int CatalogRows = MainRows; // catalog area covers the same vertical region

        // ---- panel geometry ----------------------------------------------

        private static int GridWidthPx  => SlotPx * Cols;
        private static int GridHeightPx => SlotPx * MainRows + HotbarGap + SlotPx;

        public static int PanelWidth  => GridWidthPx + PanelPadX * 2;
        public static int PanelHeight => PanelPadY + TitleHeight + TitleGap
                                       + GridHeightPx + PanelPadY;

        // Top-left corner of the panel, screen-centred.
        public static void GetPanelRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            w = PanelWidth;
            h = PanelHeight;
            x = (screenW - w) / 2;
            y = (screenH - h) / 2;
        }

        // Title is centred horizontally; this returns the Y of its top edge.
        public static int TitleY(int screenW, int screenH)
        {
            GetPanelRect(screenW, screenH, out _, out int py, out _, out _);
            return py + PanelPadY;
        }

        // Slot rect for slotIndex 0..(TotalSlots-1).
        //   0..(MainSlotCount-1) — main inventory, row-major top-down, 9 per row.
        //   MainSlotCount..(TotalSlots-1) — hotbar (left → right), drawn
        //     HotbarGap below the main grid.
        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int gridX0 = px + PanelPadX;
            int gridY0 = py + PanelPadY + TitleHeight + TitleGap;

            int col = slotIndex % Cols;
            if (slotIndex < MainSlotCount)
            {
                int row = slotIndex / Cols;
                x = gridX0 + col * SlotPx;
                y = gridY0 + row * SlotPx;
            }
            else
            {
                x = gridX0 + col * SlotPx;
                y = gridY0 + MainRows * SlotPx + HotbarGap;
            }
            w = SlotPx;
            h = SlotPx;
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
            x = px + PanelPadX;
            y = py + PanelPadY + TitleHeight + TitleGap;
            w = GridWidthPx;
            h = SearchBarHeight;
        }

        // Catalog grid rect — sits below the search bar, fills the rest of
        // the main-grid region down to the hotbar gap. The renderer places
        // catalog tiles on the same SlotPx grid so icons line up with the
        // hotbar columns visually.
        public static void GetCatalogRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int gridY0 = py + PanelPadY + TitleHeight + TitleGap;
            int catalogTop = gridY0 + SearchBarHeight + SearchBarGap;
            x = px + PanelPadX;
            y = catalogTop;
            w = GridWidthPx;
            // Bottom of catalog = top of hotbar row - HotbarGap. The catalog
            // therefore takes up the rows it has, minus the search-bar's
            // intrusion. Compute from the survival main-grid height instead
            // of redeclaring it.
            int mainBottom = gridY0 + MainRows * SlotPx;
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
            int col = (mx - cx) / SlotPx;
            int row = (my - cy) / SlotPx;
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
