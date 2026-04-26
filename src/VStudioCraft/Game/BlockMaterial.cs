namespace VStudioCraft.Game
{
    // Block material category — used by the audio bank to pick which
    // break / place / step sample to play, and centralises a few other
    // material-specific behaviours that previously had to be re-derived
    // from BlockType at every call site.
    //
    // Roughly mirrors Minecraft Alpha's "step sound" enum (the only place
    // 1.1.2 actually expressed material categorically) — Stone, Wood,
    // Dirt (grass+dirt+clay), Sand (sand+gravel — both granular, similar
    // step), Glass, Cloth (wool+sponge), Leaves (foliage). Air maps to
    // None which lets PlayStep early-out cleanly when the block under
    // the player's feet is empty (jumping, standing on the edge).
    internal enum BlockMaterial : byte
    {
        None = 0,
        Stone,    // stone, cobble, ores, bricks, obsidian, furnace, metal blocks
        Wood,     // logs, planks, bookshelf, crafting table, chest
        Dirt,     // grass, dirt, clay
        Sand,     // sand, gravel
        Glass,    // glass
        Cloth,    // wool, sponge
        Leaves,   // leaves, foliage sprites
    }

    internal static class BlockMaterialData
    {
        // Map a BlockType to its audio/step material. Tools and items
        // map to None — the player never walks on them and you can't
        // break/place them.
        public static BlockMaterial For(BlockType t)
        {
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.Lava:
                case BlockType.FlowingLava:
                    return BlockMaterial.None;

                case BlockType.Grass:
                case BlockType.Dirt:
                case BlockType.Clay:
                    return BlockMaterial.Dirt;

                case BlockType.Sand:
                case BlockType.Gravel:
                    return BlockMaterial.Sand;

                case BlockType.Stone:
                case BlockType.Cobblestone:
                case BlockType.Bedrock:
                case BlockType.CoalOre:
                case BlockType.IronOre:
                case BlockType.GoldOre:
                case BlockType.DiamondOre:
                case BlockType.RedstoneOre:
                case BlockType.GoldBlock:
                case BlockType.IronBlock:
                case BlockType.DiamondBlock:
                case BlockType.Bricks:
                case BlockType.MossyCobblestone:
                case BlockType.Obsidian:
                case BlockType.Furnace:
                case BlockType.LitFurnace:
                    return BlockMaterial.Stone;

                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Bookshelf:
                case BlockType.CraftingTable:
                case BlockType.Chest:
                    return BlockMaterial.Wood;

                case BlockType.Leaves:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                    return BlockMaterial.Leaves;

                case BlockType.Glass:
                    return BlockMaterial.Glass;

                case BlockType.Wool:
                case BlockType.Sponge:
                    return BlockMaterial.Cloth;

                case BlockType.Tnt:
                    // Tnt has gunpowder feel — Alpha treated it as sand-ish
                    // for the step sound. Match.
                    return BlockMaterial.Sand;

                case BlockType.Torch:
                    // Torch isn't walked on (non-collidable) but breaking
                    // one should feel woody. Categorise as Wood so the
                    // break SFX picks the right bank.
                    return BlockMaterial.Wood;

                default:
                    // Tools, items, and any future BlockType the audio
                    // layer hasn't been taught about. Silent is the safe
                    // default — better than picking a random sample.
                    return BlockMaterial.None;
            }
        }
    }
}
