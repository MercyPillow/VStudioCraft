using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // Shared base for the Tier 3 #10 hostile mobs (Zombie, Skeleton,
    // Spider, Creeper). Pulls the bookkeeping that's identical across
    // all four — health + hurt flash, yaw, attack-cooldown, target
    // tracking, line-of-sight chase, melee-range hit on the player —
    // into one place, so each subclass only writes the bits that are
    // actually different (HP cap, walk speed, attack damage, drops, AI
    // tweaks).
    //
    // AI: each tick, if the player is within DetectRange, the mob steers
    // toward the player on the XZ plane (no path planning yet — true
    // A* on a 16-block window is roadmap-deferred; for V1 a straight
    // line + auto-jump on a 1-block lip behaves correctly in the
    // overwhelming majority of overworld-relief situations). If the
    // player is within AttackRange the mob calls TryAttack which checks
    // a per-instance attack-cooldown timer and routes a TakeDamage
    // call to the player. Out of detect range, the mob wanders the
    // same way Pig does (random heading every 5 s).
    //
    // Subclasses override:
    //   - MaxHealth (HP cap)
    //   - WalkSpeed (m/s)
    //   - DetectRange (blocks the mob aggro-radius)
    //   - AttackRange (blocks for melee)
    //   - AttackDamage (HP per hit on the player)
    //   - AttackCooldownSeconds (gate on TryAttack)
    //   - SpawnDrops (called once on death — appends to the drop list)
    //
    // Light-level gating, AABB shape, gravity, and rendering are
    // handled by callers (World.SpawnHostilesInChunk, GameRenderer).
    internal abstract class HostileMob : Entity
    {
        public const float Gravity = 28f;       // matches Player + Pig
        public const float MaxFallSpeed = 78f;
        public const float HurtFlashSeconds = 0.30f;
        public const float WanderInterval = 5f;

        // Yaw the mob faces (radians, +Z = 0). Drives the body model's
        // orientation in the renderer + the chase-step heading.
        public float Yaw;

        // Health + hurt-flash. Hurt flash refreshes on TakeDamage and
        // decays to 0 in HurtFlashSeconds; the renderer ramps a red
        // tint over body texture while it's > 0.
        public int Health;
        public bool IsDead => Health <= 0;
        public float HurtTimer;

        // Attack cooldown — counts down every tick; TryAttack only
        // fires when this is ≤ 0 and refills it on a successful hit.
        public float AttackCooldown;

        // Wander timer + walk flag, used when the player isn't in
        // detect range (mob falls back to Pig-style aimless wander).
        protected float _wanderTimer;
        protected bool _walking;

        // Per-mob deterministic RNG so wander decisions don't churn
        // any shared global RNG.
        protected readonly Random _rng;

        protected HostileMob(Vector3 spawnPos, int seed)
        {
            Position = spawnPos;
            _rng = new Random(seed);
            _wanderTimer = (float)(_rng.NextDouble() * WanderInterval);
            Yaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
            Health = MaxHealth;
        }

        // Per-mob tunables. Subclasses override these to differentiate
        // the four hostiles.
        public abstract int MaxHealth { get; }
        public abstract float WalkSpeed { get; }
        public abstract float DetectRange { get; }
        public abstract float AttackRange { get; }
        public abstract int AttackDamage { get; }
        public abstract float AttackCooldownSeconds { get; }

        // Standard tick. Caller passes the current player position so we
        // can do the chase decision without HostileMob taking a hard
        // dependency on Player (keeps the testing surface small + lets
        // the renderer freeze the mob during modals by just not calling
        // Update, same as Pig.TickPigs).
        public void Update(float dt, World world, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead) return;

            if (HurtTimer > 0f)       { HurtTimer       -= dt; if (HurtTimer       < 0f) HurtTimer       = 0f; }
            if (AttackCooldown > 0f)  { AttackCooldown  -= dt; if (AttackCooldown  < 0f) AttackCooldown  = 0f; }

            float dx = playerPos.X - Position.X;
            float dz = playerPos.Z - Position.Z;
            float horizDist = (float)Math.Sqrt(dx * dx + dz * dz);

            if (horizDist <= DetectRange)
            {
                // Chase: face the player and walk toward them on XZ.
                // Yaw is computed atan2-style so the renderer can
                // orient the body without any per-axis cleanup.
                Yaw = (float)Math.Atan2(dx, dz);
                if (horizDist > AttackRange * 0.85f)
                {
                    Velocity.X = (dx / Math.Max(horizDist, 1e-4f)) * WalkSpeed;
                    Velocity.Z = (dz / Math.Max(horizDist, 1e-4f)) * WalkSpeed;
                }
                else
                {
                    // Within attack range — stop walking so we don't
                    // bump-shove the player through walls.
                    Velocity.X = 0f;
                    Velocity.Z = 0f;
                }

                // Auto-jump: if we're on the ground and there's a
                // 1-block lip in front of us at the body's chest level,
                // hop. Single-block step-up is enough for terrain
                // relief without needing a real path planner. Skipped
                // when we're already mid-air.
                if (OnGround && horizDist > AttackRange && Math.Abs(dx) + Math.Abs(dz) > 0.1f)
                {
                    float fx = dx / Math.Max(horizDist, 1e-4f);
                    float fz = dz / Math.Max(horizDist, 1e-4f);
                    int probeX = (int)Math.Floor(Position.X + fx * (HalfWidth + 0.1f));
                    int probeZ = (int)Math.Floor(Position.Z + fz * (HalfWidth + 0.1f));
                    int probeY = (int)Math.Floor(Position.Y);
                    var lipBlock = world.GetBlock(probeX, probeY, probeZ);
                    var aboveLip = world.GetBlock(probeX, probeY + 1, probeZ);
                    if (BlockData.IsSolid(lipBlock) && !BlockData.IsSolid(aboveLip))
                    {
                        Velocity.Y = 8.5f; // matches Player jump strength
                    }
                }

                // Melee attack — gated by cooldown, fires when player
                // is in melee range. Subclass-supplied AttackDamage and
                // cooldown control the cadence.
                if (horizDist <= AttackRange && AttackCooldown <= 0f)
                {
                    damageSink.DamagePlayer(AttackDamage);
                    AttackCooldown = AttackCooldownSeconds;
                    OnAttacked(damageSink);
                }
            }
            else
            {
                // Wander — same shape as Pig.Update.
                _wanderTimer -= dt;
                if (_wanderTimer <= 0f)
                {
                    _wanderTimer = WanderInterval;
                    _walking = _rng.NextDouble() < 0.6;
                    if (_walking) Yaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
                }
                if (_walking)
                {
                    Velocity.X = (float)Math.Sin(Yaw) * WalkSpeed;
                    Velocity.Z = (float)Math.Cos(Yaw) * WalkSpeed;
                }
                else
                {
                    Velocity.X = 0f;
                    Velocity.Z = 0f;
                }
            }

            // Gravity + terminal velocity, identical to Pig + Player.
            Velocity.Y -= Gravity * dt;
            if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;

            IntegrateMotion(dt, world);
        }

        // Hook for subclasses that do something extra on a successful
        // attack (Creeper is the obvious case — its melee bump should
        // start the fuse instead of just biting). Default: nothing.
        protected virtual void OnAttacked(IPlayerDamageSink damageSink) { }

        public void TakeDamage(int amount)
        {
            if (amount <= 0 || Health <= 0) return;
            Health -= amount;
            if (Health < 0) Health = 0;
            HurtTimer = HurtFlashSeconds;
            // Tiny upward pop so the hit reads as feedback.
            Velocity.Y = 4.5f;
        }

        public void GetAabb(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(Position.X - HalfWidth, Position.Y, Position.Z - HalfWidth);
            max = new Vector3(Position.X + HalfWidth, Position.Y + Height, Position.Z + HalfWidth);
        }

        // Death drops — called once on the tick that crosses Health to
        // 0. Subclasses append item drops to the world's drop list.
        public abstract void SpawnDeathDrops(IDropSink drops);
    }

    // Decoupling shim — HostileMob calls back into the renderer to
    // damage the player without taking a hard dependency on
    // GameRenderer / Player. GameRenderer implements this on itself
    // (or wraps Player.TakeDamage in a tiny adapter).
    internal interface IPlayerDamageSink
    {
        void DamagePlayer(int amount);
    }

    // Decoupling shim — HostileMob.SpawnDeathDrops asks for a drop
    // entity to be inserted into the world without the mob class
    // knowing how the renderer's _drops list is structured.
    internal interface IDropSink
    {
        void SpawnDrop(Vector3 pos, BlockType item, int count, Vector3 velocity);
    }
}
