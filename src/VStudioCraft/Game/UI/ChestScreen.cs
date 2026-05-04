namespace VStudioCraft.Game
{
    // Chest screen layout. Alpha single-chest UI (chestRows=3):
    //
    //   ┌──────────── CHEST ─────────────────┐
    //   │  □ □ □ □ □ □ □ □ □                 │   <- chest row 0
    //   │  □ □ □ □ □ □ □ □ □                 │   <- chest row 1
    //   │  □ □ □ □ □ □ □ □ □                 │   <- chest row 2
    //   │  ────────────────────────────      │   <- divider
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 0
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 1
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 2
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 3
    //   │                                     │
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player hotbar
    //   └─────────────────────────────────────┘
    //
    // Tier 10 #53 — large-chest variant (chestRows=6) doubles the chest
    // grid to 6×9=54 slots. The lower 27 slots map to the secondary
    // half of the pair; GameRenderer.HandleChestClick splits the click
    // across the two ChestTileEntities.
    //
    // Slot indexing convention (chestRows = number of chest rows, 3 or 6):
    //   0..(chestRows*9 - 1)              chest slots (top→bottom, left→right)
    //   chestRows*9 .. chestRows*9+35     player main inventory (36 slots)
    //   chestRows*9+36 .. chestRows*9+44  player hotbar (9 slots)
    //
    // The player inventory backing array is the SAME Inventory used by the
    // hotbar — opening a chest doesn't fork inventory state. The chest
    // slots live in the ChestTileEntity / pair the player opened; the
    // screen merely binds to it for the duration of the modal.
    internal static class ChestScreen
    {
        // Single-chest constants — kept for backward compat with call
        // sites that don't care about double chests (server protocol,
        // multiplayer-only paths).
        public const int ChestSlotCount  = ChestTileEntity.SlotCount; // 27
        public const int ChestStart      = 0;
        public const int InvMainStart    = ChestSlotCount;             // 27
        public const int InvMainCount    = 36;
        public const int InvHotbarStart  = InvMainStart + InvMainCount; // 63
        public const int InvHotbarCount  = 9;
        public const int TotalSlots      = InvHotbarStart + InvHotbarCount; // 72

        public const int DoubleChestSlotCount = ChestTileEntity.SlotCount * 2; // 54
        public const string Title            = "CHEST";
        public const string DoubleTitle      = "LARGE CHEST";

        // ---- base (scale=1) pixel sizes — match FurnaceScreen so the
        // panels feel consistent.
        private const int SlotPxBase       = 55;
        private const int IconPxBase       = 44;
        private const int SlotBorderPxBase = 2;
        private const int HotbarGapBase    = 10;
        private const int PanelPadXBase    = 17;
        private const int PanelPadYBase    = 17;
        private const int TitleScaleBase   = 2;
        private const int TitleGapBase     = 14;
        // Vertical gap between the chest grid and the player inventory.
        private const int ChestInvGapBase  = 14;
        private const int HotbarGapAboveBase = 40;

        public static int SlotPx(int viewW, int viewH)         => UiScale.S(SlotPxBase, viewW, viewH);
        public static int IconPx(int viewW, int viewH)         => UiScale.S(IconPxBase, viewW, viewH);
        public static int SlotBorderPx(int viewW, int viewH)   => UiScale.S(SlotBorderPxBase, viewW, viewH);
        public static int HotbarGap(int viewW, int viewH)      => UiScale.S(HotbarGapBase, viewW, viewH);
        public static int PanelPadX(int viewW, int viewH)      => UiScale.S(PanelPadXBase, viewW, viewH);
        public static int PanelPadY(int viewW, int viewH)      => UiScale.S(PanelPadYBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)       => UiScale.S(TitleGapBase, viewW, viewH);
        public static int ChestInvGap(int viewW, int viewH)    => UiScale.S(ChestInvGapBase, viewW, viewH);

        public static int TitleScale(int viewW, int viewH)
        {
            int s = (int)(TitleScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }
        public static int TitleHeight(int viewW, int viewH)
            => HotbarTextures.GlyphCellH * TitleScale(viewW, viewH);

        // ---- per-row layout helpers ------------------------------------
        public static int ChestSlotsFor(int chestRows)   => chestRows * 9;
        public static int InvMainStartFor(int chestRows) => chestRows * 9;
        public static int InvHotbarStartFor(int chestRows) => chestRows * 9 + 36;
        public static int TotalSlotsFor(int chestRows)   => chestRows * 9 + 45;
        public static string TitleFor(int chestRows)     => chestRows >= 6 ? DoubleTitle : Title;

        private static int GridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 9;
        private static int ChestGridHeightPx(int viewW, int viewH, int chestRows)
            => SlotPx(viewW, viewH) * chestRows;
        private static int InvGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 4 + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);

        public static int PanelWidth(int viewW, int viewH)
            => GridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH, int chestRows = 3)
        {
            return PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                 + ChestGridHeightPx(viewW, viewH, chestRows) + ChestInvGap(viewW, viewH)
                 + InvGridHeightPx(viewW, viewH) + PanelPadY(viewW, viewH);
        }

        public static void GetPanelRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h, int chestRows = 3)
        {
            w = PanelWidth(screenW, screenH);
            h = PanelHeight(screenW, screenH, chestRows);
            x = (screenW - w) / 2;

            int centeredY = (screenH - h) / 2;
            int hotbarTop = HotbarLayout.BarTopY(screenW, screenH);
            int gap = UiScale.S(HotbarGapAboveBase, screenW, screenH);
            int maxY = hotbarTop - gap - h;

            y = centeredY < maxY ? centeredY : maxY;
            if (y < 0) y = 0;
        }

        public static int TitleY(int screenW, int screenH, int chestRows = 3)
        {
            GetPanelRect(screenW, screenH, out _, out int py, out _, out _, chestRows);
            return py + PanelPadY(screenW, screenH);
        }

        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h, int chestRows = 3)
        {
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _, chestRows);
            int titleH = TitleHeight(screenW, screenH);
            int titleGap = TitleGap(screenW, screenH);
            int chestTop = py + padY + titleH + titleGap;
            int chestBottom = chestTop + ChestGridHeightPx(screenW, screenH, chestRows);
            int invTop = chestBottom + ChestInvGap(screenW, screenH);
            int gridX0 = px + padX;
            int gap = HotbarGap(screenW, screenH);

            int invMainStart   = InvMainStartFor(chestRows);
            int invHotbarStart = InvHotbarStartFor(chestRows);

            if (slotIndex < invMainStart)
            {
                // Chest slots — chestRows×9 grid, row-major.
                int local = slotIndex;
                int row = local / 9;
                int col = local % 9;
                x = gridX0 + col * slot;
                y = chestTop + row * slot;
            }
            else if (slotIndex < invHotbarStart)
            {
                // Player main — 9×4 grid below the chest grid.
                int local = slotIndex - invMainStart;
                int row = local / 9;
                int col = local % 9;
                x = gridX0 + col * slot;
                y = invTop + row * slot;
            }
            else
            {
                // Player hotbar — 9×1, with HotbarGap above.
                int hCol = slotIndex - invHotbarStart;
                x = gridX0 + hCol * slot;
                y = invTop + 4 * slot + gap;
            }
            w = slot;
            h = slot;
        }

        public static int HitTest(int screenW, int screenH, int mx, int my, int chestRows = 3)
        {
            int total = TotalSlotsFor(chestRows);
            for (int i = 0; i < total; i++)
            {
                GetSlotRect(i, screenW, screenH, out int sx, out int sy, out int sw, out int sh, chestRows);
                if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) return i;
            }
            return -1;
        }

        // Map screen-space slot index to backing inventory index, or -1
        // for chest slots (caller handles those separately).
        public static int InventoryIndexFor(int slotIndex, int chestRows = 3)
        {
            int invMainStart = InvMainStartFor(chestRows);
            if (slotIndex < invMainStart) return -1;
            return slotIndex - invMainStart;
        }

        // Map screen-space slot index to chest entity index, or -1 for
        // inventory slots. For double-chest the index is in 0..53; the
        // caller dispatches 0..26→primary, 27..53→secondary.
        public static int ChestIndexFor(int slotIndex, int chestRows = 3)
        {
            int invMainStart = InvMainStartFor(chestRows);
            if (slotIndex >= 0 && slotIndex < invMainStart) return slotIndex;
            return -1;
        }
    }
}
