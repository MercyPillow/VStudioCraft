using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural 10-frame block-break "crack" overlay. Frames 0..9 represent
    // increasing damage: frame 0 is barely scratched, frame 9 is on the verge
    // of shattering and covers most of the tile. Stored as a Texture2DArray
    // so the break-overlay shader can pick the active frame via a uLayer
    // uniform without rebinding.
    //
    // Style: dark grey lines on a fully transparent background. The overlay
    // shader alpha-tests at < 0.5, so we author every pixel as either fully
    // transparent or near-opaque dark — same convention as the torch tile.
    //
    // Each frame is generated from a deterministic seed so the crack pattern
    // is stable across runs (no flicker if a chunk re-meshes mid-break) and
    // each successive frame is a strict superset of the previous one — once
    // a crack has appeared, it doesn't move, just gets joined by new ones.
    //
    // Coverage targets (rough): frame 0 ≈ 8%, frame 4 ≈ 45%, frame 9 ≈ 90%.
    // The growth is roughly linear so each break stage feels equally damaging.
    internal static class CrackTextures
    {
        public const int TileSize = 16;
        public const int FrameCount = 10;

        // Number of crack segments added per frame. 4 short segments × 10 frames
        // = 40 segments total, which (with light late-frame thickening) covers
        // most of a 16×16 tile while keeping the scratchy line aesthetic.
        private const int SegmentsPerFrame = 4;

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
            // Single shared RNG so the segments are stable across runs and
            // each later frame literally extends the earlier one.
            var rng = new Random(0xC4AC); // "crack"

            for (int frame = 0; frame < FrameCount; frame++)
            {
                for (int s = 0; s < SegmentsPerFrame; s++)
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

        // One short jagged line, 4..7 pixels long, starting in a sparsely
        // populated region of the tile. Direction is biased to one of three
        // axes (horizontal, vertical, diagonal) and the line veers occasionally
        // so it reads as organic rather than ruler-straight. From frame 5
        // onward we occasionally paint a perpendicular neighbor too, which
        // gives the late frames enough fill to read as "almost shattered"
        // without losing the line-based crack aesthetic.
        private static void DrawCrackSegment(byte[] pixels, Random rng, int frameIndex)
        {
            PickSparseStart(pixels, rng, out int x, out int y);
            int len = 4 + rng.Next(4); // 4..7

            // 0 = horizontal, 1 = vertical, 2 = diagonal
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

            // Late-frame thickening: from the midpoint of the animation onward,
            // each painted pixel has a 25% chance of also painting one
            // perpendicular neighbor. Frame 0..4 stay pure single-pixel lines.
            bool thicken = frameIndex >= 5;

            for (int i = 0; i < len; i++)
            {
                PaintCrackPixel(pixels, x, y, rng);

                if (thicken && rng.Next(4) == 0)
                {
                    // Pick one perpendicular neighbor to also darken.
                    int sign = rng.Next(2) == 0 ? -1 : 1;
                    if (dy == 0)       PaintCrackPixel(pixels, x, y + sign, rng);
                    else if (dx == 0)  PaintCrackPixel(pixels, x + sign, y, rng);
                    else               PaintCrackPixel(pixels, x + sign, y - sign, rng); // diag → anti-diag
                }

                // Veer occasionally so the line isn't a straight ruler stroke.
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

        // Pick a starting pixel by sampling a handful of candidates and choosing
        // the one whose 5×5 neighborhood currently has the fewest crack pixels.
        // This spreads new segments into empty regions instead of letting them
        // pile on top of existing cracks — which is what gives the late frames
        // their "evenly damaged tile" look rather than a couple of dense blobs.
        private static void PickSparseStart(byte[] pixels, Random rng, out int x, out int y)
        {
            const int candidates = 4;
            int bestX = 0, bestY = 0;
            int bestScore = int.MaxValue;
            for (int c = 0; c < candidates; c++)
            {
                int cx = 2 + rng.Next(TileSize - 4);
                int cy = 2 + rng.Next(TileSize - 4);
                int score = NeighborhoodCoverage(pixels, cx, cy);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestX = cx;
                    bestY = cy;
                }
            }
            x = bestX;
            y = bestY;
        }

        // Count painted pixels in a 5×5 box around (cx, cy), clamped to bounds.
        private static int NeighborhoodCoverage(byte[] pixels, int cx, int cy)
        {
            int count = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                int yy = cy + dy;
                if (yy < 0 || yy >= TileSize) continue;
                for (int dx = -2; dx <= 2; dx++)
                {
                    int xx = cx + dx;
                    if (xx < 0 || xx >= TileSize) continue;
                    int idx = (yy * TileSize + xx) * 4;
                    if (pixels[idx + 3] > 0) count++;
                }
            }
            return count;
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
