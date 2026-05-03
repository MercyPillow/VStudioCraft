using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 6 #35 — Animated falling sand / gravel. Tracks one in-
    // flight block: integer column (X, Z) plus a float Y that
    // descends under gravity. World.TickFallingPhysics spawns one of
    // these instead of doing the cell-swap directly, so the player
    // sees a smooth descent rather than a 100ms-cadence teleport.
    //
    // Step shape mirrors ThrownProjectile: an Update(dt) that the
    // owner calls each frame, returning a Result enum that tells the
    // owner to either keep the entity around for next frame OR commit
    // it to the block grid and remove it from the live list.
    //
    // Lifecycle:
    //   1. Spawned by TickFallingPhysics — World clears the source
    //      cell to Air and adds a FallingBlockEntity at the same
    //      coordinate (Y = old wy, VelY = 0).
    //   2. Each frame, Update(dt) advances Y under gravity, capped at
    //      TerminalVelocity. After the step, it scans every integer
    //      cell the block PASSED THROUGH this frame from oldFloorY
    //      down to newFloorY, looking for the first cell K where
    //      (X, K-1, Z) is non-air. The block lands at K — meaning
    //      the bottom of the falling block snaps to the top of the
    //      first solid cell beneath it.
    //   3. World commits via SetBlock(X, K, Z, Type) and removes the
    //      entity. SetBlock's edit hook re-enqueues anything above
    //      the source cell that might also fall now, so cascaded
    //      stacks animate one block per spawn cadence rather than
    //      teleporting in lockstep.
    internal sealed class FallingBlockEntity
    {
        public int X;
        public int Z;
        public float Y;          // float — bottom of the falling block
        public float VelY;       // negative = falling

        public BlockType Type;

        // Real Alpha falling-sand uses g ≈ 16 m/s² (half of Earth's
        // — Minecraft physics is "moon-tier"). Terminal cap so a fall
        // from y=100 doesn't tunnel through chunk boundaries before a
        // landing probe catches it.
        public const float Gravity = 16f;
        public const float TerminalVelocity = -20f;

        // Render scale — the block draws at 0.95× to leave a tiny
        // visual gap so it doesn't z-fight the grid block that
        // landed-state will replace it with on the next frame after
        // commit. Cosmetic only.
        public const float RenderScale = 0.95f;

        public enum StepResult { KeepFlying, LandAt }

        public int LandedY;  // valid only when StepResult == LandAt

        public StepResult Update(World world, float dt)
        {
            float oldY = Y;
            VelY -= Gravity * dt;
            if (VelY < TerminalVelocity) VelY = TerminalVelocity;
            Y += VelY * dt;

            int oldFloor = (int)System.Math.Floor(oldY);
            int newFloor = (int)System.Math.Floor(Y);

            // Walk every integer cell we crossed this frame from the
            // top going down. The first cell K where (K-1) is solid
            // is the landing height. Without this loop a fast fall
            // could overshoot a thin floor and tunnel through it.
            for (int k = oldFloor; k >= newFloor; k--)
            {
                if (k <= 0)
                {
                    LandedY = 0;
                    return StepResult.LandAt;
                }
                var below = world.GetBlock(X, k - 1, Z);
                if (below != BlockType.Air)
                {
                    LandedY = k;
                    return StepResult.LandAt;
                }
            }
            return StepResult.KeepFlying;
        }

        // Tint used by the overlay-shader cube. Sand → desaturated
        // tan (matches the atlas sand tile read at distance), gravel
        // → mid grey. Same single-colour-per-entity language as
        // ThrownProjectile.GetRenderColor.
        public Vector3 GetRenderColor()
        {
            switch (Type)
            {
                case BlockType.Sand:   return new Vector3(0.93f, 0.86f, 0.62f);
                case BlockType.Gravel: return new Vector3(0.55f, 0.55f, 0.55f);
                default:               return new Vector3(0.80f, 0.80f, 0.80f);
            }
        }

        public Vector3 RenderPosition => new Vector3(X + (1f - RenderScale) * 0.5f, Y, Z + (1f - RenderScale) * 0.5f);
    }
}
