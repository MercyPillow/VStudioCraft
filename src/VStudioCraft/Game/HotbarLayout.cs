namespace VStudioCraft.Game
{
    // On-screen hotbar layout — single source of truth for the bar's
    // pixel dimensions and per-slot positions. Both the HUD renderer
    // (which draws icons on top of the bar texture) and the inventory
    // panel (which anchors itself above the hotbar) read from here so
    // there is exactly one place that knows where the bar lives.
    //
    // The tricky bit, and the bug this module replaced: the bar texture
    // is 182×22 with a 1-px frame and 9 × 20-px slot wells. Previously
    // each of BarPx / BarH / SlotPx / IconPx / FramePx was computed by
    // multiplying its source-pixel size by a float Scale and casting
    // independently to int. At non-integer scales those rounded
    // separately, the slot pitch drifted out of sync with the bar's
    // actual width, and the icons stopped lining up with the wells in
    // the bar texture.
    //
    // The fix: derive every dimension from a single PixelScale =
    // BarPx / BarWidth (computed once, rounded once). Slot/icon/
    // highlight rectangles are then expressed in source-texture
    // coordinates and converted at draw time, so they always land on
    // the same texel a player sees on the bar — at any viewport size.
    //
    // The bar's aspect ratio (182:22 ≈ 8.27:1) is also locked here:
    // BarH is derived from BarPx via the source ratio, not by a second
    // independent rounding, so the chrome can't squash when the
    // viewport changes.
    internal static class HotbarLayout
    {
        // Base scale: 2× pixel-double + 15% chunky bump (the look we
        // shipped at the original tool-window size). UiScale layers on
        // top so the bar grows with the viewport.
        private const float BaseScale = 2.3f;

        // Distance from the bottom of the bar to the bottom of the
        // viewport — base value, fed through UiScale so it grows with
        // the rest of the chrome.
        private const int BottomMarginBase = 28;

        // Source-pixel → screen-pixel scale. ALL hotbar metrics route
        // through this so they can never round inconsistently.
        public static float PixelScale(int viewW, int viewH)
            => BaseScale * UiScale.For(viewW, viewH);

        public static int BarPx(int viewW, int viewH)
            => (int)(HotbarTextures.BarWidth * PixelScale(viewW, viewH) + 0.5f);

        // Lock the bar to the source 182:22 aspect ratio. Computing
        // BarH from BarPx (rather than from BarHeight × scale rounded
        // independently) guarantees the bar can't squish at any scale.
        public static int BarH(int viewW, int viewH)
        {
            int bp = BarPx(viewW, viewH);
            return (int)((float)bp * HotbarTextures.BarHeight / HotbarTextures.BarWidth + 0.5f);
        }

        public static int BarX(int viewW, int viewH)
            => (viewW - BarPx(viewW, viewH)) / 2;

        public static int BarTopY(int viewW, int viewH)
            => viewH - BarH(viewW, viewH) - UiScale.S(BottomMarginBase, viewW, viewH);

        public static int BottomMargin(int viewW, int viewH)
            => UiScale.S(BottomMarginBase, viewW, viewH);

        // Effective per-axis screen-pixels-per-source-pixel. Same as
        // PixelScale within rounding (BarPx = round(BarWidth * scale))
        // — exposed here so callers can convert source-tex coordinates
        // (which is the natural way to specify slot rects) into screen
        // coordinates without recomputing the scale.
        public static float SrcPxPerScreenPxX(int viewW, int viewH)
            => (float)BarPx(viewW, viewH) / HotbarTextures.BarWidth;
        public static float SrcPxPerScreenPxY(int viewW, int viewH)
            => (float)BarH(viewW, viewH) / HotbarTextures.BarHeight;

        // Slot rect (the well inside the bar's frame), expressed in
        // screen pixels. Slot i in the source texture occupies the
        // x-range [1 + i*20, 1 + (i+1)*20], y-range [1, 21]. Converting
        // through the source-pixel scale keeps slots locked to the
        // wells visible on the bar texture even at non-integer scales.
        public static void GetSlotRect(int slotIndex, int viewW, int viewH,
            out int x, out int y, out int w, out int h)
        {
            float sx = SrcPxPerScreenPxX(viewW, viewH);
            float sy = SrcPxPerScreenPxY(viewW, viewH);
            int barX = BarX(viewW, viewH);
            int barY = BarTopY(viewW, viewH);
            float xL = barX + (1 + slotIndex * HotbarTextures.SlotInner) * sx;
            float xR = barX + (1 + (slotIndex + 1) * HotbarTextures.SlotInner) * sx;
            float yT = barY + 1 * sy;
            float yB = barY + (1 + HotbarTextures.SlotInner) * sy;
            x = (int)(xL + 0.5f);
            y = (int)(yT + 0.5f);
            w = (int)(xR + 0.5f) - x;
            h = (int)(yB + 0.5f) - y;
        }

        // Icon rect (centered in the slot well). The source texture
        // pads the 16-px icon with a 2-px gutter on each side of the
        // 20-px well, so we drive everything off that 2-px gutter.
        public static void GetIconRect(int slotIndex, int viewW, int viewH,
            out int x, out int y, out int w, out int h)
        {
            float sx = SrcPxPerScreenPxX(viewW, viewH);
            float sy = SrcPxPerScreenPxY(viewW, viewH);
            int barX = BarX(viewW, viewH);
            int barY = BarTopY(viewW, viewH);
            const int IconPadSrc = (HotbarTextures.SlotInner - HotbarTextures.IconInner) / 2; // 2
            float xL = barX + (1 + slotIndex * HotbarTextures.SlotInner + IconPadSrc) * sx;
            float xR = xL + HotbarTextures.IconInner * sx;
            float yT = barY + (1 + IconPadSrc) * sy;
            float yB = yT + HotbarTextures.IconInner * sy;
            x = (int)(xL + 0.5f);
            y = (int)(yT + 0.5f);
            w = (int)(xR + 0.5f) - x;
            h = (int)(yB + 0.5f) - y;
        }

        // Selected-slot highlight rect (24×24 in source pixels, drawn
        // 2 px wider than the well so the frame visibly overlaps the
        // bar). Centred on the slot's centre.
        public static void GetHighlightRect(int slotIndex, int viewW, int viewH,
            out int x, out int y, out int w, out int h)
        {
            float sx = SrcPxPerScreenPxX(viewW, viewH);
            float sy = SrcPxPerScreenPxY(viewW, viewH);
            int barX = BarX(viewW, viewH);
            int barY = BarTopY(viewW, viewH);
            float cx = barX + (1 + slotIndex * HotbarTextures.SlotInner + HotbarTextures.SlotInner / 2f) * sx;
            float cy = barY + (HotbarTextures.BarHeight / 2f) * sy;
            int hw = (int)(HotbarTextures.HighlightSize * sx + 0.5f);
            int hh = (int)(HotbarTextures.HighlightSize * sy + 0.5f);
            x = (int)(cx - hw / 2f + 0.5f);
            y = (int)(cy - hh / 2f + 0.5f);
            w = hw;
            h = hh;
        }
    }
}
