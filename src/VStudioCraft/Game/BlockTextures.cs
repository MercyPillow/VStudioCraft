using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural 16x16 tiles arranged as a 2D texture array, emulating Alpha
    // 1.1.2_01's coarse three-tone noise look. Palettes approximate the alpha
    // terrain.png, but no pixels are copied — every tile is regenerated from a
    // seeded RNG so the output is original art with an alpha-era feel.
    internal static class BlockTextures
    {
        public const int TileSize = 16;
        public const int LayerCount = 33;

        public const int TileGrassTop = 0;
        public const int TileGrassSide = 1;
        public const int TileDirt = 2;
        public const int TileStone = 3;
        public const int TileSand = 4;
        public const int TileCobblestone = 5;
        public const int TileBedrock = 6;
        public const int TileGravel = 7;
        public const int TileClay = 8;
        public const int TileCoalOre = 9;
        public const int TileIronOre = 10;
        public const int TileGoldOre = 11;
        public const int TileDiamondOre = 12;
        public const int TileRedstoneOre = 13;
        public const int TileLogTop = 14;
        public const int TileLogSide = 15;
        public const int TilePlanks = 16;
        public const int TileLeaves = 17;
        public const int TileWater = 18;
        public const int TileLava = 19;
        public const int TileGoldBlock = 20;
        public const int TileIronBlock = 21;
        public const int TileDiamondBlock = 22;
        public const int TileBricks = 23;
        public const int TileTntTop = 24;
        public const int TileTntBottom = 25;
        public const int TileTntSide = 26;
        public const int TileBookshelfSide = 27;
        public const int TileMossyCobblestone = 28;
        public const int TileObsidian = 29;
        public const int TileSponge = 30;
        public const int TileGlass = 31;
        public const int TileWool = 32;

        // A 2D texture array — one layer per tile. Greedy meshing can emit merged
        // quads with UVs exceeding [0,1]; with a layered texture and Repeat wrap the
        // fragment shader just samples texelFetch-equivalent `texture(array, vec3(fract(uv), layer))`.
        public static int CreateAtlas()
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tex);

            // Allocate storage for all layers up front.
            GL.TexImage3D(
                TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba,
                TileSize, TileSize, LayerCount, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            var layerPixels = new byte[TileSize * TileSize * 4];
            UploadLayer(layerPixels, TileGrassTop, GenerateGrassTop);
            UploadLayer(layerPixels, TileGrassSide, GenerateGrassSide);
            UploadLayer(layerPixels, TileDirt, GenerateDirt);
            UploadLayer(layerPixels, TileStone, GenerateStone);
            UploadLayer(layerPixels, TileSand, GenerateSand);
            UploadLayer(layerPixels, TileCobblestone, GenerateCobblestone);
            UploadLayer(layerPixels, TileBedrock, GenerateBedrock);
            UploadLayer(layerPixels, TileGravel, GenerateGravel);
            UploadLayer(layerPixels, TileClay, GenerateClay);
            UploadLayer(layerPixels, TileCoalOre, GenerateCoalOre);
            UploadLayer(layerPixels, TileIronOre, GenerateIronOre);
            UploadLayer(layerPixels, TileGoldOre, GenerateGoldOre);
            UploadLayer(layerPixels, TileDiamondOre, GenerateDiamondOre);
            UploadLayer(layerPixels, TileRedstoneOre, GenerateRedstoneOre);
            UploadLayer(layerPixels, TileLogTop, GenerateLogTop);
            UploadLayer(layerPixels, TileLogSide, GenerateLogSide);
            UploadLayer(layerPixels, TilePlanks, GeneratePlanks);
            UploadLayer(layerPixels, TileLeaves, GenerateLeaves);
            UploadLayer(layerPixels, TileWater, GenerateWater);
            UploadLayer(layerPixels, TileLava, GenerateLava);
            UploadLayer(layerPixels, TileGoldBlock, GenerateGoldBlock);
            UploadLayer(layerPixels, TileIronBlock, GenerateIronBlock);
            UploadLayer(layerPixels, TileDiamondBlock, GenerateDiamondBlock);
            UploadLayer(layerPixels, TileBricks, GenerateBricks);
            UploadLayer(layerPixels, TileTntTop, GenerateTntTop);
            UploadLayer(layerPixels, TileTntBottom, GenerateTntBottom);
            UploadLayer(layerPixels, TileTntSide, GenerateTntSide);
            UploadLayer(layerPixels, TileBookshelfSide, GenerateBookshelfSide);
            UploadLayer(layerPixels, TileMossyCobblestone, GenerateMossyCobblestone);
            UploadLayer(layerPixels, TileObsidian, GenerateObsidian);
            UploadLayer(layerPixels, TileSponge, GenerateSponge);
            UploadLayer(layerPixels, TileGlass, GenerateGlass);
            UploadLayer(layerPixels, TileWool, GenerateWool);

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        private delegate void LayerFiller(byte[] pixels);

        private static void UploadLayer(byte[] pixels, int layer, LayerFiller fill)
        {
            Array.Clear(pixels, 0, pixels.Length);
            fill(pixels);
            GL.TexSubImage3D(
                TextureTarget.Texture2DArray, 0,
                0, 0, layer,
                TileSize, TileSize, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }

        private static void SetPixel(byte[] p, int px, int py, byte r, byte g, byte b, byte a = 255)
        {
            int idx = (py * TileSize + px) * 4;
            p[idx] = r;
            p[idx + 1] = g;
            p[idx + 2] = b;
            p[idx + 3] = a;
        }

        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        private static (byte r, byte g, byte b) Pick(Random rng, (byte r, byte g, byte b)[] palette, int[] weights)
        {
            int total = 0;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            int pick = rng.Next(total);
            int acc = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                acc += weights[i];
                if (pick < acc) return palette[i];
            }
            return palette[palette.Length - 1];
        }

        private static void NoiseFill(byte[] pixels, int seed, (byte r, byte g, byte b)[] palette, int[] weights)
        {
            var rng = new Random(seed);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        // Pixel-level random jitter around a base color — used for structured tiles
        // (planks, bricks, logs) where the shape is deterministic but we still want
        // the gritty per-pixel variation typical of alpha terrain.
        private static void SetJittered(byte[] pixels, int x, int y, byte r, byte g, byte b, int jitter, Random rng)
        {
            int j = rng.Next(jitter * 2 + 1) - jitter;
            SetPixel(pixels, x, y, Clamp(r + j), Clamp(g + j), Clamp(b + j));
        }

        private static void GenerateDirt(byte[] pixels)
        {
            var rng = new Random(0x0D17);
            var palette = new (byte, byte, byte)[]
            {
                (134, 96, 67),
                (107, 76, 48),
                (156, 116, 80),
                (92, 63, 38),
            };
            var weights = new[] { 10, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateStone(byte[] pixels)
        {
            var rng = new Random(0x570E);
            var palette = new (byte, byte, byte)[]
            {
                (124, 124, 124),
                (108, 108, 108),
                (140, 140, 140),
                (92, 92, 92),
            };
            var weights = new[] { 12, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateGrassTop(byte[] pixels)
        {
            var rng = new Random(0x6A55);
            var palette = new (byte, byte, byte)[]
            {
                (93, 150, 57),
                (76, 126, 44),
                (110, 170, 70),
                (63, 108, 36),
            };
            var weights = new[] { 11, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateSand(byte[] pixels)
        {
            var rng = new Random(0x5A4D);
            var palette = new (byte, byte, byte)[]
            {
                (219, 209, 150),
                (206, 194, 132),
                (232, 223, 170),
                (190, 176, 116),
            };
            var weights = new[] { 14, 4, 3, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateGrassSide(byte[] pixels)
        {
            var dirtRng = new Random(0x51DE);
            var dirtPalette = new (byte, byte, byte)[]
            {
                (134, 96, 67),
                (107, 76, 48),
                (156, 116, 80),
                (92, 63, 38),
            };
            var dirtWeights = new[] { 10, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(dirtRng, dirtPalette, dirtWeights);
                SetPixel(pixels, x, y, r, g, b);
            }

            // Green overhang along the top edge. v=0 is the bottom of the texture,
            // so the green fringe lives at high y to render along the top of the face.
            var grassRng = new Random(0x67A5);
            var grassPalette = new (byte, byte, byte)[]
            {
                (93, 150, 57),
                (76, 126, 44),
                (110, 170, 70),
                (63, 108, 36),
            };
            var grassWeights = new[] { 11, 5, 4, 1 };

            var fringe = new int[TileSize];
            for (int x = 0; x < TileSize; x++) fringe[x] = 2 + grassRng.Next(4);

            for (int x = 0; x < TileSize; x++)
            {
                int depth = fringe[x];
                for (int d = 0; d < depth; d++)
                {
                    int y = TileSize - 1 - d;
                    var (r, g, b) = Pick(grassRng, grassPalette, grassWeights);
                    SetPixel(pixels, x, y, r, g, b);
                }
            }
        }

        private static void GenerateCobblestone(byte[] pixels)
        {
            var rng = new Random(0xCB15);
            var light = new (byte, byte, byte)[]
            {
                (140, 140, 140),
                (156, 156, 156),
                (124, 124, 124),
                (168, 168, 168),
            };
            var lightWeights = new[] { 10, 6, 5, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, light, lightWeights);
                SetPixel(pixels, x, y, r, g, b);
            }
            // Scatter darker "cracks" — short clusters mimicking cobble pits.
            var dark = new (byte, byte, byte)[]
            {
                (72, 72, 72),
                (56, 56, 56),
                (88, 88, 88),
            };
            var darkWeights = new[] { 6, 3, 2 };
            for (int n = 0; n < 22; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(3) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    var (dr, dg, db) = Pick(rng, dark, darkWeights);
                    SetPixel(pixels, x, y, dr, dg, db);
                }
            }
        }

        private static void GenerateBedrock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (56, 56, 56),
                (40, 40, 40),
                (72, 72, 72),
                (28, 28, 28),
            };
            NoiseFill(pixels, 0xBED0, palette, new[] { 12, 6, 4, 2 });
        }

        private static void GenerateGravel(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (140, 134, 126),
                (112, 105, 95),
                (160, 150, 138),
                (88, 78, 64),
                (168, 142, 110),
            };
            NoiseFill(pixels, 0x61A4, palette, new[] { 10, 6, 4, 3, 2 });
        }

        private static void GenerateClay(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (166, 170, 186),
                (150, 154, 172),
                (180, 184, 198),
                (138, 142, 162),
            };
            NoiseFill(pixels, 0xC1A7, palette, new[] { 12, 5, 3, 2 });
        }

        // Shared helper: stone base + scattered ore clusters. Each ore picks a
        // palette + a distinct seed so clusters land in different spots.
        private static void GenerateOre(byte[] pixels, int seed, (byte r, byte g, byte b)[] orePalette, int[] oreWeights)
        {
            var stonePalette = new (byte, byte, byte)[]
            {
                (124, 124, 124),
                (108, 108, 108),
                (140, 140, 140),
                (92, 92, 92),
            };
            var stoneWeights = new[] { 12, 5, 4, 1 };
            var rng = new Random(seed);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, stonePalette, stoneWeights);
                SetPixel(pixels, x, y, r, g, b);
            }
            int clusters = 3 + rng.Next(3);
            for (int n = 0; n < clusters; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(4) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    var (or_, og, ob) = Pick(rng, orePalette, oreWeights);
                    SetPixel(pixels, x, y, or_, og, ob);
                }
            }
        }

        private static void GenerateCoalOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (30, 30, 30),
                (48, 48, 48),
                (18, 18, 18),
            };
            GenerateOre(pixels, 0xC0A1, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateIronOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (186, 150, 120),
                (166, 130, 100),
                (210, 170, 140),
            };
            GenerateOre(pixels, 0x1201, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateGoldOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (240, 214, 78),
                (220, 190, 58),
                (252, 232, 120),
            };
            GenerateOre(pixels, 0x601D, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateDiamondOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (120, 230, 230),
                (90, 200, 210),
                (180, 250, 250),
            };
            GenerateOre(pixels, 0x0D1A, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateRedstoneOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (200, 40, 30),
                (230, 60, 50),
                (160, 20, 14),
            };
            GenerateOre(pixels, 0xDEA5, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateLogTop(byte[] pixels)
        {
            // Alpha log tops are a pale-tan field with darker concentric rings
            // and a dark heartwood pip. Sample radius from slightly off-centre
            // so the rings don't look like a perfect target.
            var rng = new Random(0x106);
            float cx = TileSize / 2f - 0.5f + 0.4f;
            float cy = TileSize / 2f - 0.5f - 0.3f;
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                byte r, g, b;
                // Tight dark core (heartwood), then alternating light field and
                // thin dark ring lines at integer radii.
                float frac = d - (float)Math.Floor(d);
                bool onRing = frac < 0.35f && d > 1.2f;
                if (d < 1.3f)      { r = 82;  g = 58; b = 30; }  // pip
                else if (onRing)   { r = 128; g = 92; b = 50; }  // ring line
                else               { r = 188; g = 150; b = 96; } // field
                SetJittered(pixels, x, y, r, g, b, 7, rng);
            }
        }

        private static void GenerateLogSide(byte[] pixels)
        {
            // Alpha oak bark: warm brown field with vertical grooves, scattered
            // dark cracks, and a couple of knots. The stripes give the "tall bark"
            // read; the cracks and knots stop it looking like a barcode.
            var rng = new Random(0x5106);

            // Base: per-column tone so adjacent pixels in a column share a shade.
            // This mimics bark fibres better than purely random noise.
            byte[] columnR = new byte[TileSize];
            byte[] columnG = new byte[TileSize];
            byte[] columnB = new byte[TileSize];
            for (int x = 0; x < TileSize; x++)
            {
                int stripe = x % 4;
                byte r, g, b;
                if (stripe == 0)      { r = 84;  g = 60;  b = 32; }   // groove
                else if (stripe == 1) { r = 118; g = 88;  b = 50; }
                else if (stripe == 2) { r = 134; g = 100; b = 58; }
                else                  { r = 108; g = 80;  b = 46; }
                columnR[x] = r; columnG[x] = g; columnB[x] = b;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, columnR[x], columnG[x], columnB[x], 6, rng);

            // Short horizontal cracks — 1-3px long, dark, scattered.
            for (int n = 0; n < 14; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int len = 1 + rng.Next(3);
                for (int d = 0; d < len; d++)
                {
                    int x = (cx + d) % TileSize;
                    SetJittered(pixels, x, cy, 58, 42, 22, 5, rng);
                }
            }

            // A couple of knots: small dark blobs with a lighter ring.
            for (int n = 0; n < 2; n++)
            {
                int kx = rng.Next(TileSize);
                int ky = 2 + rng.Next(TileSize - 4);
                SetPixel(pixels, kx, ky, 52, 36, 18);
                int kxp = (kx + 1) % TileSize;
                int kxm = (kx - 1 + TileSize) % TileSize;
                SetJittered(pixels, kxp, ky, 96, 70, 40, 5, rng);
                SetJittered(pixels, kxm, ky, 96, 70, 40, 5, rng);
                SetJittered(pixels, kx, (ky + 1) % TileSize, 96, 70, 40, 5, rng);
                SetJittered(pixels, kx, (ky - 1 + TileSize) % TileSize, 96, 70, 40, 5, rng);
            }
        }

        private static void GeneratePlanks(byte[] pixels)
        {
            // Four-row-tall planks: one-pixel darker groove at the top of each plank.
            var rng = new Random(0xB12C);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)123 : (byte)160;
                byte g = groove ? (byte)91 : (byte)124;
                byte b = groove ? (byte)52 : (byte)74;
                SetJittered(pixels, x, y, r, g, b, 7, rng);
            }
        }

        private static void GenerateLeaves(byte[] pixels)
        {
            // Fast-graphics-style opaque leaves: dense green noise with some
            // near-black specks reading as gaps through the canopy.
            var palette = new (byte, byte, byte)[]
            {
                (48, 92, 30),
                (40, 78, 24),
                (62, 108, 38),
                (30, 60, 16),
                (20, 40, 10),
            };
            NoiseFill(pixels, 0x1EAF, palette, new[] { 10, 6, 3, 4, 2 });
        }

        private static void GenerateWater(byte[] pixels)
        {
            // Alpha still water: calm bluish noise with ~160 alpha so stone and sand
            // below read through. Blending is enabled in the renderer's transparent pass.
            var rng = new Random(0xAA7E);
            var palette = new (byte, byte, byte)[]
            {
                (56, 92, 204),
                (42, 78, 184),
                (78, 120, 220),
                (32, 60, 160),
            };
            var weights = new[] { 12, 5, 4, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b, a: 160);
            }
        }

        private static void GenerateLava(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (214, 78, 22),
                (184, 52, 12),
                (240, 140, 40),
                (250, 200, 60),
            };
            NoiseFill(pixels, 0x1A7A, palette, new[] { 10, 6, 4, 2 });
        }

        private static void GenerateGoldBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (240, 220, 60),
                (220, 196, 40),
                (252, 240, 120),
                (190, 170, 24),
            };
            NoiseFill(pixels, 0x901D, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateIronBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (220, 220, 220),
                (196, 196, 196),
                (240, 240, 240),
                (170, 170, 170),
            };
            NoiseFill(pixels, 0x1AB0, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateDiamondBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (90, 210, 220),
                (60, 180, 200),
                (140, 240, 240),
                (40, 150, 180),
            };
            NoiseFill(pixels, 0xD1A0, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateBricks(byte[] pixels)
        {
            // Four-row brick courses, offset by 4 columns on odd rows. Mortar is
            // one pixel along the bottom of each row and one pixel at each brick
            // boundary; bricks themselves are red-brown with per-pixel jitter.
            var rng = new Random(0xB71C);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                int row = y / 4;
                bool mortarY = (y % 4 == 0);
                int rowOffset = (row % 2 == 0) ? 0 : 4;
                bool mortarX = ((x + rowOffset) % 8 == 0);
                bool mortar = mortarY || mortarX;
                if (mortar)
                    SetJittered(pixels, x, y, 170, 170, 170, 6, rng);
                else
                    SetJittered(pixels, x, y, 150, 82, 62, 10, rng);
            }
        }

        private static void GenerateTntTop(byte[] pixels)
        {
            // Red with a small grey detonator cap dead-centre.
            var rng = new Random(0x0717);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, 200, 40, 40, 9, rng);
            for (int y = 7; y <= 8; y++)
            for (int x = 7; x <= 8; x++)
                SetPixel(pixels, x, y, 90, 90, 90);
        }

        private static void GenerateTntBottom(byte[] pixels)
        {
            // Sandy tan — matches alpha's "fuse base" bottom.
            var rng = new Random(0x071B);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, 206, 180, 102, 8, rng);
        }

        private static void GenerateTntSide(byte[] pixels)
        {
            // Red body with a pale "TNT" label band along the top.
            var rng = new Random(0x0715);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                byte r, g, b;
                if (y >= 12) { r = 220; g = 220; b = 210; }
                else if (y >= 5 && y <= 10 && (x == 2 || x == 7 || x == 13)) { r = 130; g = 24; b = 24; }
                else { r = 200; g = 40; b = 40; }
                SetJittered(pixels, x, y, r, g, b, 8, rng);
            }
        }

        private static void GenerateBookshelfSide(byte[] pixels)
        {
            // Planks along the top two and bottom two rows; middle 12 rows are
            // vertical book spines in rotating colors with dark gutters between.
            var rng = new Random(0xB00C);
            var bookColors = new (byte, byte, byte)[]
            {
                (172, 60, 50),
                (70, 110, 180),
                (60, 130, 80),
                (180, 160, 80),
                (90, 70, 160),
                (120, 80, 50),
            };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                if (y < 2 || y >= 14)
                {
                    bool groove = (y == 0 || y == 15);
                    byte r = groove ? (byte)123 : (byte)160;
                    byte g = groove ? (byte)91 : (byte)124;
                    byte b = groove ? (byte)52 : (byte)74;
                    SetJittered(pixels, x, y, r, g, b, 6, rng);
                }
                else
                {
                    bool gutter = (x % 3 == 0);
                    if (gutter)
                    {
                        SetPixel(pixels, x, y, 40, 30, 20);
                    }
                    else
                    {
                        int spine = (x / 3) + (y < 8 ? 0 : 3);
                        var c = bookColors[(spine * 7 + 3) % bookColors.Length];
                        SetJittered(pixels, x, y, c.Item1, c.Item2, c.Item3, 7, rng);
                    }
                }
            }
        }

        private static void GenerateMossyCobblestone(byte[] pixels)
        {
            // Start from cobblestone, then tint scattered pixels toward green.
            GenerateCobblestone(pixels);
            var rng = new Random(0xC0005);
            for (int n = 0; n < 24; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(3) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    int idx = (y * TileSize + x) * 4;
                    byte curR = pixels[idx];
                    byte curG = pixels[idx + 1];
                    byte curB = pixels[idx + 2];
                    byte mossR = (byte)(curR * 3 / 8);
                    byte mossG = Clamp(curG * 4 / 5 + 40);
                    byte mossB = (byte)(curB * 3 / 8);
                    SetPixel(pixels, x, y, mossR, mossG, mossB);
                }
            }
        }

        private static void GenerateObsidian(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (26, 20, 42),
                (38, 30, 58),
                (16, 12, 28),
                (52, 42, 76),
            };
            NoiseFill(pixels, 0x0B51, palette, new[] { 12, 6, 4, 2 });
        }

        private static void GenerateSponge(byte[] pixels)
        {
            var rng = new Random(0x5906);
            var palette = new (byte, byte, byte)[]
            {
                (210, 190, 70),
                (180, 164, 50),
                (230, 212, 100),
            };
            var weights = new[] { 10, 5, 3 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
            // Scatter dark pore pixels.
            for (int n = 0; n < 20; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                SetPixel(pixels, cx, cy, 100, 80, 30);
            }
        }

        private static void GenerateGlass(byte[] pixels)
        {
            // No alpha in the pipeline yet — we approximate with a bright pale
            // interior and a muted frame so the tile reads as a glass pane.
            var rng = new Random(0x61A5);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool border = (x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1);
                if (border)
                {
                    SetJittered(pixels, x, y, 190, 200, 210, 6, rng);
                }
                else
                {
                    SetJittered(pixels, x, y, 240, 244, 250, 6, rng);
                }
            }
        }

        private static void GenerateWool(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (232, 232, 228),
                (218, 218, 214),
                (244, 244, 240),
            };
            NoiseFill(pixels, 0x5011, palette, new[] { 12, 5, 4 });
        }
    }
}
