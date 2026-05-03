using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Per-block persistent state for a Furnace placed in the world.
    //
    // A furnace cell has three slots and two timers:
    //   * Input  — what's being smelted (e.g. IronOre)
    //   * Fuel   — what's burning (e.g. Coal)
    //   * Output — finished product, accumulates until pulled
    //   * BurnTimeTicks    — ticks remaining on the current fuel unit; 0 = not lit
    //   * MaxBurnTimeTicks — full duration of the current fuel unit (drives the
    //                       flame icon's drain animation in the UI)
    //   * CookProgressTicks — how many ticks the input slot has been smelting;
    //                         resets if smelting can't continue (no fuel, slot
    //                         emptied, output full, etc.)
    //
    // The world owns a Dictionary<(int wx, int wy, int wz), FurnaceTileEntity>
    // (see World.FurnaceEntities) so a furnace's state survives chunk
    // load/unload (chunks don't carry tile-entity state — the dict is
    // top-level on World and gets persisted alongside the chunk dictionary).
    //
    // The renderer ticks every entry in the dict each game tick (20Hz):
    // see GameRenderer.TickFurnaces. The block visual swap (Furnace ↔
    // LitFurnace) happens whenever IsBurning transitions; the renderer
    // calls World.SetBlock on the swap, which marks the chunk dirty so
    // the new block face is meshed.
    // Cardinal direction the front (door) face of an oriented block points
    // toward. North = -Z, South = +Z, East = +X, West = -X. Default
    // (North) is what newly-instantiated entities get; placement code
    // overwrites it from player look direction.
    internal enum BlockFacing : byte
    {
        North = 0,
        South = 1,
        East  = 2,
        West  = 3,
    }

    internal sealed class FurnaceTileEntity
    {
        public ItemStack Input;
        public ItemStack Fuel;
        public ItemStack Output;

        // Cardinal direction the door face points. Set at placement time
        // from the player's yaw — see GameRenderer.TryPlaceBlock. Both
        // Furnace and LitFurnace blocks share the same entity dict so the
        // facing carries over the burn/extinguish swap.
        public BlockFacing Facing;

        // Ticks remaining on the current fuel unit. 0 → not burning.
        public int BurnTimeTicks;
        // Original burn-time of the unit currently in BurnTimeTicks; zero
        // when not burning. UI uses BurnTimeTicks / MaxBurnTimeTicks for
        // the flame fill ratio.
        public int MaxBurnTimeTicks;
        // Ticks the input slot has been smelting. Reaches CookTimeTicks
        // → consume one input, produce one output, reset to 0.
        public int CookProgressTicks;

        public bool IsBurning => BurnTimeTicks > 0;

        // Can the current Input/Output pair accept one more smelt cycle?
        // Returns false when the input slot is empty / not smeltable, or
        // the output slot already holds a different kind / is full.
        public bool CanSmelt()
        {
            if (Input.IsEmpty) return false;
            if (!FurnaceRecipes.IsSmeltable(Input.Type)) return false;
            var result = FurnaceRecipes.ResultFor(Input.Type);
            if (result.IsEmpty) return false;
            if (Output.IsEmpty) return true;
            if (Output.Type != result.Type) return false;
            if (Output.Count + result.Count > Output.MaxStackSize) return false;
            return true;
        }

        // Advance one game tick. Pure state-machine; no side effects on
        // the world. Returns true if the burning visual state changed
        // (caller swaps Furnace ↔ LitFurnace block in response).
        public bool Tick()
        {
            bool wasBurning = IsBurning;

            // 1) Drain active fuel one tick.
            if (BurnTimeTicks > 0)
            {
                BurnTimeTicks--;
                if (BurnTimeTicks == 0) MaxBurnTimeTicks = 0;
            }

            // 2) If we've run out of fuel, try to ignite a new unit IF
            // there's something to smelt. Alpha behaviour: a furnace
            // with fuel and no input doesn't auto-ignite — fuel only
            // burns once an input is present.
            if (BurnTimeTicks == 0 && !Fuel.IsEmpty && CanSmelt())
            {
                int t = FurnaceRecipes.BurnTicksFor(Fuel.Type);
                if (t > 0)
                {
                    BurnTimeTicks = t;
                    MaxBurnTimeTicks = t;
                    int newCount = Fuel.Count - 1;
                    Fuel = newCount <= 0
                        ? ItemStack.Empty
                        : new ItemStack(Fuel.Type, newCount, Fuel.Durability);
                }
            }

            // 3) Advance smelt progress only while actively burning AND
            // a valid smelt is set up. Otherwise reset progress (Alpha
            // resets when the input is removed mid-cook).
            if (IsBurning && CanSmelt())
            {
                CookProgressTicks++;
                if (CookProgressTicks >= FurnaceRecipes.CookTimeTicks)
                {
                    var result = FurnaceRecipes.ResultFor(Input.Type);
                    // Produce one output.
                    if (Output.IsEmpty)
                        Output = result;
                    else
                        Output = new ItemStack(Output.Type, Output.Count + result.Count, Output.Durability);
                    // Consume one input.
                    int newInput = Input.Count - 1;
                    Input = newInput <= 0
                        ? ItemStack.Empty
                        : new ItemStack(Input.Type, newInput, Input.Durability);
                    CookProgressTicks = 0;
                }
            }
            else
            {
                CookProgressTicks = 0;
            }

            return IsBurning != wasBurning;
        }

        // Drops produced when the furnace block is broken. Spills any
        // non-empty input/fuel/output stacks. Caller is expected to
        // also drop the Furnace block itself (Block.DropFor handles the
        // LitFurnace → Furnace remap).
        public IEnumerable<ItemStack> SpillContents()
        {
            if (!Input.IsEmpty) yield return Input;
            if (!Fuel.IsEmpty) yield return Fuel;
            if (!Output.IsEmpty) yield return Output;
        }
    }
}
