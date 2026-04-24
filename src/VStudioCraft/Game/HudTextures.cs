using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Procedural HUD sprite sheets. Mirrors the BlockTextures approach — every
    // pixel is generated at init from geometric primitives so we don't ship any
    // asset files.
    //
    // Sheet layouts:
    //   Heart sheet (48×16):   full / half / empty hearts side-by-side
    //   Drumstick sheet (48×16): full / half / empty drumsticks side-by-side
    //
    // Convention matches the block atlas: pixels are uploaded with py=0 first,
    // and sampled with UV.y = aPos.y (no flip), so aPos.y=0 → py=0 → top of
    // sprite at the top of the screen quad. See the block atlas GenerateGrassSide
    // for the same "v=0 is the bottom of the texture" note.
    internal static class HudTextures
    {
        public const int IconPx = 16;
        public const int SheetWidth = 48;
        public const int SheetHeight = 16;

        public const float UvFullX = 0f;
        public const float UvHalfX = 1f / 3f;
        public const float UvEmptyX = 2f / 3f;
        public const float UvWidth = 1f / 3f;

        // Back-compat aliases (the renderer's older Heart-named constants still
        // compile against the same values).
        public const int HeartPx = IconPx;
        public const int HeartSheetWidth = SheetWidth;
        public const int HeartSheetHeight = SheetHeight;
        public const float HeartUvFullX = UvFullX;
        public const float HeartUvHalfX = UvHalfX;
        public const float HeartUvEmptyX = UvEmptyX;
        public const float HeartUvWidth = UvWidth;

        public static int CreateHeartSheet() => CreateSheet(WriteHeart);

        public static int CreateDrumstickSheet() => CreateSheet(WriteDrumstick);

        private delegate void IconWriter(byte[] pixels, int offsetX, bool full, bool empty);

        private static int CreateSheet(IconWriter writer)
        {
            var pixels = new byte[SheetWidth * SheetHeight * 4];
            writer(pixels, offsetX: 0,  full: true,  empty: false); // full
            writer(pixels, offsetX: 16, full: false, empty: false); // half
            writer(pixels, offsetX: 32, full: false, empty: true);  // empty container

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                SheetWidth, SheetHeight, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }

        // ---- heart ---------------------------------------------------------
        //
        // Two 3.5-radius circles form the top bumps, joined by a V-triangle that
        // tapers to a tip at the bottom. Reads like the <3 emoji — crisp and
        // bubbly, much more iconic than a continuous implicit curve.
        //
        //   Left bump centre  : (3.5, 4.5)
        //   Right bump centre : (11.5, 4.5)
        //   V tip             : (7.5, 14)
        private const float HeartBumpY = 4.5f;
        private const float HeartLeftBumpX = 3.5f;
        private const float HeartRightBumpX = 11.5f;
        private const float HeartBumpR = 3.5f;
        private const float HeartTipX = 7.5f;
        private const float HeartTipY = 14f;

        private static bool IsInsideHeart(float fx, float fy)
        {
            float dxL = fx - HeartLeftBumpX, dyL = fy - HeartBumpY;
            if (dxL * dxL + dyL * dyL <= HeartBumpR * HeartBumpR) return true;
            float dxR = fx - HeartRightBumpX, dyR = fy - HeartBumpY;
            if (dxR * dxR + dyR * dyR <= HeartBumpR * HeartBumpR) return true;

            if (fy >= HeartBumpY && fy <= HeartTipY)
            {
                float t = (fy - HeartBumpY) / (HeartTipY - HeartBumpY);
                float leftEdge = (HeartLeftBumpX - HeartBumpR) + t * (HeartTipX - (HeartLeftBumpX - HeartBumpR));
                float rightEdge = (HeartRightBumpX + HeartBumpR) + t * (HeartTipX - (HeartRightBumpX + HeartBumpR));
                if (fx >= leftEdge && fx <= rightEdge) return true;
            }
            return false;
        }

        private static void WriteHeart(byte[] pixels, int offsetX, bool full, bool empty)
        {
            byte outR = 60,  outG = 0,   outB = 0;     // dark outer border
            byte shadeR = 160, shadeG = 14, shadeB = 24; // shaded right side of the red fill
            byte fillR = 230, fillG = 30,  fillB = 40;   // main red
            byte hiR = 255, hiG = 180, hiB = 180;        // soft highlight on upper-left bump
            byte slotR = 55, slotG = 55, slotB = 55;     // empty heart interior

            // Precompute the inside-mask so the outline sweep doesn't need to
            // recompute the geometry for every neighbour test.
            var inside = new bool[IconPx, IconPx];
            for (int py = 0; py < IconPx; py++)
            for (int px = 0; px < IconPx; px++)
                inside[px, py] = IsInsideHeart(px + 0.5f, py + 0.5f);

            for (int py = 0; py < IconPx; py++)
            for (int px = 0; px < IconPx; px++)
            {
                int idx = (py * SheetWidth + offsetX + px) * 4;
                if (!inside[px, py])
                {
                    pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = pixels[idx + 3] = 0;
                    continue;
                }

                // 8-neighbour outline detection: any out-of-bounds or outside
                // neighbour means this pixel is on the border.
                bool isOutline = false;
                for (int ny = -1; ny <= 1 && !isOutline; ny++)
                for (int nx = -1; nx <= 1 && !isOutline; nx++)
                {
                    if (nx == 0 && ny == 0) continue;
                    int qx = px + nx, qy = py + ny;
                    if (qx < 0 || qx >= IconPx || qy < 0 || qy >= IconPx || !inside[qx, qy]) isOutline = true;
                }
                if (isOutline)
                {
                    pixels[idx] = outR; pixels[idx + 1] = outG; pixels[idx + 2] = outB; pixels[idx + 3] = 255;
                    continue;
                }

                bool rightHalf = (px + 0.5f) > 7.5f;
                if (empty || (!full && rightHalf))
                {
                    pixels[idx] = slotR; pixels[idx + 1] = slotG; pixels[idx + 2] = slotB; pixels[idx + 3] = 255;
                    continue;
                }

                // Highlight sits on the upper-left bump (classic pixel-art heart
                // shine). Shade sits on the lower-right to hint at volume.
                bool highlight = (px == 2 && py == 3) || (px == 3 && py == 3) || (px == 2 && py == 4);
                bool shaded = (px >= 11 && py >= 5 && py <= 8) || (px >= 9 && py >= 9 && py <= 11);
                if (highlight)
                {
                    pixels[idx] = hiR; pixels[idx + 1] = hiG; pixels[idx + 2] = hiB;
                }
                else if (shaded)
                {
                    pixels[idx] = shadeR; pixels[idx + 1] = shadeG; pixels[idx + 2] = shadeB;
                }
                else
                {
                    pixels[idx] = fillR; pixels[idx + 1] = fillG; pixels[idx + 2] = fillB;
                }
                pixels[idx + 3] = 255;
            }
        }

        // ---- drumstick -----------------------------------------------------
        //
        // Meat blob (ellipse) in the upper-left, bone (a diagonal capsule) going
        // down and right to a small knob. Half-drumstick clips the right-hand
        // half of the fill to grey (matches how Minecraft's hunger bar splits
        // odd-value drumsticks).
        //
        //   Meat ellipse : centre (5, 5.5), rx=3.8, ry=3.2
        //   Bone line    : (5,5.5) → (13,13.5), radius 1.4
        //   Knob         : centre (13, 13.5), r=1.8
        private const float MeatCx = 5f;
        private const float MeatCy = 5.5f;
        private const float MeatRx = 3.8f;
        private const float MeatRy = 3.2f;
        private const float BoneAx = 5f, BoneAy = 5.5f;
        private const float BoneBx = 13f, BoneBy = 13.5f;
        private const float BoneR = 1.4f;
        private const float KnobR = 1.8f;

        private static bool IsInsideDrumstick(float fx, float fy, out bool isMeat)
        {
            isMeat = false;

            // Ellipse test for the meat
            float nx = (fx - MeatCx) / MeatRx;
            float ny = (fy - MeatCy) / MeatRy;
            if (nx * nx + ny * ny <= 1f) { isMeat = true; return true; }

            // Capsule test for the bone: perpendicular distance to segment AB.
            float abx = BoneBx - BoneAx, aby = BoneBy - BoneAy;
            float apx = fx - BoneAx, apy = fy - BoneAy;
            float abLen2 = abx * abx + aby * aby;
            float t = (apx * abx + apy * aby) / abLen2;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float cx = BoneAx + abx * t;
            float cy = BoneAy + aby * t;
            float dx = fx - cx, dy = fy - cy;
            if (dx * dx + dy * dy <= BoneR * BoneR) return true;

            // Knob at the tail end
            float kdx = fx - BoneBx, kdy = fy - BoneBy;
            if (kdx * kdx + kdy * kdy <= KnobR * KnobR) return true;

            return false;
        }

        private static void WriteDrumstick(byte[] pixels, int offsetX, bool full, bool empty)
        {
            byte outR = 50,  outG = 30,  outB = 10;    // dark brown outline
            byte meatR = 170, meatG = 90,  meatB = 50;   // cooked-meat brown
            byte meatHiR = 220, meatHiG = 140, meatHiB = 80; // highlight on the meat
            byte boneR = 230, boneG = 210, boneB = 150;  // tan bone
            byte boneHiR = 255, boneHiG = 240, boneHiB = 200; // bone highlight
            byte slotR = 55, slotG = 55, slotB = 55;     // empty container interior

            var inside = new bool[IconPx, IconPx];
            var isMeatMask = new bool[IconPx, IconPx];
            for (int py = 0; py < IconPx; py++)
            for (int px = 0; px < IconPx; px++)
            {
                inside[px, py] = IsInsideDrumstick(px + 0.5f, py + 0.5f, out bool m);
                isMeatMask[px, py] = m && inside[px, py];
            }

            for (int py = 0; py < IconPx; py++)
            for (int px = 0; px < IconPx; px++)
            {
                int idx = (py * SheetWidth + offsetX + px) * 4;
                if (!inside[px, py])
                {
                    pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = pixels[idx + 3] = 0;
                    continue;
                }

                bool isOutline = false;
                for (int ny = -1; ny <= 1 && !isOutline; ny++)
                for (int nx = -1; nx <= 1 && !isOutline; nx++)
                {
                    if (nx == 0 && ny == 0) continue;
                    int qx = px + nx, qy = py + ny;
                    if (qx < 0 || qx >= IconPx || qy < 0 || qy >= IconPx || !inside[qx, qy]) isOutline = true;
                }
                if (isOutline)
                {
                    pixels[idx] = outR; pixels[idx + 1] = outG; pixels[idx + 2] = outB; pixels[idx + 3] = 255;
                    continue;
                }

                bool rightHalf = (px + 0.5f) > 7.5f;
                if (empty || (!full && rightHalf))
                {
                    pixels[idx] = slotR; pixels[idx + 1] = slotG; pixels[idx + 2] = slotB; pixels[idx + 3] = 255;
                    continue;
                }

                if (isMeatMask[px, py])
                {
                    // Meat highlight on the upper-left of the meat blob.
                    bool hi = (px == 3 && py == 4) || (px == 4 && py == 4) || (px == 3 && py == 5);
                    if (hi)
                    {
                        pixels[idx] = meatHiR; pixels[idx + 1] = meatHiG; pixels[idx + 2] = meatHiB;
                    }
                    else
                    {
                        pixels[idx] = meatR; pixels[idx + 1] = meatG; pixels[idx + 2] = meatB;
                    }
                }
                else
                {
                    // Bone. Upper edge is slightly brighter to fake a light source.
                    bool hi = (px + py) % 4 == 0; // sparse dither-highlight
                    if (hi)
                    {
                        pixels[idx] = boneHiR; pixels[idx + 1] = boneHiG; pixels[idx + 2] = boneHiB;
                    }
                    else
                    {
                        pixels[idx] = boneR; pixels[idx + 1] = boneG; pixels[idx + 2] = boneB;
                    }
                }
                pixels[idx + 3] = 255;
            }
        }
    }
}
