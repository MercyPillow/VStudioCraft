namespace VStudioCraft.Game
{
    // Single source of truth for the UI/HUD pixel multiplier.
    //
    // Every UI module (hotbar, survival HUD, inventory, pause menu, options
    // menu, crosshair) treats its hardcoded sizes as the "scale 1.0" design
    // baseline — the dimensions that look right when the viewport is around
    // ReferenceHeight pixels tall. At larger viewports we grow the multiplier
    // linearly with height so a fullscreen 1080p / 1440p window doesn't end
    // up with a pinhole-sized HUD. At smaller viewports we never shrink past
    // 1.0 so the design always renders at least at its intended pixel size
    // (sub-baseline windows are an IDE tool-pane case where shrinking the
    // chrome below readable would just make things worse).
    //
    // The cap stops 4K+ monitors from inflating the inventory to absurd
    // sizes — at 2.0× a 1664×1056 panel covers most of a 2160-tall display,
    // which is enough.
    //
    // Both InventoryScreen layout methods and the renderer's per-frame draws
    // call this with the current viewport size, so hit-tests always agree
    // with what the player saw on the previous frame.
    internal static class UiScale
    {
        private const float ReferenceHeight = 720f;
        private const float Min = 1.0f;
        private const float Max = 2.0f;

        public static float For(int viewW, int viewH)
        {
            if (viewH <= 0) return 1f;
            float s = viewH / ReferenceHeight;
            if (s < Min) s = Min;
            if (s > Max) s = Max;
            return s;
        }

        // Scale a base-design pixel size to the current viewport. Rounded
        // to the nearest int so adjacent calls stay pixel-aligned (e.g. a
        // grid of slots at S(SlotPx) lines up cleanly with itself).
        public static int S(int basePx, int viewW, int viewH)
        {
            return (int)(basePx * For(viewW, viewH) + 0.5f);
        }
    }
}
