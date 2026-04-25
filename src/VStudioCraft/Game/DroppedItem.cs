using OpenTK;

namespace VStudioCraft.Game
{
    // Dropped item entity. Spawned when a survival break completes, sits
    // in the world bobbing + spinning, picked up when the player walks
    // close. There's no general entity system yet, so GameRenderer just
    // owns a flat List<DroppedItem> and ticks them in lockstep with the
    // day cycle (frozen while paused / inventory open).
    //
    // Physics is intentionally simple: gravity, ground collision against
    // a single block-cell test under the drop centre, light horizontal
    // drag. No drop-vs-drop or drop-vs-wall sliding — drops thrown into a
    // wall just pile against it once they fall to the ground.
    internal sealed class DroppedItem
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public ItemStack Stack;

        // Seconds since spawn. Drives the bob phase + lifetime check.
        public float AgeSec;

        // After spawning, ignore pickup attempts for a beat so the player
        // who just mined the block doesn't grab it back on the same
        // physics step. Counts down to zero once spawned.
        public float PickupCooldownSec;

        // Despawn after this many seconds even if uncollected — matches
        // Alpha's 5-minute drop lifetime so the world doesn't litter
        // forever if the player wanders off.
        public const float MaxLifetimeSec = 300f;

        // Collision cube half-size (the drop is a 0.25-block cube;
        // collision is treated as a centred point with this Y offset
        // for the floor-rest check).
        public const float HalfSize = 0.125f;

        // Distance at which the player can collect a drop. Alpha uses
        // about 1.5 blocks, generous enough that brushing past floor
        // drops works without precise aiming.
        public const float PickupRadius = 1.5f;

        // Cooldown applied at spawn-time so a single break doesn't
        // immediately re-pick.
        public const float SpawnPickupCooldown = 0.5f;
    }
}
