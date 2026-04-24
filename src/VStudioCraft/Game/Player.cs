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

        public Vector3 Position;
        public Vector3 Velocity;
        public bool OnGround;

        public void Update(float dt, Vector3 wishHorizVel, bool wantJump, World world)
        {
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
