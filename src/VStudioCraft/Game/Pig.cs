using OpenTK;

namespace VStudioCraft.Game
{
    // First passive mob (Alpha 1.1.2_01: pig was the first non-player
    // entity). Shorter and wider than the player AABB — Alpha's pig
    // hitbox is 0.9 m tall × 0.9 m wide.
    //
    // The wander AI, gravity, hurt-flash, and TakeDamage all live on the
    // PassiveMob base now (see PassiveMob.cs); this file is the per-mob
    // tunables (HP cap, walk speed, hitbox dims, drops).
    //
    // Drops: 1..3 RawPorkchop on death, scatter-velocity as a small
    // outward+upward impulse so multiple porkchops fan instead of
    // stacking. Cooked porkchops are obtained by smelting the raw drop
    // (see FurnaceRecipes).
    internal sealed class Pig : PassiveMob
    {
        public const float HitboxHalfWidth = 0.45f;
        public const float HitboxHeight    = 0.9f;

        public Pig(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth => 10;
        public override float WalkSpeed => 1.2f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 1..3 RawPorkchop — Alpha 1.1.2 drop range.
            int count = 1 + _rng.Next(3);
            for (int i = 0; i < count; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.4f, 0),
                    BlockType.RawPorkchop, 1,
                    RandomScatterVelocity());
            }
        }
    }
}
