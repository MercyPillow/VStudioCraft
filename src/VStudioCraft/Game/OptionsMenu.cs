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
    internal static class OptionsMenu
    {
        public enum ActionId
        {
            None,
            Back,
            ToggleHunger,
        }

        public const int RowWidth   = 380;
        public const int RowHeight  = 40;
        public const int RowGap     = 12;
        public const int SectionGap = 26;
        // Vertical distance from the title baseline down to the first row.
        public const int TitleGap   = 36;

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
            bool hungerEnabled, bool isSurvival)
        {
            // 4 rows: SURVIVAL heading, hunger toggle, gap-as-section, BACK.
            // Section heading height matches a row's height for layout simplicity;
            // it just isn't clickable.
            var labels = new[]
            {
                new Row { Id = ActionId.None,         Label = "SURVIVAL",
                          IsSection = true },
                new Row { Id = ActionId.ToggleHunger, Label = HungerLabel(hungerEnabled),
                          IsDisabled = !isSurvival },
                new Row { Id = ActionId.Back,         Label = "BACK" },
            };

            // Total height = rows + inter-row gaps + one extra section-gap
            // before the BACK button (the visual break between options and
            // dismiss).
            int n = labels.Length;
            int totalH = n * RowHeight + (n - 1) * RowGap + (SectionGap - RowGap);
            int startY = (screenH - totalH) / 2;
            int x = (screenW - RowWidth) / 2;

            int cursorY = startY;
            for (int i = 0; i < n; i++)
            {
                if (i == n - 1) cursorY += SectionGap - RowGap; // extra gap before BACK
                labels[i].X = x;
                labels[i].Y = cursorY;
                labels[i].W = RowWidth;
                labels[i].H = RowHeight;
                cursorY += RowHeight + RowGap;
            }
            return labels;
        }

        public static ActionId HitTest(int screenW, int screenH, int mx, int my,
            bool hungerEnabled, bool isSurvival)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival);
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
        // Title is drawn at scale 3 (HotbarTextures.GlyphCellH * 3 px tall).
        public static int TitleY(int screenW, int screenH, bool hungerEnabled, bool isSurvival)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival);
            int firstY = rows[0].Y;
            return firstY - TitleGap - HotbarTextures.GlyphCellH * 3;
        }

        private static string HungerLabel(bool on) =>
            on ? "HUNGER BAR: ON" : "HUNGER BAR: OFF";
    }
}
