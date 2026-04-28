namespace VStudioCraft.Game
{
    // Tier 6 #47 — Connect-to-server form reachable from Title →
    // Multiplayer. Two text fields (server address, username) +
    // Connect / Back buttons + a one-line error label area used by
    // the host's ConnectFailed event to surface socket / handshake
    // errors so the user can fix and retry without leaving the screen.
    internal static class MultiplayerConnectScreen
    {
        public enum ActionId
        {
            None,
            FocusServer,
            FocusUsername,
            Connect,
            Back,
        }

        // Match WorldCreateScreen field sizes — the two screens are
        // visually a pair (both new-flow forms) so chrome should
        // read identically.
        private const int FieldWidthBase  = 360;
        private const int FieldHeightBase = 36;
        private const int FieldGapBase    = 14;
        private const int LabelGapBase    = 4;
        private const int ErrorLineGapBase = 12;
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
        public static int ErrorLineGap(int viewW, int viewH)     => UiScale.S(ErrorLineGapBase, viewW, viewH);
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

        // Total content height: 2 fields + 1 inner gap + error-line
        // gap + 1 line of error text + footer gap + footer button.
        public static int ContentHeight(int viewW, int viewH)
        {
            int fh = FieldHeight(viewW, viewH);
            int fg = FieldGap(viewW, viewH);
            int lg = LabelGap(viewW, viewH);
            int labelLine = HotbarTextures.GlyphCellH * System.Math.Max(1, UiScale.S(2, viewW, viewH));
            int errorLine = HotbarTextures.GlyphCellH * System.Math.Max(1, UiScale.S(2, viewW, viewH));
            int fieldsBlock = 2 * (labelLine + lg + fh) + fg;
            return fieldsBlock + ErrorLineGap(viewW, viewH) + errorLine + FooterGap(viewW, viewH) + FooterButtonH(viewW, viewH);
        }

        private static (int x, int y) TopLeft(int screenW, int screenH)
        {
            int fw = FieldWidth(screenW, screenH);
            int total = ContentHeight(screenW, screenH);
            int x = (screenW - fw) / 2;
            int y = (screenH - total) / 2;
            return (x, y);
        }

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

        public static (int x, int y, int w, int h) GetServerFieldRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var (_, fy) = FieldRowYs(screenW, screenH, 0);
            return (x0, fy, FieldWidth(screenW, screenH), FieldHeight(screenW, screenH));
        }

        public static (int x, int y, int w, int h) GetUsernameFieldRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var (_, fy) = FieldRowYs(screenW, screenH, 1);
            return (x0, fy, FieldWidth(screenW, screenH), FieldHeight(screenW, screenH));
        }

        public static int GetRowLabelY(int screenW, int screenH, int rowIndex)
        {
            var (rowY, _) = FieldRowYs(screenW, screenH, rowIndex);
            return rowY;
        }

        public static (int x, int y, int w, int h) GetErrorLineRect(int screenW, int screenH)
        {
            var u = GetUsernameFieldRect(screenW, screenH);
            int errorLine = HotbarTextures.GlyphCellH * System.Math.Max(1, UiScale.S(2, screenW, screenH));
            return (u.x, u.y + u.h + ErrorLineGap(screenW, screenH), u.w, errorLine);
        }

        public static (int x, int y, int w, int h) GetConnectRect(int screenW, int screenH)
        {
            var (x0, _) = TopLeft(screenW, screenH);
            var err = GetErrorLineRect(screenW, screenH);
            int bw = FooterButtonW(screenW, screenH);
            int bh = FooterButtonH(screenW, screenH);
            int bg = FooterButtonGap(screenW, screenH);
            int fw = FieldWidth(screenW, screenH);
            int totalFooterW = bw * 2 + bg;
            int fx = x0 + (fw - totalFooterW) / 2;
            int fy = err.y + err.h + FooterGap(screenW, screenH);
            return (fx, fy, bw, bh);
        }

        public static (int x, int y, int w, int h) GetBackRect(int screenW, int screenH)
        {
            var (cx, cy, cw, ch) = GetConnectRect(screenW, screenH);
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
            var s = GetServerFieldRect(screenW, screenH);
            if (mx >= s.x && mx < s.x + s.w && my >= s.y && my < s.y + s.h) return ActionId.FocusServer;
            var u = GetUsernameFieldRect(screenW, screenH);
            if (mx >= u.x && mx < u.x + u.w && my >= u.y && my < u.y + u.h) return ActionId.FocusUsername;
            var c = GetConnectRect(screenW, screenH);
            if (mx >= c.x && mx < c.x + c.w && my >= c.y && my < c.y + c.h) return ActionId.Connect;
            var b = GetBackRect(screenW, screenH);
            if (mx >= b.x && mx < b.x + b.w && my >= b.y && my < b.y + b.h) return ActionId.Back;
            return ActionId.None;
        }
    }
}
