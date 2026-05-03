namespace VStudioCraft.Game
{
    // Crafting-table screen layout. Mirrors InventoryScreen's static-helper
    // pattern so the renderer + click router never disagree about slot
    // rectangles. Geometry:
    //
    //   ┌──────────── CRAFTING ──────────────┐
    //   │       □ □ □                        │   <- 3×3 grid (slots 0..8)
    //   │       □ □ □    →    □              │   <- arrow + output (slot 9)
    //   │       □ □ □                        │
    //   │  ─────────────────────────────     │   <- divider
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 0 (10..18)
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 1 (19..27)
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 2 (28..36)
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player main row 3 (37..45)
    //   │                                     │   <- HotbarGap separator
    //   │  □ □ □ □ □ □ □ □ □                 │   <- player hotbar (46..54)
    //   └─────────────────────────────────────┘
    //
    // Slot indexing convention (consistent across renderer + click router):
    //   0..8    crafting input grid (row-major: index = row*3 + col)
    //   9       crafting output slot (read-only — clicks pull from it)
    //   10..45  player main inventory (36 slots, mirrors Inventory.Slots[0..35])
    //   46..54  player hotbar (mirrors Inventory.Slots[36..44])
    //
    // The inventory backing array is the SAME Inventory used by the
    // hotbar — opening the crafting screen doesn't fork inventory state.
    // Only the 3×3 grid + output slot are owned by the screen (held on
    // GameRenderer). When the screen closes, anything left in the grid
    // tosses back to the player like a closed-with-cursor inventory.
    internal static class CraftingScreen
    {
        public const int GridCols = 3;
        public const int GridRows = 3;
        public const int GridSlotCount = GridCols * GridRows; // 9
        public const int OutputSlot = 9;

        // Slot index ranges (inclusive lo, exclusive hi).
        public const int InvMainStart  = 10;
        public const int InvMainCount  = 36;
        public const int InvHotbarStart = InvMainStart + InvMainCount; // 46
        public const int InvHotbarCount = 9;
        public const int TotalSlots     = InvHotbarStart + InvHotbarCount; // 55

        // ---- base (scale=1) pixel sizes — match InventoryScreen so the
        // panels feel consistent.
        private const int SlotPxBase       = 55;
        private const int IconPxBase       = 44;
        private const int SlotBorderPxBase = 2;
        private const int HotbarGapBase    = 10;
        private const int PanelPadXBase    = 17;
        private const int PanelPadYBase    = 17;
        private const int TitleScaleBase   = 2;
        private const int TitleGapBase     = 14;
        // Vertical gap between the crafting block (3×3 + output) and the
        // divider line that separates it from the player inventory.
        private const int CraftBlockGapBase = 14;
        // Horizontal gap between the 3×3 grid and the output slot.
        private const int ArrowZoneBase    = 70;
        // Floor against the on-screen hotbar — matches InventoryScreen.
        private const int HotbarGapAboveBase = 40;

        public const string Title = "CRAFTING";

        public static int SlotPx(int viewW, int viewH)        => UiScale.S(SlotPxBase, viewW, viewH);
        public static int IconPx(int viewW, int viewH)        => UiScale.S(IconPxBase, viewW, viewH);
        public static int SlotBorderPx(int viewW, int viewH)  => UiScale.S(SlotBorderPxBase, viewW, viewH);
        public static int HotbarGap(int viewW, int viewH)     => UiScale.S(HotbarGapBase, viewW, viewH);
        public static int PanelPadX(int viewW, int viewH)     => UiScale.S(PanelPadXBase, viewW, viewH);
        public static int PanelPadY(int viewW, int viewH)     => UiScale.S(PanelPadYBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)      => UiScale.S(TitleGapBase, viewW, viewH);
        public static int CraftBlockGap(int viewW, int viewH) => UiScale.S(CraftBlockGapBase, viewW, viewH);
        public static int ArrowZonePx(int viewW, int viewH)   => UiScale.S(ArrowZoneBase, viewW, viewH);

        public static int TitleScale(int viewW, int viewH)
        {
            int s = (int)(TitleScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }
        public static int TitleHeight(int viewW, int viewH)
            => HotbarTextures.GlyphCellH * TitleScale(viewW, viewH);

        // Width of the player-inventory grid (9 slots wide) — sets the
        // panel's overall width, since that's the widest row.
        private static int InvGridWidthPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 9;
        // Inventory section height: 4 main rows + gap + 1 hotbar row.
        private static int InvGridHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 4 + HotbarGap(viewW, viewH) + SlotPx(viewW, viewH);
        // Crafting-block height: 3 rows.
        private static int CraftBlockHeightPx(int viewW, int viewH)
            => SlotPx(viewW, viewH) * 3;

        public static int PanelWidth(int viewW, int viewH)
            => InvGridWidthPx(viewW, viewH) + PanelPadX(viewW, viewH) * 2;
        public static int PanelHeight(int viewW, int viewH)
        {
            return PanelPadY(viewW, viewH) + TitleHeight(viewW, viewH) + TitleGap(viewW, viewH)
                 + CraftBlockHeightPx(viewW, viewH) + CraftBlockGap(viewW, viewH)
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

        // Top-left of the 3×3 crafting grid (slot 0). Centred horizontally
        // inside the panel so the grid + arrow + output reads as a balanced
        // tray, not jammed against the left edge.
        private static void GetCraftingBlockOrigin(int screenW, int screenH,
            out int gridX, out int gridY, out int outputX, out int outputY)
        {
            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);
            int padY = PanelPadY(screenW, screenH);
            int titleH = TitleHeight(screenW, screenH);
            int titleGap = TitleGap(screenW, screenH);
            int arrowZone = ArrowZonePx(screenW, screenH);
            int craftBlockTop = py + padY + titleH + titleGap;

            int gridW = slot * 3;
            int totalW = gridW + arrowZone + slot;
            int blockX = px + padX + ((PanelWidth(screenW, screenH) - padX * 2) - totalW) / 2;
            gridX = blockX;
            gridY = craftBlockTop;
            outputX = blockX + gridW + arrowZone;
            // Output slot is centred vertically on the middle row of the grid.
            outputY = craftBlockTop + slot;
        }

        // Slot rect for slotIndex 0..(TotalSlots-1).
        public static void GetSlotRect(int slotIndex, int screenW, int screenH,
            out int x, out int y, out int w, out int h)
        {
            int slot = SlotPx(screenW, screenH);
            int padX = PanelPadX(screenW, screenH);

            GetPanelRect(screenW, screenH, out int px, out int py, out _, out _);
            GetCraftingBlockOrigin(screenW, screenH, out int gridX, out int gridY, out int outputX, out int outputY);

            if (slotIndex >= 0 && slotIndex < GridSlotCount)
            {
                int row = slotIndex / GridCols;
                int col = slotIndex % GridCols;
                x = gridX + col * slot;
                y = gridY + row * slot;
            }
            else if (slotIndex == OutputSlot)
            {
                x = outputX;
                y = outputY;
            }
            else
            {
                // Inventory section anchors below the crafting block,
                // CraftBlockGap further down. Slots 10..54 map to a
                // 4×9 main grid + gap + 1×9 hotbar identical to the
                // InventoryScreen geometry.
                int invTop = gridY + slot * 3 + CraftBlockGap(screenW, screenH);
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

        // Where to draw the "→" arrow between the 3×3 grid and the output
        // slot. Returns the centre of the arrow zone vertically; renderer
        // draws a small chevron sprite or a triangle here.
        public static void GetArrowCenter(int screenW, int screenH,
            out int cx, out int cy)
        {
            GetCraftingBlockOrigin(screenW, screenH, out int gridX, out int gridY, out int outputX, out int outputY);
            int slot = SlotPx(screenW, screenH);
            cx = (gridX + slot * 3 + outputX) / 2;
            cy = gridY + slot + slot / 2; // middle of row 1
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

        // Map a "screen-space slot index" (0..54 in our coords) onto the
        // backing data: -1 if it's a craft input/output slot (caller
        // handles those separately), otherwise the corresponding
        // Inventory.Slots index (0..44 with the hotbar at 36..44).
        public static int InventoryIndexFor(int slotIndex)
        {
            if (slotIndex < InvMainStart) return -1;
            return slotIndex - InvMainStart;
        }
    }
}
