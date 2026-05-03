using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Tier 9 #53 V3 — In-game key-binding capture UI. Sub-screen of
    // the Options menu (clicking "CONTROLS" in OptionsMenu opens
    // this), shows a row per gameplay binding with the current key
    // displayed on the right. Clicking a binding row enters
    // "capture mode" — the renderer paints the row in a hot colour
    // and the next KeyDown in the host's input handler rebinds
    // that action to the pressed key. Esc cancels capture without
    // changing anything.
    //
    // Rebound bindings persist to %APPDATA%\VStudioCraft\
    // keybindings.cfg (KeyBindings.SaveToDisk is called when the
    // capture completes), so a rebind survives a restart.
    //
    // Layout mirrors OptionsMenu — same row-stack pattern + same
    // hit-test result shape. Sized and laid out via UiScale so it
    // reads cleanly at 1080p / 1440p / 4K.
    internal static class ControlsMenu
    {
        public enum ActionId
        {
            None,
            Back,
            Defaults,
            // Bind0..Bind7 — one per binding in KeyBindings.All. The
            // hit-test maps the row's index (0..7) to one of these
            // and the host's HandleControlsMenuAction reads the
            // ordinal to know which binding to capture.
            Bind0, Bind1, Bind2, Bind3, Bind4, Bind5, Bind6, Bind7,
        }

        private const int RowWidthBase   = 380;
        private const int RowHeightBase  = 36;
        private const int RowGapBase     = 8;
        private const int SectionGapBase = 26;
        private const int TitleGapBase   = 36;
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
            public string Label;        // e.g. "Forward"
            public string KeyName;      // e.g. "W" — empty for non-binding rows
            public bool IsCapturing;    // true → row drawn in hot colour, "press a key..." prompt
            public bool IsSection;      // true → render-only, not clickable
        }

        // Map a binding row index (0..7) to its ActionId. Used by both
        // BuildRows when assigning per-row Id and by HandleControlsMenuAction
        // when reading the row's index back out.
        public static int BindIndexFor(ActionId id)
        {
            switch (id)
            {
                case ActionId.Bind0: return 0;
                case ActionId.Bind1: return 1;
                case ActionId.Bind2: return 2;
                case ActionId.Bind3: return 3;
                case ActionId.Bind4: return 4;
                case ActionId.Bind5: return 5;
                case ActionId.Bind6: return 6;
                case ActionId.Bind7: return 7;
                default:             return -1;
            }
        }
        public static ActionId ActionForBindIndex(int idx)
        {
            switch (idx)
            {
                case 0: return ActionId.Bind0;
                case 1: return ActionId.Bind1;
                case 2: return ActionId.Bind2;
                case 3: return ActionId.Bind3;
                case 4: return ActionId.Bind4;
                case 5: return ActionId.Bind5;
                case 6: return ActionId.Bind6;
                case 7: return ActionId.Bind7;
                default: return ActionId.None;
            }
        }

        public static Row[] BuildRows(int screenW, int screenH, int captureBindingIdx)
        {
            var bindings = KeyBindings.All;
            var rowList = new List<Row>(bindings.Length + 3);
            rowList.Add(new Row { Id = ActionId.None, Label = "BINDINGS", IsSection = true });
            for (int i = 0; i < bindings.Length; i++)
            {
                rowList.Add(new Row
                {
                    Id = ActionForBindIndex(i),
                    Label = bindings[i].Label,
                    KeyName = FormatKey(bindings[i].Get()),
                    IsCapturing = (captureBindingIdx == i),
                });
            }
            rowList.Add(new Row { Id = ActionId.Defaults, Label = "RESET DEFAULTS" });
            rowList.Add(new Row { Id = ActionId.Back,     Label = "BACK" });
            var rows = rowList.ToArray();

            int rowW   = RowWidth(screenW, screenH);
            int rowH   = RowHeight(screenW, screenH);
            int rowGap = RowGap(screenW, screenH);
            int secGap = SectionGap(screenW, screenH);

            int n = rows.Length;
            // Last 2 rows (DEFAULTS, BACK) get an extra section-gap before them.
            int totalH = n * rowH + (n - 1) * rowGap + (secGap - rowGap);
            int startY = (screenH - totalH) / 2;
            int x = (screenW - rowW) / 2;

            int cursorY = startY;
            for (int i = 0; i < n; i++)
            {
                if (i == n - 2) cursorY += secGap - rowGap; // gap before DEFAULTS
                rows[i].X = x;
                rows[i].Y = cursorY;
                rows[i].W = rowW;
                rows[i].H = rowH;
                cursorY += rowH + rowGap;
            }
            return rows;
        }

        public static ActionId HitTest(int screenW, int screenH, int mx, int my, int captureBindingIdx)
        {
            var rows = BuildRows(screenW, screenH, captureBindingIdx);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r.IsSection) continue;
                if (mx < r.X || mx >= r.X + r.W || my < r.Y || my >= r.Y + r.H) continue;
                return r.Id;
            }
            return ActionId.None;
        }

        public static int TitleY(int screenW, int screenH, int captureBindingIdx)
        {
            var rows = BuildRows(screenW, screenH, captureBindingIdx);
            int firstY = rows[0].Y;
            int titleScale = TitleFontScale(screenW, screenH);
            return firstY - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }

        // Pretty-print a Keys enum value for the binding row's right
        // side. Most are fine as-is ("W", "Space"); a few canonical
        // names are noisy and worth shortening so the row reads
        // naturally. Falls through to ToString() for everything else.
        public static string FormatKey(System.Windows.Forms.Keys k)
        {
            switch (k)
            {
                case System.Windows.Forms.Keys.ShiftKey:   return "SHIFT";
                case System.Windows.Forms.Keys.ControlKey: return "CTRL";
                case System.Windows.Forms.Keys.Menu:       return "ALT";
                case System.Windows.Forms.Keys.Space:      return "SPACE";
                case System.Windows.Forms.Keys.Return:     return "ENTER";
                case System.Windows.Forms.Keys.Tab:        return "TAB";
                case System.Windows.Forms.Keys.Back:       return "BKSP";
                case System.Windows.Forms.Keys.Escape:     return "ESC";
            }
            return k.ToString().ToUpperInvariant();
        }
    }
}
