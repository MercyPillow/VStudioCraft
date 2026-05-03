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

        // Tier 9 #54 V3 — Rail meta values for curves. Straight rails
        // stay at 0 (N-S) / 1 (E-W); the four corner variants encode
        // which two cardinal sides are open. For example RailMetaCornerNE
        // is open at North and East, so a cart entering from the north
        // exits to the east (and vice versa).
        public const int RailMetaCornerNE = 2;
        public const int RailMetaCornerNW = 3;
        public const int RailMetaCornerSE = 4;
        public const int RailMetaCornerSW = 5;

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
                bool isCurve = railMeta >= RailMetaCornerNE && railMeta <= RailMetaCornerSW;

                // Lock the cart's altitude to the rail surface (same
                // for straights AND curves).
                float targetY = cy + RailRideOffset;
                Position.Y = targetY;
                Velocity.Y = 0f;

                if (!isCurve)
                {
                    // === Straight rail: original V2 axis-locked path ===
                    bool axisIsX = (railMeta & RailMetaAxisMask) != 0;

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

                    if (axisIsX)
                        Yaw = Velocity.X >= 0f ? (float)(Math.PI * 0.5) : -(float)(Math.PI * 0.5);
                    else
                        Yaw = Velocity.Z >= 0f ? 0f : (float)Math.PI;

                    if (forwardPressed && rider != null)
                    {
                        // Codebase yaw convention: forward =
                        // (sin(yaw), 0, -cos(yaw)). yaw=0 → facing -Z
                        // (north). The Z-axis thrust uses -cos(yaw)
                        // so a cart on a N-S rail with the rider
                        // facing north thrusts toward -Z (correct
                        // forward direction), not +Z.
                        if (axisIsX)
                        {
                            float fx = (float)Math.Sin(riderYaw);
                            Velocity.X += Math.Sign(fx) * ThrustAccel * dt;
                            if (Math.Abs(Velocity.X) > MaxSpeed)
                                Velocity.X = Math.Sign(Velocity.X) * MaxSpeed;
                        }
                        else
                        {
                            float fz = -(float)Math.Cos(riderYaw);
                            Velocity.Z += Math.Sign(fz) * ThrustAccel * dt;
                            if (Math.Abs(Velocity.Z) > MaxSpeed)
                                Velocity.Z = Math.Sign(Velocity.Z) * MaxSpeed;
                        }
                    }

                    float decay = (float)Math.Pow(Friction, dt * 60.0);
                    if (axisIsX) Velocity.X *= decay;
                    else         Velocity.Z *= decay;
                }
                else
                {
                    // === Curve rail: redirect the cart's axis based on
                    // which open side it's currently moving INTO ===
                    //
                    // Each curve has 2 open cardinal sides:
                    //   NE = N + E (north and east)
                    //   NW = N + W
                    //   SE = S + E
                    //   SW = S + W
                    //
                    // The cart's incoming axis is whichever has the
                    // larger absolute velocity; we redirect that to
                    // the perpendicular open side. If both open sides
                    // are along the cart's current axis (impossible
                    // for these 4 corners — N/S is Z, E/W is X, so
                    // any corner straddles both axes), we'd no-op.
                    bool openN = railMeta == RailMetaCornerNE || railMeta == RailMetaCornerNW;
                    bool openS = railMeta == RailMetaCornerSE || railMeta == RailMetaCornerSW;
                    bool openE = railMeta == RailMetaCornerNE || railMeta == RailMetaCornerSE;
                    bool openW = railMeta == RailMetaCornerNW || railMeta == RailMetaCornerSW;

                    float cellCx = cx + 0.5f;
                    float cellCz = cz + 0.5f;
                    float dxFromCentre = Position.X - cellCx;
                    float dzFromCentre = Position.Z - cellCz;
                    float speed = (float)Math.Sqrt(Velocity.X * Velocity.X + Velocity.Z * Velocity.Z);

                    // If we just crossed the cell centre, snap to the
                    // perpendicular open side. The signed distance
                    // tells us which side we entered from:
                    //   * Velocity.X > 0 + cart approaching cell from
                    //     west: we entered from W; redirect along the
                    //     other open axis.
                    // Specifically: if moving +X (east) and W is open,
                    // the cart was entering from W and now needs to
                    // continue toward whichever of N/S is open.
                    bool axisIsX = Math.Abs(Velocity.X) > Math.Abs(Velocity.Z);

                    // Aim the cart along its current axis but funnel
                    // it through the cell centre. Centre the cart on
                    // the perpendicular axis so it stays on the rail
                    // visually while approaching the centre.
                    if (axisIsX)
                    {
                        Position.Z = cellCz;
                        Velocity.Z = 0f;
                    }
                    else
                    {
                        Position.X = cellCx;
                        Velocity.X = 0f;
                    }

                    // At the cell centre (within a small epsilon),
                    // hand off to the open perpendicular axis.
                    const float SnapEps = 0.05f;
                    if (axisIsX && Math.Abs(dxFromCentre) < SnapEps && speed > 0.01f)
                    {
                        // Entered along X. Was entering from W (vel +X) or E (vel -X)?
                        bool fromW = Velocity.X > 0f;
                        bool exitOpensN = (fromW && openW && openN) || (!fromW && openE && openN);
                        bool exitOpensS = (fromW && openW && openS) || (!fromW && openE && openS);
                        if (exitOpensN)
                        {
                            // Redirect from +X (or -X) to -Z (north).
                            float keep = speed;
                            Velocity.X = 0f;
                            Velocity.Z = -keep;
                            Position.X = cellCx;
                        }
                        else if (exitOpensS)
                        {
                            float keep = speed;
                            Velocity.X = 0f;
                            Velocity.Z = +keep;
                            Position.X = cellCx;
                        }
                    }
                    else if (!axisIsX && Math.Abs(dzFromCentre) < SnapEps && speed > 0.01f)
                    {
                        bool fromN = Velocity.Z > 0f;
                        bool exitOpensE = (fromN && openN && openE) || (!fromN && openS && openE);
                        bool exitOpensW = (fromN && openN && openW) || (!fromN && openS && openW);
                        if (exitOpensE)
                        {
                            float keep = speed;
                            Velocity.Z = 0f;
                            Velocity.X = +keep;
                            Position.Z = cellCz;
                        }
                        else if (exitOpensW)
                        {
                            float keep = speed;
                            Velocity.Z = 0f;
                            Velocity.X = -keep;
                            Position.Z = cellCz;
                        }
                    }

                    // Cart yaw — pick a diagonal facing for visual
                    // hint that the cart is on a curve.
                    Yaw = (float)Math.Atan2(Velocity.X, Velocity.Z);

                    // Rider thrust on a curve — same direction-handoff
                    // logic; pick whichever axis the cart is currently on.
                    // Z thrust uses -cos(yaw) for the same forward-
                    // convention reason as the straight branch.
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
                            float fz = -(float)Math.Cos(riderYaw);
                            Velocity.Z += Math.Sign(fz) * ThrustAccel * dt;
                            if (Math.Abs(Velocity.Z) > MaxSpeed)
                                Velocity.Z = Math.Sign(Velocity.Z) * MaxSpeed;
                        }
                    }

                    float decay = (float)Math.Pow(Friction, dt * 60.0);
                    Velocity.X *= decay;
                    Velocity.Z *= decay;
                }

                // Integrate motion through standard collider so a cart
                // that runs into a wall stops cleanly. Shared by both
                // straight + curve branches.
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
