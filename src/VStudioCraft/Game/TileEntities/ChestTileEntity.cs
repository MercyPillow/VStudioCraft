using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Per-block persistent state for a Chest placed in the world.
    //
    // A chest cell stores a flat 27-slot inventory (Alpha's single-chest
    // size — 9 wide × 3 tall in the UI). Slots are an ItemStack[27] so the
    // empty/non-empty test is the same as everywhere else in the inventory
    // code. The world owns a Dictionary<(int wx, int wy, int wz),
    // ChestTileEntity> (see World.ChestEntities) so chest contents survive
    // chunk load/unload — chunks don't carry tile-entity state.
    //
    // Chest is oriented (the door/lock face points toward the player at
    // placement time). Like the furnace, the orientation is stamped on the
    // entity at placement and read back by ChunkMesher when emitting the
    // four lateral faces, so we don't burn a per-block metadata byte. The
    // top/bottom faces don't depend on facing — top is always the lid tile,
    // bottom is plain planks.
    //
    // No tick: chests are passive containers. The renderer doesn't iterate
    // ChestEntities each tick — only when the player opens one (LMB on
    // contents) or breaks one (spill).
    internal sealed class ChestTileEntity
    {
        // 9 wide × 3 tall — Alpha's single-chest layout. Index = row*9 + col,
        // with row 0 along the top of the chest UI.
        public const int SlotCount = 27;

        public readonly ItemStack[] Slots = new ItemStack[SlotCount];

        // Cardinal direction the door (front) face points. Set at
        // placement time from the player's yaw — see
        // GameRenderer.TryPlaceBlock. Default (North) matches the value
        // a freshly-instantiated entity gets so legacy v6 saves load
        // with a sensible orientation.
        public BlockFacing Facing;

        // Drops produced when the chest block is broken. Spills every
        // non-empty slot in row-major order so the visual scatter looks
        // consistent (top-row slots fall first). Caller is expected to
        // also drop the Chest block itself.
        public IEnumerable<ItemStack> SpillContents()
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (!Slots[i].IsEmpty) yield return Slots[i];
            }
        }

        // True when no slot holds anything. Used by the cleanup path
        // that removes empty entities from the world dict so saves stay
        // compact (a freshly-placed chest with nothing in it doesn't
        // need to persist).
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < Slots.Length; i++)
                    if (!Slots[i].IsEmpty) return false;
                return true;
            }
        }
    }
}
