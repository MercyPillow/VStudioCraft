using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // First passive mob (Alpha 1.1.2_01: pig was the first non-player
    // entity). Shorter and wider than the player AABB — Alpha's pig
    // hitbox is 0.9 m tall × 0.9 m wide.
    //
    // AI is intentionally simple ("wander"): every WanderInterval seconds
    // we pick a fresh random heading + idle/walk decision; while walking
    // we set Velocity.X/Z from the heading at WalkSpeed; while idling we
    // zero horizontal velocity. Gravity + collision is the same Entity
    // walker the player uses, so a pig walking off a cliff falls and a
    // pig bumping a wall just stops moving until the next wander tick.
    //
    // Health is the Alpha pig's 10 HP (5 hearts). Combat: GameRenderer's
    // LMB path scans for a pig under the look-vector before doing the
    // block raycast, applies damage based on the held tool kind, and on
    // death the pig spawns a RawPorkchop drop (Alpha 319) at its feet.
    internal sealed class Pig : Entity
    {
        public const float PigHalfWidth = 0.45f;
        public const float PigHeight = 0.9f;
        public const float WalkSpeed = 1.2f;          // gentle saunter
        public const float Gravity = 28f;             // matches Player
        public const float MaxFallSpeed = 78f;
        public const int MaxHealth = 10;
        public const float WanderInterval = 5f;       // re-pick every 5 s
        public const float AttackKnockback = 5.5f;    // upward pop on hit

        // Yaw the pig is currently facing (radians, +Z = 0). Driven by
        // the wander AI and read by the renderer to orient the body.
        public float Yaw;

        public int Health = MaxHealth;
        public bool IsDead => Health <= 0;

        // Brief red-flash timer for the renderer to draw a damage tint.
        // Refreshed on TakeDamage; decays each tick.
        public float HurtTimer;
        public const float HurtFlashSeconds = 0.30f;

        // Time until the next wander decision. Starts randomised so a
        // freshly-spawned crowd of pigs doesn't all change direction
        // in lockstep.
        private float _wanderTimer;
        private bool _walking;

        // Stable per-pig RNG so wander decisions don't churn the game's
        // shared RNG (which we want to keep deterministic for terrain).
        private readonly Random _rng;

        public Pig(Vector3 spawnPos, int seed)
        {
            // Override the default Entity AABB shape to match Alpha pig.
            HalfWidth = PigHalfWidth;
            Height = PigHeight;
            Position = spawnPos;
            _rng = new Random(seed);
            _wanderTimer = (float)(_rng.NextDouble() * WanderInterval);
            Yaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
        }

        public void Update(float dt, World world)
        {
            if (IsDead) return;

            if (HurtTimer > 0f) { HurtTimer -= dt; if (HurtTimer < 0f) HurtTimer = 0f; }

            // Wander tick: every WanderInterval the pig flips between
            // walking + idle, and if walking picks a fresh heading. The
            // 60% walk / 40% idle split keeps a paddock looking natural
            // — some pigs are moving, some are standing.
            _wanderTimer -= dt;
            if (_wanderTimer <= 0f)
            {
                _wanderTimer = WanderInterval;
                _walking = _rng.NextDouble() < 0.6;
                if (_walking) Yaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
            }

            // Horizontal velocity from current heading. Stop entirely if
            // we hit a wall last tick (Velocity.X/Z would be zero) — the
            // wander tick will pick a new direction shortly.
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

            // Gravity + terminal velocity, identical to player land physics.
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
    }
}
