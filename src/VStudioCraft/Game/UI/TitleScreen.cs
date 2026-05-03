namespace VStudioCraft.Game
{
    // Tier 6 #47 — Top-level main menu. Four buttons stacked vertically
    // with a "VStudioCraft" title above. Same Button/HitTest contract
    // every other modal uses (PauseMenu, DeathScreen) so the host can
    // route clicks generically. UiScale-aware so chrome reads
    // consistently across viewport sizes.
    internal static class TitleScreen
    {
        public enum ActionId
        {
            None,
            SinglePlayer,
            Multiplayer,
            Settings,
            Quit,
        }

        // Base (scale=1) sizes — scaled at lookup time. Slightly bigger
        // buttons than PauseMenu since this is the "grand entry" screen
        // and reads better with a larger title-press feel.
        private const int ButtonWidthBase  = 360;
        private const int ButtonHeightBase = 44;
        private const int ButtonGapBase    = 12;
        private const int TitleGapBase     = 56;
        private const int TitleFontScaleBase = 5;

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
            ActionId.SinglePlayer,
            ActionId.Multiplayer,
            ActionId.Settings,
            ActionId.Quit,
        };

        // Uppercase to match the bitmap font (no lowercase glyphs).
        private static readonly string[] Labels = new[]
        {
            "SINGLE PLAYER",
            "MULTIPLAYER",
            "SETTINGS",
            "QUIT",
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

        // Y of the title text's top edge, sitting above the first
        // button. Mirrors PauseMenu / DeathScreen exactly so the chrome
        // alignment reads as one family across the modal stack.
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
