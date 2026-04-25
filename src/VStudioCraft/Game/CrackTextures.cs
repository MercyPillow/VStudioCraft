using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural 10-frame block-break "crack" overlay. Frames 0..9 represent
    // increasing damage: frame 0 is barely scratched, frame 9 is on the verge
    // of shattering. Stored as a Texture2DArray so the break-overlay shader
    // can pick the active frame via a uLayer uniform without rebinding.
    //
    // Style: dark grey lines on a fully transparent background. The overlay
    // shader alpha-tests at < 0.5, so we author every pixel as either fully
    // transparent or near-opaque dark — same convention as the torch tile.
    //
    // Each frame is generated from a deterministic seed so the crack pattern
    // is stable across runs (no flicker if a chunk re-meshes mid-break) and
    // each successive frame is a strict superset of the previous one — once
    // a crack has appeared, it doesn't move, just gets joined by new ones.
    internal static class CrackTextures
    {
        public const int TileSize = 16;
        public const int FrameCount = 10;

        public static int CreateAtlas()
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tex);

            GL.TexImage3D(
                TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba,
                TileSize, TileSize, FrameCount, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            // Buffer is reused across layers; we keep the previous frame's
            // pixels so each frame is a superset of the one before.
            var pixels = new byte[TileSize * TileSize * 4];
            // Pre-pick the cracks across all 10 frames using one master RNG so
            // the per-frame growth is monotone. Each frame contributes a few
            // new line segments; later frames also widen earlier ones.
            var rng = new Random(0xC4AC); // "crack"
            for (int frame = 0; frame < FrameCount; frame++)
            {
                int newSegments = 1 + frame; // 1, 2, ..., 10 cumulative growth
                for (int s = 0; s < newSegments; s++)
                {
                    DrawCrackSegment(pixels, rng, frame);
                }
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, frame,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            }

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        // One short jagged line. We pick a random start, an axis-biased direction
        // and a length 3..6, then walk pixel by pixel, occasionally veering one
        // pixel sideways so the crack reads as organic rather than ruler-straight.
        // Later frames get slightly thicker (a 50% chance of also painting the
        // perpendicular neighbour) so the damage looks like it's growing, not
        // just spreading.
        private static void DrawCrackSegment(byte[] pixels, Random rng, int frameIndex)
        {
            int x = 1 + rng.Next(TileSize - 2);
            int y = 1 + rng.Next(TileSize - 2);
            int len = 3 + rng.Next(4); // 3..6

            // 0 = horizontal-ish, 1 = vertical-ish, 2 = diagonal
            int axis = rng.Next(3);
            int dx, dy;
            switch (axis)
            {
                case 0: dx = rng.Next(2) == 0 ? -1 : 1; dy = 0; break;
                case 1: dx = 0; dy = rng.Next(2) == 0 ? -1 : 1; break;
                default:
                    dx = rng.Next(2) == 0 ? -1 : 1;
                    dy = rng.Next(2) == 0 ? -1 : 1;
                    break;
            }

            bool thicken = frameIndex >= 4;

            for (int i = 0; i < len; i++)
            {
                PaintCrackPixel(pixels, x, y, rng);

                if (thicken && rng.Next(2) == 0)
                {
                    // Smear one pixel perpendicular to the travel direction.
                    int px = x + (dy != 0 ? 1 : 0);
                    int py = y + (dx != 0 ? 1 : 0);
                    PaintCrackPixel(pixels, px, py, rng);
                }

                // Veer occasionally so the line isn't ruler-straight.
                if (rng.Next(3) == 0)
                {
                    if (dy == 0) y += rng.Next(2) == 0 ? -1 : 1;
                    else if (dx == 0) x += rng.Next(2) == 0 ? -1 : 1;
                }

                x += dx;
                y += dy;
                if (x < 0 || x >= TileSize || y < 0 || y >= TileSize) break;
            }
        }

        private static void PaintCrackPixel(byte[] pixels, int x, int y, Random rng)
        {
            if (x < 0 || x >= TileSize || y < 0 || y >= TileSize) return;
            int idx = (y * TileSize + x) * 4;
            // Don't lighten an existing crack pixel — once dark, stays dark
            // (monotone-darken keeps frame N a strict superset of frame N-1).
            if (pixels[idx + 3] > 0) return;
            // Slight per-pixel variation so the crack doesn't read as a single
            // uniform colour at non-pixel-perfect zoom levels.
            int jitter = rng.Next(20);
            byte v = (byte)(20 + jitter);
            pixels[idx]     = v;
            pixels[idx + 1] = v;
            pixels[idx + 2] = v;
            pixels[idx + 3] = 230;
        }
    }
}
