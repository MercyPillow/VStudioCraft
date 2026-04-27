using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
// Aliased to avoid collision with OpenTK.Graphics.OpenGL.PixelFormat,
// which is used pervasively in this file for GL.TexImage3D etc.
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiImageLockMode = System.Drawing.Imaging.ImageLockMode;

namespace VStudioCraft.Game
{
    // Procedural 16x16 tiles arranged as a 2D texture array, emulating Alpha
    // 1.1.2_01's coarse three-tone noise look. Palettes approximate the alpha
    // terrain.png, but no pixels are copied — every tile is regenerated from a
    // seeded RNG so the output is original art with an alpha-era feel.
    internal static class BlockTextures
    {
        public const int TileSize = 16;
        // Layers 0..37 are blocks (sourced from terrain.png in alpha mode,
        // procedurally generated otherwise). Layers 38..57 are tool icons
        // and 58..66 are non-tool ingredient items (Stick, Coal, ingots,
        // Diamond, Flint, ClayBall/Brick, Bowl). In alpha-textures mode
        // the entire 38..66 range is sliced out of a single alpha_tools.png
        // (Alpha packs tools and ingredients together at canonical Notch
        // coordinates); in procedural mode both ranges are synthesised
        // directly. Material order in the tool slice matches BlockType
        // enum: wood, stone, iron, diamond, gold.
        //
        // Layers 67+ are "tail block" tiles — additional block face textures
        // added after the tool/item slice was frozen. They source from
        // terrain.png in alpha mode (same as 0..37) and from procedural
        // generators in procedural mode. The tool/item slice loop bounds
        // itself with FirstTailBlockLayer so it doesn't try to read these
        // out of alpha_tools.png. Currently used for the multi-face
        // crafting table tiles (top + side). Adding more multi-face
        // blocks later (furnace, dispenser, jukebox, etc.) follows the
        // same pattern: append to the tail range.
        public const int BlockLayerCount = 38;
        public const int ToolLayerCount = 20;
        public const int ItemLayerCount = 9;
        public const int FirstItemLayer = BlockLayerCount + ToolLayerCount; // 58
        public const int FirstTailBlockLayer = FirstItemLayer + ItemLayerCount; // 67
        // 9 tail tiles: CraftingTableTop, CraftingTableSide, FurnaceTop,
        // FurnaceSide, FurnaceFront, FurnaceFrontLit, ChestTop,
        // ChestSide, ChestFront. Each new multi-face block gets a
        // contiguous block of layers appended here so the tool/item
        // slice (which assumes everything past it sources from
        // terrain.png) stays untouched.
        public const int TailBlockLayerCount = 9;
        // Tail item layers — appended past the tail blocks. Items added
        // after the original 9-item slice (Tier 3 #9 onward: porkchops
        // raw + cooked) live here so we don't have to renumber the
        // existing item / tail-block indices and break v7 saves.
        public const int FirstTailItemLayer = FirstTailBlockLayer + TailBlockLayerCount; // 76
        public const int TailItemLayerCount = 2;
        public const int LayerCount = FirstTailItemLayer + TailItemLayerCount;          // 78
        // Porkchop tile indices.
        public const int TileRawPorkchop    = 76;
        public const int TileCookedPorkchop = 77;

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
        public const int TileTorch = 33;
        public const int TileDandelion = 34;
        public const int TileRose = 35;
        public const int TileBrownMushroom = 36;
        public const int TileRedMushroom = 37;

        // Tool layer indices. Order is material-major (wood, stone, iron,
        // diamond, gold within each kind), kind-grouped to mirror the
        // enum chunking in BlockType. Match GetTileIndex in Block.cs.
        public const int TileWoodSword      = 38;
        public const int TileStoneSword     = 39;
        public const int TileIronSword      = 40;
        public const int TileDiamondSword   = 41;
        public const int TileGoldSword      = 42;
        public const int TileWoodShovel     = 43;
        public const int TileStoneShovel    = 44;
        public const int TileIronShovel     = 45;
        public const int TileDiamondShovel  = 46;
        public const int TileGoldShovel     = 47;
        public const int TileWoodPickaxe    = 48;
        public const int TileStonePickaxe   = 49;
        public const int TileIronPickaxe    = 50;
        public const int TileDiamondPickaxe = 51;
        public const int TileGoldPickaxe    = 52;
        public const int TileWoodAxe        = 53;
        public const int TileStoneAxe       = 54;
        public const int TileIronAxe        = 55;
        public const int TileDiamondAxe     = 56;
        public const int TileGoldAxe        = 57;

        // Item layer indices (58..66). Always procedural — no PNG source
        // in either alpha terrain.png or alpha_tools.png. Order matches
        // BlockType enum: stick, coal, iron/gold ingot, diamond, flint,
        // clay ball/brick, bowl.
        public const int TileStick     = 58;
        public const int TileCoal      = 59;
        public const int TileIronIngot = 60;
        public const int TileGoldIngot = 61;
        public const int TileDiamond   = 62;
        public const int TileFlint     = 63;
        public const int TileClayBall  = 64;
        public const int TileClayBrick = 65;
        public const int TileBowl      = 66;

        // Tail block layers — additional block-face tiles appended past
        // the tool/item slice. These come from terrain.png (alpha mode)
        // or a procedural generator (procedural mode), NOT alpha_tools.png.
        public const int TileCraftingTableTop  = 67;
        public const int TileCraftingTableSide = 68;
        // Furnace face tiles. Top + bottom share one tile (a stone cap
        // with rim), the four sides share one tile (plain stone block
        // with iron edge banding), and the "front" face has its own
        // pair — unlit (a dark furnace mouth) and lit (the same mouth
        // with an orange/yellow glow). The render path swaps the side
        // texture between FurnaceFront and FurnaceFrontLit by switching
        // the underlying block id (Furnace ↔ LitFurnace), so we only
        // need one block-id per state, not metadata. Without an
        // orientation byte we draw the front on every side so the door
        // is always visible — Alpha shows it on a single face, but
        // metadata-orientation is a Tier 2 task and the all-faces look
        // is unmistakable.
        public const int TileFurnaceTop      = 69;
        public const int TileFurnaceSide     = 70;
        public const int TileFurnaceFront    = 71;
        public const int TileFurnaceFrontLit = 72;
        // Chest face tiles. Top is the planked lid seen from above with
        // the iron lock-plate visible. Side is the planked carcass with
        // a single horizontal iron band. Front carries the same band
        // plus the centred lock + door split. The bottom face reuses
        // TilePlanks (the underside of an Alpha chest is just planks),
        // so we only need three layers and route them through the
        // oriented mesher path so the front lands on the placer-facing
        // side and the other three sides draw TileChestSide.
        public const int TileChestTop   = 73;
        public const int TileChestSide  = 74;
        public const int TileChestFront = 75;

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
            UploadLayer(layerPixels, TileTorch, GenerateTorch);
            UploadLayer(layerPixels, TileDandelion, GenerateDandelion);
            UploadLayer(layerPixels, TileRose, GenerateRose);
            UploadLayer(layerPixels, TileBrownMushroom, GenerateBrownMushroom);
            UploadLayer(layerPixels, TileRedMushroom, GenerateRedMushroom);

            // Tool layers — generate procedural pixel-art sprites that
            // match the procedural block aesthetic instead of pulling
            // from alpha_tools.png. The procedural atlas is the "no
            // assets, just code" mode, so the tools should look like
            // they were drawn by the same hand as the blocks. The PNG
            // path is still available via CreateAtlasFromAlphaTerrain
            // for players who prefer the canon Alpha art.
            GenerateProceduralToolLayers(layerPixels);

            // Item layers — always procedural (neither alpha PNG has
            // entries for the ingredient set). Same path used by the
            // alpha-textures atlas in CreateAtlasFromAlphaTerrain.
            GenerateProceduralItemLayers(layerPixels);

            // Tail block tiles — procedural CraftingTable face textures.
            // These come from terrain.png in alpha mode and from these
            // generators in procedural mode.
            UploadLayer(layerPixels, TileCraftingTableTop, GenerateCraftingTableTop);
            UploadLayer(layerPixels, TileCraftingTableSide, GenerateCraftingTableSide);
            UploadLayer(layerPixels, TileFurnaceTop, GenerateFurnaceTop);
            UploadLayer(layerPixels, TileFurnaceSide, GenerateFurnaceSide);
            UploadLayer(layerPixels, TileFurnaceFront, GenerateFurnaceFront);
            UploadLayer(layerPixels, TileFurnaceFrontLit, GenerateFurnaceFrontLit);
            UploadLayer(layerPixels, TileChestTop, GenerateChestTop);
            UploadLayer(layerPixels, TileChestSide, GenerateChestSide);
            UploadLayer(layerPixels, TileChestFront, GenerateChestFront);

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        // Slice each tool tile out of alpha_tools.png and upload it to its
        // destination layer. Caller has already bound the Texture2DArray.
        // No-op if the tools PNG can't be decoded (older builds without
        // AlphaToolsData, or a corrupt resource); the affected layers
        // simply stay at whatever was there before (transparent for a
        // freshly-allocated atlas).
        private static void UploadToolLayersFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            // Stops at FirstTailBlockLayer so the tail-block range (which
            // sources from terrain.png) doesn't get sliced out of the
            // tools PNG.
            for (int layer = BlockLayerCount; layer < FirstTailBlockLayer; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Slice each tail-item tile (porkchops and any future appended
        // food/ingredient items) out of alpha_tools.png and upload it.
        // Same idempotent contract as UploadToolLayersFromAlphaTools — a
        // missing tools PNG just leaves the previously-painted procedural
        // pixels in place, so the atlas degrades gracefully instead of
        // turning porkchop icons into magenta error tiles.
        private static void UploadTailItemLayersFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            for (int layer = FirstTailItemLayer; layer < LayerCount; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue; // sentinel — keep the procedural fill
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Procedural tool atlas. Walks tool layers 38..57 and synthesises
        // a 16×16 sprite for each, deriving (kind, material) from the
        // AlphaTileCoords entry — the (col, row) we use for slicing the
        // PNG happens to also encode "row 4..7 = sword/shovel/pickaxe/axe"
        // and "col 0..4 = wood/stone/iron/diamond/gold", so the same
        // table drives both decoders. Each sprite is a wood handle
        // running diagonally bottom-left → upper-right with a metal head
        // at the upper-right end; the head shape branches per kind.
        // Material colours pick the head palette so iron looks pale,
        // gold looks yellow, etc.
        private static void GenerateProceduralToolLayers(byte[] layerPixels)
        {
            for (int layer = BlockLayerCount; layer < LayerCount; layer++)
            {
                Array.Clear(layerPixels, 0, layerPixels.Length);
                var (col, row) = AlphaTileCoords[layer];
                ToolKind kind;
                switch (row)
                {
                    case 4: kind = ToolKind.Sword;   break;
                    case 5: kind = ToolKind.Shovel;  break;
                    case 6: kind = ToolKind.Pickaxe; break;
                    case 7: kind = ToolKind.Axe;     break;
                    default: kind = ToolKind.Sword;  break; // unreachable for tool rows
                }
                ToolMaterial mat;
                switch (col)
                {
                    case 0: mat = ToolMaterial.Wood;    break;
                    case 1: mat = ToolMaterial.Stone;   break;
                    case 2: mat = ToolMaterial.Iron;    break;
                    case 3: mat = ToolMaterial.Diamond; break;
                    case 4: mat = ToolMaterial.Gold;    break;
                    default: mat = ToolMaterial.Wood;   break;
                }
                GenerateTool(layerPixels, kind, mat, layer);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Material palette for tool heads. Three shades — base, highlight,
        // shadow — used by GenerateTool to give the head a chunky,
        // shaded look at 16×16 instead of a flat colour blob. Wood is
        // also used for the handle of every tool regardless of head
        // material so the silhouette reads "tool" before "X material".
        private static (byte r, byte g, byte b) MaterialBase(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (158, 113, 60);
                case ToolMaterial.Stone:   return (130, 130, 130);
                case ToolMaterial.Iron:    return (210, 210, 220);
                case ToolMaterial.Gold:    return (240, 210, 70);
                case ToolMaterial.Diamond: return (110, 225, 220);
            }
            return (200, 200, 200);
        }
        private static (byte r, byte g, byte b) MaterialHi(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (190, 145, 85);
                case ToolMaterial.Stone:   return (170, 170, 170);
                case ToolMaterial.Iron:    return (240, 240, 248);
                case ToolMaterial.Gold:    return (255, 235, 110);
                case ToolMaterial.Diamond: return (170, 255, 250);
            }
            return (235, 235, 235);
        }
        private static (byte r, byte g, byte b) MaterialLo(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (110, 75, 35);
                case ToolMaterial.Stone:   return (88, 88, 88);
                case ToolMaterial.Iron:    return (155, 155, 168);
                case ToolMaterial.Gold:    return (180, 150, 30);
                case ToolMaterial.Diamond: return (60, 165, 175);
            }
            return (140, 140, 140);
        }

        // Draw one tool sprite into the 16×16 layer buffer. The sprite is
        // composed of two parts:
        //   1. A wooden handle running diagonally from (4,11) up to
        //      (10,5), 1 px wide with a 1-px shadow track. Same on every
        //      tool so the silhouette reads tool-like.
        //   2. A material-coloured head at the upper-right end, shape
        //      branching on `kind`. Heads are drawn with three shades
        //      (lo edge / base body / hi inner) for the chunky pixel-art
        //      look the procedural blocks already use.
        // `seed` is the layer index — it gives each material+kind
        // combination its own deterministic jitter (subtle dithering on
        // the head body) so the 20 sprites don't look like 20 colour
        // swaps of the same drawing.
        private static void GenerateTool(byte[] pixels, ToolKind kind, ToolMaterial material, int seed)
        {
            // ---- handle (wood) -----------------------------------------
            // Diagonal stair-step from lower-left to upper-right, with a
            // shadow line below it for depth. Handle ends in a small
            // pommel at the lower-left corner.
            (byte r, byte g, byte b) wood   = (138, 95, 50);
            (byte r, byte g, byte b) woodHi = (172, 125, 70);
            (byte r, byte g, byte b) woodLo = (95, 60, 30);

            // (x, y) pairs for the handle core — runs from (4,11) to
            // (10,5). Each step is one pixel right and one pixel up.
            int[] hxs = { 4, 5, 6, 7, 8, 9, 10 };
            int[] hys = { 11, 10, 9, 8, 7, 6, 5 };
            for (int i = 0; i < hxs.Length; i++)
            {
                SetPixel(pixels, hxs[i], hys[i], wood.r, wood.g, wood.b);
                // Highlight pixel above-left of handle for dim 3D.
                if (hxs[i] - 1 >= 0)
                    SetPixel(pixels, hxs[i] - 1, hys[i], woodHi.r, woodHi.g, woodHi.b);
                // Shadow pixel below-right.
                if (hys[i] + 1 < TileSize)
                    SetPixel(pixels, hxs[i], hys[i] + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // Pommel cap at the handle end (3,12).
            SetPixel(pixels, 3, 12, woodLo.r, woodLo.g, woodLo.b);

            // ---- head --------------------------------------------------
            var baseC = MaterialBase(material);
            var hiC   = MaterialHi(material);
            var loC   = MaterialLo(material);
            var rng   = new Random(0x70 + seed);

            switch (kind)
            {
                case ToolKind.Sword:   DrawSwordHead(pixels, baseC, hiC, loC, rng);   break;
                case ToolKind.Shovel:  DrawShovelHead(pixels, baseC, hiC, loC, rng);  break;
                case ToolKind.Pickaxe: DrawPickaxeHead(pixels, baseC, hiC, loC, rng); break;
                case ToolKind.Axe:     DrawAxeHead(pixels, baseC, hiC, loC, rng);     break;
            }
        }

        // Sword head: long pointed blade running along the same diagonal
        // as the handle, with a small crossguard where the two meet.
        // Tip at (13,2), crossguard at (10,5)/(11,4)/(9,6) etc.
        private static void DrawSwordHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Crossguard — two pixels straddling the joint.
            SetPixel(pixels, 9, 4, loC.r, loC.g, loC.b);
            SetPixel(pixels, 11, 6, loC.r, loC.g, loC.b);

            // Blade — diagonal from (10,5) up to tip (13,2). Two pixels
            // wide for chunkiness; outer edge dim, inner edge bright.
            int[] bxs = { 10, 11, 12, 13 };
            int[] bys = { 5, 4, 3, 2 };
            for (int i = 0; i < bxs.Length; i++)
            {
                SetPixel(pixels, bxs[i], bys[i], hiC.r, hiC.g, hiC.b);
                if (bxs[i] + 1 < TileSize) SetPixel(pixels, bxs[i] + 1, bys[i], baseC.r, baseC.g, baseC.b);
                if (bys[i] - 1 >= 0)       SetPixel(pixels, bxs[i], bys[i] - 1, loC.r, loC.g, loC.b);
            }
            // Tip pixel — single point past (13,2).
            SetPixel(pixels, 14, 1, loC.r, loC.g, loC.b);
        }

        // Shovel head: small rectangular spade scoop above the handle joint.
        // ~3×3 with a tapered point, sits at (10..12, 2..4).
        private static void DrawShovelHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top edge / shoulders.
            SetPixel(pixels, 10, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 11, 2, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 2, loC.r, loC.g, loC.b);
            // Body.
            SetPixel(pixels, 10, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 12, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 10, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 4, baseC.r, baseC.g, baseC.b);
            // Tapered tip toward the handle.
            SetPixel(pixels, 11, 5, loC.r, loC.g, loC.b);
        }

        // Pickaxe head: horizontal bar with two prongs flanking the handle
        // joint. Sits at y=3..4 from x=8..14 with the joint at (10,5).
        private static void DrawPickaxeHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top edge (highlight band).
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 3, hiC.r, hiC.g, hiC.b);
            // Bar body.
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 4, baseC.r, baseC.g, baseC.b);
            // Prong tips — curve down at both ends to suggest pick points.
            SetPixel(pixels, 8, 5, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 5, loC.r, loC.g, loC.b);
            // End caps darker.
            SetPixel(pixels, 8, 3, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 3, loC.r, loC.g, loC.b);
            // Top spike on the far end (visual flourish for pickaxe shape).
            SetPixel(pixels, 13, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 9, 2, loC.r, loC.g, loC.b);
        }

        // Axe head: asymmetric triangular blade widening on the far side
        // of the handle. Sits at x=10..14, y=2..5 with the bulk at the
        // upper-right corner.
        private static void DrawAxeHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top wedge — narrow at the handle, wider at the far edge.
            SetPixel(pixels, 12, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 13, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 2, loC.r, loC.g, loC.b);
            // Mid band — full width with highlight stripe.
            SetPixel(pixels, 11, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 13, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 14, 3, baseC.r, baseC.g, baseC.b);
            // Bottom band — body.
            SetPixel(pixels, 10, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 13, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 14, 4, loC.r, loC.g, loC.b);
            // Bottom edge tapers back to the handle.
            SetPixel(pixels, 11, 5, loC.r, loC.g, loC.b);
            SetPixel(pixels, 12, 5, loC.r, loC.g, loC.b);
        }

        // Walk item layers 58..66 and paint a 16×16 sprite per item.
        // Same shape as GenerateProceduralToolLayers: clear the layer
        // buffer, dispatch to the per-item generator, upload via
        // TexSubImage3D into the bound Texture2DArray. Items don't share
        // a base layout the way tools do (every tool reuses the diagonal
        // wood handle), so each generator is self-contained — sticks
        // are a vertical brown bar, coal is a black blob, ingots are
        // small loaf rectangles, etc. All 9 sprites are designed to
        // read at hotbar / inventory slot scales.
        private static void GenerateProceduralItemLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileStick,     GenerateStickItem);
            UploadItem(layerPixels, TileCoal,      GenerateCoalItem);
            UploadItem(layerPixels, TileIronIngot, GenerateIronIngotItem);
            UploadItem(layerPixels, TileGoldIngot, GenerateGoldIngotItem);
            UploadItem(layerPixels, TileDiamond,   GenerateDiamondItem);
            UploadItem(layerPixels, TileFlint,     GenerateFlintItem);
            UploadItem(layerPixels, TileClayBall,  GenerateClayBallItem);
            UploadItem(layerPixels, TileClayBrick, GenerateClayBrickItem);
            UploadItem(layerPixels, TileBowl,      GenerateBowlItem);
            // Tail-item porkchops (Tier 3 #9 — pig drop + furnace
            // smelt result). Same UploadItem path; layer indices live
            // past the tail-block range.
            UploadItem(layerPixels, TileRawPorkchop,    GenerateRawPorkchopItem);
            UploadItem(layerPixels, TileCookedPorkchop, GenerateCookedPorkchopItem);
        }

        // Local helper mirroring UploadLayer (which is private elsewhere
        // in this file) but scoped to the item slice. Clears the layer
        // buffer first so item generators can assume a fully transparent
        // background and only paint pixels they care about — matches the
        // tool generator convention.
        private static void UploadItem(byte[] layerPixels, int layer, Action<byte[]> generator)
        {
            Array.Clear(layerPixels, 0, layerPixels.Length);
            generator(layerPixels);
            GL.TexSubImage3D(
                TextureTarget.Texture2DArray, 0,
                0, 0, layer,
                TileSize, TileSize, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
        }

        // Stick — a wooden bar running diagonally from lower-left to
        // upper-right, mirroring the tool-handle silhouette so a stick
        // reads as "the handle without the head". 1 px core with hi/lo
        // tracks for chunky 3D feel.
        private static void GenerateStickItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (138, 95, 50);
            (byte r, byte g, byte b) woodHi = (172, 125, 70);
            (byte r, byte g, byte b) woodLo = (95, 60, 30);
            int[] xs = { 4, 5, 6, 7, 8, 9, 10, 11 };
            int[] ys = { 12, 11, 10, 9, 8, 7, 6, 5 };
            for (int i = 0; i < xs.Length; i++)
            {
                SetPixel(pixels, xs[i], ys[i], wood.r, wood.g, wood.b);
                if (xs[i] - 1 >= 0)
                    SetPixel(pixels, xs[i] - 1, ys[i], woodHi.r, woodHi.g, woodHi.b);
                if (ys[i] + 1 < TileSize)
                    SetPixel(pixels, xs[i], ys[i] + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // End caps — slightly darker pommel + tip so the stick's
            // ends read as terminations rather than running off-tile.
            SetPixel(pixels, 3, 13, woodLo.r, woodLo.g, woodLo.b);
            SetPixel(pixels, 12, 4, woodLo.r, woodLo.g, woodLo.b);
        }

        // Coal — irregular black lump centred in the tile, 4-shade
        // jittered fill so it reads as a chunky charcoal blob rather
        // than a solid black square.
        private static void GenerateCoalItem(byte[] pixels)
        {
            var rng = new Random(0xC0A1);
            // Lump silhouette — hand-drawn approximation of a roundish
            // chunk with a few flat planes. Pixels marked 1 are interior.
            int[,] mask = new int[TileSize, TileSize];
            int[] rows = { 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0 };
            int[] xMin = { 0, 0, 0, 4, 3, 3, 2, 2, 3, 3, 4, 4, 5, 0, 0, 0 };
            int[] xMax = { 0, 0, 0, 11, 12, 13, 13, 13, 13, 12, 12, 11, 10, 0, 0, 0 };
            for (int y = 0; y < TileSize; y++)
            {
                if (rows[y] == 0) continue;
                for (int x = xMin[y]; x <= xMax[y]; x++) mask[x, y] = 1;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                if (mask[x, y] == 0) continue;
                // Rim pixels (mask edges) get the darkest shade, body
                // pixels jitter between 3 mid-greys so the lump looks
                // textured instead of flat. 1-in-7 sparkle pixels add
                // a faint highlight, evoking facet glints.
                bool rim = mask[Math.Max(0, x - 1), y] == 0
                        || mask[Math.Min(TileSize - 1, x + 1), y] == 0
                        || mask[x, Math.Max(0, y - 1)] == 0
                        || mask[x, Math.Min(TileSize - 1, y + 1)] == 0;
                byte v;
                if (rim) v = 18;
                else if (rng.Next(7) == 0) v = 90;
                else v = (byte)(40 + rng.Next(20));
                SetPixel(pixels, x, y, v, v, v);
            }
        }

        // Iron ingot — small pale-silver loaf, two-row band shape with
        // a brighter top edge for 3D. Same general silhouette as a
        // bullion bar — narrower at the top, wider at the bottom.
        private static void GenerateIronIngotItem(byte[] pixels)
        {
            DrawIngot(pixels, baseR: 215, baseG: 215, baseB: 222,
                              hiR:   245, hiG:   245, hiB:   250,
                              loR:   145, loG:   145, loB:   158, seed: 0x1B07);
        }

        // Gold ingot — same silhouette as iron, yellow palette.
        private static void GenerateGoldIngotItem(byte[] pixels)
        {
            DrawIngot(pixels, baseR: 240, baseG: 205, baseB: 60,
                              hiR:   255, hiG:   235, hiB:   110,
                              loR:   175, loG:   140, loB:   25, seed: 0x6011);
        }

        // Shared ingot drawer — trapezoidal bar centred in the tile,
        // 3-shade banding. Top row sits at y=6 (narrow), middle rows
        // y=7..9 (full body), bottom row y=10 (slightly narrower than
        // body so the shape tapers, suggesting a moulded ingot).
        private static void DrawIngot(byte[] pixels,
            byte baseR, byte baseG, byte baseB,
            byte hiR,   byte hiG,   byte hiB,
            byte loR,   byte loG,   byte loB,
            int seed)
        {
            var rng = new Random(seed);
            // Top edge — narrow + bright.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 6, hiR, hiG, hiB);
            // Body rows.
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 7, baseR, baseG, baseB, 6, rng);
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 8, baseR, baseG, baseB, 6, rng);
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 9, baseR, baseG, baseB, 6, rng);
            // Bottom edge — narrow + dark for the moulded look.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 10, loR, loG, loB);
            // Side ticks — a single dark pixel at each corner to round
            // the silhouette and sell the trapezoid shape.
            SetPixel(pixels, 4, 7, loR, loG, loB);
            SetPixel(pixels, 11, 9, loR, loG, loB);
        }

        // Diamond — small cyan rhombus with a bright facet on the
        // upper-right. 4 pixels tall, 5 pixels wide, kite-shaped to
        // suggest a cut gem.
        private static void GenerateDiamondItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (90, 220, 230);
            (byte r, byte g, byte b) hiC   = (190, 255, 255);
            (byte r, byte g, byte b) loC   = (40, 160, 180);

            // Top tip
            SetPixel(pixels, 8, 5, hiC.r, hiC.g, hiC.b);
            // Row 6: 3 wide
            SetPixel(pixels, 7, 6, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 8, 6, hiC.r,   hiC.g,   hiC.b);
            SetPixel(pixels, 9, 6, baseC.r, baseC.g, baseC.b);
            // Row 7: 5 wide — widest band
            SetPixel(pixels, 6, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 7, 7, hiC.r,   hiC.g,   hiC.b);
            SetPixel(pixels, 8, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 9, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 10,7, loC.r,   loC.g,   loC.b);
            // Row 8: 3 wide
            SetPixel(pixels, 7, 8, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 8, 8, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 9, 8, loC.r,   loC.g,   loC.b);
            // Row 9: bottom tip
            SetPixel(pixels, 8, 9, loC.r, loC.g, loC.b);
        }

        // Flint — angular dark-grey shard. Asymmetric polygon to read
        // as a knapped flake rather than a regular shape.
        private static void GenerateFlintItem(byte[] pixels)
        {
            var rng = new Random(0xF11A);
            (byte r, byte g, byte b) baseC = (75, 75, 80);
            (byte r, byte g, byte b) hiC   = (130, 130, 135);
            (byte r, byte g, byte b) loC   = (40, 40, 45);

            // Hand-painted shard outline. y0 is top, the shape leans
            // right and tapers toward the bottom.
            int[][] rows = new int[][]
            {
                new[] { 7, 8 },                  // y=4
                new[] { 6, 7, 8, 9 },            // y=5
                new[] { 5, 6, 7, 8, 9, 10 },     // y=6
                new[] { 4, 5, 6, 7, 8, 9, 10 },  // y=7
                new[] { 4, 5, 6, 7, 8, 9, 10, 11 }, // y=8
                new[] { 5, 6, 7, 8, 9, 10 },     // y=9
                new[] { 6, 7, 8, 9 },            // y=10
                new[] { 7, 8 },                  // y=11
            };
            for (int i = 0; i < rows.Length; i++)
            {
                int y = 4 + i;
                foreach (int x in rows[i])
                {
                    // Top-left edge highlights, bottom-right edge dark.
                    bool isTopLeft = (x == rows[i][0]) || (i < 2);
                    bool isBottomRight = (x == rows[i][rows[i].Length - 1]) || (i >= rows.Length - 2);
                    if (isTopLeft && !isBottomRight)
                        SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                    else if (isBottomRight)
                        SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                    else
                        SetJittered(pixels, x, y, baseC.r, baseC.g, baseC.b, 8, rng);
                }
            }
        }

        // Clay ball — round pale-grey ball with shading. Roughly a 6×6
        // disc anchored centre-tile with a brighter top-left highlight
        // and a darker bottom-right shadow.
        private static void GenerateClayBallItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (185, 195, 210);
            (byte r, byte g, byte b) hiC   = (220, 225, 235);
            (byte r, byte g, byte b) loC   = (130, 145, 160);

            // 6×6 disc with corners cut.
            int cx = 8, cy = 8, r = 3;
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r * r + 1) continue;
                // Highlight on upper-left, shadow on lower-right.
                if (dx + dy <= -2)
                    SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                else if (dx + dy >= 3)
                    SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                else
                    SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            }
        }

        // Clay brick — small reddish-orange rectangle with mortar-grey
        // edges. Same hue as the Bricks block tile so the ingredient
        // visually maps to its smelt-input source.
        private static void GenerateClayBrickItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (180, 100, 75);
            (byte r, byte g, byte b) hiC   = (210, 130, 100);
            (byte r, byte g, byte b) loC   = (130, 70, 50);
            var rng = new Random(0xB71D);
            // Rectangle 8 wide × 4 tall, centred.
            for (int y = 6; y <= 9; y++)
            for (int x = 4; x <= 11; x++)
            {
                bool top    = (y == 6);
                bool bottom = (y == 9);
                bool left   = (x == 4);
                bool right  = (x == 11);
                if (top || left)
                    SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                else if (bottom || right)
                    SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                else
                    SetJittered(pixels, x, y, baseC.r, baseC.g, baseC.b, 10, rng);
            }
        }

        // Bowl — wooden half-circle with a darker rim. Rendered as an
        // open-topped vessel: rim along the top, sloped sides, flat
        // bottom. Reads as "bowl" at hotbar size despite the small
        // canvas.
        private static void GenerateBowlItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (158, 113, 60);
            (byte r, byte g, byte b) woodHi = (190, 145, 85);
            (byte r, byte g, byte b) woodLo = (110, 75, 35);
            (byte r, byte g, byte b) inside = (90, 60, 28);

            // Rim — top of the bowl, two pixels tall, full width.
            for (int x = 3; x <= 12; x++)
            {
                SetPixel(pixels, x, 7, woodHi.r, woodHi.g, woodHi.b);
                SetPixel(pixels, x, 8, wood.r,   wood.g,   wood.b);
            }
            // Hollow interior — darker brown, slight inset.
            for (int x = 4; x <= 11; x++) SetPixel(pixels, x, 8, inside.r, inside.g, inside.b);
            // Curved sides — sloped inward as we go down.
            int[] leftEdge  = { 4, 5, 6 };
            int[] rightEdge = { 11, 10, 9 };
            int[] yRows     = { 9, 10, 11 };
            for (int i = 0; i < yRows.Length; i++)
            {
                int y = yRows[i];
                int xL = leftEdge[i];
                int xR = rightEdge[i];
                SetPixel(pixels, xL, y, woodLo.r, woodLo.g, woodLo.b);
                SetPixel(pixels, xR, y, woodLo.r, woodLo.g, woodLo.b);
                for (int x = xL + 1; x < xR; x++)
                    SetPixel(pixels, x, y, wood.r, wood.g, wood.b);
            }
            // Bottom highlight — single bright pixel along the inner
            // bottom rim so the bowl reads as concave.
            SetPixel(pixels, 7, 11, woodHi.r, woodHi.g, woodHi.b);
            SetPixel(pixels, 8, 11, woodHi.r, woodHi.g, woodHi.b);
        }

        // Raw porkchop — pink slab of meat with a darker inset and a
        // light-cream marbling streak. Sits in a 12×7 area centred on
        // the tile so it reads as a chunky cut at hotbar scale.
        private static void GenerateRawPorkchopItem(byte[] pixels)
        {
            (byte r, byte g, byte b) skin    = (235, 145, 145);
            (byte r, byte g, byte b) skinHi  = (250, 175, 175);
            (byte r, byte g, byte b) skinLo  = (180, 95, 105);
            (byte r, byte g, byte b) marbling = (245, 220, 200);
            for (int y = 5; y <= 11; y++)
            for (int x = 2; x <= 13; x++)
            {
                bool edge = (x == 2 || x == 13 || y == 5 || y == 11);
                if (edge) SetPixel(pixels, x, y, skinLo.r, skinLo.g, skinLo.b);
                else      SetPixel(pixels, x, y, skin.r,   skin.g,   skin.b);
            }
            // Highlight bar across the upper inside.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 6, skinHi.r, skinHi.g, skinHi.b);
            // Marbling streak — one bright cream line across the middle.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 8, marbling.r, marbling.g, marbling.b);
        }

        // Cooked porkchop — the same silhouette, but darker / browner
        // (cooked exterior) with a tan interior so the cooked vs. raw
        // contrast reads even at small scale. Crisper edge ring.
        private static void GenerateCookedPorkchopItem(byte[] pixels)
        {
            (byte r, byte g, byte b) crust   = (130, 78, 38);
            (byte r, byte g, byte b) crustHi = (170, 105, 55);
            (byte r, byte g, byte b) crustLo = (78, 42, 18);
            (byte r, byte g, byte b) inside  = (200, 140, 90);
            (byte r, byte g, byte b) insideHi = (225, 175, 125);
            for (int y = 5; y <= 11; y++)
            for (int x = 2; x <= 13; x++)
            {
                bool edge = (x == 2 || x == 13 || y == 5 || y == 11);
                if (edge) SetPixel(pixels, x, y, crustLo.r, crustLo.g, crustLo.b);
                else      SetPixel(pixels, x, y, crust.r,   crust.g,   crust.b);
            }
            // Tan interior fill.
            for (int y = 7; y <= 9; y++)
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, y, inside.r, inside.g, inside.b);
            // Lighter highlight across the top of the interior.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 7, insideHi.r, insideHi.g, insideHi.b);
            // Crisp top ridge.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 6, crustHi.r, crustHi.g, crustHi.b);
        }

        // Tile coordinates in Alpha 1.1.2_01's terrain.png. Format is
        // (col, row), each cell 16×16 pixels in a 16×16 grid (256×256
        // total). The embedded PNG is sliced once at atlas-build time
        // and uploaded layer-per-layer into the same Texture2DArray
        // shape as the procedural atlas, so the rest of the renderer
        // doesn't need to know which one is active.
        //
        // Coordinates picked to match the canonical Alpha layout. A few
        // procedural tiles don't have a perfect 1:1 in vanilla Alpha
        // (clay, redstone ore, tall grass) — those use the closest
        // semantic match from the same era.
        // Tool tile coords reference alpha_tools.png (NOT terrain.png).
        // Layers 38..57 use the parallel ToolLayer flag below; CopyTile
        // is called against a different source buffer for those layers
        // when the alpha-textures path is active. Procedural mode has no
        // tool art and just leaves those layers blank/magenta — the
        // tools-PNG decode runs unconditionally because the procedural
        // atlas still wants the same total layer count to keep the
        // shader's sampler bindings stable.
        private static readonly (int col, int row)[] AlphaTileCoords = new (int, int)[LayerCount]
        {
            /* TileGrassTop          */ (0, 0),
            /* TileGrassSide         */ (3, 0),
            /* TileDirt              */ (2, 0),
            /* TileStone             */ (1, 0),
            /* TileSand              */ (2, 1),
            /* TileCobblestone       */ (0, 1),
            /* TileBedrock           */ (1, 1),
            /* TileGravel            */ (3, 1),
            /* TileClay              */ (8, 4),
            /* TileCoalOre           */ (2, 2),
            /* TileIronOre           */ (1, 2),
            /* TileGoldOre           */ (0, 2),
            /* TileDiamondOre        */ (2, 3),
            /* TileRedstoneOre       */ (3, 3),
            /* TileLogTop            */ (5, 1),
            /* TileLogSide           */ (4, 1),
            /* TilePlanks            */ (4, 0),
            /* TileLeaves            */ (4, 3),
            /* TileWater             */ (15, 13),
            /* TileLava              */ (15, 15),
            /* TileGoldBlock         */ (7, 1),
            /* TileIronBlock         */ (6, 1),
            /* TileDiamondBlock      */ (8, 1),
            /* TileBricks            */ (7, 0),
            /* TileTntTop            */ (9, 0),
            /* TileTntBottom         */ (10, 0),
            /* TileTntSide           */ (8, 0),
            /* TileBookshelfSide     */ (3, 2),
            /* TileMossyCobblestone  */ (4, 2),
            /* TileObsidian          */ (5, 2),
            /* TileSponge            */ (0, 3),
            /* TileGlass             */ (1, 3),
            /* TileWool              */ (0, 4),
            /* TileTorch             */ (0, 5),
            /* TileDandelion         */ (13, 0),
            /* TileRose              */ (12, 0),
            /* TileBrownMushroom     */ (13, 1),
            /* TileRedMushroom       */ (12, 1),

            // Tools — coordinates into alpha_tools.png. Standard Alpha
            // items.png layout: row 4 = swords, row 5 = shovels, row 6
            // = pickaxes, row 7 = axes; cols 0..4 = wood, stone, iron,
            // diamond, gold (left-to-right material order).
            /* TileWoodSword         */ (0, 4),
            /* TileStoneSword        */ (1, 4),
            /* TileIronSword         */ (2, 4),
            /* TileDiamondSword      */ (3, 4),
            /* TileGoldSword         */ (4, 4),
            /* TileWoodShovel        */ (0, 5),
            /* TileStoneShovel       */ (1, 5),
            /* TileIronShovel        */ (2, 5),
            /* TileDiamondShovel     */ (3, 5),
            /* TileGoldShovel        */ (4, 5),
            /* TileWoodPickaxe       */ (0, 6),
            /* TileStonePickaxe      */ (1, 6),
            /* TileIronPickaxe       */ (2, 6),
            /* TileDiamondPickaxe    */ (3, 6),
            /* TileGoldPickaxe       */ (4, 6),
            /* TileWoodAxe           */ (0, 7),
            /* TileStoneAxe          */ (1, 7),
            /* TileIronAxe           */ (2, 7),
            /* TileDiamondAxe        */ (3, 7),
            /* TileGoldAxe           */ (4, 7),

            // Items — coordinates into alpha_tools.png (the same 256×256
            // sheet that holds the tool icons). Alpha 1.1.2 packs ALL
            // craftable items into a single PNG; the columns past col 4
            // (which holds the gold-tier tools) house the ingredient
            // sprites. Verified against the embedded sheet: e.g. Coal
            // at (7,0) reads pure black, Diamond at (7,3) reads cyan,
            // GoldIngot at (7,2) reads yellow.
            /* TileStick             */ (5, 3),
            /* TileCoal              */ (7, 0),
            /* TileIronIngot         */ (7, 1),
            /* TileGoldIngot         */ (7, 2),
            /* TileDiamond           */ (7, 3),
            /* TileFlint             */ (6, 0),
            /* TileClayBall          */ (9, 3),
            /* TileClayBrick         */ (6, 1),
            /* TileBowl              */ (7, 4),

            // Tail blocks — coordinates into terrain.png (NOT alpha_tools).
            // Crafting table top is the work-bench grid at (11,2); side
            // is the tool-rack art at (11,3). The bottom face reuses
            // TilePlanks (4,0) at the GetTileIndex lookup so we don't
            // need a third entry.
            /* TileCraftingTableTop  */ (11, 2),
            /* TileCraftingTableSide */ (11, 3),
            // Furnace face tiles in Alpha 1.1.2's terrain.png. Side at
            // (13,2) is the iron-banded stone wall panel. Front-off at
            // (12,2) shows the dark furnace mouth; front-on at (13,3) is
            // the same mouth with an orange/yellow glow lit inside.
            // The top face is NOT present in this build's terrain.png
            // (Alpha shipped it but the embedded resource here is missing
            // that tile), so TileFurnaceTop is always generated
            // procedurally — even in the alpha-textures atlas path.
            // The (-1,-1) sentinel below documents that and trips a
            // runtime assert if anything tries to slice it. See
            // CreateAtlasFromAlphaTerrain for the procedural-override.
            /* TileFurnaceTop        */ (-1, -1),
            /* TileFurnaceSide       */ (13, 2),
            /* TileFurnaceFront      */ (12, 2),
            /* TileFurnaceFrontLit   */ (13, 3),
            // Chest tiles in Alpha 1.1.2's terrain.png. Top at (9,1) is
            // the planked lid with the iron lock-plate; side at (10,1)
            // is the planked carcass with a horizontal iron band;
            // front at (11,1) adds the door split + centred lock to
            // the band. The bottom face reuses TilePlanks (4,0) via
            // GetTileIndex, so no fourth entry is needed.
            /* TileChestTop          */ (9, 1),
            /* TileChestSide         */ (10, 1),
            /* TileChestFront        */ (11, 1),
            // Tail items (Tier 3 #9 — pig drops). Both porkchop tiles
            // are sourced from alpha_tools.png at the canonical Notch
            // coords for the food row: raw at (7,5) and cooked at (8,5).
            // Procedural mode synthesises substitutes; see
            // GenerateRawPorkchopItem / GenerateCookedPorkchopItem.
            /* TileRawPorkchop       */ (7, 5),
            /* TileCookedPorkchop    */ (8, 5),
        };

        // True for layers whose source PNG is alpha_tools.png; false for
        // layers sourced from terrain.png. CreateAtlasFromAlphaTerrain
        // selects the right source buffer per layer using this flag.
        private static bool IsToolLayer(int layer) => layer >= BlockLayerCount;

        // Image-based atlas: read the embedded Alpha terrain.png, slice
        // it into 16×16 tiles using AlphaTileCoords, and upload one
        // layer per tile in the same order as CreateAtlas. The result
        // is a drop-in replacement for the procedural atlas — same
        // dimensions, same layer count, same wrap/filter — so callers
        // can swap between them at runtime without needing to rebuild
        // shaders or vertex layouts.
        //
        // Returns 0 if the embedded resource isn't present (caller is
        // expected to fall back to the procedural atlas) — this keeps
        // older deployed builds that haven't had the PNG re-embedded
        // from going black-screen. Any other failure (decoder fault,
        // GL upload error) propagates as an exception.
        public static int CreateAtlasFromAlphaTerrain()
        {
            byte[] bgra; int srcW, srcH;
            if (!TryDecodeEmbeddedTerrain(out bgra, out srcW, out srcH))
                return 0;

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tex);
            GL.TexImage3D(
                TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba,
                TileSize, TileSize, LayerCount, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            var layerPixels = new byte[TileSize * TileSize * 4];
            // Block layers from terrain.png (the bgra buffer just decoded).
            for (int layer = 0; layer < BlockLayerCount; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
            // Tool layers from alpha_tools.png — separately decoded to its
            // own cached buffer so the two atlases stay independent
            // (terrain can be hot-swappable in the future without
            // disturbing tool icons).
            // Tool AND item layers (38..66) share alpha_tools.png as their
            // source — Alpha packs swords/shovels/pickaxes/axes plus all
            // ingredient sprites (coal, diamond, ingots, stick, flint,
            // clay ball/brick, bowl) into the same 256×256 sheet, so a
            // single decode + slice loop covers both ranges.
            UploadToolLayersFromAlphaTools(layerPixels);

            // Tail block layers (67+) — sourced from terrain.png, same as
            // the 0..37 range. Done after the tool slice so the loop ranges
            // stay non-overlapping. AlphaTileCoords entries for these layers
            // index into terrain.png coordinates.
            //
            // TileFurnaceTop is the one exception: this build's embedded
            // terrain.png doesn't carry the furnace-top tile, so its
            // AlphaTileCoords entry is the (-1,-1) sentinel. We skip it
            // in the slice loop and fill it from the procedural
            // GenerateFurnaceTop below — that generator paints a top
            // built from the same stone-wall + iron-banding palette as
            // the side, so the four lateral faces and the cap read as
            // one continuous block.
            for (int layer = FirstTailBlockLayer; layer < FirstTailItemLayer; layer++)
            {
                if (layer == TileFurnaceTop) continue;
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
            // Procedural furnace-top override (see comment above). Uses
            // the same UploadLayer path as the procedural atlas — the
            // Texture2DArray is still bound to `tex` at this point.
            UploadLayer(layerPixels, TileFurnaceTop, GenerateFurnaceTop);

            // Tail-item layers (76+, Tier 3 #9 porkchops). Sourced from
            // alpha_tools.png — Notch packs the food row into the same
            // sheet as tools/items at canonical (col, row) coordinates.
            // Falls back silently if the embedded PNG is missing (the
            // procedural path painted these slots; they'll just stay
            // procedural in the otherwise-Alpha atlas).
            UploadTailItemLayersFromAlphaTools(layerPixels);

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        // Cached decoded buffer. The atlas builder runs on the GL render
        // thread, but WPF imaging has thread-affinity quirks — the safest
        // way to dodge them is to decode once and reuse the byte buffer
        // for any subsequent rebuild. The cache is process-scoped: every
        // world-load on the same VS session gets the already-decoded
        // pixels with no PNG decoder pass at all.
        private static byte[] s_cachedBgra;
        private static int s_cachedW, s_cachedH;
        private static bool s_decodeAttempted;
        private static bool s_decodeFailed;

        // Parallel decode cache for alpha_tools.png. Tools are independent
        // from terrain (loaded from a different base64 constant) but
        // reuse the same decode path — the only divergence is the source
        // bytes. A separate cache avoids re-decoding on every atlas
        // rebuild and keeps the two PNG buffers from clobbering each
        // other's lifetimes.
        private static byte[] s_toolsCachedBgra;
        private static int s_toolsCachedW, s_toolsCachedH;
        private static bool s_toolsDecodeFailed;
        // Surfaced through AlphaTerrainStatus so we can show the actual
        // failure reason in the Options label instead of a generic
        // "UNAVAILABLE" — we were guessing at the failure cause and
        // chasing the wrong fix; a visible message turns this into
        // a one-click diagnosis next time it happens.
        private static string s_decodeStatus;

        // Decode the embedded PNG into a tightly-packed BGRA byte array.
        // WPF's BitmapDecoder is the simplest decoder we already have a
        // reference to (PresentationCore); System.Drawing would also
        // work but adds platform baggage. The PNG ships in the assembly
        // as a manifest resource — preferred name is
        // "VStudioCraft.Assets.alpha_terrain.png" (set via the project's
        // <LogicalName>) but we tolerate the default-derived name too,
        // which is what some build paths or older csproj edits produce.
        //
        // Returns false if no candidate resource is found in the
        // assembly's manifest, or if the decoder throws (a stale build
        // or unusual WPF host setup) — caller falls back to procedural
        // art rather than crashing the render thread.
        private static bool TryDecodeEmbeddedTerrain(out byte[] bgra, out int width, out int height)
        {
            // Fast path: reuse cached buffer once we've decoded once.
            if (s_cachedBgra != null)
            {
                bgra = s_cachedBgra; width = s_cachedW; height = s_cachedH;
                return true;
            }
            // Don't keep retrying a decode that's already failed — once
            // is enough; subsequent calls just take the fallback.
            if (s_decodeFailed)
            {
                bgra = null; width = 0; height = 0;
                return false;
            }
            s_decodeAttempted = true;

            bgra = null; width = 0; height = 0;
            try
            {
                bool ok = TryDecodeEmbeddedTerrainCore(out bgra, out width, out height);
                if (!ok && s_decodeStatus == null)
                    s_decodeStatus = "no resource";
                return ok;
            }
            catch (Exception ex)
            {
                // Capture the type+first-line of the message so the
                // Options label can surface it. We don't include the
                // full message because it may contain paths/locale text
                // that won't fit; the type alone narrows the cause to
                // GDI+ vs IO vs reflection vs something stranger.
                s_decodeFailed = true;
                s_decodeStatus = ex.GetType().Name;
                bgra = null; width = 0; height = 0;
                return false;
            }
        }

        // GDI+ decoder. We previously used WPF's BitmapDecoder, but in
        // some VS hosting setups it threw mid-decode (CopyPixels on a
        // non-Dispatcher thread, or codec initialisation that hadn't
        // run yet) and silently fell back to procedural. System.Drawing
        // is already referenced for the WinForms host, has no thread
        // affinity and no Dispatcher dependency, and locks the bitmap's
        // pixel data directly so we get raw BGRA out the other side
        // without going through WPF's imaging stack.
        //
        // Source-of-bytes precedence:
        //   1. AlphaTerrainData.Base64 — a string constant compiled into
        //      the assembly's IL. This is the primary source because
        //      <EmbeddedResource> turned out to be fragile in some
        //      developer VS configurations (the resource silently went
        //      missing from the deployed DLL); a const string is
        //      something the compiler simply cannot drop.
        //   2. Embedded manifest resource — kept as a secondary source
        //      so existing builds that still ship the resource keep
        //      working, and so a future regenerated PNG can be picked
        //      up from the resource without re-running the codegen
        //      script for the base64 constant.
        private static bool TryDecodeEmbeddedTerrainCore(out byte[] bgra, out int width, out int height)
        {
            bgra = null; width = 0; height = 0;

            byte[] pngBytes = TryLoadPngBytes();
            if (pngBytes == null) return false;

            using (var src = new MemoryStream(pngBytes))
            using (var bmp = new Bitmap(src))
            {
                width  = bmp.Width;
                height = bmp.Height;

                var rect = new Rectangle(0, 0, width, height);
                var data = bmp.LockBits(rect, GdiImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
                try
                {
                    int dstStride = width * 4;
                    bgra = new byte[dstStride * height];
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                        Marshal.Copy(rowPtr, bgra, y * dstStride, dstStride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }

            // Cache the decoded buffer so subsequent toggles reuse it
            // instead of re-running the decoder.
            s_cachedBgra = bgra;
            s_cachedW = width;
            s_cachedH = height;
            return true;
        }

        // Decode alpha_tools.png from the inlined AlphaToolsData.Base64
        // constant. Same GDI+ path as the terrain decoder — kept separate
        // because the tools sheet has its own size (256×256) and its own
        // failure mode (older builds without AlphaToolsData simply skip
        // tool icons rather than going blank for everything).
        private static bool TryDecodeEmbeddedTools(out byte[] bgra, out int width, out int height)
        {
            if (s_toolsCachedBgra != null)
            {
                bgra = s_toolsCachedBgra; width = s_toolsCachedW; height = s_toolsCachedH;
                return true;
            }
            if (s_toolsDecodeFailed)
            {
                bgra = null; width = 0; height = 0; return false;
            }

            bgra = null; width = 0; height = 0;
            try
            {
                string b64 = AlphaToolsData.Base64;
                if (string.IsNullOrEmpty(b64))
                {
                    s_toolsDecodeFailed = true;
                    return false;
                }
                byte[] pngBytes = Convert.FromBase64String(b64);

                using (var src = new MemoryStream(pngBytes))
                using (var bmp = new Bitmap(src))
                {
                    width = bmp.Width;
                    height = bmp.Height;
                    var rect = new Rectangle(0, 0, width, height);
                    var data = bmp.LockBits(rect, GdiImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
                    try
                    {
                        int dstStride = width * 4;
                        bgra = new byte[dstStride * height];
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                            Marshal.Copy(rowPtr, bgra, y * dstStride, dstStride);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                }
                s_toolsCachedBgra = bgra;
                s_toolsCachedW = width;
                s_toolsCachedH = height;
                return true;
            }
            catch
            {
                s_toolsDecodeFailed = true;
                bgra = null; width = 0; height = 0;
                return false;
            }
        }

        // Resolve PNG bytes from the most reliable source available.
        // Returns null only if both the inlined base64 constant and
        // the embedded manifest resource are absent — at which point
        // we genuinely have no terrain data and the caller should
        // fall back to procedural.
        private static byte[] TryLoadPngBytes()
        {
            // 1. Inline base64 constant — present unconditionally in
            //    every build because it's IL, not a resource. This
            //    path makes the toggle work regardless of whatever
            //    VS configuration ate the embedded resource last time.
            try
            {
                var b64 = AlphaTerrainData.Base64;
                if (!string.IsNullOrEmpty(b64))
                    return Convert.FromBase64String(b64);
            }
            catch
            {
                // Fall through to manifest resource path — extremely
                // unlikely (the constant is valid base64 by codegen
                // construction) but we don't want a corrupted constant
                // to take out the whole alpha-textures option.
            }

            // 2. Manifest resource fallback (legacy / belt-and-braces).
            var asm = Assembly.GetExecutingAssembly();

            // Resource-name resolution order:
            //   1. The LogicalName we set in the csproj (preferred).
            //   2. Default-mapped name (RootNamespace + path), in case
            //      LogicalName wasn't picked up by the build.
            //   3. Anything containing "alpha_terrain.png" — last-ditch
            //      heuristic so a future rename still works without
            //      touching this code.
            string[] candidates =
            {
                "VStudioCraft.Assets.alpha_terrain.png",
                "VStudioCraft.Game.Assets.alpha_terrain.png",
            };
            string match = null;
            var allNames = asm.GetManifestResourceNames();
            foreach (var c in candidates)
            {
                foreach (var n in allNames)
                {
                    if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase))
                    {
                        match = n;
                        break;
                    }
                }
                if (match != null) break;
            }
            if (match == null)
            {
                foreach (var n in allNames)
                {
                    if (n.IndexOf("alpha_terrain", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = n;
                        break;
                    }
                }
            }
            if (match == null) return null;

            try
            {
                using (var s = asm.GetManifestResourceStream(match))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // Eagerly decode the embedded terrain on a known-good thread
        // (typically the WPF UI thread at world-load time). Calling this
        // before any toggle ensures the cache is populated, so the GL
        // render-thread rebuild later just reads the cached BGRA buffer
        // and never touches the WPF imaging stack itself. Safe to call
        // multiple times — it's a no-op once the cache is populated.
        public static void PrewarmEmbeddedTerrain()
        {
            if (s_cachedBgra != null || s_decodeFailed) return;
            byte[] _; int __, ___;
            TryDecodeEmbeddedTerrain(out _, out __, out ___);
        }

        // Diagnostic surface for the Options menu. After PrewarmEmbeddedTerrain
        // runs (or after the first toggle attempt), this reflects whether the
        // alpha terrain.png is actually usable — false here means the toggle
        // is a no-op and the user is silently getting procedural in both
        // ON and OFF positions, which is exactly the "I enable it but
        // nothing changes" symptom we're trying to make legible.
        public static bool AlphaTerrainAvailable => s_cachedBgra != null;
        public static bool AlphaTerrainAttempted => s_decodeAttempted || s_cachedBgra != null;
        public static bool AlphaTerrainFailed => s_decodeFailed;
        // Short human-readable status (exception type, "no resource",
        // or null when not yet attempted). Surfaced in OptionsMenu
        // when the toggle would otherwise show a misleading state.
        public static string AlphaTerrainStatus => s_decodeStatus;

        // Copy a single 16×16 tile from the source BGRA buffer at
        // (col*16, row*16) into a tightly-packed RGBA buffer suitable
        // for GL upload. Two transforms happen here:
        //
        //   1. BGRA → RGBA channel swap (PNG decoder gives us BGRA;
        //      GL upload expects RGBA in our pipeline).
        //
        //   2. Vertical flip — PNG row 0 is the *top* of the source
        //      tile, but the procedural atlas (and therefore the mesh
        //      UVs that drive the renderer) treats data row 0 as the
        //      *bottom* of the rendered face (see comments on
        //      GenerateGrassSide / GenerateTorch — the green fringe
        //      and flame head sit at high y to land at the *top* of
        //      a face). Without this flip, terrain.png sliced tiles
        //      would render upside-down: grass-side fringe at the
        //      bottom, torches pointing down, flowers inverted.
        private static void CopyTile(byte[] srcBgra, int srcW, int srcH, int col, int row, byte[] dstRgba)
        {
            int x0 = col * TileSize;
            int y0 = row * TileSize;
            // Bounds check: an out-of-range coordinate (e.g. someone
            // edits AlphaTileCoords past 16×16) should produce a magenta
            // tile rather than crash, so the bad mapping is visible
            // in-game rather than silently failing.
            if (x0 < 0 || y0 < 0 || x0 + TileSize > srcW || y0 + TileSize > srcH)
            {
                for (int i = 0; i < dstRgba.Length; i += 4)
                {
                    dstRgba[i + 0] = 255;
                    dstRgba[i + 1] = 0;
                    dstRgba[i + 2] = 255;
                    dstRgba[i + 3] = 255;
                }
                return;
            }

            int srcStride = srcW * 4;
            for (int ty = 0; ty < TileSize; ty++)
            {
                // Read the source row in PNG order (top-to-bottom)…
                int srcRow = (y0 + ty) * srcStride + x0 * 4;
                // …but write to the dst row that mirrors it vertically,
                // so high-y in source ends up at low-y in dst (= bottom
                // of rendered face), matching procedural convention.
                int dstY = TileSize - 1 - ty;
                int dstRow = dstY * TileSize * 4;
                for (int tx = 0; tx < TileSize; tx++)
                {
                    byte b = srcBgra[srcRow + tx * 4 + 0];
                    byte g = srcBgra[srcRow + tx * 4 + 1];
                    byte r = srcBgra[srcRow + tx * 4 + 2];
                    byte a = srcBgra[srcRow + tx * 4 + 3];
                    dstRgba[dstRow + tx * 4 + 0] = r;
                    dstRgba[dstRow + tx * 4 + 1] = g;
                    dstRgba[dstRow + tx * 4 + 2] = b;
                    dstRgba[dstRow + tx * 4 + 3] = a;
                }
            }
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
            // Fancy-graphics-style alpha-tested leaves: dense green noise
            // punched through with ~25% holes so adjacent leaf blocks read
            // through each other as a layered canopy. Matches the alpha (4,3)
            // tile's silhouette behaviour — the shader's `if (tex.a < 0.5)
            // discard;` does the cut-out, no blending. Without the holes the
            // canopy collapsed to a single solid hull and every block behind
            // the front face was invisible.
            var rng = new Random(0x1EAF);
            var palette = new (byte, byte, byte)[]
            {
                (48, 92, 30),
                (40, 78, 24),
                (62, 108, 38),
                (30, 60, 16),
                (20, 40, 10),
            };
            var weights = new[] { 10, 6, 3, 4, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                // Roughly 1-in-4 cells punch through entirely. The choice
                // is deterministic per-tile (seeded RNG) so two adjacent
                // leaf blocks share the same hole pattern — that makes the
                // layered effect read as foliage rather than a moiré.
                bool hole = rng.Next(4) == 0;
                if (hole)
                {
                    SetPixel(pixels, x, y, 0, 0, 0, 0);
                    continue;
                }
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b, 255);
            }
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

        // Crafting table top — plank base with a 3×3 grid scratched in,
        // hinting at the work surface. The grid is drawn with a slightly
        // darker tint (not pure black) so it reads at a glance without
        // shouting "IDE icon" at the player.
        private static void GenerateCraftingTableTop(byte[] pixels)
        {
            // Start from a planks-style base.
            var rng = new Random(0xC2A1);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)110 : (byte)148;
                byte g = groove ? (byte)80 : (byte)112;
                byte b = groove ? (byte)44 : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 5, rng);
            }
            // Overlay the 3×3 work grid: vertical bars at x=5 and x=10,
            // horizontal bars at y=5 and y=10. 1px wide, dark brown.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 5, 60, 38, 18);
                SetPixel(pixels, x, 10, 60, 38, 18);
            }
            for (int y = 0; y < TileSize; y++)
            {
                SetPixel(pixels, 5, y, 60, 38, 18);
                SetPixel(pixels, 10, y, 60, 38, 18);
            }
            // Outer rim — frames the work surface against the side art.
            for (int i = 0; i < TileSize; i++)
            {
                SetPixel(pixels, i, 0, 88, 60, 30);
                SetPixel(pixels, i, TileSize - 1, 88, 60, 30);
                SetPixel(pixels, 0, i, 88, 60, 30);
                SetPixel(pixels, TileSize - 1, i, 88, 60, 30);
            }
        }

        // Crafting table side — plank base with two simple tool
        // silhouettes (a saw blade outline + a hammer head) drawn in
        // dark brown. Doesn't need to be readable at micro-scale; it
        // just needs to NOT look like plain planks so the player can
        // tell the block apart from a normal Planks block at a glance.
        private static void GenerateCraftingTableSide(byte[] pixels)
        {
            var rng = new Random(0xC2B2);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)110 : (byte)148;
                byte g = groove ? (byte)80 : (byte)112;
                byte b = groove ? (byte)44 : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 5, rng);
            }
            // Hammer (top-left): rectangular head + diagonal handle.
            // Head: x=2..6, y=2..4
            for (int y = 2; y <= 4; y++)
            for (int x = 2; x <= 6; x++)
                SetPixel(pixels, x, y, 60, 38, 18);
            // Handle: diagonal from (5,4) toward (10,9).
            for (int i = 0; i < 6; i++)
                SetPixel(pixels, 5 + i, 4 + i, 80, 50, 22);

            // Saw blade (bottom-right): triangular silhouette.
            // Spine: y=12, x=8..14
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 12, 60, 38, 18);
            // Teeth: alternating below the spine
            for (int x = 8; x <= 14; x += 2)
                SetPixel(pixels, x, 13, 60, 38, 18);
            // Handle: y=10..11, x=14
            SetPixel(pixels, 14, 10, 80, 50, 22);
            SetPixel(pixels, 14, 11, 80, 50, 22);
        }

        // Furnace top — derived from the side palette so the cap reads
        // as the same material as the four lateral faces. Stone-wall
        // jitter base, iron banding around all four edges (looking down
        // onto the block we see the rim from every direction, not just
        // top/bottom), and a darker recessed vent hole in the centre
        // that lets smoke out. Built deliberately to look like a 90°
        // rotation of the side art with a vent overlay; that way an
        // observer glancing at a furnace from above never sees a seam
        // between the cap and the walls.
        //
        // This generator is invoked from BOTH atlas paths (procedural
        // CreateAtlas and alpha-art CreateAtlasFromAlphaTerrain) because
        // this build's embedded terrain.png is missing the furnace-top
        // tile entirely.
        private static void GenerateFurnaceTop(byte[] pixels)
        {
            // Same RNG seed shape as GenerateFurnaceSide so the noise
            // grain looks like it came off the same stamping machine
            // (different seed value to avoid identical pixels — that
            // would make the top read as the side, just rotated).
            var rng = new Random(0xF0A1);
            // Stone-wall base — same RGB and jitter amount as
            // GenerateFurnaceSide.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron banding on ALL four edges (2 px deep) — the side
            // tile only bands top/bottom because its left/right edges
            // tile against the next side tile. The top face has no
            // neighbour to tile against, so the banding wraps the
            // whole rim.
            for (int i = 0; i < TileSize; i++)
            {
                // Top and bottom rows.
                SetPixel(pixels, i, 0, 86, 86, 86);
                SetPixel(pixels, i, 1, 100, 100, 100);
                SetPixel(pixels, i, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, i, TileSize - 1, 86, 86, 86);
                // Left and right columns. Skip the corners we already
                // wrote above so the corner shade matches the
                // top/bottom band rather than getting overwritten.
                if (i >= 2 && i <= TileSize - 3)
                {
                    SetPixel(pixels, 0, i, 86, 86, 86);
                    SetPixel(pixels, 1, i, 100, 100, 100);
                    SetPixel(pixels, TileSize - 2, i, 100, 100, 100);
                    SetPixel(pixels, TileSize - 1, i, 86, 86, 86);
                }
            }
            // Rivet dots — three on each edge, placed at the same x/y
            // offsets the side tile uses (3, 8, 13) so the bands read
            // as continuous fasteners around the block.
            for (int s = 3; s < TileSize; s += 5)
            {
                SetPixel(pixels, s, 0, 180, 180, 180);
                SetPixel(pixels, s, TileSize - 1, 180, 180, 180);
                SetPixel(pixels, 0, s, 180, 180, 180);
                SetPixel(pixels, TileSize - 1, s, 180, 180, 180);
            }
            // Central vent hole — 4×4 near-black square in the middle
            // (6..9). The smelter has to vent somewhere; this is the
            // only Alpha cue that the top differs from a wall panel.
            for (int y = 6; y <= 9; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 24, 24, 22);
            }
            // Vent rim — one-pixel highlight on the upper/left edges
            // and shadow on the lower/right so the hole reads as
            // recessed rather than painted on. Matches the lighting
            // direction used by other recessed details (e.g. the
            // furnace mouth on the front face).
            for (int x = 5; x <= 10; x++)
            {
                SetPixel(pixels, x, 5, 160, 160, 160);
                SetPixel(pixels, x, 10, 80, 80, 80);
            }
            for (int y = 5; y <= 10; y++)
            {
                SetPixel(pixels, 5, y, 160, 160, 160);
                SetPixel(pixels, 10, y, 80, 80, 80);
            }
        }

        // Furnace side — plain stone wall with horizontal iron banding
        // top and bottom. The banding hints at structural reinforcement
        // and lets the side read distinct from a plain Stone block.
        private static void GenerateFurnaceSide(byte[] pixels)
        {
            var rng = new Random(0xF0B2);
            // Stone wall base.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron-grey horizontal bands at the top and bottom edges (2 px
            // each). These are the "rivetted plate" detail Alpha shows on
            // the furnace's vertical flanks — without orientation
            // metadata they apply to all four sides, which still reads
            // as "metal-edged stone" from any angle.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 0, 86, 86, 86);
                SetPixel(pixels, x, 1, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 1, 86, 86, 86);
            }
            // Rivet dots — three evenly-spaced highlights on each iron
            // band so the surface reads as fastened plate.
            for (int x = 3; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 0, 180, 180, 180);
                SetPixel(pixels, x, TileSize - 1, 180, 180, 180);
            }
        }

        // Furnace front (unlit) — stone wall with a recessed dark mouth
        // in the lower-middle. The mouth is the smelter door; lit
        // version below adds the orange/yellow flame glow inside.
        // Shared base palette with the side tile so the four lateral
        // faces blend into a single block silhouette before the door
        // detail is overlaid.
        private static void GenerateFurnaceFront(byte[] pixels)
        {
            var rng = new Random(0xF0C3);
            // Stone wall base — same recipe as the side tile so the
            // edges blend.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron rim banding (top and bottom) — keeps the four-sides
            // silhouette consistent with FurnaceSide.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 0, 86, 86, 86);
                SetPixel(pixels, x, 1, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 1, 86, 86, 86);
            }
            // Furnace mouth — recessed dark opening at (4..11, 5..12).
            // Two-tone: outer frame medium-dark, inner cavity near-black.
            for (int y = 5; y <= 12; y++)
            for (int x = 4; x <= 11; x++)
            {
                SetPixel(pixels, x, y, 24, 24, 22);
            }
            // Frame — slightly lighter ring so the cavity reads as
            // recessed rather than painted on.
            for (int x = 4; x <= 11; x++)
            {
                SetPixel(pixels, x, 5, 70, 70, 68);
                SetPixel(pixels, x, 12, 70, 70, 68);
            }
            for (int y = 5; y <= 12; y++)
            {
                SetPixel(pixels, 4, y, 70, 70, 68);
                SetPixel(pixels, 11, y, 70, 70, 68);
            }
        }

        // Furnace front (lit) — same base as FurnaceFront with orange
        // and yellow pixels filling the mouth cavity to convey active
        // smelting. The lit-state block (BlockType.LitFurnace) swaps in
        // when the per-position tile entity has fuel burning; switching
        // back to Furnace clears the glow without needing a per-tick
        // texture change.
        private static void GenerateFurnaceFrontLit(byte[] pixels)
        {
            // Start with the unlit version so the frame, banding, and
            // cavity outline all match exactly.
            GenerateFurnaceFront(pixels);
            var rng = new Random(0xF0D4);
            // Fill the cavity (interior of the frame at x=5..10, y=6..11)
            // with a flame palette: bottom-third hot orange, middle
            // yellow, top dim red — gives the impression of flames
            // licking up inside the mouth.
            for (int y = 6; y <= 11; y++)
            for (int x = 5; x <= 10; x++)
            {
                byte r, g, b;
                if (y >= 9)
                {
                    // Hot core near the bottom — orange-red.
                    r = 255; g = 130; b = 30;
                }
                else if (y >= 7)
                {
                    // Mid flame — yellow-orange.
                    r = 248; g = 188; b = 60;
                }
                else
                {
                    // Top flame tips — dim red shading into smoke.
                    r = 168; g = 60; b = 18;
                }
                SetJittered(pixels, x, y, r, g, b, 16, rng);
            }
        }

        // Chest top — planked lid with a small iron lock-plate centred
        // on the front edge. The plate's "front" edge in the texture is
        // the +Z edge in world space; that lines up with the default
        // BlockFacing so a chest placed under the standard south-facing
        // default reads correctly.
        private static void GenerateChestTop(byte[] pixels)
        {
            // Plank base — same recipe as GeneratePlanks but a slightly
            // darker tone so the lid reads as "treated wood" rather than
            // bare planks.
            var rng = new Random(0xCE51);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron lock-plate: a small rectangle straddling the front
            // edge of the lid (front = high y, since the side/front
            // tiles place the band high too — see GenerateChestSide).
            // Plate sits at x=6..9, y=11..13.
            for (int y = 11; y <= 13; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 110, 110, 110);
            }
            // Plate highlight (top edge) and shadow (bottom edge).
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, 11, 150, 150, 150);
                SetPixel(pixels, x, 13, 70, 70, 70);
            }
            // A single keyhole pixel at the centre of the plate.
            SetPixel(pixels, 7, 12, 30, 30, 30);
            SetPixel(pixels, 8, 12, 30, 30, 30);
        }

        // Chest side — plank carcass with a single horizontal iron
        // band across the upper third (y=4..5). No lock or door split,
        // so the side reads as "the back/sides of a wooden box".
        private static void GenerateChestSide(byte[] pixels)
        {
            var rng = new Random(0xCE62);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron band — 2 pixels tall at y=4..5, with shadow on the
            // lower edge so the band reads as a strap rather than a
            // painted line.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 4, 130, 130, 130);
                SetPixel(pixels, x, 5, 96, 96, 96);
            }
            // Three rivets at evenly spaced offsets on the band.
            for (int x = 2; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 4, 180, 180, 180);
            }
        }

        // Chest front — same plank base + iron band as the side, plus
        // a vertical door split down the centre column and a small
        // iron lock straddling the band at the door split. This is
        // the face that lands on the side of the chest facing the
        // placer (handled by GetTileIndexForOriented).
        private static void GenerateChestFront(byte[] pixels)
        {
            var rng = new Random(0xCE73);
            // Plank base.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron band — same coordinates as the side so the band
            // wraps continuously around the chest.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 4, 130, 130, 130);
                SetPixel(pixels, x, 5, 96, 96, 96);
            }
            for (int x = 2; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 4, 180, 180, 180);
            }
            // Door split: a 1px dark seam down the middle of the
            // lower two-thirds (y=6..15, x=8). Skip the band rows so
            // the seam doesn't break the band silhouette.
            for (int y = 6; y < TileSize; y++)
            {
                SetPixel(pixels, 8, y, 70, 46, 24);
            }
            // Iron lock — 4×3 plate centred on the door split,
            // straddling the band so the lock visually fastens the
            // band in place. Plate at x=6..9, y=6..8.
            for (int y = 6; y <= 8; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 110, 110, 110);
            }
            // Plate highlight + shadow.
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, 6, 150, 150, 150);
                SetPixel(pixels, x, 8, 70, 70, 70);
            }
            // Keyhole — two near-black pixels at the plate centre.
            SetPixel(pixels, 7, 7, 30, 30, 30);
            SetPixel(pixels, 8, 7, 30, 30, 30);
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
            // Alpha-tested glass: opaque pale frame, fully transparent
            // interior. The fragment shader's `if (tex.a < 0.5) discard;`
            // cuts the centre out at draw time, leaving just the 1-pixel
            // border visible — matches Alpha 1.1.2_01's canonical glass
            // tile (clear pane outlined in a muted frame). Combined with
            // the mesher's IsAlphaTestedCube routing, adjacent glass blocks
            // emit their shared face so a row of windows reads as a stack
            // of frames the player can see through, not a hollow shell.
            //
            // Two pixels in from each edge is left as frame as well so the
            // outline is a touch thicker and reads cleanly at 16×16 nearest-
            // filter — a 1-px border alone disappears at distance.
            var rng = new Random(0x61A5);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool outer = (x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1);
                bool inner = (x == 1 || y == 1 || x == TileSize - 2 || y == TileSize - 2);
                if (outer)
                {
                    SetJittered(pixels, x, y, 190, 200, 210, 6, rng);
                }
                else if (inner)
                {
                    // Slight highlight on the inner ring — sells the bevel.
                    SetJittered(pixels, x, y, 220, 228, 236, 5, rng);
                }
                else
                {
                    // Discarded by the shader; the alpha=0 makes it transparent.
                    SetPixel(pixels, x, y, 0, 0, 0, 0);
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

        private static void GenerateTorch(byte[] pixels)
        {
            // Pixel-art torch on a transparent background. The fragment shader
            // does an alpha-test discard at < 0.5 so any pixel left at alpha=0
            // disappears — no blending needed.
            //
            // Coordinate convention: y=0 is the bottom of the texture, which is
            // also the bottom of the rendered cross-sprite face. The wooden
            // shaft runs y=0..7 in the centre, the flame burns y=8..12.
            var rng = new Random(0x707C);

            // Base: fully transparent.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Wooden shaft — 2px wide, centred on x=7..8, height 0..7.
            for (int y = 0; y <= 7; y++)
            {
                // Slight per-row jitter so it doesn't read as a straight bar.
                SetJittered(pixels, 7, y, 138, 96, 54, 8, rng);
                SetJittered(pixels, 8, y, 118, 80, 42, 8, rng);
            }

            // Flame head — three short rows of warming colour. Hottest core
            // (white-yellow) sits at the centre, surrounded by orange, red,
            // and finally a faint outer halo so the silhouette is more than
            // a perfect rectangle.
            //
            // y=8 (base of flame): orange/red, slightly wider than shaft.
            SetPixel(pixels, 6, 8, 220, 100, 30, 220);
            SetPixel(pixels, 7, 8, 250, 200, 60, 255);
            SetPixel(pixels, 8, 8, 250, 200, 60, 255);
            SetPixel(pixels, 9, 8, 220, 100, 30, 220);
            // y=9: bright yellow centre, orange outer.
            SetPixel(pixels, 6, 9, 230, 130, 40, 200);
            SetPixel(pixels, 7, 9, 252, 232, 130, 255);
            SetPixel(pixels, 8, 9, 252, 232, 130, 255);
            SetPixel(pixels, 9, 9, 230, 130, 40, 200);
            // y=10: yellow tapering, red shoulders.
            SetPixel(pixels, 7, 10, 252, 220, 110, 255);
            SetPixel(pixels, 8, 10, 252, 220, 110, 255);
            SetPixel(pixels, 6, 10, 200, 70, 24, 160);
            SetPixel(pixels, 9, 10, 200, 70, 24, 160);
            // y=11: small orange tip.
            SetPixel(pixels, 7, 11, 240, 160, 50, 220);
            SetPixel(pixels, 8, 11, 240, 160, 50, 220);
            // y=12: faint outer glow above the flame.
            SetPixel(pixels, 7, 12, 220, 110, 30, 140);
            SetPixel(pixels, 8, 12, 220, 110, 30, 140);
        }

        // ----- Cross-sprite flora tiles -----
        // All five tiles share the same convention as Torch: y=0 is the bottom
        // of the rendered face, transparent background, the fragment shader
        // discards alpha < 0.5 so they live in the opaque pass with no blending.

        private static void GenerateDandelion(byte[] pixels)
        {
            // Tall green stem with a small yellow flower-head sitting on top
            // and a pair of leaf nubs partway up the stem. Alpha-era dandelions
            // were a 4-5 pixel cluster of yellow on a single-pixel stem.
            var rng = new Random(0xDA4D);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=1..10 at x=7
            for (int y = 1; y <= 10; y++)
                SetJittered(pixels, 7, y, 80, 130, 50, 8, rng);
            // Pair of leaf nubs midway.
            SetJittered(pixels, 6, 4, 70, 120, 44, 8, rng);
            SetJittered(pixels, 8, 6, 70, 120, 44, 8, rng);

            // Yellow flower head: 3x3 cluster centred at (7, 11), with the
            // outer corners slightly dimmer to soften the silhouette.
            SetPixel(pixels, 7, 11, 248, 220, 60, 255);
            SetPixel(pixels, 6, 11, 230, 196, 50, 255);
            SetPixel(pixels, 8, 11, 230, 196, 50, 255);
            SetPixel(pixels, 7, 10, 230, 196, 50, 255);
            SetPixel(pixels, 7, 12, 252, 232, 90, 255);
            SetPixel(pixels, 6, 12, 220, 180, 40, 220);
            SetPixel(pixels, 8, 12, 220, 180, 40, 220);
            SetPixel(pixels, 6, 10, 200, 160, 30, 180);
            SetPixel(pixels, 8, 10, 200, 160, 30, 180);
        }

        private static void GenerateRose(byte[] pixels)
        {
            // Single tall stem with red bloom petals on top. Rose in alpha was
            // visibly a vertical green stick with a red triangular flower.
            var rng = new Random(0x6053);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=0..9 at x=7
            for (int y = 0; y <= 9; y++)
                SetJittered(pixels, 7, y, 70, 120, 44, 8, rng);
            // Single leaf jutting out partway up.
            SetJittered(pixels, 8, 5, 86, 140, 56, 8, rng);
            SetJittered(pixels, 6, 3, 86, 140, 56, 8, rng);

            // Red bloom: a triangular cluster of petals above the stem.
            // Inner is brighter red, outer is darker maroon for shading.
            SetPixel(pixels, 7, 10, 220, 50, 50, 255);
            SetPixel(pixels, 6, 10, 180, 30, 30, 255);
            SetPixel(pixels, 8, 10, 180, 30, 30, 255);
            SetPixel(pixels, 7, 11, 240, 70, 70, 255);
            SetPixel(pixels, 6, 11, 220, 50, 50, 255);
            SetPixel(pixels, 8, 11, 220, 50, 50, 255);
            SetPixel(pixels, 5, 11, 160, 24, 24, 220);
            SetPixel(pixels, 9, 11, 160, 24, 24, 220);
            SetPixel(pixels, 7, 12, 252, 100, 90, 255);
            SetPixel(pixels, 6, 12, 220, 50, 50, 220);
            SetPixel(pixels, 8, 12, 220, 50, 50, 220);
            SetPixel(pixels, 7, 13, 200, 36, 36, 200);
            // Tiny yellow centre pollen pip.
            SetPixel(pixels, 7, 12, 252, 220, 80, 255);
        }

        private static void GenerateBrownMushroom(byte[] pixels)
        {
            // Squat stem with a dome cap. Brown is the "harmless" mushroom in
            // alpha, slightly smaller than the red one.
            var rng = new Random(0xB607);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem (cream) y=0..4 at x=7..8
            for (int y = 0; y <= 4; y++)
            {
                SetJittered(pixels, 7, y, 220, 210, 184, 6, rng);
                SetJittered(pixels, 8, y, 200, 190, 164, 6, rng);
            }

            // Cap: brown dome roughly 5 wide, 3 tall at y=5..7
            // Row y=5 (cap underside): darker brown shadow
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 5, 92, 64, 40, 6, rng);
            // Row y=6 (cap body): mid brown
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 6, 150, 108, 70, 8, rng);
            // Row y=7 (cap top): lighter highlight
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 7, 180, 140, 96, 8, rng);
            // Row y=8: tip
            SetJittered(pixels, 7, 8, 160, 120, 80, 6, rng);
            SetJittered(pixels, 8, 8, 160, 120, 80, 6, rng);
        }

        private static void GenerateRedMushroom(byte[] pixels)
        {
            // Same skeleton as the brown mushroom but with a taller red cap
            // and white spots on top — the "amanita" silhouette.
            var rng = new Random(0xED70);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=0..4 at x=7..8
            for (int y = 0; y <= 4; y++)
            {
                SetJittered(pixels, 7, y, 230, 220, 196, 5, rng);
                SetJittered(pixels, 8, y, 210, 200, 176, 5, rng);
            }

            // Cap underside (gills) y=5
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 5, 220, 200, 184, 5, rng);
            // Cap body y=6..8 in red
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 6, 200, 36, 36, 8, rng);
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 7, 220, 50, 50, 8, rng);
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 8, 200, 36, 36, 8, rng);
            SetJittered(pixels, 7, 9, 180, 24, 24, 6, rng);
            SetJittered(pixels, 8, 9, 180, 24, 24, 6, rng);

            // White spots scattered on the cap.
            SetPixel(pixels, 6, 7, 240, 240, 232, 255);
            SetPixel(pixels, 9, 7, 240, 240, 232, 255);
            SetPixel(pixels, 8, 6, 240, 240, 232, 255);
            SetPixel(pixels, 7, 8, 240, 240, 232, 255);
        }

    }
}
