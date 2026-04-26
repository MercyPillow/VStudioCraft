namespace VStudioCraft.Game
{
    // Layout + hit-testing for the Options screen, opened from the pause
    // menu's "Options" button. Like PauseMenu, the layout constants live
    // in one place so the render thread (drawing) and UI thread (clicks)
    // can never disagree about hit rectangles.
    //
    // Currently only one toggle: SURVIVAL → HUNGER BAR. The toggle reads
    // its value from GameRenderer.HungerEnabled and a click flips it.
    // Hidden when the world's GameMode is Creative (the bar is survival-
    // only) — we still draw the row but with a disabled visual.
    //
    // Pixel sizes are base values fed through UiScale (see UiScale.cs)
    // so the panel grows with the viewport and stays readable at 1080p+
    // without making the click rects drift away from where they're drawn.
    internal static class OptionsMenu
    {
        public enum ActionId
        {
            None,
            Back,
            ToggleHunger,
            ToggleRealTextures,
        }

        private const int RowWidthBase   = 380;
        private const int RowHeightBase  = 40;
        private const int RowGapBase     = 12;
        private const int SectionGapBase = 26;
        // Vertical distance from the title baseline down to the first row.
        private const int TitleGapBase   = 36;
        // Title at scale 3 in the bitmap font (24 px tall at 1×).
        private const int TitleFontScaleBase = 3;

        public static int RowWidth(int viewW, int viewH)   => UiScale.S(RowWidthBase, viewW, viewH);
        public static int RowHeight(int viewW, int viewH)  => UiScale.S(RowHeightBase, viewW, viewH);
        public static int RowGap(int viewW, int viewH)     => UiScale.S(RowGapBase, viewW, viewH);
        public static int SectionGap(int viewW, int viewH) => UiScale.S(SectionGapBase, viewW, viewH);
        public static int TitleGap(int viewW, int viewH)   => UiScale.S(TitleGapBase, viewW, viewH);
        public static int TitleFontScale(int viewW, int viewH)
        {
            int s = (int)(TitleFontScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }

        public struct Row
        {
            public int X, Y, W, H;
            public ActionId Id;
            public string Label;
            public bool IsSection;   // section heading — not clickable
            public bool IsDisabled;  // drawn dim, ignores clicks
        }

        // We compose the row list on demand because section headings and
        // disabled rows depend on game state (creative mode disables the
        // hunger toggle). Kept tiny so the per-frame allocation is cheap.
        public static Row[] BuildRows(int screenW, int screenH,
            bool hungerEnabled, bool isSurvival, bool useRealTextures)
        {
            // SURVIVAL section: hunger toggle (disabled in creative).
            // GRAPHICS section: alpha-textures toggle (always available).
            // BACK button at the bottom.
            // Section heading height matches a row's height for layout simplicity;
            // it just isn't clickable.
            var labels = new[]
            {
                new Row { Id = ActionId.None,               Label = "SURVIVAL",
                          IsSection = true },
                new Row { Id = ActionId.ToggleHunger,       Label = HungerLabel(hungerEnabled),
                          IsDisabled = !isSurvival },
                new Row { Id = ActionId.None,               Label = "GRAPHICS",
                          IsSection = true },
                new Row { Id = ActionId.ToggleRealTextures, Label = TexturesLabel(useRealTextures) },
                new Row { Id = ActionId.Back,               Label = "BACK" },
            };

            int rowW   = RowWidth(screenW, screenH);
            int rowH   = RowHeight(screenW, screenH);
            int rowGap = RowGap(screenW, screenH);
            int secGap = SectionGap(screenW, screenH);

            // Total height = rows + inter-row gaps + one extra section-gap
            // before the BACK button (the visual break between options and
            // dismiss).
            int n = labels.Length;
            int totalH = n * rowH + (n - 1) * rowGap + (secGap - rowGap);
            int startY = (screenH - totalH) / 2;
            int x = (screenW - rowW) / 2;

            int cursorY = startY;
            for (int i = 0; i < n; i++)
            {
                if (i == n - 1) cursorY += secGap - rowGap; // extra gap before BACK
                labels[i].X = x;
                labels[i].Y = cursorY;
                labels[i].W = rowW;
                labels[i].H = rowH;
                cursorY += rowH + rowGap;
            }
            return labels;
        }

        public static ActionId HitTest(int screenW, int screenH, int mx, int my,
            bool hungerEnabled, bool isSurvival, bool useRealTextures)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival, useRealTextures);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r.IsSection || r.IsDisabled) continue;
                if (mx >= r.X && mx < r.X + r.W && my >= r.Y && my < r.Y + r.H)
                    return r.Id;
            }
            return ActionId.None;
        }

        // Y of the title text's top edge, sitting just above the first row.
        public static int TitleY(int screenW, int screenH,
            bool hungerEnabled, bool isSurvival, bool useRealTextures)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival, useRealTextures);
            int firstY = rows[0].Y;
            int titleScale = TitleFontScale(screenW, screenH);
            return firstY - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }

        private static string HungerLabel(bool on) =>
            on ? "HUNGER BAR: ON" : "HUNGER BAR: OFF";

        private static string TexturesLabel(bool on)
        {
            // If the user has toggled ON but the embedded PNG can't be
            // decoded (stale build with no resource, decoder threw, etc.)
            // the renderer silently falls back to procedural — which
            // looks identical to "OFF" in-game and produced the
            // "I enable it but nothing happens" symptom. Surface the
            // actual reason from BlockTextures.AlphaTerrainStatus so
            // the label tells the truth and we can debug from a
            // screenshot instead of guessing.
            if (on && BlockTextures.AlphaTerrainAttempted && !BlockTextures.AlphaTerrainAvailable)
            {
                string status = BlockTextures.AlphaTerrainStatus;
                return string.IsNullOrEmpty(status)
                    ? "ALPHA TEXTURES: UNAVAILABLE"
                    : "ALPHA TEXTURES: " + status.ToUpperInvariant();
            }
            return on ? "ALPHA TEXTURES: ON" : "ALPHA TEXTURES: OFF";
        }
    }
}
