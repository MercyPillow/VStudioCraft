namespace VStudioCraft.Game
{
    // Furnace screen layout. Alpha furnace UI:
    //
    //   ┌──────────── FURNACE ───────────────┐
    //   │       □ (input)                    │
    //   │           ━━━▶  □ (output)         │
    //   │       □ (fuel)                     │
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
    //   0       furnace input (smelt source)
    //   1       furnace fuel
    //   2       furnace output
    //   3..38   player main inventory (36 slots, mirrors Inventory.Slots[0..35])
    //   39..47  player hotbar (mirrors Inventory.Slots[36..44])
    //
    // The flame icon between input/fuel fills bottom-up based on the
    // current fuel's burnTime/maxBurnTime ratio. The arrow between
    // input/output fills left-to-right based on cookProgress/CookTime.
    // The screen reads these values from the FurnaceTileEntity via the
    // renderer; the screen geometry doesn't store them.
    //
    // The player inventory backing array is the SAME Inventory used by
    // the hotbar — opening the furnace screen doesn't fork inventory
    // state. The 3 furnace slots live in the FurnaceTileEntity for the
    // block the player is interacting with; the screen merely binds to
    // it for the duration of the modal.
    internal static class FurnaceScreen
    {
        public const int InputSlot  = 0;
        public const int FuelSlot   = 1;
        public const int OutputSlot = 2;
        public const int FurnaceSlotCount = 3;

        public const int InvMainStart  = 3;
        public const int InvMainCount  = 36;
        public const int InvHotbarStart = InvMainStart + InvMainCount; // 39
        public const int InvHotbarCount = 9;
        public const int TotalSlots     = InvHotbarStart + InvHotbarCount; // 48

        // ---- base (scale=1) pixel sizes — match CraftingScreen so the
        // panels feel consistent.
        private const int SlotPxBase       = 55;
        private const int IconPxBase       = 44;
        private const int SlotBorderPxBase = 2;
        private const int HotbarGapBase    = 10;
        private const int PanelPadXBase    = 17;
        private const int PanelPadYBase    = 17;
        private const int TitleScaleBase   = 2;
        private const int TitleGapBase     = 14;
        private const int FurnaceBlockGapBase = 14;
        // Horizontal gap between the input/fuel column and the output slot
        // (the arrow zone, mirrors CraftingScreen.ArrowZoneBase).
        private const int ArrowZoneBase    = 70;
        // Vertical gap between input and fuel slots (where the flame
        // icon sits).
        private const int FlameZoneBase    = 22;
        private const int HotbarGapAboveBase = 40;

        public const string Title = "FURNACE";

        public static int SlotPx(int viewW, int viewH)         => UiScale.S(SlotPxBase, viewW, viewH);
        public static int IconPx(int viewW, int viewH)         => UiScale.S(IconPxBase, viewW, viewH);
        public static int SlotBorderPx(int viewW, int viewH)   => UiScale.S(SlotBorderPxBase, viewW, viewH);
        public static int HotbarGap(int viewW, int viewH)      => UiScale.S(HotbarGapBase, viewW, viewH);
        public static int PanelPadX(int viewW, int viewH)      => UiScale.S(PanelPadXBase, viewW, viewH);
        public static int PanelPadY(int viewW, int viewH)      => UiScale.S(PanelPadYBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)       => UiScale.S(TitleGapBase, viewW, viewH);
        public static int FurnaceBlockGap(int viewW, int viewH)=> UiScale.S(FurnaceBlockGapBase, viewW, viewH);
        public static int ArrowZonePx(int viewW, int viewH)    => UiScale.S(ArrowZoneBase, viewW, viewH);
        public static int FlameZonePx(int viewW, int viewH)    => UiScale.S(FlameZoneBase, viewW, viewH);

        public static int TitleScale(int viewW, int viewH)
        {
            int s = (int)(TitleScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }
        public static int TitleHeight(int viewW, int viewH)
            => HotbarTextures.GlyphCellH * TitleScale(viewW, viewH);

        private static int InvGridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 9;
        private static int InvGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 4 + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);
        // Furnace-block height: input slot + flame zone + fuel slot.
        private static int FurnaceBlockHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 2 + FlameZonePx(viewW, viewH);

        public static int PanelWidth(int viewW, int viewH)
            => InvGridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
        {
            return PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                 + FurnaceBlockHeightPx(viewW, viewH) + FurnaceBlockGap(viewW, viewH)
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

        // Top-left of the input slot, output slot, and fuel slot. The
        // input/fuel column is centred horizontally inside the panel
        // alongside the output slot to the right (with the arrow zone
        // in between).
        private static void GetFurnaceBlockOrigin(int screenW, int screenH,
            out int inputX, out int inputY,
            out int fuelX,  out int fuelY,
            out int outputX, out int outputY)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            int titleH = TitleHeight(screenW, screenH);
            int titleGap = TitleGap(screenW, screenH);
            int arrowZone = ArrowZonePx(screenW, screenH);
            int flameZone = FlameZonePx(screenW, screenH);
            int blockTop = py + padY + titleH + titleGap;

            int totalW = slot + arrowZone + slot;
            int blockX = px + padX + ((PanelWidth(screenW, screenH) - padX * 2) - totalW) / 2;

            inputX = blockX;
            inputY = blockTop;
            fuelX = blockX;
            fuelY = blockTop + slot + flameZone;
            outputX = blockX + slot + arrowZone;
            // Output slot vertically centred on the input/fuel column.
            outputY = blockTop + (FurnaceBlockHeightPx(screenW, screenH) - slot) / 2;
        }

        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            GetFurnaceBlockOrigin(screenW, screenH,
                out int inputX, out int inputY,
                out int fuelX, out int fuelY,
                out int outputX, out int outputY);

            if (slotIndex == InputSlot)       { x = inputX;  y = inputY;  }
            else if (slotIndex == FuelSlot)   { x = fuelX;   y = fuelY;   }
            else if (slotIndex == OutputSlot) { x = outputX; y = outputY; }
            else
            {
                // Inventory section anchors below the furnace block,
                // FurnaceBlockGap further down.
                int blockTop = inputY;
                int blockBottom = fuelY + slot;
                int invTop = blockBottom + FurnaceBlockGap(screenW, screenH);
                int invX0 = px + padX;
                int local = slotIndex - InvMainStart;
                int gap = HotbarGap(screenW, screenH);
                if (local < InvMainCount)
                {
                    int row = local / 9;
                    int col = local % 9;
                    x = invX0 + col * slot;
                    y = invTop + row * slot;
                }
                else
                {
                    int hCol = local - InvMainCount;
                    x = invX0 + hCol * slot;
                    y = invTop + 4 * slot + gap;
                }
            }
            w = slot;
            h = slot;
        }

        // Centre of the arrow zone (between input and output slots).
        // The arrow's horizontal fill ratio is cookProgress/CookTime.
        public static void GetArrowCenter(int screenW, int screenH,
            out int cx, out int cy)
        {
            GetFurnaceBlockOrigin(screenW, screenH,
                out int inputX, out int inputY, out _, out _,
                out int outputX, out _);
            int slot = SlotPx(screenW, screenH);
            cx = (inputX + slot + outputX) / 2;
            // Arrow is at the vertical midpoint of the furnace block
            // (matches the centred output slot).
            cy = inputY + (FurnaceBlockHeightPx(screenW, screenH)) / 2;
        }

        // Centre of the flame zone (between input and fuel slots). The
        // flame fills bottom-up as the current fuel unit burns down.
        public static void GetFlameCenter(int screenW, int screenH,
            out int cx, out int cy)
        {
            GetFurnaceBlockOrigin(screenW, screenH,
                out int inputX, out int inputY,
                out _, out int fuelY, out _, out _);
            int slot = SlotPx(screenW, screenH);
            cx = inputX + slot / 2;
            cy = (inputY + slot + fuelY) / 2;
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
        // for the furnace slots (caller handles those separately).
        public static int InventoryIndexFor(int slotIndex)
        {
            if (slotIndex < InvMainStart) return -1;
            return slotIndex - InvMainStart;
        }
    }
}
