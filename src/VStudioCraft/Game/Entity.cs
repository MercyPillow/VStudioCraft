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
                if (BlockData.IsSolid(world.GetBlock(x, y, z))) return true;
            }
            return false;
        }
    }
}
