namespace VStudioCraft.Game
{
    // Tier 6 — Loading-screen layout helpers. No interactive elements
    // (the player can't do anything while a world is loading), so the
    // helper just exposes layout rectangles + font scales the renderer
    // uses to position chrome. Same UiScale-aware sizing convention
    // as PauseMenu / DeathScreen / TitleScreen so the loading screen
    // reads as part of the same modal family across viewport sizes.
    internal static class LoadingScreen
    {
        // Dirt-tile pixel size at scale=1; UiScale-scaled at lookup.
        // 32 px reads as a comfortable Minecraft-era tile pattern at
        // 1080p; smaller windows shrink it proportionally.
        private const int TilePixelSizeBase     = 32;
        private const int TitleFontScaleBase    = 5;
        private const int SubtitleFontScaleBase = 2;
        private const int TitleYOffsetBase      = 80;   // up from centre
        private const int ProgressBarWBase      = 360;
        private const int ProgressBarHBase      = 18;
        private const int ProgressBarYOffsetBase = 60;  // down from centre

        public static int TilePixelSize(int viewW, int viewH)
            => UiScale.S(TilePixelSizeBase, viewW, viewH);

        public static int TitleFontScale(int viewW, int viewH)
        {
            int s = (int)(TitleFontScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }

        public static int SubtitleFontScale(int viewW, int viewH)
        {
            int s = (int)(SubtitleFontScaleBase * UiScale.For(viewW, viewH) + 0.5f);
            return s < 1 ? 1 : s;
        }

        // Title text top-Y. Centred above the viewport midpoint so the
        // title-subtitle-progress stack sits visually balanced.
        public static int TitleY(int viewW, int viewH)
            => viewH / 2 - UiScale.S(TitleYOffsetBase, viewW, viewH);

        // Subtitle sits a fixed gap below the centre so the progress
        // bar (when present) lands further down without overlapping.
        public static int SubtitleY(int viewW, int viewH)
            => viewH / 2 + UiScale.S(8, viewW, viewH);

        // Progress bar rect — viewport-centred horizontally, sits a
        // small distance below the subtitle. Skipped entirely when
        // _loadingProgress < 0 (indeterminate phases like the MP
        // handshake before any chunks have arrived).
        public static (int x, int y, int w, int h) ProgressRect(int viewW, int viewH)
        {
            int bw = UiScale.S(ProgressBarWBase, viewW, viewH);
            int bh = UiScale.S(ProgressBarHBase, viewW, viewH);
            int by = viewH / 2 + UiScale.S(ProgressBarYOffsetBase, viewW, viewH);
            return ((viewW - bw) / 2, by, bw, bh);
        }
    }
}
