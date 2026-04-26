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

            // Tools — 5 materials (wood = Planks, stone = Cobblestone,
            // iron = IronIngot, diamond = Diamond, gold = GoldIngot)
            // × 4 kinds (pickaxe / shovel / axe / sword). Material
            // → output mapping:
            (BlockType mat, BlockType pick, BlockType shovel, BlockType axe, BlockType sword)[] mats =
            {
                (BlockType.Planks,      BlockType.WoodPickaxe,    BlockType.WoodShovel,    BlockType.WoodAxe,    BlockType.WoodSword),
                (BlockType.Cobblestone, BlockType.StonePickaxe,   BlockType.StoneShovel,   BlockType.StoneAxe,   BlockType.StoneSword),
                (BlockType.IronIngot,   BlockType.IronPickaxe,    BlockType.IronShovel,    BlockType.IronAxe,    BlockType.IronSword),
                (BlockType.Diamond,     BlockType.DiamondPickaxe, BlockType.DiamondShovel, BlockType.DiamondAxe, BlockType.DiamondSword),
                (BlockType.GoldIngot,   BlockType.GoldPickaxe,    BlockType.GoldShovel,    BlockType.GoldAxe,    BlockType.GoldSword),
            };
            foreach (var m in mats)
            {
                list.Add(new ShapedRecipe(Pickaxe(m.mat),     new ItemStack(m.pick,   1)));
                list.Add(new ShapedRecipe(Shovel2Tall(m.mat), new ItemStack(m.shovel, 1)));
                list.Add(new ShapedRecipe(Axe(m.mat),         new ItemStack(m.axe,    1)));
                list.Add(new ShapedRecipe(Sword2Tall(m.mat),  new ItemStack(m.sword,  1)));
            }

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
