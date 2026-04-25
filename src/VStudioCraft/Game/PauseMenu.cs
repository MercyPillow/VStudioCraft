namespace VStudioCraft.Game
{
    // Pause-menu layout shared between the render thread (which draws the
    // buttons) and the UI thread (which hit-tests clicks against them). Keeping
    // the layout in one place means the two threads can never disagree about
    // where a button is — the rectangles are derived from the same constants.
    internal static class PauseMenu
    {
        public enum ActionId
        {
            None,
            BackToGame,
            Options,
            Save,
            Quit,
        }

        public const int ButtonWidth  = 320;
        public const int ButtonHeight = 40;
        public const int ButtonGap    = 12;
        // Vertical distance from the title baseline down to the first button.
        public const int TitleGap     = 36;

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
            ActionId.Quit,
        };

        // Uppercase to match the bitmap-font glyph table — the font has no
        // lowercase glyphs (lower → upper remap inside HotbarTextures), but
        // writing them upper here keeps the source readable.
        private static readonly string[] Labels = new[]
        {
            "BACK TO GAME",
            "OPTIONS",
            "SAVE",
            "QUIT",
        };

        public static int Count => Order.Length;

        public static Button GetButton(int index, int screenW, int screenH)
        {
            int n = Order.Length;
            int totalH = n * ButtonHeight + (n - 1) * ButtonGap;
            int startY = (screenH - totalH) / 2;
            int x = (screenW - ButtonWidth) / 2;
            return new Button
            {
                X = x,
                Y = startY + index * (ButtonHeight + ButtonGap),
                W = ButtonWidth,
                H = ButtonHeight,
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

        // Y of the title text's top edge, sitting just above the first button.
        // The title is drawn at scale 3 (24 px tall), so we pull back enough
        // for both the gap and the title height.
        public static int TitleY(int screenH)
        {
            int n = Order.Length;
            int totalH = n * ButtonHeight + (n - 1) * ButtonGap;
            int startY = (screenH - totalH) / 2;
            return startY - TitleGap - HotbarTextures.GlyphCellH * 3;
        }
    }
}
