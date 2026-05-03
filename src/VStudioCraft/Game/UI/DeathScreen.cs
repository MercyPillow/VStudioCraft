namespace VStudioCraft.Game
{
    // Tier 5 #29 — Death modal. Shown when Player.Health hits 0; the
    // world halts (via IsWorldHalted) and the player picks Respawn or
    // Title Screen instead of insta-respawning. Same shared-layout
    // pattern as PauseMenu so the UI thread's click hit-test always
    // agrees with where the renderer drew the buttons.
    //
    // Two buttons in fixed order:
    //   0 — RESPAWN
    //   1 — TITLE SCREEN
    // Title is "YOU DIED!" rendered larger than the buttons so the
    // wash + title feel like an Alpha-style game-over screen.
    internal static class DeathScreen
    {
        public enum ActionId
        {
            None,
            Respawn,
            Title,
        }

        // Base (scale=1) sizes — fed through UiScale at lookup time
        // so the buttons grow with a fullscreen window. Match the
        // PauseMenu base sizes for a consistent modal-button feel
        // across both screens.
        private const int ButtonWidthBase  = 320;
        private const int ButtonHeightBase = 40;
        private const int ButtonGapBase    = 12;
        private const int TitleGapBase     = 36;
        private const int TitleFontScaleBase = 4;  // bigger than PauseMenu's 3

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

        private static readonly ActionId[] Order = new[]
        {
            ActionId.Respawn,
            ActionId.Title,
        };

        // Uppercase to match the bitmap font (no lowercase glyphs).
        private static readonly string[] Labels = new[]
        {
            "RESPAWN",
            "TITLE SCREEN",
        };

        public static int Count => Order.Length;

        public static Button GetButton(int index, int screenW, int screenH)
        {
            int n = Order.Length;
            int bw = ButtonWidth(screenW, screenH);
            int bh = ButtonHeight(screenW, screenH);
            int gap = ButtonGap(screenW, screenH);
            int totalH = n * bh + (n - 1) * gap;
            int startY = (screenH - totalH) / 2;
            int x = (screenW - bw) / 2;
            return new Button
            {
                X = x,
                Y = startY + index * (bh + gap),
                W = bw,
                H = bh,
                Id = Order[index],
                Label = Labels[index],
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

        // Y of the title text's top edge, sitting just above the first
        // button. Mirrors PauseMenu.TitleY exactly so the title +
        // button stack reads as a single visual unit.
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
