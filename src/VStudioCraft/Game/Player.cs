using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // First-person walker with AABB-vs-voxel collision. Position tracks the feet
    // (AABB min Y, centre of X/Z). The camera sits at Position + (0, EyeHeight, 0).
    internal sealed class Player
    {
        public const float HalfWidth = 0.3f;   // AABB half-extent in X and Z
        public const float Height = 1.8f;      // AABB Y extent
        public const float EyeHeight = 1.62f;  // camera offset above feet

        public const float WalkSpeed = 4.3f;
        public const float SprintSpeed = 7.0f;
        public const float Gravity = 28f;        // m/s²
        public const float JumpSpeed = 8.4f;     // apex ≈ 1.26 blocks
        public const float MaxFallSpeed = 78f;

        // Swim physics. In water gravity is much weaker (you sink slowly), the
        // terminal speed is bounded both ways (drag), and Space pushes you up
        // instead of behaving like a ground-jump. The horizontal scale matches
        // Alpha's "water is sticky" feel — half walking speed in either axis.
        public const float WaterGravity = 8f;       // m/s²
        public const float WaterMaxFall = 3f;       // sinks slowly
        public const float WaterMaxRise = 4.5f;     // upward terminal while holding Space
        public const float SwimUpAccel = 22f;       // m/s² applied while Space held
        public const float WaterMoveScale = 0.5f;   // horizontal velocity multiplier

        // Bobbing: pure visual offset added to the camera Y when in water. The
        // amplitude is small (Alpha's bob is similarly subtle) and the cadence
        // is tied to the swim cycle so head-strokes read as the bob beats.
        public const float SwimBobAmplitude = 0.05f;
        public const float SwimBobFrequency = 4f;   // radians/sec

        // Small sub-step cap so fast motion (e.g. terminal-velocity fall) can't skip
        // through a block in a single tick. 0.05 = imperceptible wall gap.
        private const float MaxSubStep = 0.05f;

        // Alpha health: 10 hearts × 2 HP = 20 HP max. Even values = full hearts,
        // odd values = N/2 full + one half-heart rendered at the right edge.
        public const int MaxHealth = 20;

        // Hunger mirrors health (10 drumsticks × 2 points = 20 max). Alpha
        // 1.1.2_01 didn't actually have hunger — it arrived in Beta 1.8 — but
        // we render the bar now so the HUD layout feels complete, and we've
        // scaffolded the field so a future "food + decay" system slots in
        // without changing the UI again. Pinned at MaxHunger for now.
        public const int MaxHunger = 20;

        // Air supply (Alpha: 300 ticks ≈ 15s). Two air points per rendered
        // bubble, so 20 max = 10 bubbles, identical scale to hearts/hunger.
        // Decays only while the head (top half of the AABB) is submerged in
        // water. Once it reaches zero, drowning damage starts in survival.
        public const int MaxAir = 20;

        public Vector3 Position;
        public Vector3 Velocity;
        public bool OnGround;

        // Survival HP. Creative mode keeps this pinned at MaxHealth.
        public int Health = MaxHealth;
        public int Hunger = MaxHunger;
        public int Air = MaxAir;
        public bool IsDead => Health <= 0;

        // Last-known submerged state, sampled by the renderer for HUD + survival
        // damage. Cached on each Player.Update so callers don't re-scan the AABB.
        public bool WasInWater;
        public bool WasHeadInWater;

        // Driven inside Update; the camera reads it via SwimBobOffset to add a
        // gentle vertical sway while submerged. Decays back to 0 once you exit
        // water so the camera doesn't lurch.
        public float SwimBobPhase;
        public float SwimBobOffset;

        // One-shot: set to the drop height (in blocks) whenever the player
        // transitions from airborne→grounded. The renderer reads it once per
        // frame to apply fall damage in survival mode, then clears it.
        public float LastFallDistance;

        // Highest Y reached while airborne — the "peak" from which fall distance
        // is measured. Reset to current Y while on the ground so small hops
        // don't accumulate.
        private float _fallPeakY;

        public void Update(float dt, Vector3 wishHorizVel, bool wantJump, World world)
        {
            bool wasOnGround = OnGround;

            // Sample water state once per tick — both the "any contact"
            // version (drives swim physics) and the "head submerged" version
            // (drives breathing / drowning). Cached on the player so the
            // renderer's HUD + damage path reuses these without re-scanning.
            bool inWater = ScanInWater(world, fromY: Position.Y, toY: Position.Y + Height);
            bool headInWater = ScanInWater(world,
                fromY: Position.Y + EyeHeight - 0.1f,
                toY:   Position.Y + EyeHeight + 0.1f);
            WasInWater = inWater;
            WasHeadInWater = headInWater;

            // Horizontal velocity is driven directly by input (snappy,
            // Minecraft-like). In water we scale it down so swimming reads
            // sluggish vs. walking on land.
            float horizScale = inWater ? WaterMoveScale : 1f;
            Velocity.X = wishHorizVel.X * horizScale;
            Velocity.Z = wishHorizVel.Z * horizScale;

            // Vertical: water uses a smaller gravity and clamps both signs
            // (drag), so you sink slowly and can't free-fall through a deep
            // pool. Holding Space accelerates upward while submerged — the
            // continuous accel with a soft terminal feels closer to Alpha's
            // "tap-tap-tap to surface" than a single jump impulse would.
            if (inWater)
            {
                Velocity.Y -= WaterGravity * dt;
                if (wantJump) Velocity.Y += SwimUpAccel * dt;
                if (Velocity.Y < -WaterMaxFall) Velocity.Y = -WaterMaxFall;
                if (Velocity.Y >  WaterMaxRise) Velocity.Y =  WaterMaxRise;
            }
            else
            {
                Velocity.Y -= Gravity * dt;
                if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;
                if (wantJump && OnGround)
                {
                    Velocity.Y = JumpSpeed;
                    OnGround = false;
                }
            }

            var step = Velocity * dt;
            MoveAxis(0, step.X, world);
            MoveAxis(1, step.Y, world);
            MoveAxis(2, step.Z, world);

            UpdateFallTracking(wasOnGround, world);

            // Bob phase advances while submerged; offset eases back to zero
            // once you surface so the camera doesn't snap. Amplitude only
            // applies when actually moving in water — standing still in
            // shallow water shouldn't make the screen wobble.
            if (inWater)
            {
                SwimBobPhase += SwimBobFrequency * dt;
                float speedFrac = (float)Math.Min(1.0, Math.Sqrt(
                    Velocity.X * Velocity.X + Velocity.Z * Velocity.Z) / WalkSpeed);
                float target = (float)Math.Sin(SwimBobPhase) * SwimBobAmplitude * speedFrac;
                SwimBobOffset += (target - SwimBobOffset) * Math.Min(1f, 8f * dt);
            }
            else
            {
                SwimBobOffset += (0f - SwimBobOffset) * Math.Min(1f, 8f * dt);
            }
        }

        private void UpdateFallTracking(bool wasOnGround, World world)
        {
            if (!OnGround)
            {
                // Track the high-water mark for the current airborne arc.
                if (Position.Y > _fallPeakY) _fallPeakY = Position.Y;
                return;
            }

            if (!wasOnGround)
            {
                // Just landed. Emit the fall distance unless the player is in
                // water — water cancels fall damage (classic Alpha rule).
                // Re-scan at the post-move position so a one-tick plunge into
                // water from above still cancels: WasInWater snapshots BEFORE
                // we moved this tick, and falls fast enough to clear the
                // surface in a single sub-step would otherwise still hurt.
                float dist = _fallPeakY - Position.Y;
                if (dist > 0f && !IsInWater(world)) LastFallDistance = dist;
            }
            _fallPeakY = Position.Y;
        }

        public bool IsInWater(World world) =>
            ScanInWater(world, Position.Y, Position.Y + Height);

        // Scans the AABB at a given Y range for any water cell (source or
        // flowing — both count for buoyancy and breathing). Water reach was
        // bumped to 7 so flowing cells are common; treating them identically
        // to source water keeps swim physics sane in any flooded area.
        private bool ScanInWater(World world, float fromY, float toY)
        {
            float minX = Position.X - HalfWidth, maxX = Position.X + HalfWidth;
            float minZ = Position.Z - HalfWidth, maxZ = Position.Z + HalfWidth;
            int bx0 = (int)Math.Floor(minX);
            int bx1 = (int)Math.Floor(maxX - 1e-5f);
            int by0 = (int)Math.Floor(fromY);
            int by1 = (int)Math.Floor(toY - 1e-5f);
            int bz0 = (int)Math.Floor(minZ);
            int bz1 = (int)Math.Floor(maxZ - 1e-5f);
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            for (int z = bz0; z <= bz1; z++)
            {
                var b = world.GetBlock(x, y, z);
                if (b == BlockType.Water || b == BlockType.FlowingWater) return true;
            }
            return false;
        }

        public void TakeDamage(int amount)
        {
            if (amount <= 0 || Health <= 0) return;
            Health -= amount;
            if (Health < 0) Health = 0;
        }

        public void HealFull()
        {
            Health = MaxHealth;
            Air = MaxAir;
            LastFallDistance = 0f;
            _fallPeakY = Position.Y;
        }

        private void MoveAxis(int axis, float delta, World world)
        {
            if (Math.Abs(delta) < 1e-6f)
            {
                if (axis == 1 && delta == 0f)
                {
                    // Keep OnGround accurate: test a tiny probe downward.
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

        private bool IsStandingOnSomething(World world)
        {
            var probe = Position;
            probe.Y -= 1e-3f;
            return Collides(probe, world);
        }

        private static float GetAxis(Vector3 v, int axis) =>
            axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);

        private static void SetAxis(ref Vector3 v, int axis, float val)
        {
            if (axis == 0) v.X = val;
            else if (axis == 1) v.Y = val;
            else v.Z = val;
        }

        private static bool Collides(Vector3 pos, World world)
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
