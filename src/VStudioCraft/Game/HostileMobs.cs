using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // The four Tier 3 #10 hostile mobs, all sharing HostileMob's chase
    // AI + attack loop + hurt flash. Differentiation is purely
    // tunables + per-mob death drops + per-mob render shape (the
    // renderer dispatches on concrete type, see GameRenderer's
    // RenderHostiles).

    // Zombie — straightforward humanoid melee mob. Player-shaped AABB
    // (matches Alpha — a zombie occupies the same volume the player
    // does), chases on sight, bumps for 2 HP every 1.0 s. Drops nothing
    // useful in Alpha 1.1.2 (zombies didn't drop feathers until later
    // versions, didn't drop iron ingots/carrots/potatoes/rotten flesh
    // until well after our era), so the death drop is empty.
    internal sealed class Zombie : HostileMob
    {
        public const float HitboxHalfWidth = 0.3f;
        public const float HitboxHeight    = 1.8f;

        public Zombie(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 1.0f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.4f;
        public override int   AttackDamage          => 2;
        public override float AttackCooldownSeconds => 1.0f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // Alpha 1.1.2_01 zombies dropped nothing on death (rotten
            // flesh was Beta 1.8). Empty by design.
        }
    }

    // Skeleton — same humanoid shape as Zombie but with the bow-drop
    // niche. Slightly faster (1.1 m/s) but lower HP (20 → matches
    // Zombie in Alpha; skeletons share the zombie HP pool). Attacks
    // at melee range only for V1 — bow combat itself is roadmap-
    // deferred to Tier 4 #17, so the skeleton currently bumps the
    // player like a zombie. The drops (Bow + Arrow) are inert
    // collectibles per the roadmap entry.
    //
    // Drop quantities pulled from Alpha 1.1.2_01: 0..2 arrows, 0..2
    // bones (we don't have Bone yet — added with later mobs); we ship
    // Bow as a 1-in-N drop to match Alpha rarity. Bone is dropped as
    // 1..2 arrows for V1 since arrows are the closer-themed alternative.
    internal sealed class Skeleton : HostileMob
    {
        public const float HitboxHalfWidth = 0.3f;
        public const float HitboxHeight    = 1.8f;

        public Skeleton(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 1.1f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.4f;
        public override int   AttackDamage          => 2;
        public override float AttackCooldownSeconds => 1.0f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 0..2 arrows — Alpha drop range. Bow itself ships only on
            // a 1-in-3 lucky death so collecting one feels meaningful.
            int arrows = _rng.Next(0, 3);
            for (int i = 0; i < arrows; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.5f, 0),
                    BlockType.Arrow, 1,
                    RandomScatterVelocity());
            }
            if (_rng.Next(3) == 0)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.5f, 0),
                    BlockType.Bow, 1,
                    RandomScatterVelocity());
            }
        }

        private Vector3 RandomScatterVelocity()
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float speed = 1.5f + (float)_rng.NextDouble() * 1.0f;
            return new Vector3(
                (float)Math.Cos(angle) * speed,
                3.0f + (float)_rng.NextDouble() * 1.5f,
                (float)Math.Sin(angle) * speed);
        }
    }

    // Spider — short, wide arachnid. Faster than the humanoids
    // (1.6 m/s) and has a much larger detect range (Alpha spider is
    // light-independent at light < 7 once aggro'd, but for V1 we just
    // give it the standard 16-block range like the others). Lower HP
    // (16). Drops 0..2 String. Attack damage matches Zombie.
    //
    // Note: Alpha spiders climb walls, but vertical pathing is a much
    // bigger surface than the chase AI we have today — wall-climb is
    // documented as missing in features.md and slated for the
    // pathfinding overhaul that would also unlock A*.
    internal sealed class Spider : HostileMob
    {
        public const float HitboxHalfWidth = 0.7f;
        public const float HitboxHeight    = 0.9f;

        public Spider(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth             => 16;
        public override float WalkSpeed             => 1.6f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.6f;
        public override int   AttackDamage          => 2;
        public override float AttackCooldownSeconds => 1.0f;

        public override void SpawnDeathDrops(IDropSink drops)
        {
            int strings = _rng.Next(0, 3);
            for (int i = 0; i < strings; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.4f, 0),
                    BlockType.String, 1,
                    RandomScatterVelocity());
            }
        }

        private Vector3 RandomScatterVelocity()
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float speed = 1.5f + (float)_rng.NextDouble() * 1.0f;
            return new Vector3(
                (float)Math.Cos(angle) * speed,
                3.0f + (float)_rng.NextDouble() * 1.5f,
                (float)Math.Sin(angle) * speed);
        }
    }

    // Creeper — silent ambush mob. Approaches the player and starts a
    // fuse when within attack range; the fuse blows on FuseTime
    // seconds. Real Alpha behaviour is "explode = blast block damage
    // + huge player damage in a radius"; the explosion algorithm is
    // Tier 8 #43, so for V1 the fuse just does flat 6 HP damage on
    // detonation (a chunky Alpha-creeper hit) and removes the mob
    // without modifying terrain. The visual fuse-flash is rendered
    // via FuseTimer in HostileMob's hurt-tint path (creeper-specific
    // colour ramp from green to white-hot).
    //
    // Drops 0..1 Gunpowder.
    internal sealed class Creeper : HostileMob
    {
        public const float HitboxHalfWidth = 0.3f;
        public const float HitboxHeight    = 1.7f;
        public const float FuseTime        = 1.5f;
        public const int   ExplosionDamage = 6;

        // Fuse-state. -1 = not lit. >=0 = countdown in seconds.
        public float FuseTimer = -1f;
        // Latched on the tick that detonation fires, so the renderer
        // can despawn the model and the world-tick can issue the
        // damage hit. We can't directly call DamagePlayer from
        // OnAttacked (that's where the fuse starts) without bypassing
        // the cooldown — instead we let the fuse run independently of
        // the main attack loop.
        public bool DetonatedThisFrame;

        public Creeper(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 1.05f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.5f;
        // Creepers don't melee — the "attack" hook just starts the
        // fuse. Setting AttackDamage to 0 means HostileMob.Update's
        // standard TryAttack does nothing and we run the fuse via
        // OnAttacked + a per-tick fuse decrement.
        public override int   AttackDamage          => 0;
        public override float AttackCooldownSeconds => 1.0f;

        protected override void OnAttacked(IPlayerDamageSink damageSink)
        {
            // First melee contact lights the fuse. Re-arming on every
            // bump would chain-extend the fuse forever, so we only
            // start it if it's not already running.
            if (FuseTimer < 0f) FuseTimer = FuseTime;
        }

        // Per-tick fuse advance. Called by the world tick alongside
        // Update; separated so the fuse can keep ticking even if the
        // player runs out of attack range (Alpha: once a creeper is
        // primed, walking away doesn't always defuse it).
        public void TickFuse(float dt, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead || FuseTimer < 0f) return;
            FuseTimer -= dt;
            // Defuse on retreat (Alpha behaviour for the early creeper
            // before priming was reworked): if the player walked out
            // of attack range during the fuse, cancel. Same Y gate as
            // the standard attack — a creeper in a cave underneath the
            // player must NOT detonate up through the rock; if the
            // player has gone vertically out of reach, cancel the fuse.
            float dx = playerPos.X - Position.X;
            float dy = playerPos.Y - Position.Y;
            float dz = playerPos.Z - Position.Z;
            const float VerticalReach = 1.5f;
            if (dx * dx + dz * dz > AttackRange * AttackRange * 4f
                || Math.Abs(dy) > VerticalReach)
            {
                FuseTimer = -1f;
                return;
            }
            if (FuseTimer <= 0f)
            {
                damageSink.DamagePlayer(ExplosionDamage);
                Health = 0;
                DetonatedThisFrame = true;
            }
        }

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // Detonation eats the corpse — no drops on explosion-death.
            // Player kills (sword/punch) drop 0..2 gunpowder.
            if (DetonatedThisFrame) return;
            int powder = _rng.Next(0, 3);
            for (int i = 0; i < powder; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.4f, 0),
                    BlockType.Gunpowder, 1,
                    RandomScatterVelocity());
            }
        }

        private Vector3 RandomScatterVelocity()
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float speed = 1.5f + (float)_rng.NextDouble() * 1.0f;
            return new Vector3(
                (float)Math.Cos(angle) * speed,
                3.0f + (float)_rng.NextDouble() * 1.5f,
                (float)Math.Sin(angle) * speed);
        }
    }
}
