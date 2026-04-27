using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // The three Tier 3 #12 passive mobs alongside the existing Pig.
    // All share PassiveMob's wander AI + gravity + hurt-flash + AABB
    // physics — the per-mob differentiation is purely tunables (HP,
    // walk speed, hitbox dims), per-mob death drops, and (Chicken
    // only) an egg-lay timer that ticks alongside the base wander.
    // Render shape is dispatched on concrete type by GameRenderer's
    // RenderPassives.

    // Cow — quadruped, Alpha hitbox 0.9 m wide × 1.4 m tall. Same HP
    // (10) as Pig, slightly slower (1.0 m/s — heavier-looking saunter).
    // Drops in Alpha 1.1.2_01: 0..2 Leather + 1..3 Raw Porkchop. Beef
    // (Raw + Cooked) wasn't added until Beta 1.8 (2011-09-15), so the
    // Alpha-era cow shared the pig drop. We match the era explicitly
    // — the roadmap entry calls this out.
    internal sealed class Cow : PassiveMob
    {
        public const float HitboxHalfWidth = 0.45f;
        public const float HitboxHeight    = 1.4f;

        public Cow(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth => 10;
        public override float WalkSpeed => 1.0f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 0..2 Leather + 1..3 Raw Porkchop (Alpha 1.1.2_01).
            int leather = _rng.Next(0, 3);
            for (int i = 0; i < leather; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.6f, 0),
                    BlockType.Leather, 1,
                    RandomScatterVelocity());
            }
            int meat = 1 + _rng.Next(3);
            for (int i = 0; i < meat; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.6f, 0),
                    BlockType.RawPorkchop, 1,
                    RandomScatterVelocity());
            }
        }
    }

    // Sheep — quadruped slightly shorter than Cow, Alpha hitbox
    // 0.9 m wide × 1.3 m tall. HP 8, walk 1.0 m/s. Drops 1 Wool block
    // on death (Alpha 1.1.2 sheep dropped a single white wool — colour
    // variants and shears arrived later, so we ship one default Wool
    // block). The block is passed through the standard drop path so
    // it bobs + can be picked up like any other block-on-ground.
    internal sealed class Sheep : PassiveMob
    {
        public const float HitboxHalfWidth = 0.45f;
        public const float HitboxHeight    = 1.3f;

        public Sheep(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth => 8;
        public override float WalkSpeed => 1.0f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // Single Wool block. Alpha sheep didn't drop multiple wools
            // until shearing existed, and shearing shipped after our era.
            drops.SpawnDrop(
                Position + new Vector3(0, 0.5f, 0),
                BlockType.Wool, 1,
                RandomScatterVelocity());
        }
    }

    // Chicken — small biped, Alpha hitbox 0.4 m wide × 0.7 m tall. HP 4,
    // walk 1.6 m/s (skittish, faster than the quadrupeds). Drops 0..2
    // Feather on death + lays an Egg every ~5..10 minutes while alive.
    //
    // Egg-lay: Alpha rolls a per-tick chance equivalent to "every
    // 6000..12000 game ticks" (5..10 minutes at 20 Hz). We approximate
    // by counting down a per-instance timer and dropping an Egg into the
    // world via the drop sink when it expires. Timer reseeds randomly
    // on each lay so successive eggs aren't perfectly periodic.
    //
    // The drop sink is passed in via the renderer's TickPassives loop —
    // chicken updates take it as a parameter (extending the base Update
    // signature would force every passive to thread the sink through;
    // simpler to override Update and have the renderer pass the sink
    // separately when the mob is a Chicken).
    internal sealed class Chicken : PassiveMob
    {
        public const float HitboxHalfWidth = 0.2f;
        public const float HitboxHeight    = 0.7f;

        // Egg-lay window. 6000..12000 ticks at 20 Hz = 300..600 s.
        public const float EggLayMinSeconds = 300f;
        public const float EggLayMaxSeconds = 600f;

        // Countdown to the next egg lay. Init randomised so a freshly-
        // spawned coop doesn't lay all at once.
        public float EggTimer;

        public Chicken(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            EggTimer = SampleEggInterval();
        }

        public override int   MaxHealth => 4;
        public override float WalkSpeed => 1.6f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            int feathers = _rng.Next(0, 3);
            for (int i = 0; i < feathers; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.4f, 0),
                    BlockType.Feather, 1,
                    RandomScatterVelocity());
            }
        }

        // Tick the egg-lay timer. Called by the renderer's TickPassives
        // alongside Update. When the timer expires we drop an Egg into
        // the world at the chicken's position with a small upward
        // impulse, then re-roll the next interval.
        public void TickEggLay(float dt, IDropSink drops)
        {
            if (IsDead) return;
            EggTimer -= dt;
            if (EggTimer <= 0f)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.3f, 0),
                    BlockType.Egg, 1,
                    new Vector3(0f, 1.5f, 0f));
                EggTimer = SampleEggInterval();
            }
        }

        private float SampleEggInterval()
            => EggLayMinSeconds + (float)_rng.NextDouble() * (EggLayMaxSeconds - EggLayMinSeconds);
    }
}
