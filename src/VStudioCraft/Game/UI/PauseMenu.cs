namespace VStudioCraft.Game
{
    // Pause-menu layout shared between the render thread (which draws the
    // buttons) and the UI thread (which hit-tests clicks against them). Keeping
    // the layout in one place means the two threads can never disagree about
    // where a button is — the rectangles are derived from the same constants.
    //
    // Pixel sizes are *base* values; both the renderer and hit-test go
    // through GetButton (which scales them via UiScale.S using the current
    // viewport size), so the buttons grow with a fullscreen window and the
    // click rects always match what the player sees.
    internal static class PauseMenu
    {
        public enum ActionId
        {
            None,
            BackToGame,
            Options,
            Save,
            Quit,
            // Phase 7 — toggles in-process LAN host. Single ActionId
            // (not separate Open/Close) because the two are mutually
            // exclusive — you're either hosting or you aren't, and the
            // button label switches on that state.
            ToggleLan,
        }

        // Base (scale=1) sizes — fed through UiScale at lookup time.
        private const int ButtonWidthBase  = 320;
        private const int ButtonHeightBase = 40;
        private const int ButtonGapBase    = 12;
        // Vertical distance from the title baseline down to the first button.
        private const int TitleGapBase     = 36;
        // Title rendered at scale 3 over the bitmap font (24 px tall at 1×).
        private const int TitleFontScaleBase = 3;

        public static int ButtonWidth(int viewW, int viewH)  => UiScale.S(ButtonWidthBase, viewW, viewH);
        public static int ButtonHeight(int viewW, int viewH) => UiScale.S(ButtonHeightBase, viewW, viewH);
        public static int ButtonGap(int viewW, int viewH)    => UiScale.S(ButtonGapBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)     => UiScale.S(TitleGapBase, viewW, viewH);
        public static int TitleFontScale(int viewW, int viewH)
        {
            int s = (int)(TitleFontScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }

        public struct Button
        {
            public int X, Y, W, H;
            public ActionId Id;
            public string Label;
        }

        // Order shown top-to-bottom in the menu.
        private static readonly ActionId[] Order = new[]
        {
            ActionId.BackToGame,
            ActionId.Options,
            ActionId.Save,
            ActionId.ToggleLan,
            ActionId.Quit,
        };

        // Uppercase to match the bitmap-font glyph table — the font has no
        // lowercase glyphs (lower → upper remap inside HotbarTextures), but
        // writing them upper here keeps the source readable.
        //
        // ToggleLan's label is dynamic ("OPEN TO LAN" vs "CLOSE LAN")
        // depending on whether a host session is running; resolved per
        // call inside GetButton via the isHostingLan flag the renderer
        // passes in.
        private static readonly string[] Labels = new[]
        {
            "BACK TO GAME",
            "OPTIONS",
            "SAVE",
            "OPEN TO LAN",
            "QUIT",
        };

        public static int Count => Order.Length;

        public static Button GetButton(int index, int screenW, int screenH)
            => GetButton(index, screenW, screenH, isHostingLan: false);

        public static Button GetButton(int index, int screenW, int screenH, bool isHostingLan)
        {
            int n = Order.Length;
            int bw = ButtonWidth(screenW, screenH);
            int bh = ButtonHeight(screenW, screenH);
            int gap = ButtonGap(screenW, screenH);
            int totalH = n * bh + (n - 1) * gap;
            int startY = (screenH - totalH) / 2;
            int x = (screenW - bw) / 2;
            string label = Labels[index];
            // Phase 7 — flip the LAN button's label based on host state
            // so the same slot shows "OPEN TO LAN" when off and "CLOSE
            // LAN" when on. Geometry stays identical so the click hit-
            // rect is the same in both states.
            if (Order[index] == ActionId.ToggleLan && isHostingLan)
                label = "CLOSE LAN";
            return new Button
            {
                X = x,
                Y = startY + index * (bh + gap),
                W = bw,
                H = bh,
                Id = Order[index],
                Label = label,
            };
        }

        public static ActionId HitTest(int screenW, int screenH, int mx, int my)
        {
            for (int i = 0; i < Order.Length; i++)
            {
                var b = GetButton(i, screenW, screenH);
                if (mx >= b.X && mx < b.X + b.W && my >= b.Y && my < b.Y + b.H)
                    return b.Id;
            }
            return ActionId.None;
        }

        // Y of the title text's top edge, sitting just above the first button.
        // Title scales with the viewport (TitleFontScale), so the gap+height
        // both grow together.
        public static int TitleY(int screenW, int screenH)
        {
            int n = Order.Length;
            int bh = ButtonHeight(screenW, screenH);
            int gap = ButtonGap(screenW, screenH);
            int totalH = n * bh + (n - 1) * gap;
            int startY = (screenH - totalH) / 2;
            int titleScale = TitleFontScale(screenW, screenH);
            return startY - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }
    }
}
