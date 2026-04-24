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
            byte[] rgba = new byte[SunPx * SunPx * 4];
            float cx = (SunPx - 1) * 0.5f;
            float cy = (SunPx - 1) * 0.5f;
            float rOuter = SunPx * 0.46f;   // disc edge
            float rInner = SunPx * 0.32f;   // bright core
            float rGlow  = SunPx * 0.50f;   // soft halo

            for (int py = 0; py < SunPx; py++)
            for (int px = 0; px < SunPx; px++)
            {
                float dx = px - cx, dy = py - cy;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                byte r, g, b, a;
                if (d <= rInner)
                {
                    // Hot white-yellow core
                    r = 255; g = 250; b = 220; a = 255;
                }
                else if (d <= rOuter)
                {
                    // Body: interpolate to warmer yellow at the edge
                    float t = (d - rInner) / (rOuter - rInner);
                    r = 255;
                    g = (byte)(250 - 40 * t);
                    b = (byte)(220 - 140 * t);
                    a = 255;
                }
                else if (d <= rGlow)
                {
                    // Halo: fade alpha to zero beyond the disc
                    float t = (d - rOuter) / (rGlow - rOuter);
                    r = 255; g = 210; b = 80;
                    a = (byte)(255 * (1f - t));
                }
                else
                {
                    r = g = b = a = 0;
                }
                int idx = (py * SunPx + px) * 4;
                rgba[idx + 0] = r;
                rgba[idx + 1] = g;
                rgba[idx + 2] = b;
                rgba[idx + 3] = a;
            }
            return Upload2D(rgba, SunPx, SunPx);
        }

        public static int CreateMoonTexture()
        {
            byte[] rgba = new byte[MoonPx * MoonPx * 4];
            float cx = (MoonPx - 1) * 0.5f;
            float cy = (MoonPx - 1) * 0.5f;
            float rOuter = MoonPx * 0.44f;
            float rGlow  = MoonPx * 0.50f;

            // Fixed "craters" (circles darker than the body) — picked by hand so
            // every moonrise looks the same without a full phase system.
            var craters = new (float cx, float cy, float r)[]
            {
                (MoonPx * 0.38f, MoonPx * 0.42f, MoonPx * 0.07f),
                (MoonPx * 0.58f, MoonPx * 0.40f, MoonPx * 0.05f),
                (MoonPx * 0.48f, MoonPx * 0.58f, MoonPx * 0.08f),
                (MoonPx * 0.62f, MoonPx * 0.60f, MoonPx * 0.04f),
            };

            for (int py = 0; py < MoonPx; py++)
            for (int px = 0; px < MoonPx; px++)
            {
                float dx = px - cx, dy = py - cy;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                byte r, g, b, a;
                if (d <= rOuter)
                {
                    // Pale off-white body. Crater test: if any crater contains
                    // the pixel, darken by a fixed amount.
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
                else if (d <= rGlow)
                {
                    float t = (d - rOuter) / (rGlow - rOuter);
                    r = 235; g = 235; b = 225;
                    a = (byte)(255 * (1f - t));
                }
                else
                {
                    r = g = b = a = 0;
                }
                int idx = (py * MoonPx + px) * 4;
                rgba[idx + 0] = r;
                rgba[idx + 1] = g;
                rgba[idx + 2] = b;
                rgba[idx + 3] = a;
            }
            return Upload2D(rgba, MoonPx, MoonPx);
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
