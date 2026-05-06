using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Crafting recipe registry. Two kinds of recipes:
    //
    //   * SHAPED: ingredients live at specific positions inside a tight
    //     bounding box (e.g. a pickaxe is "MMM / .S. / .S." — three
    //     materials across the top, sticks down the middle column).
    //     Shaped recipes match if their pattern can be placed anywhere
    //     inside the 3×3 input grid such that every cell of the pattern
    //     hits an exact-type match in the input AND every other input
    //     cell is empty. This lets a 2×2 recipe match in any of the
    //     four 2×2 sub-rectangles of a 3×3 grid (Alpha behaviour: a
    //     crafting-table recipe still works if the player puts the
    //     planks in the bottom-right 2×2 instead of the top-left).
    //
    //   * SHAPELESS: ingredients are an unordered multiset that the
    //     input grid's nonempty cells must equal exactly. Position
    //     doesn't matter. Used for "1 wood log → 4 planks" and for
    //     dye-mixing-style recipes that aren't position-sensitive.
    //
    // Tools and item ingredients in shaped patterns are matched by
    // ItemStack.Type only — a fresh stick and a stick from a stack of 64
    // both match a "Stick" pattern cell. Counts are consumed at craft
    // time (1 per craft per ingredient cell).
    //
    // The match function is allocation-free for the grid scan (it walks
    // a small fixed-size pattern); a single List<ItemStack> is created
    // for the shapeless multiset comparison. Hot enough to matter
    // because the renderer rematches every UI frame to keep the output
    // slot live.
    internal static class CraftingRecipes
    {
        // A shaped recipe carries a (rows × cols) pattern of BlockTypes
        // (Air = empty cell) and the output ItemStack. Pattern dimensions
        // must be 1..3 in each axis (anything bigger doesn't fit the
        // grid).
        public struct ShapedRecipe
        {
            public BlockType[,] Pattern; // [row, col]
            public ItemStack Output;
            public ShapedRecipe(BlockType[,] pattern, ItemStack output)
            {
                Pattern = pattern;
                Output = output;
            }
            public int Rows => Pattern.GetLength(0);
            public int Cols => Pattern.GetLength(1);
        }

        // A shapeless recipe is a multiset of (BlockType, count) pairs.
        // Order in the input grid is ignored.
        public struct ShapelessRecipe
        {
            public BlockType[] Ingredients; // flat list, repeats allowed
            public ItemStack Output;
            public ShapelessRecipe(BlockType[] ingredients, ItemStack output)
            {
                Ingredients = ingredients;
                Output = output;
            }
        }

        // The starter recipe table. Closes the canonical Alpha early-game
        // loop: log → planks → sticks → wood pickaxe → cobble → stone
        // pickaxe → iron → iron pickaxe → diamond. Plus a few quality-
        // of-life adds (bowl, bricks, crafting-table-from-planks so
        // players can build their own without the creative catalog).
        public static readonly List<ShapedRecipe> Shaped = BuildShaped();
        public static readonly List<ShapelessRecipe> Shapeless = BuildShapeless();

        private static List<ShapelessRecipe> BuildShapeless()
        {
            var list = new List<ShapelessRecipe>();
            // 1 wood log → 4 planks. Position-independent because the
            // player typically just drops the log into the first empty
            // cell. Alpha rule: 1 log at any position, output 4 planks.
            list.Add(new ShapelessRecipe(
                new[] { BlockType.WoodLog },
                new ItemStack(BlockType.Planks, 4)));
            // Tier 4 #14 — Mushroom Stew. Bowl + brown mushroom + red
            // mushroom in any positions → 1 mushroom stew. The bowl
            // is ALSO returned to the player when the stew is eaten
            // (handled in the food-eat path), so the recipe consumes
            // the bowl as input and the eat path mints a fresh one.
            list.Add(new ShapelessRecipe(
                new[] { BlockType.Bowl, BlockType.BrownMushroom, BlockType.RedMushroom },
                new ItemStack(BlockType.MushroomStew, 1)));
            // Tier 4 #26 — Book. Three paper in any positions → 1 book.
            // Alpha treats this as shapeless (the player can drop the
            // three paper sheets anywhere in the grid). Output is a
            // single book — the leather-bound combination of those
            // pages. Used downstream for Bookshelf (3 books per shelf).
            list.Add(new ShapelessRecipe(
                new[] { BlockType.Paper, BlockType.Paper, BlockType.Paper },
                new ItemStack(BlockType.Book, 1)));
            // Tier 8 #50 — 1 Bone → 3 Bone Meal. Shapeless (the
            // player drops a single bone anywhere in the grid).
            // Canonical Alpha: bone meal stacks to 64, so the
            // single-bone craft gives 3 — three crafts net 9 bone
            // meal, enough to fully grow a sapling and have some
            // left for wheat.
            list.Add(new ShapelessRecipe(
                new[] { BlockType.Bone },
                new ItemStack(BlockType.BoneMeal, 3)));
            return list;
        }

        // Shaped helpers — keep recipe construction readable. P=planks,
        // S=stick, etc., with X for empty cells. C# multidimensional
        // array literals are noisy, so we route through these factories.
        private static BlockType[,] Tool3Wide(BlockType mat)
            => new BlockType[,]
            {
                { mat,             mat,         mat },
                { BlockType.Air,   BlockType.Stick, BlockType.Air },
                { BlockType.Air,   BlockType.Stick, BlockType.Air },
            };

        private static BlockType[,] Sword2Tall(BlockType mat)
            => new BlockType[,]
            {
                { mat },
                { mat },
                { BlockType.Stick },
            };

        private static BlockType[,] Shovel2Tall(BlockType mat)
            => new BlockType[,]
            {
                { mat },
                { BlockType.Stick },
                { BlockType.Stick },
            };

        // Axe is L-shaped: two materials in the top-left corner plus one
        // below, with sticks down the middle column.
        private static BlockType[,] Axe(BlockType mat)
            => new BlockType[,]
            {
                { mat,           mat },
                { mat,           BlockType.Stick },
                { BlockType.Air, BlockType.Stick },
            };

        // Pickaxe: 3-wide material row over 2 sticks down the middle.
        private static BlockType[,] Pickaxe(BlockType mat) => Tool3Wide(mat);

        // Tier 4 #14 — Hoe: same 2-row L-shape as the axe but mirrored
        // so the materials sit in the top row across cols 0..1, and the
        // sticks run down the right column. Alpha pattern is "MM. /
        // .S. / .S." — two materials top-left, sticks down the centre
        // column. Two materials (not three) is what distinguishes a
        // hoe from an axe.
        private static BlockType[,] Hoe(BlockType mat)
            => new BlockType[,]
            {
                { mat,           mat },
                { BlockType.Air, BlockType.Stick },
                { BlockType.Air, BlockType.Stick },
            };

        // Tier 4 #19 — Armor pattern helpers. Four shapes shared across
        // every craftable material (leather/iron/diamond/gold —
        // chainmail has no recipe, see HostileMobs for the mob-drop
        // path). Patterns are stored row-major with the same
        // "Alpha cells, top-down" orientation the other tool helpers
        // use; a cell of BlockType.Air means "must be empty in the
        // input grid for this recipe to match". The matcher accepts
        // any tight bounding box that fits inside the 3×3 input
        // (Alpha behaviour: the player can place the materials
        // anywhere in the grid as long as the relative layout is
        // preserved).
        //
        // Helmet — top-row + flanks ("MMM / M.M"). Two-row pattern:
        // the third row is all empty so the matcher's tight-box
        // logic doesn't require a leading empty row.
        private static BlockType[,] ArmorHelmet(BlockType mat)
            => new BlockType[,]
            {
                { mat, mat,           mat },
                { mat, BlockType.Air, mat },
            };
        // Chestplate — top flanks + full bottom two rows ("M.M / MMM
        // / MMM"). Three-row 3-col pattern.
        private static BlockType[,] ArmorChestplate(BlockType mat)
            => new BlockType[,]
            {
                { mat, BlockType.Air, mat },
                { mat, mat,           mat },
                { mat, mat,           mat },
            };
        // Leggings — full top + leg columns ("MMM / M.M / M.M").
        // Three-row 3-col pattern.
        private static BlockType[,] ArmorLeggings(BlockType mat)
            => new BlockType[,]
            {
                { mat, mat,           mat },
                { mat, BlockType.Air, mat },
                { mat, BlockType.Air, mat },
            };
        // Boots — two flank columns over two rows ("M.M / M.M").
        // Two-row 3-col pattern.
        private static BlockType[,] ArmorBoots(BlockType mat)
            => new BlockType[,]
            {
                { mat, BlockType.Air, mat },
                { mat, BlockType.Air, mat },
            };

        private static List<ShapedRecipe> BuildShaped()
        {
            var list = new List<ShapedRecipe>();

            // Sticks: two planks vertical, anywhere in the grid → 4 sticks.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks },
                    { BlockType.Planks },
                },
                new ItemStack(BlockType.Stick, 4)));

            // Tier 8 #46 — Ladder. 7 sticks in an H pattern (rails on
            // cols 0 + 2, rungs on col 1 rows 0 + 1 + 2 — actually
            // canonical Alpha is "S.S / SSS / S.S", 7 sticks total) →
            // 3 ladders. Stick is the only ingredient so the recipe
            // bypasses the planks-vs-other-wood debate that comes with
            // multi-wood-type variants in later versions.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Stick, BlockType.Air,   BlockType.Stick },
                    { BlockType.Stick, BlockType.Stick, BlockType.Stick },
                    { BlockType.Stick, BlockType.Air,   BlockType.Stick },
                },
                new ItemStack(BlockType.Ladder, 3)));

            // Tier 8 #46 part 2 — Wooden Fence. 6 sticks in two
            // horizontal rows of 3 → 2 fences. Canonical Alpha 1.0.14
            // recipe ("SSS / SSS"). Output is 2 — net cost 3 sticks
            // per fence, so a single 4-plank → 4-stick craft fully
            // pays for one fence with sticks left over.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Stick, BlockType.Stick, BlockType.Stick },
                    { BlockType.Stick, BlockType.Stick, BlockType.Stick },
                },
                new ItemStack(BlockType.Fence, 2)));

            // Tier 8 #45 V1 — Slab recipes. Three of the source
            // material in a horizontal row → 6 slabs. Canonical
            // Alpha 1.0.5_01 recipe applied to each material; the
            // 6-slab output makes a 3-stone craft economically
            // identical to splitting the source into two cubes
            // worth of slabs (3 stone → 6 half-blocks = 3 cubes
            // worth of material — slabs cost the same as their
            // source).
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Stone, BlockType.Stone, BlockType.Stone },
                },
                new ItemStack(BlockType.StoneSlab, 6)));
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Cobblestone, BlockType.Cobblestone, BlockType.Cobblestone },
                },
                new ItemStack(BlockType.CobblestoneSlab, 6)));
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Bricks, BlockType.Bricks, BlockType.Bricks },
                },
                new ItemStack(BlockType.BrickSlab, 6)));
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.WoodSlab, 6)));

            // Tier 8 #45 V2 — Stair recipes. 6 of the source material
            // arranged in a 3-step staircase (canonical Alpha 1.0.5_01
            // pattern: "S.. / SS. / SSS") → 4 stairs. Output of 4
            // means a single stair costs 1.5 source blocks — slightly
            // material-efficient compared to slabs (1 cube per 2
            // slabs), reflecting the more complex L-shape geometry.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Air,    BlockType.Air },
                    { BlockType.Planks, BlockType.Planks, BlockType.Air },
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.WoodStairs, 4)));
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Cobblestone, BlockType.Air,         BlockType.Air },
                    { BlockType.Cobblestone, BlockType.Cobblestone, BlockType.Air },
                    { BlockType.Cobblestone, BlockType.Cobblestone, BlockType.Cobblestone },
                },
                new ItemStack(BlockType.CobblestoneStairs, 4)));

            // Tier 8 #49 V1 — Dispenser. 7 cobblestone (U-shape ring),
            // 1 bow in the centre, 1 redstone dust at the bottom-
            // centre. Canonical Alpha 1.0.16 recipe. Output is 1
            // dispenser. The bow is consumed (Alpha didn't return
            // it), and the dust is the redstone-input that wires
            // the dispenser into the power network.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Cobblestone,  BlockType.Cobblestone,  BlockType.Cobblestone },
                    { BlockType.Cobblestone,  BlockType.Bow,          BlockType.Cobblestone },
                    { BlockType.Cobblestone,  BlockType.RedstoneDust, BlockType.Cobblestone },
                },
                new ItemStack(BlockType.Dispenser, 1)));

            // Tier 8 #51 — Glowstone block from 4 dust in a 2×2.
            // Canonical Alpha 1.1.2_01 recipe; not actually craftable
            // in vanilla Alpha (glowstone dust drops were nether-only
            // and the recipe is a Beta-era addition), but pairing it
            // with the dust drop gives the player a cycle (place +
            // mine creative-spawned glowstone → 2..4 dust → re-craft
            // a block) without needing the nether to ship first.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.GlowstoneDust, BlockType.GlowstoneDust },
                    { BlockType.GlowstoneDust, BlockType.GlowstoneDust },
                },
                new ItemStack(BlockType.Glowstone, 1)));

            // Crafting Table: 2×2 planks → 1 crafting table.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.CraftingTable, 1)));

            // Bowls: three planks across the bottom in a V shape (Alpha
            // is "P.P / .P." — two planks at the corners, one beneath).
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Air,    BlockType.Planks },
                    { BlockType.Air,    BlockType.Planks, BlockType.Air    },
                },
                new ItemStack(BlockType.Bowl, 4)));

            // Brick block: 4 clay bricks → 1 brick block.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.ClayBrick, BlockType.ClayBrick },
                    { BlockType.ClayBrick, BlockType.ClayBrick },
                },
                new ItemStack(BlockType.Bricks, 1)));

            // Tier 8 #51 V13 — Nether Brick block: 4 nether brick
            // items in a 2×2 → 1 nether brick block. Same shape as
            // the clay-brick recipe above. Each nether brick item is
            // smelted from netherrack in a furnace, so a player
            // visiting the Nether can mine netherrack, smelt it back
            // home, and craft the bricks back into a usable block
            // (matching canonical Alpha-era 1.1.2 build flow).
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.NetherBrickItem, BlockType.NetherBrickItem },
                    { BlockType.NetherBrickItem, BlockType.NetherBrickItem },
                },
                new ItemStack(BlockType.NetherBrick, 1)));

            // Tier 9 #54 V1 — Boat: 5 planks in a U shape.
            // Canonical Alpha 1.1.2_01 pattern is a hollow trough:
            //     P . P
            //     P P P
            // (3-wide × 2-tall, top-middle empty). Yields 1 Boat item.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Air,    BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.Boat, 1)));

            // Tier 9 #54 V2 — Rails: 6 iron ingots + 1 stick in the
            // canonical Alpha 1.1.2_01 layout:
            //     I . I
            //     I S I
            //     I . I
            // Yields 16 rails per craft.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.IronIngot, BlockType.Air,   BlockType.IronIngot },
                    { BlockType.IronIngot, BlockType.Stick, BlockType.IronIngot },
                    { BlockType.IronIngot, BlockType.Air,   BlockType.IronIngot },
                },
                new ItemStack(BlockType.Rail, 16)));

            // Tier 9 #54 V2 — Minecart: 5 iron ingots in a U shape.
            //     I . I
            //     I I I
            // Yields 1 Minecart.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.IronIngot, BlockType.Air,       BlockType.IronIngot },
                    { BlockType.IronIngot, BlockType.IronIngot, BlockType.IronIngot },
                },
                new ItemStack(BlockType.Minecart, 1)));

            // Furnace: 8 cobblestone in a U-shape ringing an empty middle
            // cell. Alpha pattern is "CCC / C.C / CCC" — the hollow center
            // is what makes it a smelter rather than just a stone block.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Cobblestone, BlockType.Cobblestone, BlockType.Cobblestone },
                    { BlockType.Cobblestone, BlockType.Air,         BlockType.Cobblestone },
                    { BlockType.Cobblestone, BlockType.Cobblestone, BlockType.Cobblestone },
                },
                new ItemStack(BlockType.Furnace, 1)));

            // Chest: 8 planks ringing an empty middle cell — same U/box
            // pattern as the furnace, just planks instead of cobble.
            // Alpha pattern is "PPP / P.P / PPP".
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                    { BlockType.Planks, BlockType.Air,    BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.Chest, 1)));

            // Tier 8 #44 — Sign. 6 planks (top two rows) + 1 stick
            // (centre of bottom row) → 1 SignItem. Alpha 1.1.2_01
            // yields 1 sign per craft (modern Minecraft yields 3);
            // we go with the Alpha-faithful 1-yield matching the
            // project's stated target version. Pattern is:
            //
            //   PPP
            //   PPP
            //   .S.
            //
            // Crafting a sign consumes 7 inventory slots' worth of
            // ingredients per output — by far the most-expensive
            // single-output recipe in Alpha. Decorative-only blocks
            // were considered "luxury" in the early-Alpha economy.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                    { BlockType.Air,    BlockType.Stick,  BlockType.Air },
                },
                new ItemStack(BlockType.SignItem, 1)));

            // Tools — 5 materials (wood = Planks, stone = Cobblestone,
            // iron = IronIngot, diamond = Diamond, gold = GoldIngot)
            // × 4 kinds (pickaxe / shovel / axe / sword). Material
            // → output mapping:
            (BlockType mat, BlockType pick, BlockType shovel, BlockType axe, BlockType sword, BlockType hoe)[] mats =
            {
                (BlockType.Planks,      BlockType.WoodPickaxe,    BlockType.WoodShovel,    BlockType.WoodAxe,    BlockType.WoodSword,    BlockType.WoodHoe),
                (BlockType.Cobblestone, BlockType.StonePickaxe,   BlockType.StoneShovel,   BlockType.StoneAxe,   BlockType.StoneSword,   BlockType.StoneHoe),
                (BlockType.IronIngot,   BlockType.IronPickaxe,    BlockType.IronShovel,    BlockType.IronAxe,    BlockType.IronSword,    BlockType.IronHoe),
                (BlockType.Diamond,     BlockType.DiamondPickaxe, BlockType.DiamondShovel, BlockType.DiamondAxe, BlockType.DiamondSword, BlockType.DiamondHoe),
                (BlockType.GoldIngot,   BlockType.GoldPickaxe,    BlockType.GoldShovel,    BlockType.GoldAxe,    BlockType.GoldSword,    BlockType.GoldHoe),
            };
            foreach (var m in mats)
            {
                list.Add(new ShapedRecipe(Pickaxe(m.mat),     new ItemStack(m.pick,   1)));
                list.Add(new ShapedRecipe(Shovel2Tall(m.mat), new ItemStack(m.shovel, 1)));
                list.Add(new ShapedRecipe(Axe(m.mat),         new ItemStack(m.axe,    1)));
                list.Add(new ShapedRecipe(Sword2Tall(m.mat),  new ItemStack(m.sword,  1)));
                // Tier 4 #14 — hoes follow the same per-material loop.
                list.Add(new ShapedRecipe(Hoe(m.mat),         new ItemStack(m.hoe,    1)));
            }

            // Tier 4 #14 — Bread. Three wheat in a horizontal row → 1
            // bread. Alpha pattern is "WWW" (any horizontal 3-wide
            // strip in the grid). Output is one loaf; restoration tier
            // is wired in the food-eat path (5 HP/hunger per loaf).
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.WheatItem, BlockType.WheatItem, BlockType.WheatItem },
                },
                new ItemStack(BlockType.Bread, 1)));

            // Tier 4 #26 — Paper. Three sugar cane (the harvested
            // SugarCaneItem, not the in-world block) in a horizontal
            // row → 3 paper. Alpha pattern is "CCC" with a yield of 3
            // (one paper per cane). Same shape as bread but distinct
            // ingredient so the matcher has no ambiguity — recipes are
            // disambiguated by ItemStack.Type.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.SugarCaneItem, BlockType.SugarCaneItem, BlockType.SugarCaneItem },
                },
                new ItemStack(BlockType.Paper, 3)));

            // Tier 4 #26 — Bookshelf. Three planks across the top, three
            // books across the middle, three planks across the bottom
            // → 1 bookshelf block. Alpha pattern is "PPP / BBB / PPP".
            // Bookshelf already exists as an in-world block (id 47);
            // this just wires the recipe so players can craft it
            // instead of relying on the creative catalog.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                    { BlockType.Book,   BlockType.Book,   BlockType.Book },
                    { BlockType.Planks, BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.Bookshelf, 1)));

            // Tier 4 #16 — Wooden Door. 6 planks in a 3×2 column-pair —
            // Alpha pattern is "PP. / PP. / PP." (two columns of three
            // planks each, sat against the LEFT edge of the grid).
            // Output: 1 wooden door item. The matcher anchors the
            // pattern to (rOff,cOff) so the recipe is technically
            // ambiguous with right-edge placement; that's intentional
            // and matches Alpha — the player can also drop the planks
            // into cols 1-2 and still get the door. Hinge defaults
            // are picked at place-time, not at craft-time.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks },
                },
                new ItemStack(BlockType.WoodDoorItem, 1)));

            // Tier 4 #16 — Iron Door. Same 3×2 pattern as the wooden
            // door but ingots instead of planks → 1 iron door item.
            // Iron doors don't open by RMB (redstone-only, gated for
            // Tier 8); the recipe still mints a fully-functional placed
            // block — players just need a redstone trigger to open it.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.IronIngot, BlockType.IronIngot },
                    { BlockType.IronIngot, BlockType.IronIngot },
                    { BlockType.IronIngot, BlockType.IronIngot },
                },
                new ItemStack(BlockType.IronDoorItem, 1)));

            // Torches need coal + stick — but Torch as an item drops
            // when we have a coal-on-stick recipe (Alpha used charcoal
            // too; we only have coal yet, so the recipe is just coal +
            // stick). Output: 4 torches.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Coal },
                    { BlockType.Stick },
                },
                new ItemStack(BlockType.Torch, 4)));

            // Tier 4 #17 — Bow. Alpha pattern is sticks down the LEFT
            // and centre columns plus string down the RIGHT column,
            // arranged in a "bent bow" silhouette:
            //   . S B
            //   S . B
            //   . S B
            // Three sticks form the spine, three strings form the
            // bowstring. Output: 1 bow.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Air,   BlockType.Stick, BlockType.String },
                    { BlockType.Stick, BlockType.Air,   BlockType.String },
                    { BlockType.Air,   BlockType.Stick, BlockType.String },
                },
                new ItemStack(BlockType.Bow, 1)));

            // Tier 4 #17 — Arrow. Alpha pattern is flint top, stick
            // middle, feather bottom in a single vertical column:
            //   F
            //   S
            //   E
            // Output: 4 arrows. The matcher anchors the 1-wide
            // pattern to any of the three columns.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Flint },
                    { BlockType.Stick },
                    { BlockType.Feather },
                },
                new ItemStack(BlockType.Arrow, 4)));

            // Tier 4 #17 — Flint and Steel. Alpha pattern is iron
            // ingot top-left, flint to its lower-right (the diagonal
            // strike pose):
            //   I .
            //   . F
            // Output: 1 flint and steel. The placement / fire / TNT
            // hooks are gated on later tiers (see TryInteract no-op
            // comment); the recipe still produces a usable item slot
            // so the player can gather the ingredients early.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.IronIngot, BlockType.Air },
                    { BlockType.Air,       BlockType.Flint },
                },
                new ItemStack(BlockType.FlintAndSteel, 1)));

            // Tier 4 #15 — Empty Bucket. Alpha pattern is three iron
            // ingots arranged in a V (corners + bottom-centre):
            //   I . I
            //   . I .
            // Output: 1 empty bucket. Filled buckets aren't crafted
            // — RMB on a fluid source / cow turns this empty bucket
            // into the matching filled variant in TryInteract.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.IronIngot, BlockType.Air,       BlockType.IronIngot },
                    { BlockType.Air,       BlockType.IronIngot, BlockType.Air       },
                },
                new ItemStack(BlockType.BucketEmpty, 1)));

            // Tier 4 #23 — Fishing Rod. Alpha pattern is sticks
            // running diagonally from bottom-left to top-right with
            // string down the right column:
            //   . . S
            //   . S T
            //   S . T
            // Where S=Stick, T=String. Three sticks form the rod
            // along the diagonal, two strings dangle down the right
            // edge as the line. Output: 1 fishing rod.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Air,   BlockType.Air,   BlockType.Stick  },
                    { BlockType.Air,   BlockType.Stick, BlockType.String },
                    { BlockType.Stick, BlockType.Air,   BlockType.String },
                },
                new ItemStack(BlockType.FishingRod, 1)));

            // Tier 4 #24 — Painting. Alpha pattern is 8 sticks
            // around a single wool block in the centre — a wooden
            // frame stretched over a fabric canvas:
            //   S S S
            //   S W S
            //   S S S
            // Output: 1 painting item. Held item RMB on a wall
            // installs the Painting entity (variant chosen at
            // random; see GameRenderer.TryInteract).
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Stick, BlockType.Stick, BlockType.Stick },
                    { BlockType.Stick, BlockType.Wool,  BlockType.Stick },
                    { BlockType.Stick, BlockType.Stick, BlockType.Stick },
                },
                new ItemStack(BlockType.Painting, 1)));

            // Tier 4 #25 — Jukebox (Alpha id 84). Pattern is 8 planks
            // around 1 diamond — a wooden cabinet with a precious-gem
            // turntable spindle in the middle:
            //   P P P
            //   P D P
            //   P P P
            // Output: 1 jukebox block. Music discs themselves are
            // dungeon-loot only in Alpha 1.1.2_01 (no recipe), so the
            // discs don't appear here — they ship as creative-only
            // items until dungeon generation lands in Tier 6 #32.
            list.Add(new ShapedRecipe(
                new BlockType[,]
                {
                    { BlockType.Planks, BlockType.Planks,  BlockType.Planks },
                    { BlockType.Planks, BlockType.Diamond, BlockType.Planks },
                    { BlockType.Planks, BlockType.Planks,  BlockType.Planks },
                },
                new ItemStack(BlockType.Jukebox, 1)));

            // Tier 4 #19 — Armor recipes. 16 total (4 craftable
            // materials × 4 slots — chainmail has NO recipe and is
            // mob-drop only). Alpha 1.1.2_01 patterns:
            //
            //   Helmet:     Chestplate:   Leggings:    Boots:
            //   M M M       M . M         M M M        M . M
            //   M . M       M M M         M . M        M . M
            //   . . .       M M M         M . M        . . .
            //
            // M = material (Leather BlockType, IronIngot, Diamond,
            // GoldIngot). The four shape helpers below build the
            // BlockType[,] in pattern-row-major order; the per-
            // material loop drops the actual recipes into the table.
            (BlockType mat, BlockType helm, BlockType chest, BlockType leg, BlockType boot)[] armorMats =
            {
                (BlockType.Leather,   BlockType.LeatherHelmet, BlockType.LeatherChestplate, BlockType.LeatherLeggings, BlockType.LeatherBoots),
                (BlockType.IronIngot, BlockType.IronHelmet,    BlockType.IronChestplate,    BlockType.IronLeggings,    BlockType.IronBoots),
                (BlockType.Diamond,   BlockType.DiamondHelmet, BlockType.DiamondChestplate, BlockType.DiamondLeggings, BlockType.DiamondBoots),
                (BlockType.GoldIngot, BlockType.GoldHelmet,    BlockType.GoldChestplate,    BlockType.GoldLeggings,    BlockType.GoldBoots),
            };
            foreach (var a in armorMats)
            {
                list.Add(new ShapedRecipe(ArmorHelmet(a.mat),     new ItemStack(a.helm,  1)));
                list.Add(new ShapedRecipe(ArmorChestplate(a.mat), new ItemStack(a.chest, 1)));
                list.Add(new ShapedRecipe(ArmorLeggings(a.mat),   new ItemStack(a.leg,   1)));
                list.Add(new ShapedRecipe(ArmorBoots(a.mat),      new ItemStack(a.boot,  1)));
            }

            // Tier 4 #22 — Compass. Alpha pattern is four iron ingots
            // in a + with a single redstone dust at the centre:
            //   . I .
            //   I R I
            //   . I .
            // Wired live now that RedstoneDust exists as an ItemType
            // (Tier 8 #42). Output: 1 Compass.
            list.Add(new ShapedRecipe(
                new BlockType[3, 3]
                {
                    { BlockType.Air,       BlockType.IronIngot,   BlockType.Air      },
                    { BlockType.IronIngot, BlockType.RedstoneDust, BlockType.IronIngot },
                    { BlockType.Air,       BlockType.IronIngot,   BlockType.Air      },
                },
                new ItemStack(BlockType.Compass, 1)));

            // Tier 10 #51 — Clock. Same plus pattern as the compass
            // but with gold ingots in place of iron.
            list.Add(new ShapedRecipe(
                new BlockType[3, 3]
                {
                    { BlockType.Air,       BlockType.GoldIngot,   BlockType.Air      },
                    { BlockType.GoldIngot, BlockType.RedstoneDust, BlockType.GoldIngot },
                    { BlockType.Air,       BlockType.GoldIngot,   BlockType.Air      },
                },
                new ItemStack(BlockType.Clock, 1)));

            // Tier 10 #51 — Map. 8 paper around 1 compass = 1 map.
            // Canonical Alpha 1.1.2 recipe.
            list.Add(new ShapedRecipe(
                new BlockType[3, 3]
                {
                    { BlockType.Paper, BlockType.Paper,   BlockType.Paper },
                    { BlockType.Paper, BlockType.Compass, BlockType.Paper },
                    { BlockType.Paper, BlockType.Paper,   BlockType.Paper },
                },
                new ItemStack(BlockType.Map, 1)));

            return list;
        }

        // Match the input 3×3 grid against the recipe table. The grid is
        // passed as a flat 9-element array (row-major: index = row*3 + col).
        // Returns the output stack (Type = Air if no match).
        public static ItemStack Match(ItemStack[] grid)
        {
            if (grid == null || grid.Length != 9) return ItemStack.Empty;

            // Try shaped recipes first — they're more specific (a "2 planks
            // vertical" pattern would also satisfy a hypothetical shapeless
            // "1 plank, 1 plank" recipe, and the shaped output is the
            // intended one).
            foreach (var r in Shaped)
            {
                if (TryMatchShaped(grid, r, out var output))
                    return output;
            }
            foreach (var r in Shapeless)
            {
                if (TryMatchShapeless(grid, r, out var output))
                    return output;
            }
            return ItemStack.Empty;
        }

        // Shaped match: the recipe pattern fits inside the 3×3 grid at
        // some (rowOffset, colOffset). Every pattern cell must hit a
        // matching grid cell; every grid cell OUTSIDE the pattern's
        // footprint must be empty. Matches Alpha — you can put a 2×2
        // recipe in any of the four 2×2 corners of the 3×3 grid, but
        // not have stray ingredients elsewhere.
        private static bool TryMatchShaped(ItemStack[] grid, ShapedRecipe r, out ItemStack output)
        {
            output = ItemStack.Empty;
            int pr = r.Rows, pc = r.Cols;
            if (pr > 3 || pc > 3) return false;

            for (int rOff = 0; rOff <= 3 - pr; rOff++)
            for (int cOff = 0; cOff <= 3 - pc; cOff++)
            {
                if (PatternMatchesAt(grid, r.Pattern, rOff, cOff))
                {
                    output = r.Output;
                    return true;
                }
            }
            return false;
        }

        private static bool PatternMatchesAt(ItemStack[] grid, BlockType[,] pattern, int rOff, int cOff)
        {
            int pr = pattern.GetLength(0);
            int pc = pattern.GetLength(1);
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                ItemStack cell = grid[row * 3 + col];
                bool insideFootprint = (row >= rOff && row < rOff + pr
                                     && col >= cOff && col < cOff + pc);
                if (insideFootprint)
                {
                    BlockType expected = pattern[row - rOff, col - cOff];
                    if (expected == BlockType.Air)
                    {
                        if (!cell.IsEmpty) return false;
                    }
                    else
                    {
                        if (cell.IsEmpty || cell.Type != expected) return false;
                    }
                }
                else
                {
                    if (!cell.IsEmpty) return false;
                }
            }
            return true;
        }

        // Shapeless: build a multiset of grid contents, compare to recipe.
        // Two grids match the recipe if they contain exactly the same
        // BlockTypes (count-by-count). Position is ignored.
        private static bool TryMatchShapeless(ItemStack[] grid, ShapelessRecipe r, out ItemStack output)
        {
            output = ItemStack.Empty;
            // Count nonempty cells; early-out if length differs.
            int nonempty = 0;
            for (int i = 0; i < grid.Length; i++)
                if (!grid[i].IsEmpty) nonempty++;
            if (nonempty != r.Ingredients.Length) return false;

            // Multiset compare. r.Ingredients is small (typically 1–4
            // entries) so the O(n²) walk is fine.
            var used = new bool[r.Ingredients.Length];
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i].IsEmpty) continue;
                int hit = -1;
                for (int j = 0; j < r.Ingredients.Length; j++)
                {
                    if (used[j]) continue;
                    if (r.Ingredients[j] == grid[i].Type) { hit = j; break; }
                }
                if (hit < 0) return false;
                used[hit] = true;
            }
            output = r.Output;
            return true;
        }

        // Decrement one of each input cell after a successful craft.
        // Caller has already pulled the output stack into the cursor /
        // result slot. The grid cells map 1:1 to recipe ingredient
        // cells: every NON-EMPTY cell loses exactly 1 count, regardless
        // of whether the recipe was shaped or shapeless. (Shapeless
        // recipes also pass through one-of-each because the multiset
        // matched 1:1.) Cells that hit zero clear to Air.
        public static void ConsumeOne(ItemStack[] grid)
        {
            if (grid == null) return;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i].IsEmpty) continue;
                int newCount = grid[i].Count - 1;
                if (newCount <= 0)
                    grid[i] = ItemStack.Empty;
                else
                    grid[i] = new ItemStack(grid[i].Type, newCount, grid[i].Durability);
            }
        }
    }
}
