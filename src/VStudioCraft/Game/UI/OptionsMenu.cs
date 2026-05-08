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
            // Slider actions: clicks compute a 0..1 value from mouse-X
            // within the row and return it via HitTestEx.
            SetMasterVolume,
            SetMusicVolume,
            // Tier 10 follow-up — Render-distance slider. Slider
            // returns a 0..1 value; caller maps to the integer
            // chunk-radius range via Settings.RenderDistanceMin..Max.
            SetRenderDistance,
            // Tier 9 #53 V3 — Opens the Controls sub-screen for in-
            // game key rebinding. Closes the Options menu and pushes
            // the Controls menu modal in its place.
            OpenControls,
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
            // Slider rows render a track + filled bar from the row's left
            // edge to (X + W * Value); Label is drawn as a prefix and the
            // percentage suffix is appended at draw time.
            public bool IsSlider;
            public float Value;      // 0..1 current value (for sliders)
        }

        // We compose the row list on demand because section headings and
        // disabled rows depend on game state (creative mode disables the
        // hunger toggle). Kept tiny so the per-frame allocation is cheap.
        public static Row[] BuildRows(int screenW, int screenH,
            bool hungerEnabled, bool isSurvival, bool useRealTextures,
            float masterVolume, float musicVolume,
            int renderDistance)
        {
            // SURVIVAL section: hunger toggle (disabled in creative).
            // GRAPHICS section: alpha-textures toggle (always available).
            // AUDIO section: master + music sliders (always available).
            // BACK button at the bottom.
            // Section heading height matches a row's height for layout simplicity;
            // it just isn't clickable.
            bool audioOk = AudioEngine.IsAvailable;
            var rowList = new System.Collections.Generic.List<Row>(10);
            rowList.Add(new Row { Id = ActionId.None, Label = "SURVIVAL", IsSection = true });
            rowList.Add(new Row { Id = ActionId.ToggleHunger, Label = HungerLabel(hungerEnabled),
                                  IsDisabled = !isSurvival });
            rowList.Add(new Row { Id = ActionId.None, Label = "GRAPHICS", IsSection = true });
            rowList.Add(new Row { Id = ActionId.ToggleRealTextures, Label = TexturesLabel(useRealTextures) });
            // Tier 10 follow-up — Render distance slider. 0..1 slider
            // value maps to Settings.RenderDistanceMin..Max chunks.
            // Label appends the integer value at draw time so the
            // player can read "RENDER DISTANCE: 8" while dragging.
            rowList.Add(new Row { Id = ActionId.SetRenderDistance,
                                  Label = RenderDistanceLabel(renderDistance),
                                  IsSlider = true,
                                  Value = RenderDistanceToSlider(renderDistance) });
            rowList.Add(new Row { Id = ActionId.None, Label = "AUDIO", IsSection = true });
            rowList.Add(new Row { Id = ActionId.SetMasterVolume, Label = "ALL SOUND",
                                  IsSlider = true, Value = Clamp01(masterVolume),
                                  IsDisabled = !audioOk });
            rowList.Add(new Row { Id = ActionId.SetMusicVolume, Label = "MUSIC",
                                  IsSlider = true, Value = Clamp01(musicVolume),
                                  IsDisabled = !audioOk });
            // When audio init failed, follow the sliders with a status
            // row showing the reason. Skipped on success so the menu
            // stays compact when everything works.
            if (!audioOk)
            {
                string reason = AudioEngine.InitFailureReason ?? "unknown error";
                // Trim long messages — the row width can't show much
                // without wrapping. The full reason is also written to
                // VS's debug output via Debug.WriteLine in AudioEngine.
                if (reason.Length > 80) reason = reason.Substring(0, 77) + "...";
                rowList.Add(new Row { Id = ActionId.None,
                                      Label = "AUDIO UNAVAILABLE — " + reason.ToUpperInvariant(),
                                      IsSection = true, IsDisabled = true });
            }
            // Tier 9 #53 V3 — Controls section + entry button.
            rowList.Add(new Row { Id = ActionId.None, Label = "CONTROLS", IsSection = true });
            rowList.Add(new Row { Id = ActionId.OpenControls, Label = "KEY BINDINGS..." });
            rowList.Add(new Row { Id = ActionId.Back, Label = "BACK" });
            var labels = rowList.ToArray();

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

        // Hit-test result. For toggle/button rows, SliderValue is unused.
        // For slider rows, SliderValue is the 0..1 position the user
        // clicked at — caller persists it via Settings + AudioEngine.
        public struct HitResult
        {
            public ActionId Id;
            public float    SliderValue;
        }

        public static HitResult HitTestEx(int screenW, int screenH, int mx, int my,
            bool hungerEnabled, bool isSurvival, bool useRealTextures,
            float masterVolume, float musicVolume,
            int renderDistance)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival, useRealTextures,
                                 masterVolume, musicVolume, renderDistance);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r.IsSection || r.IsDisabled) continue;
                if (mx < r.X || mx >= r.X + r.W || my < r.Y || my >= r.Y + r.H) continue;

                if (r.IsSlider)
                {
                    // Map mouse-X across the row's width to 0..1, with a
                    // small inset so clicking the very edges still snaps
                    // exactly to 0% or 100% rather than being eaten by
                    // border thickness.
                    int border = UiScale.S(2, screenW, screenH);
                    int trackX = r.X + border;
                    int trackW = r.W - 2 * border;
                    if (trackW < 1) trackW = 1;
                    float t = (mx - trackX) / (float)trackW;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                    return new HitResult { Id = r.Id, SliderValue = t };
                }
                return new HitResult { Id = r.Id, SliderValue = 0f };
            }
            return new HitResult { Id = ActionId.None, SliderValue = 0f };
        }

        // Back-compat helper for callers that don't care about slider
        // values (e.g. read-only hover detection during render). Forwards
        // through HitTestEx with current volume values; the renderer's
        // hover highlight only needs the ActionId.
        public static ActionId HitTest(int screenW, int screenH, int mx, int my,
            bool hungerEnabled, bool isSurvival, bool useRealTextures,
            float masterVolume, float musicVolume,
            int renderDistance)
        {
            return HitTestEx(screenW, screenH, mx, my,
                hungerEnabled, isSurvival, useRealTextures,
                masterVolume, musicVolume, renderDistance).Id;
        }

        // Y of the title text's top edge, sitting just above the first row.
        public static int TitleY(int screenW, int screenH,
            bool hungerEnabled, bool isSurvival, bool useRealTextures,
            float masterVolume, float musicVolume,
            int renderDistance)
        {
            var rows = BuildRows(screenW, screenH, hungerEnabled, isSurvival, useRealTextures,
                                 masterVolume, musicVolume, renderDistance);
            int firstY = rows[0].Y;
            int titleScale = TitleFontScale(screenW, screenH);
            return firstY - TitleGap(screenW, screenH) - HotbarTextures.GlyphCellH * titleScale;
        }

        // Tier 10 follow-up — Slider math + label for the render
        // distance row. Slider value 0..1 maps linearly to the
        // integer chunk-radius range.
        public static int SliderToRenderDistance(float t)
        {
            int min = Settings.RenderDistanceMin;
            int max = Settings.RenderDistanceMax;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            int v = (int)System.Math.Round(min + t * (max - min));
            if (v < min) v = min;
            else if (v > max) v = max;
            return v;
        }
        public static float RenderDistanceToSlider(int rd)
        {
            int min = Settings.RenderDistanceMin;
            int max = Settings.RenderDistanceMax;
            if (max == min) return 0f;
            float t = (rd - min) / (float)(max - min);
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return t;
        }
        private static string RenderDistanceLabel(int rd)
            => "RENDER DISTANCE: " + rd;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

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
