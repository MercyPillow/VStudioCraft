namespace VStudioCraft.Game
{
    // Tier 6 #47 — World-create form reachable from World Select →
    // Create New. Three editable fields (name, seed, mode) + Create /
    // Back buttons, stacked vertically. Clicking a text-field rect
    // sets InputState.FocusedField; KeyPress / Backspace mutate the
    // matching buffer. Mode toggle is a button that swaps between
    // Creative and Survival in place — same pattern the OptionsMenu
    // uses for its toggle rows.
    internal static class WorldCreateScreen
    {
        public enum ActionId
        {
            None,
            FocusName,
            FocusSeed,
            ToggleMode,
            Create,
            Back,
        }

        // Base sizes. Field widths match the row width in
        // WorldSelectScreen so the panels feel like part of one chrome
        // family across the title flow.
        private const int FieldWidthBase  = 360;
        private const int FieldHeightBase = 36;
        private const int FieldGapBase    = 14;
        private const int LabelGapBase    = 4;
        private const int FooterGapBase   = 18;
        private const int FooterButtonWBase = 220;
        private const int FooterButtonHBase = 40;
        private const int FooterButtonGapBase = 12;
        private const int TitleGapBase     = 36;
        private const int TitleFontScaleBase = 4;

        public static int FieldWidth(int viewW, int viewH)       => UiScale.S(FieldWidthBase, viewW, viewH);
        public static int FieldHeight(int viewW, int viewH)      => UiScale.S(FieldHeightBase, viewW, viewH);
        public static int FieldGap(int viewW, int viewH)         => UiScale.S(FieldGapBase, viewW, viewH);
        public static int LabelGap(int viewW, int viewH)         => UiScale.S(LabelGapBase, viewW, viewH);
        public static int FooterGap(int viewW, int viewH)        => UiScale.S(FooterGapBase, viewW, viewH);
        public static int FooterButtonW(int viewW, int viewH)    => UiScale.S(FooterButtonWBase, viewW, viewH);
        public static int FooterButtonH(int viewW, int viewH)    => UiScale.S(FooterButtonHBase, viewW, viewH);
        public static int FooterButtonGap(int viewW, int viewH)  => UiScale.S(FooterButtonGapBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)         => UiScale.S(TitleGapBase, viewW, viewH);
        public static int TitleFontScale(int viewW, int viewH)
        {
            int s = (int)(TitleFontScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }

        // Total content height: 3 fields + 2 inner gaps + footer gap +
        // footer button height. Used to centre the panel on the
        // viewport vertically. Each field has a small label gap above
        // it (drawn as text by the renderer) so the label doesn't
        // overlap the field rect.
        public static int ContentHeight(int viewW, int viewH)
        {
            int fh = FieldHeight(viewW, viewH);
            int fg = FieldGap(viewW, viewH);
            int lg = LabelGap(viewW, viewH);
            int labelLine = HotbarTextures.GlyphCellH * System.Math.Max(1, UiScale.S(2, viewW, viewH));
            int fieldsBlock = 3 * (labelLine + lg + fh) + 2 * fg;
            return fieldsBlock + FooterGap(viewW, viewH) + FooterButtonH(viewW, viewH);
        }

        private static (int x, int y) TopLeft(int screenW, int screenH)
        {
            int fw = FieldWidth(screenW, screenH);
            int total = ContentHeight(screenW, screenH);
            int x = (screenW - fw) / 2;
            int y = (screenH - total) / 2;
            return (x, y);
        }

        // The three field rectangles. Each sits below its (renderer-
        // drawn) label, separated by FieldGap. Used both by the click
        // hit-test below and by the renderer to position the field
        // chrome.
        private static (int rowY, int fieldY) FieldRowYs(int screenW, int screenH, int rowIndex)
        {
            var (_, y0) = TopLeft(screenW, screenH);
            int fh = FieldHeight(screenW, screenH);
            int fg = FieldGap(screenW, screenH);
            int lg = LabelGap(screenW, screenH);
            int labelLine = HotbarTextures.GlyphCellH * System.Math.Max(1, UiScale.S(2, screenW, screenH));
            int rowH = labelLine + lg + fh;
            int rowY = y0 + rowIndex * (rowH + fg);
            int fieldY = rowY + labelLine + lg;
            return (rowY, fieldY);
        }

        public static (int x, int y, int w, int h) GetNameFieldRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var (_, fy) = FieldRowYs(screenW, screenH, 0);
            return (x0, fy, FieldWidth(screenW, screenH), FieldHeight(screenW, screenH));
        }

        public static (int x, int y, int w, int h) GetSeedFieldRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var (_, fy) = FieldRowYs(screenW, screenH, 1);
            return (x0, fy, FieldWidth(screenW, screenH), FieldHeight(screenW, screenH));
        }

        public static (int x, int y, int w, int h) GetModeToggleRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var (_, fy) = FieldRowYs(screenW, screenH, 2);
            return (x0, fy, FieldWidth(screenW, screenH), FieldHeight(screenW, screenH));
        }

        // Y of the row's label baseline (top of label text). Used by
        // the renderer to draw the field name above each field.
        public static int GetRowLabelY(int screenW, int screenH, int rowIndex)
        {
            var (rowY, _) = FieldRowYs(screenW, screenH, rowIndex);
            return rowY;
        }

        public static (int x, int y, int w, int h) GetCreateRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var modeRect = GetModeToggleRect(screenW, screenH);
            int bw = FooterButtonW(screenW, screenH);
            int bh = FooterButtonH(screenW, screenH);
            int bg = FooterButtonGap(screenW, screenH);
            int fw = FieldWidth(screenW, screenH);
            int totalFooterW = bw * 2 + bg;
            int fx = x0 + (fw - totalFooterW) / 2;
            int fy = modeRect.y + modeRect.h + FooterGap(screenW, screenH);
            return (fx, fy, bw, bh);
        }

        public static (int x, int y, int w, int h) GetBackRect(int screenW, int screenH)
        {
            var (cx, cy, cw, ch) = GetCreateRect(screenW, screenH);
            int bg = FooterButtonGap(screenW, screenH);
            return (cx + cw + bg, cy, cw, ch);
        }

        public static int TitleY(int screenW, int screenH)
        {
            var (_, y0) = TopLeft(screenW, screenH);
            int titleScale = TitleFontScale(screenW, screenH);
            return y0 - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }

        public static ActionId HitTest(int screenW, int screenH, int mx, int my)
        {
            var n = GetNameFieldRect(screenW, screenH);
            if (mx >= n.x && mx < n.x + n.w && my >= n.y && my < n.y + n.h) return ActionId.FocusName;
            var s = GetSeedFieldRect(screenW, screenH);
            if (mx >= s.x && mx < s.x + s.w && my >= s.y && my < s.y + s.h) return ActionId.FocusSeed;
            var m = GetModeToggleRect(screenW, screenH);
            if (mx >= m.x && mx < m.x + m.w && my >= m.y && my < m.y + m.h) return ActionId.ToggleMode;
            var c = GetCreateRect(screenW, screenH);
            if (mx >= c.x && mx < c.x + c.w && my >= c.y && my < c.y + c.h) return ActionId.Create;
            var b = GetBackRect(screenW, screenH);
            if (mx >= b.x && mx < b.x + b.w && my >= b.y && my < b.y + b.h) return ActionId.Back;
            return ActionId.None;
        }
    }
}
