using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Smelting recipe + fuel registry for the Furnace tile entity.
    //
    // Two tables live here:
    //
    //   * Smelt: input BlockType → output ItemStack. A furnace with a
    //     non-empty input slot whose Type is in this map and a fuel slot
    //     burning will, after CookTimeTicks of continuous burn, consume
    //     1 input and produce the mapped output stack into the result
    //     slot (merging counts if the result already holds the same
    //     output kind, doing nothing if the result holds a different
    //     kind or is full at MaxStackSize).
    //
    //   * Fuel: BlockType → burn-time-in-ticks. Anything in this map is
    //     a valid fuel; one item is consumed from the fuel slot each
    //     time burnTime hits zero, refilling burnTime to the mapped
    //     value. Fuel ticks down every game tick the furnace is
    //     "active", which is independent of whether smelting is making
    //     progress (an empty input slot still burns fuel — Alpha
    //     behaviour, the player loses the fuel if they leave a furnace
    //     lit with no input).
    //
    // CookTimeTicks is fixed at 200 (= 10 seconds at 20Hz) for every
    // recipe in Alpha; we mirror that rather than per-recipe-tunable.
    //
    // Burn times are calibrated to Alpha's seconds-per-fuel:
    //   coal       = 80s = 1600 ticks
    //   planks     = 15s = 300  ticks
    //   sapling    = 5s  = 100  ticks  (Alpha "stick" tier)
    //   stick      = 5s  = 100  ticks
    //   wood log   = 15s = 300  ticks  (same as planks; Alpha treats logs as wood-tier fuel)
    //   crafting   = 15s = 300  ticks  (any wooden block)
    //   lava bucket= not yet implemented (no buckets/fluids-as-items yet)
    //
    // The maps are read-only after first init; the furnace tile entity
    // uses TryGetValue both ways. Keeping these as Dictionary keeps the
    // furnace tick allocation-free.
    internal static class FurnaceRecipes
    {
        // Universal cook time for any smelting recipe — Alpha 1.1.2_01
        // doesn't expose per-recipe cook times. 20 ticks/sec × 10 s.
        public const int CookTimeTicks = 200;

        // Smelting transformations. Inputs are block ids the player can
        // realistically funnel into the input slot:
        //   IronOre   → IronIngot
        //   GoldOre   → GoldIngot
        //   Sand      → Glass
        //   Cobblestone → Stone   (Alpha lets you re-melt cobble to stone)
        //   ClayBall  → ClayBrick (the smelted brick item, NOT the brick block)
        //   RawPorkchop → CookedPorkchop (Tier 3 #9 — pig drops + cooking)
        //
        // (Alpha also smelts raw fish → cooked fish, but we don't have
        // fishing yet.)
        public static readonly Dictionary<BlockType, ItemStack> Smelt = BuildSmelt();

        private static Dictionary<BlockType, ItemStack> BuildSmelt()
        {
            return new Dictionary<BlockType, ItemStack>
            {
                { BlockType.IronOre,     new ItemStack(BlockType.IronIngot,      1) },
                { BlockType.GoldOre,     new ItemStack(BlockType.GoldIngot,      1) },
                { BlockType.Sand,        new ItemStack(BlockType.Glass,          1) },
                { BlockType.Cobblestone, new ItemStack(BlockType.Stone,          1) },
                { BlockType.ClayBall,    new ItemStack(BlockType.ClayBrick,      1) },
                { BlockType.RawPorkchop, new ItemStack(BlockType.CookedPorkchop, 1) },
                // Tier 8 #51 V13 — Netherrack → Nether Brick item.
                // Smelting gives 1 brick per netherrack; 4 bricks
                // craft into 1 NetherBrick block in a 2×2 grid.
                { BlockType.Netherrack,  new ItemStack(BlockType.NetherBrickItem, 1) },
            };
        }

        // Burn-time-per-unit-of-fuel, in 20Hz ticks. A furnace with a
        // unit of this fuel queued will burn for the mapped number of
        // ticks before consuming the next unit.
        public static readonly Dictionary<BlockType, int> Fuel = BuildFuel();

        private static Dictionary<BlockType, int> BuildFuel()
        {
            return new Dictionary<BlockType, int>
            {
                // Coal: top-tier basic fuel, 80 seconds per piece.
                { BlockType.Coal,         1600 },
                // Wood-tier: Alpha treats raw logs and planks as 15s fuels.
                { BlockType.WoodLog,      300 },
                { BlockType.Planks,       300 },
                // Sticks burn fast — handy filler when the player has
                // a chestful of them from chopping leaves.
                { BlockType.Stick,        100 },
                // Crafting tables and chests burn for plank-time too
                // (they're 4-plank constructs in Alpha). Useful for
                // disposing of an unwanted workbench when you've
                // upgraded to a chestful of stored gear.
                { BlockType.CraftingTable, 300 },
                // Saplings are Alpha's emergency fuel — 5s like sticks.
                // (We don't have saplings yet, but the entry is here
                // for when leaves drop saplings — Tier 1 #6.)
            };
        }

        // Convenience: is this a recognised fuel?
        public static bool IsFuel(BlockType t) => Fuel.ContainsKey(t);

        // Convenience: is this a recognised smeltable input?
        public static bool IsSmeltable(BlockType t) => Smelt.ContainsKey(t);

        // Burn ticks for a unit of `t`, or 0 if not a fuel.
        public static int BurnTicksFor(BlockType t)
            => Fuel.TryGetValue(t, out var ticks) ? ticks : 0;

        // Smelt result for `input`, or ItemStack.Empty if not smeltable.
        public static ItemStack ResultFor(BlockType input)
            => Smelt.TryGetValue(input, out var stack) ? stack : ItemStack.Empty;
    }
}
