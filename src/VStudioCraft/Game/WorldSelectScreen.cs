using System;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Tier 6 #47 — World-select submenu reachable from Title → Single
    // Player. Lists existing .voxworld saves in a scrollable column,
    // most-recently-modified first; a "Create New World" button at the
    // bottom + a Back button at the very bottom round out the layout.
    //
    // Each row is a hit-testable rectangle showing display name, seed,
    // and last-played time. Rows scroll via mouse wheel using a
    // separate scroll index on InputState (parallel to creative-catalog
    // scroll state); only RowsVisible rows are rendered at a time.
    internal static class WorldSelectScreen
    {
        public enum ActionId
        {
            None,
            SelectWorld,   // payload = absolute index into the saves array
            CreateNew,
            Back,
        }

        public const int RowsVisible = 6;

        // Base (scale=1) sizes — same UiScale convention as TitleScreen.
        // Wider rows than TitleScreen buttons because each shows three
        // bits of metadata (name + seed + mtime).
        private const int RowWidthBase    = 460;
        private const int RowHeightBase   = 44;
        private const int RowGapBase      = 4;
        private const int FooterGapBase   = 14;
        private const int FooterButtonWBase = 220;
        private const int FooterButtonHBase = 40;
        private const int FooterButtonGapBase = 12;
        private const int TitleGapBase     = 36;
        private const int TitleFontScaleBase = 4;

        public static int RowWidth(int viewW, int viewH)         => UiScale.S(RowWidthBase, viewW, viewH);
        public static int RowHeight(int viewW, int viewH)        => UiScale.S(RowHeightBase, viewW, viewH);
        public static int RowGap(int viewW, int viewH)           => UiScale.S(RowGapBase, viewW, viewH);
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

        public struct Row
        {
            public int X, Y, W, H;
            public string Path;
            public string DisplayName;
            public int Seed;
            public DateTime LastWriteUtc;
            public int AbsoluteIndex;
            public bool HeaderValid;
        }

        // Total content height (rows block + footer gap + the two
        // footer buttons). Used to centre the panel vertically.
        public static int ContentHeight(int viewW, int viewH)
        {
            int rowH = RowHeight(viewW, viewH);
            int rowGap = RowGap(viewW, viewH);
            int rowsBlockH = RowsVisible * rowH + (RowsVisible - 1) * rowGap;
            return rowsBlockH + FooterGap(viewW, viewH) + FooterButtonH(viewW, viewH);
        }

        // Top-left of the rows region. Centred horizontally by the
        // widest "row width", vertically by total content height.
        private static (int x, int y) TopLeft(int screenW, int screenH)
        {
            int rowW = RowWidth(screenW, screenH);
            int total = ContentHeight(screenW, screenH);
            int x = (screenW - rowW) / 2;
            int y = (screenH - total) / 2;
            return (x, y);
        }

        public static Row[] BuildRows(int screenW, int screenH,
            IList<WorldSaveFormat.SaveSummary> saves, int scrollIndex)
        {
            var (x0, y0) = TopLeft(screenW, screenH);
            int rowW = RowWidth(screenW, screenH);
            int rowH = RowHeight(screenW, screenH);
            int gap  = RowGap(screenW, screenH);
            int count = System.Math.Min(RowsVisible, System.Math.Max(0, saves.Count - scrollIndex));
            var rows = new Row[count];
            for (int i = 0; i < count; i++)
            {
                int abs = scrollIndex + i;
                var s = saves[abs];
                rows[i] = new Row
                {
                    X = x0,
                    Y = y0 + i * (rowH + gap),
                    W = rowW,
                    H = rowH,
                    Path = s.Path,
                    DisplayName = s.DisplayName,
                    Seed = s.Seed,
                    LastWriteUtc = s.LastWriteUtc,
                    AbsoluteIndex = abs,
                    HeaderValid = s.HeaderValid,
                };
            }
            return rows;
        }

        // Clamp scroll so the top row is always a valid index, never
        // past the last page. Caller passes the requested raw value;
        // returns the clamped one so a wheel event can't drift the
        // offset off the end.
        public static int ClampScroll(int raw, int saveCount)
        {
            int max = System.Math.Max(0, saveCount - RowsVisible);
            if (raw < 0) raw = 0;
            if (raw > max) raw = max;
            return raw;
        }

        public static (int x, int y, int w, int h) GetCreateNewRect(int screenW, int screenH)
        {
            var (x0, y0) = TopLeft(screenW, screenH);
            int rowH = RowHeight(screenW, screenH);
            int rowGap = RowGap(screenW, screenH);
            int rowsBlockH = RowsVisible * rowH + (RowsVisible - 1) * rowGap;
            int bw = FooterButtonW(screenW, screenH);
            int bh = FooterButtonH(screenW, screenH);
            int bg = FooterButtonGap(screenW, screenH);
            int rowW = RowWidth(screenW, screenH);
            // Create-New on the left half, Back on the right.
            int totalFooterW = bw * 2 + bg;
            int fx = x0 + (rowW - totalFooterW) / 2;
            int fy = y0 + rowsBlockH + FooterGap(screenW, screenH);
            return (fx, fy, bw, bh);
        }

        public static (int x, int y, int w, int h) GetBackRect(int screenW, int screenH)
        {
            var (cx, cy, cw, ch) = GetCreateNewRect(screenW, screenH);
            int bg = FooterButtonGap(screenW, screenH);
            return (cx + cw + bg, cy, cw, ch);
        }

        // Y of the title text's top edge.
        public static int TitleY(int screenW, int screenH)
        {
            var (_, y0) = TopLeft(screenW, screenH);
            int titleScale = TitleFontScale(screenW, screenH);
            return y0 - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }

        // Hit-test the entire panel — rows, Create New, and Back.
        // Returns (kind, payload): payload is the absolute saves[]
        // index for SelectWorld; -1 otherwise.
        public static (ActionId kind, int payload) HitTest(int screenW, int screenH,
            int mx, int my, IList<WorldSaveFormat.SaveSummary> saves, int scrollIndex)
        {
            var rows = BuildRows(screenW, screenH, saves, scrollIndex);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H)
                    return (ActionId.SelectWorld, r.AbsoluteIndex);
            }
            var (cx, cy, cw, ch) = GetCreateNewRect(screenW, screenH);
            if (mx >= cx && mx < cx + cw && my >= cy && my < cy + ch) return (ActionId.CreateNew, -1);
            var (bx, by, bw, bh) = GetBackRect(screenW, screenH);
            if (mx >= bx && mx < bx + bw && my >= by && my < by + bh) return (ActionId.Back, -1);
            return (ActionId.None, -1);
        }
    }
}
