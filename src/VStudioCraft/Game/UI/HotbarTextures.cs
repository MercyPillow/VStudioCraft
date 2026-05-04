using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural hotbar chrome + bitmap font. Same approach as HudTextures —
    // every pixel is generated at init from geometric primitives and lookup
    // tables so we don't ship asset files.
    //
    // Three textures live here:
    //   1. Hotbar bar  : 182×22 strip with a thin border and a chiseled
    //                    inset for each of the 9 slot wells. Always drawn.
    //   2. Selected    : 24×24 bright-white frame, drawn on top of the
    //                    currently-selected slot. Slightly larger than a
    //                    slot so the highlight visibly overlaps the bar.
    //   3. Font sheet  : 96×48 RGBA bitmap font (16 cols × 6 rows of
    //                    6×8 glyphs). Uppercase letters, digits, and a
    //                    handful of punctuation. Used for the slot tooltip
    //                    above the bar today; later for stack counts and
    //                    debug overlays.
    //
    // Layout constants below match Alpha's hotbar dimensions so the on-screen
    // proportions read as Minecraft when 2×-upscaled.
    internal static class HotbarTextures
    {
        // ---- bar ----------------------------------------------------------
        public const int BarWidth = 182;
        public const int BarHeight = 22;
        public const int SlotInner = 20;     // slot well inside the 1px frame
        public const int SlotCount = 9;
        public const int IconInner = 16;     // block icon size (centered in the well)

        // ---- selected highlight -------------------------------------------
        public const int HighlightSize = 24;

        // ---- font ---------------------------------------------------------
        public const int GlyphCellW = 6;     // 5px glyph + 1px right-padding
        public const int GlyphCellH = 8;     // 7px glyph + 1px bottom-padding
        public const int FontSheetCols = 16;
        public const int FontSheetRows = 6;
        public const int FontSheetW = FontSheetCols * GlyphCellW; // 96
        public const int FontSheetH = FontSheetRows * GlyphCellH; // 48
        // ASCII range covered: 32..127. The (ch - FontFirstChar) index
        // maps into the row-major glyph grid.
        public const int FontFirstChar = 32;
        public const int FontCharCount = FontSheetCols * FontSheetRows;

        public static int CreateBarTexture()
        {
            var pixels = new byte[BarWidth * BarHeight * 4];

            // Two-tone palette: the bar reads as a slightly-translucent dark
            // chrome with a 1px outer border that's even darker. Slot wells
            // are a sliver brighter than the bar interior so the player can
            // tell where one slot ends and the next begins.
            byte borderR = 20,  borderG = 20,  borderB = 20,  borderA = 220;
            byte barR    = 70,  barG    = 70,  barB    = 70,  barA    = 200;
            byte wellR   = 90,  wellG   = 90,  wellB   = 90,  wellA   = 200;
            byte hiR     = 140, hiG     = 140, hiB     = 140, hiA     = 200;

            for (int py = 0; py < BarHeight; py++)
            for (int px = 0; px < BarWidth; px++)
            {
                int idx = (py * BarWidth + px) * 4;

                bool onBorder = px == 0 || px == BarWidth - 1 || py == 0 || py == BarHeight - 1;
                if (onBorder)
                {
                    pixels[idx] = borderR; pixels[idx + 1] = borderG; pixels[idx + 2] = borderB; pixels[idx + 3] = borderA;
                    continue;
                }

                // Inside the 1px frame: identify which slot we're in. Slot 0
                // starts at px=1, runs SlotInner px wide. There is no inter-
                // slot gap — slots butt up against each other and the frame.
                int local = px - 1;
                int slotX = local % SlotInner;
                bool slotEdge = slotX == 0 && local > 0;       // left edge of slots 1..8
                bool wellRow  = py == 1 || py == BarHeight - 2; // top + bottom inset row of the well

                if (slotEdge)
                {
                    // Vertical separator between slots — same shade as the border.
                    pixels[idx] = borderR; pixels[idx + 1] = borderG; pixels[idx + 2] = borderB; pixels[idx + 3] = borderA;
                }
                else if (wellRow)
                {
                    // Subtle top/bottom highlight inside each slot to fake a chamfer.
                    pixels[idx] = hiR; pixels[idx + 1] = hiG; pixels[idx + 2] = hiB; pixels[idx + 3] = hiA;
                }
                else
                {
                    pixels[idx] = wellR; pixels[idx + 1] = wellG; pixels[idx + 2] = wellB; pixels[idx + 3] = wellA;
                }

                // Mute the bar's interior by 1 shade where we're up against
                // the outer frame so the eye reads a soft inset.
                if (!slotEdge && !wellRow && (py == 1 || py == BarHeight - 2))
                {
                    pixels[idx] = barR; pixels[idx + 1] = barG; pixels[idx + 2] = barB; pixels[idx + 3] = barA;
                }
            }

            return UploadRgba(pixels, BarWidth, BarHeight);
        }

        public static int CreateSelectedHighlightTexture()
        {
            // 24×24 bright frame: a 2-px thick white border on the outside
            // and a 1-px black shadow inside it, leaving the centre transparent
            // so the slot icon underneath shows through unchanged.
            var pixels = new byte[HighlightSize * HighlightSize * 4];
            for (int py = 0; py < HighlightSize; py++)
            for (int px = 0; px < HighlightSize; px++)
            {
                int idx = (py * HighlightSize + px) * 4;
                int dx = Math.Min(px, HighlightSize - 1 - px);
                int dy = Math.Min(py, HighlightSize - 1 - py);
                int d = Math.Min(dx, dy);
                if (d <= 1)
                {
                    // Outer 2 rings — bright white with a slight inner shadow row.
                    byte v = (byte)(d == 0 ? 255 : 230);
                    pixels[idx] = v; pixels[idx + 1] = v; pixels[idx + 2] = v; pixels[idx + 3] = 255;
                }
                else if (d == 2)
                {
                    // Soft inner shadow so the highlight reads as raised on
                    // dark icons (otherwise pure white blends into the bar
                    // chrome on its inner edge).
                    pixels[idx] = 40; pixels[idx + 1] = 40; pixels[idx + 2] = 40; pixels[idx + 3] = 200;
                }
                else
                {
                    pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = pixels[idx + 3] = 0;
                }
            }
            return UploadRgba(pixels, HighlightSize, HighlightSize);
        }

        // ---- bitmap font ---------------------------------------------------
        //
        // Glyphs are stored as rows of strings — '#' = filled, anything else
        // is transparent. 5px wide × 7px tall per glyph, padded out to the
        // 6×8 cell with a transparent right column and bottom row so adjacent
        // characters don't visually touch.
        //
        // Only printable ASCII 32..127 is covered. Lowercase letters render
        // identically to uppercase — saves a lot of pixel-pushing for what's
        // basically just status text and block names today.
        public static int CreateFontTexture()
        {
            var pixels = new byte[FontSheetW * FontSheetH * 4];
            // Pre-fill transparent (already zero) — just need to paint glyphs.
            byte fr = 255, fg = 255, fb = 255, fa = 255;

            for (int ci = 0; ci < FontCharCount; ci++)
            {
                int ch = FontFirstChar + ci;
                if (ch >= 128) break;
                string[] glyph = GetGlyph((char)ch);
                if (glyph == null) continue;
                int col = ci % FontSheetCols;
                int row = ci / FontSheetCols;
                int x0 = col * GlyphCellW;
                int y0 = row * GlyphCellH;
                for (int gy = 0; gy < glyph.Length && gy < 7; gy++)
                {
                    string line = glyph[gy];
                    for (int gx = 0; gx < line.Length && gx < 5; gx++)
                    {
                        if (line[gx] != '#') continue;
                        int px = x0 + gx;
                        int py = y0 + gy;
                        int idx = (py * FontSheetW + px) * 4;
                        pixels[idx] = fr; pixels[idx + 1] = fg; pixels[idx + 2] = fb; pixels[idx + 3] = fa;
                    }
                }
            }

            return UploadRgba(pixels, FontSheetW, FontSheetH);
        }

        // Returns the glyph index (0..95) into the font sheet for an ASCII
        // character, or -1 if the character is out of range. Lowercase is
        // remapped to uppercase since we only ship one case.
        public static int GlyphIndex(char c)
        {
            if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
            if (c < FontFirstChar || c >= FontFirstChar + FontCharCount) return -1;
            return c - FontFirstChar;
        }

        // Returns the (uvOffsetX, uvOffsetY) of a glyph in the font sheet's
        // 0..1 UV space. Combined with (1/cols, 1/rows) UV scale, the sprite
        // shader samples the whole 6×8 cell — including the 1px transparent
        // pad — which is fine because the alpha-discard skips the padding.
        public static void GlyphUv(int glyphIndex, out float u, out float v)
        {
            int col = glyphIndex % FontSheetCols;
            int row = glyphIndex / FontSheetCols;
            u = (float)col / FontSheetCols;
            v = (float)row / FontSheetRows;
        }

        public const float GlyphUvW = 1f / FontSheetCols;
        public const float GlyphUvH = 1f / FontSheetRows;

        // Tier 10 #51 — Compass needle sprite. 16×16 transparent sprite
        // with a centred vertical needle: red top half (the north-
        // pointing tip), white bottom half (the south tail), a 1-px
        // dark outline so the needle reads against the pale dial face,
        // and a tiny black dot at the pivot. Drawn rotated by the
        // HUD pass on top of the static dial-face icon.
        public const int CompassNeedleSize = 16;
        public static int CreateCompassNeedleTexture()
        {
            int W = CompassNeedleSize, H = CompassNeedleSize;
            var pixels = new byte[W * H * 4];
            // Solid 0 alpha everywhere by default.
            void Px(int x, int y, byte r, byte g, byte b, byte a)
            {
                if ((uint)x >= W || (uint)y >= H) return;
                int i = (y * W + x) * 4;
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
            }
            byte tipR = 220, tipG = 50,  tipB = 50,  tipA = 255;
            byte tailR = 235, tailG = 235, tailB = 230, tailA = 255;
            byte outR = 30,   outG = 30,   outB = 30,   outA = 255;
            byte pivot = 20;
            // Needle: y=2..13 along the column at x=7..8. Top half
            // (y=2..7) is red, bottom half (y=8..13) is white.
            for (int y = 2; y <= 13; y++)
            {
                bool topHalf = y <= 7;
                byte r = topHalf ? tipR  : tailR;
                byte g = topHalf ? tipG  : tailG;
                byte b = topHalf ? tipB  : tailB;
                byte a = topHalf ? tipA  : tailA;
                Px(7, y, r, g, b, a);
                Px(8, y, r, g, b, a);
            }
            // Pointed tips — narrow to 1 px at the very top + bottom
            // so the needle reads as an arrow rather than a bar.
            Px(7, 1, tipR, tipG, tipB, tipA);
            Px(8, 1, tipR, tipG, tipB, tipA);
            Px(7, 14, tailR, tailG, tailB, tailA);
            Px(8, 14, tailR, tailG, tailB, tailA);
            // Dark outline column on each side of the shaft so the
            // needle silhouette stays visible against bright dial pixels.
            for (int y = 1; y <= 14; y++)
            {
                Px(6, y, outR, outG, outB, outA);
                Px(9, y, outR, outG, outB, outA);
            }
            // Top + bottom outline pips.
            Px(7, 0, outR, outG, outB, outA);
            Px(8, 0, outR, outG, outB, outA);
            Px(7, 15, outR, outG, outB, outA);
            Px(8, 15, outR, outG, outB, outA);
            // Small dark pivot dot at the centre — sells the rotation.
            Px(7, 7, pivot, pivot, pivot, 255);
            Px(8, 7, pivot, pivot, pivot, 255);
            Px(7, 8, pivot, pivot, pivot, 255);
            Px(8, 8, pivot, pivot, pivot, 255);
            return UploadRgba(pixels, W, H);
        }

        // ---- helpers ------------------------------------------------------
        private static int UploadRgba(byte[] pixels, int w, int h)
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }

        // 5×7 glyph table. Each row is 5 chars; '#' = filled. Anything not
        // listed below renders blank (so unsupported punctuation just appears
        // as whitespace rather than a tofu box).
        private static string[] GetGlyph(char c)
        {
            switch (c)
            {
                case ' ': return new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." };
                case '!': return new[] { "..#..", "..#..", "..#..", "..#..", ".....", ".....", "..#.." };
                case ':': return new[] { ".....", "..#..", ".....", ".....", ".....", "..#..", "....." };
                case '.': return new[] { ".....", ".....", ".....", ".....", ".....", ".....", "..#.." };
                case ',': return new[] { ".....", ".....", ".....", ".....", ".....", "..#..", ".#..." };
                case '/': return new[] { "....#", "...#.", "..#..", "..#..", ".#...", ".#...", "#...." };
                case '-': return new[] { ".....", ".....", ".....", ".###.", ".....", ".....", "....." };
                case '+': return new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." };
                case '%': return new[] { "##..#", "##.#.", "..#..", ".#.##", "#..##", ".....", "....." };
                case '(': return new[] { "...#.", "..#..", "..#..", "..#..", "..#..", "..#..", "...#." };
                case ')': return new[] { ".#...", "..#..", "..#..", "..#..", "..#..", "..#..", ".#..." };

                case '0': return new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." };
                case '1': return new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." };
                case '2': return new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" };
                case '3': return new[] { ".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###." };
                case '4': return new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." };
                case '5': return new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." };
                case '6': return new[] { ".###.", "#...#", "#....", "####.", "#...#", "#...#", ".###." };
                case '7': return new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." };
                case '8': return new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." };
                case '9': return new[] { ".###.", "#...#", "#...#", ".####", "....#", "#...#", ".###." };

                case 'A': return new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
                case 'B': return new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." };
                case 'C': return new[] { ".####", "#....", "#....", "#....", "#....", "#....", ".####" };
                case 'D': return new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." };
                case 'E': return new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" };
                case 'F': return new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." };
                case 'G': return new[] { ".####", "#....", "#....", "#..##", "#...#", "#...#", ".####" };
                case 'H': return new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
                case 'I': return new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." };
                case 'J': return new[] { "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.." };
                case 'K': return new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" };
                case 'L': return new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" };
                case 'M': return new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" };
                case 'N': return new[] { "#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#" };
                case 'O': return new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." };
                case 'P': return new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." };
                case 'Q': return new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" };
                case 'R': return new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" };
                case 'S': return new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." };
                case 'T': return new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." };
                case 'U': return new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." };
                case 'V': return new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." };
                case 'W': return new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" };
                case 'X': return new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" };
                case 'Y': return new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." };
                case 'Z': return new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" };

                default: return null;
            }
        }
    }
}
