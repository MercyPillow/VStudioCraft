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
    // Generic skin decoder + uploader for mob textures. Same algorithm
    // as SteveSkin but takes the base64 string as a parameter so a
    // single helper covers Zombie / Skeleton / Creeper. Returns 0 on
    // any failure (corrupt base64, GDI+ throw, GL.GenTexture fails) so
    // the caller can fall back to the procedural colour-cuboid rig.
    //
    // Texture layout assumption: the same 64x32 (or 64x64) Alpha-style
    // skin layout the Steve rig already uses — head + body + right-limb
    // packed in the top half, left limbs mirrored from right at draw
    // time. The mob skins from Mojang's bedrock-samples resource pack
    // ship at 64x32 which matches Alpha 1.1.2_01 byte-for-byte.
    internal static class MobSkin
    {
        public static int CreateTexture(string base64)
        {
            byte[] rgba;
            int w, h;
            if (!TryDecode(base64, out rgba, out w, out h)) return 0;

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                w, h, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            // Nearest filtering keeps the pixel art crisp; ClampToEdge so
            // a UV that lands on a face seam doesn't wrap into a
            // neighbouring face's tile.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }

        private static bool TryDecode(string b64, out byte[] rgba, out int width, out int height)
        {
            rgba = null; width = 0; height = 0;
            try
            {
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
                        // GDI+ stores Format32bppArgb as BGRA in memory on
                        // little-endian platforms; GL wants RGBA, so swizzle
                        // B↔R per pixel on the way in.
                        int dstStride = width * 4;
                        rgba = new byte[dstStride * height];
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
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
