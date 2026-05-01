using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural sky sprites: sun disc, moon disc, and a scrolling cloud
    // noise texture. All generated at runtime so the project ships no image
    // assets — same pattern as BlockTextures and HudTextures.
    internal static class SkyTextures
    {
        // Alpha's sun/moon are 30×30 pixels; ours are higher-res so the discs
        // read smooth when they project to a couple of degrees of sky.
        public const int SunPx = 32;
        public const int MoonPx = 32;
        public const int CloudPx = 256;

        public static int CreateSunTexture()
        {
            // Tier 7 #41 — Square sun, matching the canonical Alpha
            // 1.1.2 look: a flat opaque yellow square filling the
            // billboard quad with a slight inner→edge warm gradient
            // for visual depth. No circular halo or anti-aliased
            // disc — the silhouette is a hard square.
            byte[] rgba = new byte[SunPx * SunPx * 4];
            float halfPx = (SunPx - 1) * 0.5f;
            for (int py = 0; py < SunPx; py++)
            for (int px = 0; px < SunPx; px++)
            {
                // Distance to centre, normalised by half-width on
                // the longer axis — used only for a subtle gradient
                // toward the warmer yellow at the edges of the
                // square. Not a circular cutoff.
                float dx = (px - halfPx) / halfPx;
                float dy = (py - halfPx) / halfPx;
                float r2 = Math.Min(1f, Math.Max(Math.Abs(dx), Math.Abs(dy)));
                byte r = 255;
                byte g = (byte)(250 - 30 * r2);
                byte b = (byte)(200 - 100 * r2);
                byte a = 255;
                int idx = (py * SunPx + px) * 4;
                rgba[idx + 0] = r;
                rgba[idx + 1] = g;
                rgba[idx + 2] = b;
                rgba[idx + 3] = a;
            }
            return Upload2D(rgba, SunPx, SunPx);
        }

        // Tier 7 #41 — 8-frame moon phase strip texture, laid out as
        // 8 sub-images of MoonPx × MoonPx packed horizontally into a
        // single (8 * MoonPx) × MoonPx atlas. Phase 0 = full moon;
        // phases 1..3 wane the lit-from-the-EAST half (mirrors a
        // real waning moon as seen from the northern hemisphere);
        // phase 4 = new moon (dark disc); phases 5..7 wax back to
        // full. The lit fraction is determined by a half-plane cut:
        // for each phase, pixels on the "lit" side of a vertical
        // line through the moon centre are bright, the "dark" side
        // is shadowed (very faint silhouette so the disc still
        // reads as round even at new moon).
        //
        // Layout choice: a single 2D strip + UV offset is much
        // simpler than a Texture2DArray (no separate uniform plumb
        // for the layer index) — the billboard shader just receives
        // a UV-offset/scale pair from the renderer per draw call.
        public static int CreateMoonPhasesTexture(int frames = 8)
        {
            // Tier 7 #41 — Square moon phases, matching the canonical
            // Alpha 1.1.2 look: each phase frame is a flat square,
            // not a circular disc. The PHASE shadow is still a
            // vertical-ish terminator splitting the square into a
            // bright lit half + a dim dark half. Craters add
            // texture to the lit side. No outer glow halo.
            int stripW = MoonPx * frames;
            byte[] rgba = new byte[stripW * MoonPx * 4];
            float halfPx = (MoonPx - 1) * 0.5f;
            float rOuter = MoonPx * 0.5f;   // half the square edge

            // Fixed "craters" — same hand-picked positions as the
            // circular version so the moon still has its
            // recognisable face. Crater radius is small relative to
            // the square so they read as dark spots, not lobes.
            var craters = new (float cx, float cy, float r)[]
            {
                (MoonPx * 0.38f, MoonPx * 0.42f, MoonPx * 0.07f),
                (MoonPx * 0.58f, MoonPx * 0.40f, MoonPx * 0.05f),
                (MoonPx * 0.48f, MoonPx * 0.58f, MoonPx * 0.08f),
                (MoonPx * 0.62f, MoonPx * 0.60f, MoonPx * 0.04f),
            };

            for (int frame = 0; frame < frames; frame++)
            {
                // litFraction: 1 = full, 0 = new. Indexed sequence
                // 1.0, 0.75, 0.5, 0.25, 0.0, 0.25, 0.5, 0.75 cycles
                // full → waning → new → waxing → full.
                float litFrac;
                bool litLeft;   // which half of the disc is lit at this phase
                switch (frame)
                {
                    case 0: litFrac = 1.0f; litLeft = true;  break; // full
                    case 1: litFrac = 0.75f; litLeft = true; break; // waning gibbous
                    case 2: litFrac = 0.5f;  litLeft = true; break; // last quarter (right half lit, but we set litLeft as anchor)
                    case 3: litFrac = 0.25f; litLeft = true; break; // waning crescent
                    case 4: litFrac = 0.0f;  litLeft = true; break; // new moon
                    case 5: litFrac = 0.25f; litLeft = false; break; // waxing crescent
                    case 6: litFrac = 0.5f;  litLeft = false; break; // first quarter
                    default: litFrac = 0.75f; litLeft = false; break; // waxing gibbous
                }

                int frameOffsetX = frame * MoonPx;
                for (int py = 0; py < MoonPx; py++)
                for (int px = 0; px < MoonPx; px++)
                {
                    float dx = px - halfPx;
                    // Phase terminator — vertical line splitting the
                    // square. For litFrac = 1 the terminator is past
                    // the square on the dark side (whole square lit);
                    // for litFrac = 0 it's past the square on the lit
                    // side (whole square dark). For the litLeft
                    // (waning) sequence the lit half is on -X, so the
                    // terminator's X moves from +rOuter (full) toward
                    // -rOuter (new), and a pixel is lit when its dx
                    // is LESS than the boundary. The litRight (waxing)
                    // sequence mirrors: terminator from -rOuter (new)
                    // toward +rOuter (full), pixel lit when dx is
                    // GREATER than boundary.
                    //
                    // Earlier this used (1 - 2*litFrac), which gave
                    // -rOuter at litFrac=1 — making the full-moon
                    // pixel-lit test fail for every pixel inside the
                    // moon and rendering the whole disc as the dark
                    // side. Sign corrected to (2*litFrac - 1) below.
                    float boundary;
                    if (litLeft)
                        boundary = (2f * litFrac - 1f) * rOuter;
                    else
                        boundary = (1f - 2f * litFrac) * rOuter;
                    bool pixelLit = litLeft ? (dx < boundary) : (dx > boundary);

                    byte r, g, b, a;
                    if (pixelLit)
                    {
                        // Pale off-white body. Crater test: any
                        // crater containing the pixel darkens it.
                        r = 235; g = 235; b = 225;
                        foreach (var c in craters)
                        {
                            float ddx = px - c.cx, ddy = py - c.cy;
                            if (ddx * ddx + ddy * ddy <= c.r * c.r)
                            {
                                r = 180; g = 180; b = 170;
                                break;
                            }
                        }
                        a = 255;
                    }
                    else
                    {
                        // Dark side — dim navy so the silhouette
                        // still reads as a square at new moon.
                        r = 22; g = 22; b = 30;
                        a = 200;
                    }
                    int idx = (py * stripW + frameOffsetX + px) * 4;
                    rgba[idx + 0] = r;
                    rgba[idx + 1] = g;
                    rgba[idx + 2] = b;
                    rgba[idx + 3] = a;
                }
            }
            return Upload2D(rgba, stripW, MoonPx);
        }

        // Two-octave value noise thresholded into puffy clouds. The clouds are
        // pure white with semi-transparent alpha so they blend over the sky
        // and accept the fog tint at render time.
        public static int CreateCloudTexture()
        {
            byte[] rgba = new byte[CloudPx * CloudPx * 4];
            // Deterministic noise field — reseeded every run is fine because
            // the field is uploaded once and never regenerated.
            var rng = new Random(unchecked((int)0xC10D5A11));
            const int gridCoarse = 16;   // big blobs
            const int gridFine = 32;     // small wisps
            float[,] nCoarse = ValueNoise(rng, gridCoarse);
            float[,] nFine   = ValueNoise(rng, gridFine);

            for (int py = 0; py < CloudPx; py++)
            for (int px = 0; px < CloudPx; px++)
            {
                float u = px / (float)CloudPx;
                float v = py / (float)CloudPx;
                // Bilinear sample each noise grid (tiling); combine with
                // weights so coarse blobs dominate and fine wisps add texture.
                float n = 0.65f * SampleTiled(nCoarse, gridCoarse, u, v)
                        + 0.35f * SampleTiled(nFine,   gridFine,   u, v);

                // Soft threshold: below 0.45 is sky (transparent), above 0.55
                // is solid cloud; in between is a feathered edge.
                float a;
                if (n < 0.45f) a = 0f;
                else if (n > 0.55f) a = 0.85f;   // not fully opaque — Alpha clouds are a little see-through
                else a = 0.85f * (n - 0.45f) / 0.10f;

                int idx = (py * CloudPx + px) * 4;
                rgba[idx + 0] = 255;
                rgba[idx + 1] = 255;
                rgba[idx + 2] = 255;
                rgba[idx + 3] = (byte)(a * 255f);
            }
            return Upload2D(rgba, CloudPx, CloudPx, wrapRepeat: true);
        }

        private static float[,] ValueNoise(Random rng, int size)
        {
            // Grid of random values in [0,1] plus a wrapped extra row/col so
            // the bilinear interpolation tiles without seams.
            var grid = new float[size + 1, size + 1];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                grid[x, y] = (float)rng.NextDouble();
            // Wrap edges
            for (int y = 0; y <= size; y++) grid[size, y] = grid[0, y % size];
            for (int x = 0; x <= size; x++) grid[x, size] = grid[x % size, 0];
            return grid;
        }

        private static float SampleTiled(float[,] grid, int size, float u, float v)
        {
            float x = u * size;
            float y = v * size;
            int x0 = (int)Math.Floor(x) % size; if (x0 < 0) x0 += size;
            int y0 = (int)Math.Floor(y) % size; if (y0 < 0) y0 += size;
            int x1 = (x0 + 1) % size;
            int y1 = (y0 + 1) % size;
            float fx = x - (float)Math.Floor(x);
            float fy = y - (float)Math.Floor(y);
            // Smoothstep for fewer square artifacts than raw bilinear.
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = grid[x0, y0] * (1f - fx) + grid[x1, y0] * fx;
            float b = grid[x0, y1] * (1f - fx) + grid[x1, y1] * fx;
            return a * (1f - fy) + b * fy;
        }

        private static int Upload2D(byte[] rgba, int w, int h, bool wrapRepeat = false)
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            // Linear sampling on sun/moon keeps their discs smooth; on clouds
            // it hides the 256-pixel grid when tiled over a large quad.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            int wrap = wrapRepeat ? (int)TextureWrapMode.Repeat : (int)TextureWrapMode.ClampToEdge;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrap);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrap);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }
    }
}
