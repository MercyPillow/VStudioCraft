using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Tier 8 #49 V1 — Per-block persistent state for a Dispenser.
    //
    // Same shape as ChestTileEntity, just smaller — 9 slots in a 3×3
    // layout matching canonical Alpha 1.0.16 (the dispenser appeared
    // in the Pre-classic-rebuild update with its now-iconic crossbow
    // port). The world owns a Dictionary<(int wx, int wy, int wz),
    // DispenserTileEntity> so contents survive chunk load / unload.
    //
    // Facing is set at placement time from the player's view direction
    // (pointing the muzzle TOWARD the player, so eject pops items
    // toward the player who placed it — same convention chest doors
    // use). The mesher reads it back via GetTileIndexForOriented to
    // pick which lateral face shows the front tile.
    //
    // V1 ships block + facing + redstone-driven ejection + drop-on-
    // break with inventory spill. V2 polish: a 3×3 inventory UI so
    // the player can manually load items; for V1, slots are loaded
    // via creative spawning + hopper-equivalent tiers (none yet) so
    // most placed dispensers will eject nothing until that ships.
    internal sealed class DispenserTileEntity
    {
        // 3 wide × 3 tall — Alpha's dispenser layout. Index = row*3 +
        // col, with row 0 along the top of the (future) UI grid.
        public const int SlotCount = 9;

        public readonly ItemStack[] Slots = new ItemStack[SlotCount];

        // Cardinal direction the muzzle (front) face points.
        public BlockFacing Facing;

        // Spill iterator — used by the break path in GameRenderer
        // when the dispenser block is destroyed. Top-row slots
        // scatter first, mirroring how chest spill is row-major.
        public IEnumerable<ItemStack> SpillContents()
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (!Slots[i].IsEmpty) yield return Slots[i];
            }
        }

        // True when no slot holds anything. Used by the cleanup path
        // that removes empty entities from the world dict so saves
        // stay compact.
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < Slots.Length; i++)
                    if (!Slots[i].IsEmpty) return false;
                return true;
            }
        }

        // Pop ONE item out of a random non-empty slot and return it as
        // a single-item ItemStack. Returns ItemStack.Empty if every
        // slot is empty. Used by the redstone-eject path: when a
        // dispenser receives a power signal, the activator calls this
        // and spawns a DroppedItem at the muzzle position with the
        // returned stack.
        //
        // Picks a random slot among the non-empty ones (NOT the first
        // — Alpha's dispenser pulls a random item, so a full 9-slot
        // dispenser doesn't drain in row order).
        public ItemStack TakeRandomOne(System.Random rng)
        {
            // Count non-empty slots first so we can pick uniformly.
            int nonEmpty = 0;
            for (int i = 0; i < Slots.Length; i++)
                if (!Slots[i].IsEmpty) nonEmpty++;
            if (nonEmpty == 0) return ItemStack.Empty;

            int target = rng.Next(nonEmpty);
            for (int i = 0; i < Slots.Length; i++)
            {
                if (Slots[i].IsEmpty) continue;
                if (target == 0)
                {
                    var taken = new ItemStack(Slots[i].Type, 1, Slots[i].Durability);
                    int remaining = Slots[i].Count - 1;
                    Slots[i] = remaining > 0
                        ? new ItemStack(Slots[i].Type, remaining, Slots[i].Durability)
                        : ItemStack.Empty;
                    return taken;
                }
                target--;
            }
            return ItemStack.Empty;
        }
    }
}
