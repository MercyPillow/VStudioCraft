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

            var pixels = new byte[TileSize * TileSize * 4];

            // Tier 6 #47 — Try to slice the canonical Alpha break frames
            // out of terrain.png at row 15 (cols 0..9 = destroy_stage_0
            // through destroy_stage_9). Each tile in that row is a dark
            // grey-on-transparent crack overlay shaped exactly like the
            // procedural fallback below — same dark-line / monotone-
            // alpha aesthetic — so swapping to the canonical art doesn't
            // change rendering shape, just art fidelity. If the embedded
            // terrain.png isn't available (older deploy or corrupt
            // resource), fall through to the procedural path so the
            // overlays still render.
            if (BlockTextures.TryDecodeEmbeddedTerrain(out byte[] terrainBgra, out int srcW, out int srcH))
            {
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    BlockTextures.CopyTile(terrainBgra, srcW, srcH, /*col*/frame, /*row*/15, pixels);
                    // Convert the Alpha destroy_stage tile into an
                    // alpha-mask overlay. The source PNG paints the
                    // crack as dark lines on a near-opaque LIGHT
                    // background — without this transform that
                    // background would cover the block texture and
                    // it would look like the block disappears mid-
                    // break. Map luminance → alpha (white = 0, black
                    // = full) so only the dark crack lines remain
                    // visible while the breaking block's actual face
                    // stays readable underneath.
                    MaskAlphaCrackBackground(pixels);
                    GL.TexSubImage3D(
                        TextureTarget.Texture2DArray, 0,
                        0, 0, frame,
                        TileSize, TileSize, 1,
                        PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                }
            }
            else
            {
                // Procedural fallback. Single shared RNG so the segments
                // are stable across runs and each later frame literally
                // extends the earlier one (no buffer clear between
                // frames — pixels stays "monotonically darkening").
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

        // Tier 6 #47 — Convert the Alpha destroy_stage tile to a true
        // overlay mask. The source paints cracks as LIGHT pixels on a
        // DARK opaque background — opposite of what the first pass
        // assumed. Without this conversion the dark background would
        // sit opaque on top of the breaking block's face and the
        // crack lines themselves would punch through to the block
        // (the user reported seeing "block through crack lines, not
        // crack lines forming on top"), which is the inverse of what
        // we want.
        //
        // Transform per pixel:
        //   luminance = (R + G + B) / 3
        //   alpha    *= luminance / 255
        // Black background (luminance 0)   → alpha 0 (gone)
        // White cracks    (luminance 255)  → alpha unchanged (kept)
        // Mid-grey                          → partial alpha (smooth edges)
        // RGB is then pinned to dark grey so the lines render as a
        // consistent dark stroke across all 10 stage tiles regardless
        // of the source's exact palette — same shade the procedural
        // fallback uses, so the two paths are visually identical
        // except for line shape.
        private static void MaskAlphaCrackBackground(byte[] rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                byte r = rgba[i + 0];
                byte g = rgba[i + 1];
                byte b = rgba[i + 2];
                byte a = rgba[i + 3];
                int lum = (r + g + b) / 3;
                int newA = a * lum / 255;
                if (newA <= 0)
                {
                    rgba[i + 0] = 0;
                    rgba[i + 1] = 0;
                    rgba[i + 2] = 0;
                    rgba[i + 3] = 0;
                }
                else
                {
                    rgba[i + 0] = 30;
                    rgba[i + 1] = 30;
                    rgba[i + 2] = 30;
                    rgba[i + 3] = (byte)newA;
                }
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
