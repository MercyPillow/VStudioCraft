using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // Shared base for the Tier 3 #9 + #12 passive mobs (Pig, Cow, Sheep,
    // Chicken). Mirror of HostileMob: the AABB physics + wander AI +
    // hurt-flash + per-instance RNG live here so each concrete subclass
    // only writes the bits that actually differ (HP cap, walk speed,
    // hitbox dims, death drops, and any per-mob tick state — Chicken
    // overrides Update to advance its egg-lay timer on top of the
    // base wander tick).
    //
    // AI: every WanderInterval seconds we pick a fresh random heading +
    // walk/idle decision (60/40 split). While walking we drive horizontal
    // velocity from the heading at WalkSpeed; while idling we zero it.
    // Gravity + collision is the same Entity walker the player uses, so
    // a passive walking off a cliff falls and one bumping a wall just
    // stops moving until the next wander tick.
    //
    // Subclasses override:
    //   - MaxHealth (HP cap)
    //   - WalkSpeed (m/s)
    //   - SpawnDeathDrops(IDropSink) (per-mob drops)
    //   - HitboxHalfWidth / HitboxHeight (set in ctor — kept as instance
    //     fields on Entity so the AABB walker can read them)
    //   - Optionally: Update(dt, world) — Chicken adds an egg-lay timer
    //     on top of the base wander; Pig/Cow/Sheep just call base.
    //
    // Light-level gating, surface check, and rendering are handled by
    // the world spawn passes + GameRenderer — same separation as
    // HostileMob.
    internal abstract class PassiveMob : Entity
    {
        public const float Gravity            = 28f;   // matches Player + HostileMob
        public const float MaxFallSpeed       = 78f;
        public const float HurtFlashSeconds   = 0.30f;
        public const float WanderInterval     = 5f;
        public const float AttackKnockback    = 5.5f;  // upward pop on hit feedback

        // Yaw the mob is currently facing (radians, +Z = 0). Driven by
        // the wander AI and read by the renderer to orient the body.
        public float Yaw;

        // Health + hurt-flash. Hurt flash refreshes on TakeDamage and
        // decays to 0 in HurtFlashSeconds; the renderer ramps a red
        // tint over body texture while it's > 0.
        public int Health;
        public bool IsDead => Health <= 0;
        public float HurtTimer;

        // Wander state. _walking flips every WanderInterval; _wanderTimer
        // counts down to the next decision. Init randomised so a freshly-
        // spawned crowd doesn't change direction in lockstep.
        protected float _wanderTimer;
        protected bool  _walking;

        // Per-mob deterministic RNG so wander decisions don't churn the
        // world's shared RNG (which we want kept deterministic for
        // terrain-gen + flora scatter).
        protected readonly Random _rng;

        protected PassiveMob(Vector3 spawnPos, int seed)
        {
            Position = spawnPos;
            _rng = new Random(seed);
            _wanderTimer = (float)(_rng.NextDouble() * WanderInterval);
            Yaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
            Health = MaxHealth;
        }

        public abstract int   MaxHealth { get; }
        public abstract float WalkSpeed { get; }

        // Default tick — wander + gravity + integrate. Chicken overrides
        // to advance its egg timer alongside the base call.
        public virtual void Update(float dt, World world)
        {
            if (IsDead) return;

            if (HurtTimer > 0f) { HurtTimer -= dt; if (HurtTimer < 0f) HurtTimer = 0f; }

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

            Velocity.Y -= Gravity * dt;
            if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;

            IntegrateMotion(dt, world);
        }

        public void TakeDamage(int amount)
        {
            if (amount <= 0 || Health <= 0) return;
            Health -= amount;
            if (Health < 0) Health = 0;
            HurtTimer = HurtFlashSeconds;
            // Tiny upward pop so a hit reads as feedback.
            Velocity.Y = AttackKnockback;
        }

        // World-space AABB used by the click-on-mob raycast in
        // GameRenderer. Returned as (min, max).
        public void GetAabb(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(Position.X - HalfWidth, Position.Y, Position.Z - HalfWidth);
            max = new Vector3(Position.X + HalfWidth, Position.Y + Height, Position.Z + HalfWidth);
        }

        // Death drops — called once on the tick that crosses Health to
        // 0. Subclasses append item drops (or a wool block) via the
        // shared IDropSink shim defined alongside HostileMob.
        public abstract void SpawnDeathDrops(IDropSink drops);

        // Helper for subclass drop scatter — pulls a 1.5..2.5 m/s
        // outward+up impulse so the drops fan around the corpse instead
        // of stacking on the spot. Mirrors the HostileMobs.cs helper of
        // the same name (kept inline for symmetry rather than promoted
        // to a shared static — drop scatter is the only ctor-style site
        // that needs it).
        protected Vector3 RandomScatterVelocity()
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
