namespace VStudioCraft.Game
{
    // Chest screen layout. Alpha single-chest UI:
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
    // Slot indexing convention:
    //   0..26    chest slots (mirrors ChestTileEntity.Slots[0..26])
    //   27..62   player main inventory (36 slots, mirrors Inventory.Slots[0..35])
    //   63..71   player hotbar (mirrors Inventory.Slots[36..44])
    //
    // The player inventory backing array is the SAME Inventory used by the
    // hotbar — opening a chest doesn't fork inventory state. The 27 chest
    // slots live in the ChestTileEntity for the block the player opened;
    // the screen merely binds to it for the duration of the modal.
    internal static class ChestScreen
    {
        public const int ChestSlotCount = ChestTileEntity.SlotCount; // 27

        public const int ChestStart    = 0;
        public const int InvMainStart  = ChestSlotCount;             // 27
        public const int InvMainCount  = 36;
        public const int InvHotbarStart = InvMainStart + InvMainCount; // 63
        public const int InvHotbarCount = 9;
        public const int TotalSlots     = InvHotbarStart + InvHotbarCount; // 72

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

        public const string Title = "CHEST";

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

        // 9-wide grid is shared between the chest section and the player
        // inventory section, so PanelWidth uses one slot grid width.
        private static int GridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 9;
        private static int ChestGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 3;
        private static int InvGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 4 + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);

        public static int PanelWidth(int viewW, int viewH)
            => GridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
        {
            return PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                 + ChestGridHeightPx(viewW, viewH) + ChestInvGap(viewW, viewH)
                 + InvGridHeightPx(viewW, viewH) + PanelPadY(viewW, viewH);
        }

        public static void GetPanelRect(int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            w = PanelWidth(screenW, screenH);
            h = PanelHeight(screenW, screenH);
            x = (screenW - w) / 2;

            int centeredY = (screenH - h) / 2;
            int hotbarTop = HotbarLayout.BarTopY(screenW, screenH);
            int gap = UiScale.S(HotbarGapAboveBase, screenW, screenH);
            int maxY = hotbarTop - gap - h;

            y = centeredY < maxY ? centeredY : maxY;
            if (y < 0) y = 0;
        }

        public static int TitleY(int screenW, int screenH)
        {
            GetPanelRect(screenW, screenH, out _, out int py, out _, out _);
            return py + PanelPadY(screenW, screenH);
        }

        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int titleH = TitleHeight(screenW, screenH);
            int titleGap = TitleGap(screenW, screenH);
            int chestTop = py + padY + titleH + titleGap;
            int chestBottom = chestTop + ChestGridHeightPx(screenW, screenH);
            int invTop = chestBottom + ChestInvGap(screenW, screenH);
            int gridX0 = px + padX;
            int gap = HotbarGap(screenW, screenH);

            if (slotIndex < InvMainStart)
            {
                // Chest slots — 9×3 grid, row-major.
                int local = slotIndex - ChestStart;
                int row = local / 9;
                int col = local % 9;
                x = gridX0 + col * slot;
                y = chestTop + row * slot;
            }
            else if (slotIndex < InvHotbarStart)
            {
                // Player main — 9×4 grid below the chest grid.
                int local = slotIndex - InvMainStart;
                int row = local / 9;
                int col = local % 9;
                x = gridX0 + col * slot;
                y = invTop + row * slot;
            }
            else
            {
                // Player hotbar — 9×1, with HotbarGap above.
                int hCol = slotIndex - InvHotbarStart;
                x = gridX0 + hCol * slot;
                y = invTop + 4 * slot + gap;
            }
            w = slot;
            h = slot;
        }

        public static int HitTest(int screenW, int screenH, int mx, int my)
        {
            for (int i = 0; i < TotalSlots; i++)
            {
                GetSlotRect(i, screenW, screenH, out int sx, out int sy, out int sw, out int sh);
                if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) return i;
            }
            return -1;
        }

        // Map screen-space slot index to backing inventory index, or -1
        // for chest slots (caller handles those separately).
        public static int InventoryIndexFor(int slotIndex)
        {
            if (slotIndex < InvMainStart) return -1;
            return slotIndex - InvMainStart;
        }

        // Map screen-space slot index to chest entity index, or -1 for
        // inventory slots.
        public static int ChestIndexFor(int slotIndex)
        {
            if (slotIndex >= ChestStart && slotIndex < InvMainStart) return slotIndex;
            return -1;
        }
    }
}
