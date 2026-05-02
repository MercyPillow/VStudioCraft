namespace VStudioCraft.Game
{
    // Tier 8 #49 V2 — Dispenser screen layout. Smaller cousin of
    // ChestScreen: 3×3 grid for the dispenser's 9 slots stacked
    // above the player's main inventory + hotbar.
    //
    //   ┌──────────── DISPENSER ─────────────┐
    //   │            □ □ □                    │   <- dispenser row 0
    //   │            □ □ □                    │   <- dispenser row 1
    //   │            □ □ □                    │   <- dispenser row 2
    //   │  ────────────────────────────       │   <- divider
    //   │  □ □ □ □ □ □ □ □ □                  │   <- player main row 0
    //   │  □ □ □ □ □ □ □ □ □                  │   <- player main row 1
    //   │  □ □ □ □ □ □ □ □ □                  │   <- player main row 2
    //   │  □ □ □ □ □ □ □ □ □                  │   <- player main row 3
    //   │                                      │
    //   │  □ □ □ □ □ □ □ □ □                  │   <- player hotbar
    //   └──────────────────────────────────────┘
    //
    // Slot indexing convention:
    //   0..8     dispenser slots (mirrors DispenserTileEntity.Slots[0..8])
    //   9..44    player main inventory (36 slots)
    //   45..53   player hotbar (9 slots)
    //
    // The dispenser's 3×3 grid is centered horizontally above the
    // 9-wide player inventory grid — looks balanced and matches the
    // canonical Alpha layout where smaller container UIs (furnace,
    // dispenser) inset their grid against the wider player section.
    internal static class DispenserScreen
    {
        public const int DispenserSlotCount = DispenserTileEntity.SlotCount; // 9
        public const int GridCols = 3;
        public const int GridRows = 3;

        public const int DispenserStart  = 0;
        public const int InvMainStart    = DispenserSlotCount;       // 9
        public const int InvMainCount    = 36;
        public const int InvHotbarStart  = InvMainStart + InvMainCount; // 45
        public const int InvHotbarCount  = 9;
        public const int TotalSlots      = InvHotbarStart + InvHotbarCount; // 54

        // Same base sizes as ChestScreen so the panel chrome reads
        // consistent across the chest / dispenser modal family.
        private const int SlotPxBase            = 55;
        private const int IconPxBase            = 44;
        private const int SlotBorderPxBase      = 2;
        private const int HotbarGapBase         = 10;
        private const int PanelPadXBase         = 17;
        private const int PanelPadYBase         = 17;
        private const int TitleScaleBase        = 2;
        private const int TitleGapBase          = 14;
        private const int DispenserInvGapBase   = 14;
        private const int HotbarGapAboveBase    = 40;

        public const string Title = "DISPENSER";

        public static int SlotPx(int viewW, int viewH)         => UiScale.S(SlotPxBase, viewW, viewH);
        public static int IconPx(int viewW, int viewH)         => UiScale.S(IconPxBase, viewW, viewH);
        public static int SlotBorderPx(int viewW, int viewH)   => UiScale.S(SlotBorderPxBase, viewW, viewH);
        public static int HotbarGap(int viewW, int viewH)      => UiScale.S(HotbarGapBase, viewW, viewH);
        public static int PanelPadX(int viewW, int viewH)      => UiScale.S(PanelPadXBase, viewW, viewH);
        public static int PanelPadY(int viewW, int viewH)      => UiScale.S(PanelPadYBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)       => UiScale.S(TitleGapBase, viewW, viewH);
        public static int DispenserInvGap(int viewW, int viewH) => UiScale.S(DispenserInvGapBase, viewW, viewH);

        public static int TitleScale(int viewW, int viewH)
        {
            int s = (int)(TitleScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }
        public static int TitleHeight(int viewW, int viewH)
            => HotbarTextures.GlyphCellH * TitleScale(viewW, viewH);

        // Panel width is sized for the wider 9-slot player inventory;
        // the 3-slot dispenser grid is centred inside that width.
        private static int PlayerGridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 9;
        private static int DispenserGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * GridRows;
        private static int InvGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 4 + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);

        public static int PanelWidth(int viewW, int viewH)
            => PlayerGridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
        {
            return PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                 + DispenserGridHeightPx(viewW, viewH) + DispenserInvGap(viewW, viewH)
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
            int dispTop = py + padY + titleH + titleGap;
            int dispBottom = dispTop + DispenserGridHeightPx(screenW, screenH);
            int invTop = dispBottom + DispenserInvGap(screenW, screenH);
            int gridX0 = px + padX;
            int gap = HotbarGap(screenW, screenH);
            int playerGridWidth = PlayerGridWidthPx(screenW, screenH);
            // Centre the 3×3 dispenser grid horizontally inside the
            // 9-wide player section.
            int dispGridX0 = gridX0 + (playerGridWidth - slot * GridCols) / 2;

            if (slotIndex < InvMainStart)
            {
                // Dispenser slots — 3×3 grid, row-major.
                int local = slotIndex - DispenserStart;
                int row = local / GridCols;
                int col = local % GridCols;
                x = dispGridX0 + col * slot;
                y = dispTop + row * slot;
            }
            else if (slotIndex < InvHotbarStart)
            {
                int local = slotIndex - InvMainStart;
                int row = local / 9;
                int col = local % 9;
                x = gridX0 + col * slot;
                y = invTop + row * slot;
            }
            else
            {
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
        // for dispenser slots (caller handles those separately).
        public static int InventoryIndexFor(int slotIndex)
        {
            if (slotIndex < InvMainStart) return -1;
            return slotIndex - InvMainStart;
        }

        // Map screen-space slot index to dispenser entity index, or -1
        // for inventory slots.
        public static int DispenserIndexFor(int slotIndex)
        {
            if (slotIndex >= DispenserStart && slotIndex < InvMainStart) return slotIndex;
            return -1;
        }
    }
}
