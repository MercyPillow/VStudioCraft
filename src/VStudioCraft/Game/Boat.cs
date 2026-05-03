using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 9 #54 V1 — Boat. The first ridable vehicle in the
    // codebase. Floats on water, steered by the rider.
    //
    // Physics:
    //   * Subclasses Entity so it inherits AABB collision +
    //     IntegrateMotion. The hitbox is wider-than-tall (matches
    //     canonical Alpha — the boat reads as a flat dish on the
    //     water surface).
    //   * Buoyancy: when any cell underneath the boat's footprint is
    //     water, an upward force lifts the boat toward the water
    //     surface (target Y = floor(WaterY) + 0.5). Above water,
    //     gravity applies normally so a boat dragged onto land
    //     falls and slides until it finds water again.
    //   * Drag: horizontal velocity decays exponentially each tick
    //     so a player who paddles forward then releases W coasts to
    //     a smooth stop instead of sliding forever on the water.
    //   * Rider thrust: while a Player has MountedBoat == this,
    //     the rider's W-pressed state applies a small forward
    //     impulse along the boat's facing yaw. The boat's yaw
    //     follows the rider's camera yaw so steering feels natural.
    //
    // Mount/dismount: handled in the renderer's TryInteract path —
    // RMB on a boat mounts (Player.MountedBoat = boat); Sneak key
    // dismounts (clears the field, pops the player to the side of
    // the boat). The player's WASD input is suppressed while
    // mounted; only forward thrust is applied.
    //
    // Break: punching the boat (LMB) removes it from the world and
    // drops a Boat item at the impact point. No durability — Alpha
    // boats break in one hit when punched outside the water, and
    // collide-shatter on a fast collision into a wall.
    internal sealed class Boat : Entity
    {
        public const float HitboxHalfWidth  = 0.6f;
        public const float HitboxHeight     = 0.55f;
        public const float Gravity          = 24f;
        public const float MaxFallSpeed     = 30f;
        public const float HorizDrag        = 0.92f;   // per-tick multiplier (~0.92 ≈ noticeable coasting)
        public const float VerticalDrag     = 0.85f;
        public const float BuoyancyForce    = 28f;     // upward accel when submerged
        public const float WaterTargetOffset = 0.40f;   // boat sits ~0.4 above floor of the water cell
        public const float ThrustAccel      = 6.5f;    // forward acceleration applied by W
        public const float MaxForwardSpeed  = 7.0f;    // m/s when fully accelerated
        public const float YawTurnRate      = 4f;      // rad/sec the boat re-aligns to rider yaw

        // Yaw the boat faces (radians, +Z = 0). Drives the body model
        // orientation in the renderer + the thrust direction.
        public float Yaw;

        public Boat(Vector3 spawnPos, float yaw)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            Position  = spawnPos;
            Yaw       = yaw;
        }

        // Per-tick physics. Caller passes the rider (null if empty)
        // and whether the rider is pressing W (forward thrust).
        // Yaw updates toward the rider's camera yaw smoothly so
        // steering reads as continuous rotation rather than snapping.
        public void Tick(float dt, World world, Player rider, bool forwardPressed, float riderYaw)
        {
            // Yaw alignment to rider — smooth interpolation rather
            // than a snap so the boat reads as turning under the
            // player's input.
            if (rider != null)
            {
                float dyaw = WrapAngle(riderYaw - Yaw);
                float maxStep = YawTurnRate * dt;
                if (dyaw >  maxStep) dyaw =  maxStep;
                if (dyaw < -maxStep) dyaw = -maxStep;
                Yaw += dyaw;
            }

            // Buoyancy probe — sample the cell directly under the
            // boat's centre. Water-source AND flowing-water both
            // count; lava is not buoyant (boat sinks + presumably
            // catches fire, modelled by gravity-only path here).
            int cx = (int)Math.Floor(Position.X);
            int cy = (int)Math.Floor(Position.Y);
            int cz = (int)Math.Floor(Position.Z);
            bool inWater = false;
            if (world != null)
            {
                var hereT = world.GetBlock(cx, cy, cz);
                var belowT = world.GetBlock(cx, cy - 1, cz);
                if (IsWaterLike(hereT) || IsWaterLike(belowT))
                {
                    inWater = true;
                }
            }

            if (inWater)
            {
                // Target altitude = floor of the water cell + offset.
                // The boat floats at this Y, slowly settling into it.
                float targetY = cy + WaterTargetOffset;
                float dy = targetY - Position.Y;
                // Spring-damper toward target altitude. Strong
                // restoring force + heavy vertical drag keeps the
                // boat from oscillating like a yo-yo.
                Velocity.Y += dy * BuoyancyForce * dt;
                Velocity.Y *= VerticalDrag;
            }
            else
            {
                // Out of water — gravity falls toward terminal
                // velocity.
                Velocity.Y -= Gravity * dt;
                if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;
            }

            // Forward thrust (only effective when in water — paddling
            // air does nothing). Adds along the boat's facing axis.
            // Codebase yaw convention: forward = (sin(yaw), 0, -cos(yaw)).
            // yaw=0 → facing -Z (north). The Z-axis component uses
            // -cos(yaw) so paddling forward when facing north pushes
            // the boat toward -Z (correct forward direction), not +Z.
            if (forwardPressed && rider != null && inWater)
            {
                float fx =  (float)Math.Sin(Yaw);
                float fz = -(float)Math.Cos(Yaw);
                Velocity.X += fx * ThrustAccel * dt;
                Velocity.Z += fz * ThrustAccel * dt;

                // Cap forward speed so the boat doesn't keep
                // accelerating to silly velocities.
                float horizSq = Velocity.X * Velocity.X + Velocity.Z * Velocity.Z;
                if (horizSq > MaxForwardSpeed * MaxForwardSpeed)
                {
                    float scale = MaxForwardSpeed / (float)Math.Sqrt(horizSq);
                    Velocity.X *= scale;
                    Velocity.Z *= scale;
                }
            }

            // Horizontal drag — slow coast-to-stop when no input.
            // Frame-rate independent: scale drag by dt so the half-
            // life is consistent across tick intervals.
            float decay = (float)Math.Pow(HorizDrag, dt * 60.0); // 60Hz reference
            Velocity.X *= decay;
            Velocity.Z *= decay;

            if (world != null) IntegrateMotion(dt, world);
        }

        private static bool IsWaterLike(BlockType t)
            => t == BlockType.Water || t == BlockType.FlowingWater;

        // Wrap an angle delta into (-π, π].
        private static float WrapAngle(float a)
        {
            const float TwoPi = (float)(Math.PI * 2.0);
            while (a >  Math.PI) a -= TwoPi;
            while (a <= -Math.PI) a += TwoPi;
            return a;
        }
    }
}
