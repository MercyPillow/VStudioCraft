using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // Shared base for anything that walks the voxel world: Player, Pig,
    // future cow / sheep / chicken / hostile mobs. Owns the AABB-vs-block
    // collision walker and the per-axis sub-step integrator that the
    // first-person player has used since day one — Pig (Tier 3 #9) is the
    // first second user, so the physics gets lifted out of Player and
    // exposed here for any subclass to drive.
    //
    // Subclasses set HalfWidth + Height in their constructor (a Pig is
    // shorter and wider than a Player) and call IntegrateMotion(dt, world)
    // each tick after writing into Velocity. The integrator does:
    //   - sub-step per-axis sweep so high-speed motion doesn't tunnel
    //   - axial velocity zero on collision (so a wall stops you cleanly)
    //   - OnGround tracking on the Y axis (set on a downward stop, cleared
    //     when you've moved free of the floor)
    //
    // Gravity / jumping / swim physics / hurt / swing are NOT here —
    // they're player-specific or mob-specific behaviours layered on top.
    // Pig will reimplement gravity (same constants, no jump) inside its
    // own Update; Player's existing swim + jump + bob code stays in
    // Player.cs unchanged.
    internal abstract class Entity
    {
        // AABB shape. Position is the feet (AABB min Y, centre of X/Z),
        // matching the long-standing Player convention. Subclasses set
        // these once and treat them as constants thereafter.
        public float HalfWidth = 0.3f;
        public float Height = 1.8f;

        public Vector3 Position;
        public Vector3 Velocity;
        public bool OnGround;

        // Phase 5 — server-assigned network identifier. -1 in singleplayer
        // and during the brief window between local construction and the
        // server's BroadcastEntityUpdates pass spotting the entity. Used
        // by the multiplayer broadcast loop to look the entity up across
        // ticks and by the client to route inbound EntityRelMove /
        // EntityDespawn packets to the right replica. Not persisted —
        // re-allocated on every server start.
        public int NetworkId = -1;

        // Phase 5 — last position+look this entity was broadcast at
        // (set by the server's per-target anchor pass). Mob entities
        // store anchors here directly because the per-mob count is
        // small enough that the inline storage is cheaper than a
        // sidecar dict in ServerHub. Players use the AnchorX/Y/Z
        // fields on ServerClient instead — they need a lifecycle that
        // tracks login phase, which a simple Entity field doesn't have.
        public double AnchorX, AnchorY, AnchorZ;
        public float  AnchorYaw, AnchorPitch;
        public bool   AnchorValid;
        public int    TicksSinceTeleport;

        // Phase 5e — last health value the broadcast loop saw for this
        // entity. Compared to current health each tick; a decrease
        // triggers an EntityHealth packet so friends see the hurt
        // flash. Initialised to int.MinValue so the very first
        // observation always primes the field without spuriously
        // counting as "took damage". Subclasses (PassiveMob,
        // HostileMob) own the actual Health field; this is just the
        // server-side broadcast cache.
        public int LastBroadcastHealth = int.MinValue;

        // Cap on per-sub-step displacement so a fast-moving entity can't
        // skip through a 1-block wall in a single tick. 0.05 is small
        // enough that wall gaps are imperceptible and big enough that
        // a normal 4 m/s walk takes ≤ 1 sub-step at 60 Hz.
        protected const float MaxSubStep = 0.05f;

        // Apply a velocity-driven move with per-axis sub-stepping and
        // AABB collision against solid blocks. Mutates Position, may
        // zero Velocity components on contact, and updates OnGround.
        // Subclasses call this after writing Velocity for the tick.
        protected void IntegrateMotion(float dt, World world)
        {
            var step = Velocity * dt;
            MoveAxis(0, step.X, world);
            MoveAxis(2, step.Z, world);

            // Fast path for grounded entities under gravity: if we were on
            // the floor going in and the only Y velocity is gravity (≤0),
            // skip the per-substep Y collision sweep. A stationary mob on
            // flat ground can't fall through; a mob that walked horizontally
            // might have stepped off a ledge, so we still pay one
            // IsStandingOnSomething probe in that case. This collapses the
            // common idle-mob path from 1-2 Collides() per tick to 0 (and
            // is a wash for walking mobs), which adds up across PassiveCap
            // (10) + HostileCap (70) every 50 ms tick.
            if (OnGround && Velocity.Y <= 0f)
            {
                bool movedXZ = Math.Abs(step.X) > 1e-6f || Math.Abs(step.Z) > 1e-6f;
                if (!movedXZ || IsStandingOnSomething(world))
                {
                    Velocity.Y = 0f;
                    return;
                }
                // Walked off a ledge — let gravity integrate this tick.
                OnGround = false;
            }

            MoveAxis(1, step.Y, world);
        }

        protected void MoveAxis(int axis, float delta, World world)
        {
            if (Math.Abs(delta) < 1e-6f)
            {
                if (axis == 1 && delta == 0f)
                {
                    // Keep OnGround accurate even on a zero-Y tick — a tiny
                    // probe down catches "still resting on the floor".
                    OnGround = IsStandingOnSomething(world);
                }
                return;
            }

            int subSteps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / MaxSubStep));
            float subDelta = delta / subSteps;
            bool hitGround = false;
            bool moved = false;

            for (int i = 0; i < subSteps; i++)
            {
                Vector3 next = Position;
                SetAxis(ref next, axis, GetAxis(next, axis) + subDelta);

                if (Collides(next, world))
                {
                    // Tier 6 #37 Phase 4 — Auto-step over partial-cube
                    // obstacles (snow layer, future stairs / slabs).
                    // For horizontal moves (X / Z) only, when we're
                    // grounded: try lifting the player by up to
                    // MaxAutoStepHeight and re-running the move from
                    // there. If the lifted-then-stepped position is
                    // clear, settle back down to the top of the
                    // obstruction. This is what lets you walk INTO a
                    // 1/8 snow layer or a half-slab without jumping.
                    if ((axis == 0 || axis == 2) && OnGround
                        && TryAutoStep(axis, subDelta, world, out Vector3 stepped))
                    {
                        Position = stepped;
                        moved = true;
                        continue;
                    }
                    if (axis == 0) Velocity.X = 0f;
                    else if (axis == 1)
                    {
                        if (subDelta < 0) hitGround = true;
                        Velocity.Y = 0f;
                    }
                    else Velocity.Z = 0f;
                    break;
                }

                Position = next;
                moved = true;
            }

            if (axis == 1)
            {
                if (hitGround) OnGround = true;
                else if (delta < 0 && moved) OnGround = false;
            }
        }

        // Maximum step-up the auto-step system will negotiate without
        // the player jumping. 0.55 leaves headroom over the canonical
        // half-block (0.5) so a slab-on-ground reads cleanly. Snow
        // (0.125) and any future thin partial cubes fall well within.
        protected const float MaxAutoStepHeight = 0.55f;
        // Resolution for the "settle down" pass after a successful
        // step. 1/16 catches Alpha-style 1/16 partial heights and is
        // still cheap (≤ 9 Collides probes for a full-step settle).
        private const float AutoStepSettle = 1f / 16f;

        // Attempt to step up over an obstacle on the (axis) horizontal
        // axis. Returns true with `stepped` set to the new player
        // position if the step succeeded; false otherwise (caller
        // falls through to the original "zero velocity, stop" branch).
        // Conditions:
        //   1. Lifting Y by MaxAutoStepHeight from the CURRENT position
        //      must be clear (the player has overhead headroom).
        //   2. Applying the desired horizontal subDelta from that
        //      lifted position must also be clear (we'd actually fit
        //      on top of the obstacle).
        //   3. We then settle Y down in 1/16 increments until we hit
        //      something solid — that's the obstacle's top surface.
        //      The remaining Y above the obstacle is shed naturally.
        private bool TryAutoStep(int axis, float subDelta, World world, out Vector3 stepped)
        {
            stepped = Position;

            // (1) Headroom check.
            Vector3 lifted = Position;
            lifted.Y += MaxAutoStepHeight;
            if (Collides(lifted, world)) return false;

            // (2) Horizontal move from the lifted position.
            Vector3 liftedNext = lifted;
            SetAxis(ref liftedNext, axis, GetAxis(liftedNext, axis) + subDelta);
            if (Collides(liftedNext, world)) return false;

            // (3) Settle. Step Y down by AutoStepSettle until
            // immediately before the first colliding probe — that's
            // the obstruction's top surface. Loop bounded by the lift
            // amount so we never settle below the original Y.
            Vector3 settled = liftedNext;
            for (float drop = AutoStepSettle; drop <= MaxAutoStepHeight + 1e-4f; drop += AutoStepSettle)
            {
                Vector3 probe = settled;
                probe.Y = liftedNext.Y - drop;
                if (Collides(probe, world)) break;
                settled = probe;
            }

            stepped = settled;
            return true;
        }

        protected bool IsStandingOnSomething(World world)
        {
            var probe = Position;
            probe.Y -= 1e-3f;
            return Collides(probe, world);
        }

        protected static float GetAxis(Vector3 v, int axis) =>
            axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);

        protected static void SetAxis(ref Vector3 v, int axis, float val)
        {
            if (axis == 0) v.X = val;
            else if (axis == 1) v.Y = val;
            else v.Z = val;
        }

        protected bool Collides(Vector3 pos, World world)
        {
            float minX = pos.X - HalfWidth, maxX = pos.X + HalfWidth;
            float minY = pos.Y,              maxY = pos.Y + Height;
            float minZ = pos.Z - HalfWidth, maxZ = pos.Z + HalfWidth;

            int bx0 = (int)Math.Floor(minX);
            int bx1 = (int)Math.Floor(maxX - 1e-5f);
            int by0 = (int)Math.Floor(minY);
            int by1 = (int)Math.Floor(maxY - 1e-5f);
            int bz0 = (int)Math.Floor(minZ);
            int bz1 = (int)Math.Floor(maxZ - 1e-5f);

            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            for (int z = bz0; z <= bz1; z++)
            {
                var t = world.GetBlock(x, y, z);
                if (!BlockData.IsSolid(t)) continue;
                // Tier 6 #37 Phase 4 — Test against the per-block
                // partial AABB rather than assuming the whole cell
                // is solid. Default cubes return (0,0,0,1,1,1) so
                // the test reduces to the original "any overlap" for
                // them; SnowBlock returns (0,0,0,1,0.125,1) which
                // means the player can stand on top of the snow
                // layer at Y = cellY + 0.125 instead of the full
                // cellY + 1.
                var (b0x, b0y, b0z, b1x, b1y, b1z) = BlockData.GetCollisionAabb(t);
                float blockMinX = x + b0x, blockMaxX = x + b1x;
                float blockMinY = y + b0y, blockMaxY = y + b1y;
                float blockMinZ = z + b0z, blockMaxZ = z + b1z;
                if (maxX > blockMinX && minX < blockMaxX
                    && maxY > blockMinY && minY < blockMaxY
                    && maxZ > blockMinZ && minZ < blockMaxZ)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
