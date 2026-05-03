using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 9 #54 V2 — Minecart. Ridable cart that follows Rail
    // blocks. Like Boat, it's a renderer-owned entity (per-dim list
    // pattern) that subclasses Entity for AABB integration. Unlike
    // Boat, the cart's motion is constrained to the rail axis under
    // it — the rail's metadata (0=N-S, 1=E-W) decides whether
    // velocity flows along Z or X.
    //
    // Physics:
    //   * On rail: velocity is locked to the rail axis (perpendicular
    //     component zeroed each tick), gravity is suppressed (the
    //     cart sits on the rail surface), and the player's W input
    //     applies a forward impulse along the rail axis in the
    //     direction the rider is facing.
    //   * Off rail (cart pushed off the track or rail broken): the
    //     cart falls with gravity and rolls to a stop via friction.
    //     Functionally inert until pushed back onto a rail.
    //   * On a curve / intersection (V3 polish — not yet implemented):
    //     the cart picks the closest aligned direction. V2 only
    //     supports straight rails, so an intersection would just
    //     use the direction of the rail under the current cell.
    //
    // The cart hovers slightly above the rail's top surface (the
    // rail is 1/16 tall on the cell floor) so the cart visibly sits
    // on the rails rather than sinking into them.
    internal sealed class Minecart : Entity
    {
        public const float HitboxHalfWidth   = 0.45f;
        public const float HitboxHeight      = 0.6f;
        public const float Gravity           = 22f;
        public const float MaxFallSpeed      = 30f;
        public const float Friction          = 0.96f;   // per-tick multiplier on rail
        public const float OffRailFriction   = 0.85f;
        public const float ThrustAccel       = 5.0f;
        public const float MaxSpeed          = 8.0f;
        public const float RailRideOffset    = 1f / 16f + 0.02f; // sit just above rail surface
        public const int   RailMetaAxisMask  = 0x01;     // 0=N-S (Z axis), 1=E-W (X axis)

        // Yaw the cart faces (radians). Updated from the rail axis
        // each tick — the cart visually points along the track.
        public float Yaw;

        public Minecart(Vector3 spawnPos, float yaw)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            Position  = spawnPos;
            Yaw       = yaw;
        }

        // Per-tick physics. Caller passes the world (for rail
        // sampling), the rider (null if empty), and the rider's
        // input (forward thrust + camera yaw — used to pick the
        // sign of the impulse along the constrained axis).
        public void Tick(float dt, World world, Player rider, bool forwardPressed, float riderYaw)
        {
            // Sample the rail under the cart. We check the cell at
            // the cart's foot (Position.Y is the AABB foot; the
            // rail sits ON the cell floor, so the cart hovers a
            // tiny bit ABOVE the cell floor of the cell that contains
            // the cart's centre).
            int cx = (int)Math.Floor(Position.X);
            int cy = (int)Math.Floor(Position.Y - 0.05f); // probe just below the cart
            int cz = (int)Math.Floor(Position.Z);
            bool onRail = false;
            byte railMeta = 0;
            if (world != null)
            {
                var here = world.GetBlock(cx, cy, cz);
                if (here == BlockType.Rail)
                {
                    onRail = true;
                    railMeta = world.GetMeta(cx, cy, cz);
                }
                else
                {
                    // Try the cell at the cart's foot directly — handles
                    // the case where the cart hovers slightly above the
                    // rail (RailRideOffset).
                    int cy2 = (int)Math.Floor(Position.Y);
                    var here2 = world.GetBlock(cx, cy2, cz);
                    if (here2 == BlockType.Rail)
                    {
                        onRail = true;
                        cy = cy2;
                        railMeta = world.GetMeta(cx, cy, cz);
                    }
                }
            }

            if (onRail)
            {
                bool axisIsX = (railMeta & RailMetaAxisMask) != 0;
                // Lock the cart's altitude to the rail surface.
                float targetY = cy + RailRideOffset;
                Position.Y = targetY;
                Velocity.Y = 0f;

                // Centre the cart on the rail's perpendicular axis so
                // the cart sits on the centerline of the cell rather
                // than drifting off-track. Keeps the visible cart
                // aligned with the rail's two parallel iron lines.
                if (axisIsX)
                {
                    Position.Z = cz + 0.5f;
                    Velocity.Z = 0f;
                }
                else
                {
                    Position.X = cx + 0.5f;
                    Velocity.X = 0f;
                }

                // Cart's facing follows the rail axis. Yaw resolves to
                // the closest cardinal aligned with the cart's current
                // velocity sign, so a cart moving south reads as facing
                // south rather than always-north.
                if (axisIsX)
                {
                    Yaw = Velocity.X >= 0f ? (float)(Math.PI * 0.5) : -(float)(Math.PI * 0.5);
                }
                else
                {
                    Yaw = Velocity.Z >= 0f ? 0f : (float)Math.PI;
                }

                // Rider thrust — project the rider's facing onto the
                // rail axis to pick the direction of the impulse.
                if (forwardPressed && rider != null)
                {
                    if (axisIsX)
                    {
                        float fx = (float)Math.Sin(riderYaw);
                        Velocity.X += Math.Sign(fx) * ThrustAccel * dt;
                        if (Math.Abs(Velocity.X) > MaxSpeed)
                            Velocity.X = Math.Sign(Velocity.X) * MaxSpeed;
                    }
                    else
                    {
                        float fz = (float)Math.Cos(riderYaw);
                        Velocity.Z += Math.Sign(fz) * ThrustAccel * dt;
                        if (Math.Abs(Velocity.Z) > MaxSpeed)
                            Velocity.Z = Math.Sign(Velocity.Z) * MaxSpeed;
                    }
                }

                // Friction along the rail axis — per-tick decay scaled
                // to dt so the half-life is consistent across frame rates.
                float decay = (float)Math.Pow(Friction, dt * 60.0);
                if (axisIsX) Velocity.X *= decay;
                else         Velocity.Z *= decay;

                // Integrate motion through standard collider so a cart
                // that runs into a wall stops cleanly.
                if (world != null) IntegrateMotion(dt, world);
            }
            else
            {
                // Off rail — gravity falls. Friction still applies so a
                // pushed-off cart eventually stops.
                Velocity.Y -= Gravity * dt;
                if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;
                float decay = (float)Math.Pow(OffRailFriction, dt * 60.0);
                Velocity.X *= decay;
                Velocity.Z *= decay;
                if (world != null) IntegrateMotion(dt, world);
            }
        }
    }
}
