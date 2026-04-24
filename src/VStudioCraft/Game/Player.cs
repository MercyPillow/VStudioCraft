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

        public Vector3 Position;
        public Vector3 Velocity;
        public bool OnGround;

        // Survival HP. Creative mode keeps this pinned at MaxHealth.
        public int Health = MaxHealth;
        public int Hunger = MaxHunger;
        public bool IsDead => Health <= 0;

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

            // Horizontal velocity is driven directly by input (snappy, Minecraft-like).
            Velocity.X = wishHorizVel.X;
            Velocity.Z = wishHorizVel.Z;

            Velocity.Y -= Gravity * dt;
            if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;

            if (wantJump && OnGround)
            {
                Velocity.Y = JumpSpeed;
                OnGround = false;
            }

            var step = Velocity * dt;
            MoveAxis(0, step.X, world);
            MoveAxis(1, step.Y, world);
            MoveAxis(2, step.Z, world);

            UpdateFallTracking(wasOnGround, world);
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
                float dist = _fallPeakY - Position.Y;
                if (dist > 0f && !IsInWater(world)) LastFallDistance = dist;
            }
            _fallPeakY = Position.Y;
        }

        public bool IsInWater(World world)
        {
            float minX = Position.X - HalfWidth, maxX = Position.X + HalfWidth;
            float minY = Position.Y,              maxY = Position.Y + Height;
            float minZ = Position.Z - HalfWidth, maxZ = Position.Z + HalfWidth;
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
                if (world.GetBlock(x, y, z) == BlockType.Water) return true;
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
