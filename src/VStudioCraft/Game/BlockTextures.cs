using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural 16x16 tiles arranged in a 4x1 atlas, emulating Alpha 1.1.2_01's
    // coarse three-tone noise look. Tints are baked in; no biome coloring.
    internal static class BlockTextures
    {
        public const int TileSize = 16;
        public const int LayerCount = 5;

        public const int TileGrassTop = 0;
        public const int TileGrassSide = 1;
        public const int TileDirt = 2;
        public const int TileStone = 3;
        public const int TileSand = 4;

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
    }
}
