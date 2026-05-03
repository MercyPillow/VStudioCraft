using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using GdiImageLockMode = System.Drawing.Imaging.ImageLockMode;
using GdiPixelFormat  = System.Drawing.Imaging.PixelFormat;

namespace VStudioCraft.Game
{
    // Loads the canonical Steve skin texture and uploads it as a single
    // 64×64 (or 64×32) Texture2D, NEAREST-filtered (so the per-pixel art
    // stays crisp at every zoom). Mirror of BlockTextures' embedded-PNG
    // decoder, scoped down for one image: bytes come from the inlined
    // SteveSkinData.Base64 constant, decode through GDI+, swizzle BGRA →
    // RGBA, upload.
    //
    // The classic 64×32 Alpha layout and the modern 64×64 layout both
    // place the head/body/right-arm/right-leg in the top 32 rows, so the
    // same UV regions used by SkinCuboidMesh.BuildBodyPart work for
    // either source. The bottom 32 rows of a 64×64 file (the "second
    // skin layer" overlay + mirrored left-arm/left-leg textures) go
    // unused for now — the rig draws left limbs by mirroring the right
    // limb's UVs, matching what Alpha 1.1.2 actually rendered.
    //
    // Texture handle is owned by GameRenderer (initialised at GL-init
    // time, deleted on Dispose). This class is purely a one-shot upload
    // helper.
    internal static class SteveSkin
    {
        public const int ExpectedWidth  = 64;
        public const int ExpectedHeight = 64; // 32 also accepted

        // Decode + upload. Returns 0 on any failure (corrupt base64,
        // GDI+ throw, GL.GenTexture fails) — caller can check and fall
        // back to the procedural Steve rig if needed.
        public static int CreateTexture()
        {
            byte[] rgba;
            int w, h;
            if (!TryDecode(out rgba, out w, out h)) return 0;

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                w, h, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            // Nearest filtering — the skin is pixel art, no interpolation
            // wanted at any zoom level. ClampToEdge so a UV that lands on
            // the 0/1 boundary (which happens at every face seam) doesn't
            // wrap into a neighbouring face's tile.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }

        private static bool TryDecode(out byte[] rgba, out int width, out int height)
        {
            rgba = null; width = 0; height = 0;
            try
            {
                string b64 = SteveSkinData.Base64;
                if (string.IsNullOrEmpty(b64)) return false;
                byte[] pngBytes = Convert.FromBase64String(b64);

                using (var src = new MemoryStream(pngBytes))
                using (var bmp = new Bitmap(src))
                {
                    width  = bmp.Width;
                    height = bmp.Height;
                    var rect = new Rectangle(0, 0, width, height);
                    var data = bmp.LockBits(rect, GdiImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
                    try
                    {
                        // Format32bppArgb on x86/x64 stores pixels as
                        // BGRA in memory (Argb is the *interpretation*,
                        // little-endian byte layout puts B first). GL
                        // wants RGBA, so swizzle on the way in.
                        int dstStride = width * 4;
                        rgba = new byte[dstStride * height];
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                            // Copy the row, then swap B↔R per pixel.
                            int rowOff = y * dstStride;
                            Marshal.Copy(rowPtr, rgba, rowOff, dstStride);
                            for (int x = 0; x < width; x++)
                            {
                                int p = rowOff + x * 4;
                                byte b = rgba[p + 0];
                                rgba[p + 0] = rgba[p + 2];
                                rgba[p + 2] = b;
                            }
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                }
                return true;
            }
            catch
            {
                rgba = null; width = 0; height = 0;
                return false;
            }
        }
    }
}
