namespace VStudioCraft.Game
{
    // Inventory screen layout. Mirrors PauseMenu — the renderer reads slot
    // rectangles from here, and the host (eventually, when ItemStack lands)
    // hit-tests clicks against the same rects so the two threads can never
    // disagree about where a slot lives.
    //
    // The on-screen layout matches Alpha 1.1.2_01 inventory geometry:
    //   ┌──────────── INVENTORY ────────────┐
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 0 (slots 0..8)
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 1 (slots 9..17)
    //   │  □ □ □ □ □ □ □ □ □                │   <- main row 2 (slots 18..26)
    //   │                                    │   <- HotbarGap separator
    //   │  □ □ □ □ □ □ □ □ □                │   <- hotbar row  (slots 27..35)
    //   └────────────────────────────────────┘
    //
    // For now there is no ItemStack model — see features.md "Inventory /
    // items" Missing list. The bottom row mirrors the live hotbar so the
    // player can see what they're carrying; the 27 main-grid slots render
    // as empty wells. When item storage arrives, click handling lands in
    // GameHostControl using the same GetSlotRect lookup.
    internal static class InventoryScreen
    {
        public const int Cols = 9;
        public const int MainRows = 3;

        // Slot pixel size — matches the in-game hotbar's scaled slot well
        // (HotbarTextures.SlotInner * 2.3 ≈ 46) so a block icon in the
        // inventory and on the hotbar render the same physical size.
        public const int SlotPx = 46;
        public const int IconPx = 37;
        public const int SlotBorderPx = 2;

        // Vertical breathing room between the bottom of the main 3×9 grid
        // and the top of the hotbar row. Reads as "those 9 slots are the
        // hotbar" without needing a separator line.
        public const int HotbarGap = 8;

        // Outer panel padding around the slot grid (excluding the title row).
        public const int PanelPadX = 14;
        public const int PanelPadY = 14;

        // Title region above the grid: scale-2 glyph (16 px) plus 12 px
        // breathing room below it before the first slot row.
        public const int TitleScale = 2;
        public const int TitleHeight = HotbarTextures.GlyphCellH * TitleScale; // 16
        public const int TitleGap = 12;

        public const int MainSlotCount = Cols * MainRows;        // 27
        public const int HotbarSlotCount = Cols;                 // 9
        public const int TotalSlots = MainSlotCount + HotbarSlotCount; // 36

        public const string Title = "INVENTORY";

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

        // Slot rect for slotIndex 0..35.
        //   0..26 — main inventory, row-major top-down, 9 per row.
        //   27..35 — hotbar (left → right), drawn HotbarGap below the main grid.
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
    }
}
