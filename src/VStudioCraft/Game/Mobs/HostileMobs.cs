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
            // flesh was Beta 1.8). The only legitimate drop in this
            // era is the rare chainmail piece — Alpha zombies/skeletons
            // were the only source of chain armor since it had no
            // crafting recipe. Roll independently per-piece at 0.5%.
            TryDropChainmail(drops);
        }

        private void TryDropChainmail(IDropSink drops)
        {
            // 0.5% chance per piece, rolled independently for each of
            // the four chainmail slots — matches Alpha "rare drop"
            // rarity for chain armor. Player can in principle get
            // multiple pieces in one kill (very unlikely but possible),
            // mirroring Alpha behaviour.
            BlockType[] pieces =
            {
                BlockType.ChainmailHelmet,
                BlockType.ChainmailChestplate,
                BlockType.ChainmailLeggings,
                BlockType.ChainmailBoots,
            };
            for (int i = 0; i < pieces.Length; i++)
            {
                if (_rng.Next(200) == 0) // 1/200 = 0.5%
                {
                    drops.SpawnDrop(
                        Position + new Vector3(0, 0.5f, 0),
                        pieces[i], 1,
                        RandomScatterVelocity());
                }
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

    // Tier 8 #51 V6 — Zombie Pigman. Humanoid Nether-native mob,
    // canonical Alpha 1.2.0 Halloween Update creature. Neutral aggro:
    // wanders idly until the player attacks one, at which point the
    // hit pigman + every other pigman within ~16 cells flips to
    // hostile and chases for 30 seconds. After the timer expires, or
    // if the player flees out of detect range, the mob reverts to
    // neutral wandering.
    //
    // V6 ships the entity class + neutral/hostile state machine + drops;
    // the "alert nearby pigmen on hit" pack-aggro is V7 polish (each
    // one tracks its own AggroTimer independently for now). Renders
    // as a humanoid via the existing DrawHumanoid path with a pink-
    // green skin tone (zombie body + pig face).
    internal sealed class ZombiePigman : HostileMob
    {
        public const float HitboxHalfWidth = 0.3f;
        public const float HitboxHeight    = 1.8f;
        // Seconds the mob stays hostile after being hit. Canonical
        // Alpha is ~30 s; matches "the player can outrun a single
        // pigman" feel when there's no nearby pack.
        public const float AggroDurationSeconds = 30.0f;

        public ZombiePigman(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 1.0f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.4f;
        public override int   AttackDamage          => 5;   // canonical: gold-sword damage
        public override float AttackCooldownSeconds => 1.0f;

        // Aggro state. AggroTimer counts DOWN from AggroDurationSeconds
        // when set; while > 0, the base HostileMob.Update treats the
        // player as in-range and chases. When the timer expires, we
        // revert to wander mode by overriding Update.
        public float AggroTimer;
        public bool IsAggro => AggroTimer > 0f;

        public override void Update(float dt, World world, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead) return;
            if (AggroTimer > 0f)
            {
                AggroTimer -= dt;
                if (AggroTimer < 0f) AggroTimer = 0f;
                // Hostile branch — same chase loop the base class
                // runs.
                base.Update(dt, world, playerPos, damageSink);
                return;
            }

            // Neutral wander. Skips the base class's chase / attack
            // logic by directly running the wander branch shape (same
            // as Pig.Update). Updates physics with gravity + AABB
            // integration.
            if (HurtTimer > 0f)      { HurtTimer      -= dt; if (HurtTimer      < 0f) HurtTimer      = 0f; }
            if (AttackCooldown > 0f) { AttackCooldown -= dt; if (AttackCooldown < 0f) AttackCooldown = 0f; }

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

        // Override TakeDamage to flip aggro on hit. Player swings a
        // sword at a pigman → it goes hostile for AggroDurationSeconds.
        // Subsequent hits during that window REFRESH the timer to
        // full duration so a player who keeps attacking can't
        // wait out the chase by stalling.
        public new void TakeDamage(int amount)
        {
            base.TakeDamage(amount);
            AggroTimer = AggroDurationSeconds;
        }

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 0..2 cooked porkchop drops — pigmen are pork-themed and
            // we don't have rotten flesh in this build, so cooked
            // pork is the closest canonical Alpha drop. Gold ingot
            // drop (canonical 1-in-4 in Alpha) routes through the
            // existing GoldIngot item id.
            int pork = _rng.Next(0, 3);
            for (int i = 0; i < pork; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.5f, 0),
                    BlockType.CookedPorkchop, 1,
                    RandomScatterVelocity());
            }
            if (_rng.Next(4) == 0)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.5f, 0),
                    BlockType.GoldIngot, 1,
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

    // Tier 8 #51 V7 — Ghast: large flying Nether mob that hovers above
    // the netherrack mass and lobs slow fireballs at the player. Most
    // mechanically novel mob in the codebase so far:
    //
    //   * Flying — no gravity, hovers and drifts horizontally. The base
    //     HostileMob.Update assumes a grounded chase loop; we override
    //     Update entirely and skip IntegrateMotion's gravity-bound path.
    //   * Hitbox — 3×3×3 blocks (HalfWidth=1.5, Height=3). The body
    //     cube is rendered at 3x model scale on a 1m canonical
    //     mesh, giving a 3m visible body that fits the AABB; the
    //     dangling tentacle silhouette extends below as a cosmetic
    //     overshoot. Big target compensates for the long detect
    //     range — an alert player can shoot one down with arrows
    //     before it gets a fireball off.
    //   * Squishy — only 10 HP. Two well-aimed bow shots or three
    //     close-range melee swings put one down. Canonical Alpha.
    //   * Fires a FireballProjectile every FireCooldownSeconds when
    //     LOS to the player is clear. The fireball travels in a
    //     straight line at 10 m/s and explodes (radius 2) on contact —
    //     handled by the renderer's _fireballs tick.
    //   * Mob spawn: 1-in-3000 per nether-surface column, only above
    //     y > NetherrackTop+8 in open space. The renderer lifts the
    //     spawn altitude to a hover band (NetherrackTop+12..CeilingBaseY-8)
    //     when the chunk is generated.
    //
    // The chase / attack hooks of the base class are unused because
    // we don't melee — we just call into the renderer's IFireballSink
    // when the cooldown ticks down with a player in range and LOS
    // clear. Drops 0..2 Gunpowder on death (canonical Alpha).
    internal sealed class Ghast : HostileMob
    {
        public const float HitboxHalfWidth = 1.5f;
        public const float HitboxHeight    = 3.0f;
        public const float DriftSpeed      = 0.6f;   // m/s lazy lateral drift
        public const float BobAmplitude    = 0.4f;   // m vertical bob from sin wave
        public const float BobPeriodSec    = 4.0f;
        public const float FireCooldownSeconds = 3.0f;
        public const float FireDetectRange     = 36f; // matches nether visibility band
        public const float FireDetectRangeSq   = FireDetectRange * FireDetectRange;

        // Cooldown timer between fireballs. Counts down each tick;
        // refilled to FireCooldownSeconds when the ghast actually
        // fires (in-range + LOS clear).
        public float FireCooldown;

        // Bob phase + drift heading. Drift heading shifts by a small
        // random offset every WanderInterval so the ghast doesn't
        // glide forever in one direction.
        private float _bobPhase;
        private float _driftYaw;
        private float _hoverY; // anchor altitude — we bob around this

        public Ghast(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            FireCooldown = (float)(_rng.NextDouble() * FireCooldownSeconds);
            _bobPhase    = (float)(_rng.NextDouble() * Math.PI * 2.0);
            _driftYaw    = (float)(_rng.NextDouble() * Math.PI * 2.0);
            _hoverY      = spawnPos.Y;
        }

        public override int   MaxHealth             => 10;
        public override float WalkSpeed             => 0f;     // unused — flight overrides
        public override float DetectRange           => FireDetectRange;
        public override float AttackRange           => 0f;     // ranged-only — no melee
        public override int   AttackDamage          => 0;
        public override float AttackCooldownSeconds => 0f;

        public override void Update(float dt, World world, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead) return;
            if (HurtTimer > 0f) { HurtTimer -= dt; if (HurtTimer < 0f) HurtTimer = 0f; }
            if (FireCooldown > 0f) FireCooldown -= dt;

            // Lateral drift — lazy random wander. Re-roll heading
            // every WanderInterval seconds so the ghast doesn't
            // glide off into the void.
            _wanderTimer -= dt;
            if (_wanderTimer <= 0f)
            {
                _wanderTimer = WanderInterval;
                _driftYaw   += ((float)_rng.NextDouble() - 0.5f) * 1.5f;
            }

            // Aim drift toward the player on XZ when in detect range
            // (gentle, not chase) so the ghast keeps slowly closing
            // in. Otherwise drift along _driftYaw.
            float dx = playerPos.X - Position.X;
            float dz = playerPos.Z - Position.Z;
            float horizDistSq = dx * dx + dz * dz;
            float yawTarget;
            if (horizDistSq <= FireDetectRangeSq * 4f)
            {
                yawTarget = (float)Math.Atan2(dx, dz);
                Yaw = yawTarget; // face player for visual feedback
            }
            else
            {
                yawTarget = _driftYaw;
            }

            // Drift slowly along yawTarget on XZ. No gravity — we
            // handle vertical via a sine bob around _hoverY rather
            // than letting Velocity.Y accumulate.
            Velocity.X = (float)Math.Sin(yawTarget) * DriftSpeed;
            Velocity.Z = (float)Math.Cos(yawTarget) * DriftSpeed;

            // Vertical bob — sin wave around the hover anchor. We
            // override Velocity.Y each tick so the ghast neither
            // falls (no gravity) nor accumulates drift.
            _bobPhase += dt * (float)(Math.PI * 2.0 / BobPeriodSec);
            if (_bobPhase > (float)(Math.PI * 2.0)) _bobPhase -= (float)(Math.PI * 2.0);
            float targetY = _hoverY + (float)Math.Sin(_bobPhase) * BobAmplitude;
            Velocity.Y = (targetY - Position.Y) / Math.Max(dt, 1e-3f);

            // Integrate XZ + Y motion through the standard collider so
            // the ghast still respects walls (a ghast pinned against
            // a netherrack ceiling shouldn't tunnel through). We do
            // NOT call gravity — Velocity.Y is driven by the bob.
            IntegrateMotion(dt, world);

            // Re-anchor _hoverY if a wall pushed us off track. Without
            // this a ghast that bumps a ceiling would oscillate around
            // the wrong anchor forever; clamping back to the actual
            // current Y on collision means the next bob cycle reads
            // from the post-collision altitude.
            if (Math.Abs(Position.Y - targetY) > BobAmplitude * 1.5f)
            {
                _hoverY = Position.Y;
            }
        }

        // Returns true if the ghast wants to fire on this tick. The
        // renderer checks this each frame; if true, it spawns a
        // FireballProjectile at the ghast's body, aimed at the
        // player, and resets FireCooldown via NotifyFired().
        // The renderer is responsible for the LOS raycast + the
        // actual fireball construction since FireballProjectile
        // lives renderer-side (in _fireballs).
        public bool WantsToFire(Vector3 playerPos)
        {
            if (IsDead) return false;
            if (FireCooldown > 0f) return false;
            float dx = playerPos.X - Position.X;
            float dy = playerPos.Y - Position.Y;
            float dz = playerPos.Z - Position.Z;
            float distSq = dx * dx + dy * dy + dz * dz;
            return distSq <= FireDetectRangeSq;
        }

        public void NotifyFired()
        {
            FireCooldown = FireCooldownSeconds;
        }

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 0..2 gunpowder — canonical Alpha drop. Ghast Tear (the
            // brewing ingredient drop in modern Minecraft) is out of
            // scope for Alpha 1.1.2_01 — there's no brewing system
            // for it to fuel, and the item itself didn't exist until
            // Beta 1.9. Gunpowder routes through the existing item
            // path so the player can craft TNT from a ghast farm.
            int count = _rng.Next(0, 3);
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
                float speed = 1.5f + (float)_rng.NextDouble() * 1.0f;
                var vel = new Vector3(
                    (float)Math.Cos(angle) * speed,
                    3.0f + (float)_rng.NextDouble() * 1.5f,
                    (float)Math.Sin(angle) * speed);
                drops.SpawnDrop(
                    Position + new Vector3(0, Height * 0.5f, 0),
                    BlockType.Gunpowder, 1,
                    vel);
            }
        }
    }

    // Tier 8 #51 V8 — Blaze: smaller flying ranged Nether mob, fires
    // fireballs in a 3-shot volley with a long rest between volleys.
    // Designed to share FireballProjectile with Ghast so the same
    // physics + collision + impact-explode pipeline handles both.
    //
    // Differences from Ghast:
    //   * Smaller hitbox — humanoid-ish footprint (HalfWidth=0.4, h=2.0)
    //     so the player can pick one off with arrows from cover.
    //   * Lower HP (20) but smaller body makes hits harder to land.
    //   * 3-shot volley: when cooldown expires + LOS clear, fires
    //     three fireballs spaced 0.30 s apart, then rests for 4 s.
    //     This rhythm gives the player a brief "sidestep window"
    //     between volleys; ducking behind cover during the rest is
    //     the canonical counter.
    //   * Each shot in the volley has a small random spread so a
    //     stationary blaze doesn't paint a perfect line on the
    //     player — sidestep dodging works against the spread.
    //   * Drops 0..1 GoldIngot — Alpha didn't have BlazeRod (item
    //     introduced Beta 1.9 alongside brewing); GoldIngot stands
    //     in as a small Nether-treasure trophy until brewing ships.
    //
    // Spawn: rare per-column roll same as the Ghast pass. We don't
    // gate by fortress structures (they're V10 and don't exist yet),
    // so for V8 the blaze spawns in the open Nether cavern at
    // 1-in-1500 — slightly more common than ghasts. When fortresses
    // ship, that pass can move blaze spawns into spawner-cage rooms.
    internal sealed class Blaze : HostileMob
    {
        public const float HitboxHalfWidth = 0.4f;
        public const float HitboxHeight    = 2.0f;
        public const float DriftSpeed      = 0.5f;
        public const float BobAmplitude    = 0.25f;
        public const float BobPeriodSec    = 3.0f;
        public const float DetectRangeBlocks   = 24f;
        public const float DetectRangeSq       = DetectRangeBlocks * DetectRangeBlocks;
        public const float VolleyRestSeconds   = 4.0f;
        public const float VolleyShotInterval  = 0.30f;
        public const int   VolleyShotCount     = 3;
        public const float ShotSpreadRadians   = 0.05f; // ±~3° per shot

        // Volley state machine.
        //   * RestTimer counts DOWN from VolleyRestSeconds. When 0,
        //     the next firing decision starts a volley.
        //   * ShotsRemaining counts shots left in the current volley
        //     (3 → 2 → 1 → 0). When > 0 the mob fires one shot per
        //     ShotInterval and decrements; when it hits 0 we reset
        //     RestTimer to VolleyRestSeconds.
        //   * ShotCooldown counts down between shots within a volley.
        public float RestTimer;
        public int   ShotsRemaining;
        public float ShotCooldown;

        // Animation phase for the rotating-rod render. Updated each
        // tick — incremented by dt so the rotation rate is consistent
        // regardless of frame rate.
        public float RodPhase;

        // Vertical bob phase + drift heading + hover anchor — same
        // shape as Ghast.
        private float _bobPhase;
        private float _driftYaw;
        private float _hoverY;

        public Blaze(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            RestTimer = (float)(_rng.NextDouble() * VolleyRestSeconds);
            ShotsRemaining = 0;
            _bobPhase = (float)(_rng.NextDouble() * Math.PI * 2.0);
            _driftYaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
            _hoverY   = spawnPos.Y;
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 0f;
        public override float DetectRange           => DetectRangeBlocks;
        public override float AttackRange           => 0f;
        public override int   AttackDamage          => 0;
        public override float AttackCooldownSeconds => 0f;

        public override void Update(float dt, World world, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead) return;
            if (HurtTimer > 0f) { HurtTimer -= dt; if (HurtTimer < 0f) HurtTimer = 0f; }

            // Rod animation timer — wraps every 4π so float precision
            // stays stable indefinitely. Used only by the renderer.
            RodPhase += dt;
            if (RodPhase > 12.566f) RodPhase -= 12.566f; // 4π

            // Volley-state timers.
            if (RestTimer > 0f)        RestTimer    -= dt;
            if (ShotCooldown > 0f)     ShotCooldown -= dt;

            // Drift + bob — same shape as Ghast. Keeps the mob alive
            // visually even between volleys.
            _wanderTimer -= dt;
            if (_wanderTimer <= 0f)
            {
                _wanderTimer = WanderInterval;
                _driftYaw   += ((float)_rng.NextDouble() - 0.5f) * 1.0f;
            }

            float dx = playerPos.X - Position.X;
            float dz = playerPos.Z - Position.Z;
            float horizDistSq = dx * dx + dz * dz;
            float yawTarget;
            if (horizDistSq <= DetectRangeSq * 4f)
            {
                yawTarget = (float)Math.Atan2(dx, dz);
                Yaw = yawTarget;
            }
            else
            {
                yawTarget = _driftYaw;
            }

            Velocity.X = (float)Math.Sin(yawTarget) * DriftSpeed;
            Velocity.Z = (float)Math.Cos(yawTarget) * DriftSpeed;

            _bobPhase += dt * (float)(Math.PI * 2.0 / BobPeriodSec);
            if (_bobPhase > (float)(Math.PI * 2.0)) _bobPhase -= (float)(Math.PI * 2.0);
            float targetY = _hoverY + (float)Math.Sin(_bobPhase) * BobAmplitude;
            Velocity.Y = (targetY - Position.Y) / Math.Max(dt, 1e-3f);

            IntegrateMotion(dt, world);

            if (Math.Abs(Position.Y - targetY) > BobAmplitude * 1.5f)
            {
                _hoverY = Position.Y;
            }
        }

        // Returns true if the blaze wants to fire a single shot on
        // this tick. The renderer polls each frame and on a true
        // result spawns one fireball + calls NotifyFired.
        // Logic:
        //   * If a volley is in progress (ShotsRemaining > 0) and
        //     ShotCooldown <= 0, fire one shot.
        //   * Else if RestTimer <= 0 and the player is in range, start
        //     a new volley by setting ShotsRemaining = VolleyShotCount.
        //     The first shot fires this same tick.
        public bool WantsToFire(Vector3 playerPos)
        {
            if (IsDead) return false;
            float dx = playerPos.X - Position.X;
            float dy = playerPos.Y - Position.Y;
            float dz = playerPos.Z - Position.Z;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > DetectRangeSq) return false;

            if (ShotsRemaining > 0 && ShotCooldown <= 0f) return true;
            if (ShotsRemaining == 0 && RestTimer <= 0f)
            {
                // Lazy volley start — _next call to WantsToFire returns
                // true because ShotsRemaining will be > 0. We start it
                // right here so the first shot fires this same tick.
                ShotsRemaining = VolleyShotCount;
                return true;
            }
            return false;
        }

        public void NotifyFired()
        {
            if (ShotsRemaining > 0) ShotsRemaining--;
            ShotCooldown = VolleyShotInterval;
            if (ShotsRemaining == 0)
            {
                RestTimer = VolleyRestSeconds;
            }
        }

        // Random spread direction for one shot in the volley. Caller
        // computes the base aim, we perturb it by a small angle on
        // both yaw and pitch axes so the three shots in a volley
        // don't paint a perfect line.
        public Vector3 PerturbAim(Vector3 aim)
        {
            // Build a basis around aim — easier than rotating in
            // world space. Right = perpendicular to aim on XZ; Up =
            // perpendicular to both.
            Vector3 fwd = aim;
            if (fwd.LengthSquared < 1e-6f) return aim;
            fwd.Normalize();
            Vector3 right;
            // Avoid the gimbal-lock degeneracy where fwd is straight
            // up/down — almost never happens for a horizontal-facing
            // blaze, but the safety branch is cheap.
            if (Math.Abs(fwd.Y) < 0.99f)
            {
                right = Vector3.Cross(fwd, Vector3.UnitY);
            }
            else
            {
                right = Vector3.Cross(fwd, Vector3.UnitZ);
            }
            right.Normalize();
            Vector3 up = Vector3.Cross(right, fwd);

            float dx = ((float)_rng.NextDouble() * 2f - 1f) * ShotSpreadRadians;
            float dy = ((float)_rng.NextDouble() * 2f - 1f) * ShotSpreadRadians;
            Vector3 perturbed = fwd + right * dx + up * dy;
            perturbed.Normalize();
            return perturbed;
        }

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // 0..1 gold ingot. Canonical Blaze drop (BlazeRod) is
            // tied to brewing which is out of scope; gold ingot
            // routes through the existing item drop path and reads
            // as Nether treasure.
            if (_rng.Next(2) == 0)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
                float speed = 1.5f + (float)_rng.NextDouble() * 1.0f;
                drops.SpawnDrop(
                    Position + new Vector3(0, Height * 0.5f, 0),
                    BlockType.GoldIngot, 1,
                    new Vector3(
                        (float)Math.Cos(angle) * speed,
                        3.0f + (float)_rng.NextDouble() * 1.5f,
                        (float)Math.Sin(angle) * speed));
            }
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
    // bones (Tier 8 #50 ships Bone — see SpawnDeathDrops below); we
    // ship Bow as a 1-in-N drop to match Alpha rarity.
    internal sealed class Skeleton : HostileMob
    {
        public const float HitboxHalfWidth = 0.3f;
        public const float HitboxHeight    = 1.8f;

        // Ranged AI cadence. Canonical Alpha 1.1.2_01 skeleton cooldown
        // is ~1 s between arrows when in line-of-sight; we run a
        // slightly slower 1.5 s so the player has reaction time and
        // multiple skeletons aren't pelting non-stop. Detect range
        // matches the base detect range — once the skeleton sees the
        // player it both chases AND fires.
        public const float ArrowCooldownSeconds = 1.5f;
        public const float ArrowDetectRange     = 16f;
        public const float ArrowDetectRangeSq   = ArrowDetectRange * ArrowDetectRange;

        // Cooldown timer between arrows. Counts down each tick;
        // refilled to ArrowCooldownSeconds after a successful shot
        // (LOS clear, in range). Starts at a small random offset so a
        // group of skeletons doesn't fire in lockstep.
        public float ArrowCooldown;

        public Skeleton(Vector3 spawnPos, int seed) : base(spawnPos, seed)
        {
            HalfWidth = HitboxHalfWidth;
            Height    = HitboxHeight;
            ArrowCooldown = (float)(_rng.NextDouble() * ArrowCooldownSeconds);
        }

        public override int   MaxHealth             => 20;
        public override float WalkSpeed             => 1.1f;
        public override float DetectRange           => 16f;
        public override float AttackRange           => 1.4f;
        public override int   AttackDamage          => 2;
        public override float AttackCooldownSeconds => 1.0f;

        // Tier 4 #17 follow-up — base HostileMob.Update advances the
        // chase / melee path; we override only to tick the arrow
        // cooldown alongside it. The renderer polls WantsToFire each
        // frame and spawns the projectile when ready.
        public override void Update(float dt, World world, OpenTK.Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            base.Update(dt, world, playerPos, damageSink);
            if (ArrowCooldown > 0f) ArrowCooldown -= dt;
        }

        // Returns true when the skeleton wants to fire on this tick.
        // The renderer's TickSkeletonFiring runs the LOS raycast and
        // spawns the ArrowProjectile if true.
        public bool WantsToFire(OpenTK.Vector3 playerPos)
        {
            if (IsDead) return false;
            if (ArrowCooldown > 0f) return false;
            float dx = playerPos.X - Position.X;
            float dy = playerPos.Y - Position.Y;
            float dz = playerPos.Z - Position.Z;
            float distSq = dx * dx + dy * dy + dz * dz;
            return distSq <= ArrowDetectRangeSq;
        }

        public void NotifyFired() { ArrowCooldown = ArrowCooldownSeconds; }

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

            // Tier 8 #50 — 0..2 bones per kill. Same drop range as
            // arrows in canonical Alpha 1.0.14+; the player can
            // craft each bone shapelessly into 3 bone-meal at the
            // crafting table, or use bones directly as a tameable-
            // wolf treat once wolves ship.
            int bones = _rng.Next(0, 3);
            for (int i = 0; i < bones; i++)
            {
                drops.SpawnDrop(
                    Position + new Vector3(0, 0.5f, 0),
                    BlockType.Bone, 1,
                    RandomScatterVelocity());
            }

            // Skeletons share the chainmail rare-drop niche with
            // zombies — same 0.5% per-piece roll. Chain armor has no
            // craft recipe, so this is the only way the player gets it.
            TryDropChainmail(drops);
        }

        private void TryDropChainmail(IDropSink drops)
        {
            BlockType[] pieces =
            {
                BlockType.ChainmailHelmet,
                BlockType.ChainmailChestplate,
                BlockType.ChainmailLeggings,
                BlockType.ChainmailBoots,
            };
            for (int i = 0; i < pieces.Length; i++)
            {
                if (_rng.Next(200) == 0) // 1/200 = 0.5%
                {
                    drops.SpawnDrop(
                        Position + new Vector3(0, 0.5f, 0),
                        pieces[i], 1,
                        RandomScatterVelocity());
                }
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

    // Tier 4 #18 — Slime. Bouncing cube mob that splits into smaller
    // copies on death. Three sized variants — Big (size=2), Medium
    // (size=1), Small (size=0) — sharing one class so the split path
    // can spawn a constructor-call chain that doesn't need a per-size
    // subclass. Size determines hitbox, HP, attack damage, and walk
    // speed (smaller = faster — matches Alpha 1.1.2_01 where the tiny
    // baby slimes are visibly twitchy compared to the lumbering big
    // ones).
    //
    // Locomotion is "bounce, don't walk": every BounceInterval seconds
    // the slime checks OnGround and, if it's standing, sets Velocity.Y
    // to BounceJump and Velocity.XZ to a step toward the player (or a
    // random heading if the player is out of detect range). Mid-air
    // it lets gravity finish the arc — the standard chase-loop's
    // continuous XZ steering would cancel the airborne ballistic feel,
    // so we override Update entirely instead of layering a "jump
    // sometimes" hook on top of HostileMob.Update. The auto-jump in
    // the base class is also redundant for a mob whose every move is
    // already a jump, so skipping it keeps the silhouette clean.
    //
    // Drops: only Small slimes drop slimeballs (0..2). Big and Medium
    // drop nothing on death — the visible "drop" of a big slime is
    // its split into smaller copies, not an item drop. Match Alpha
    // 1.1.2_01 exactly here.
    //
    // Spawn rule: the slime-chunk pattern in World.SpawnHostilesInChunk
    // (every chunk where the chunkX/chunkZ hash matches a 1-in-1024
    // mask) at Y < 40, regardless of light. Slimes don't gate on
    // light the way the other hostiles do — that's the whole reason
    // they feel like a different species despite sharing the chase
    // surface.
    internal sealed class Slime : HostileMob
    {
        public const float BounceJump     = 4.5f;  // m/s upward kick on each bounce
        public const float BounceInterval = 1.2f;  // seconds between bounces while grounded

        public readonly int Size;

        // Hitbox + tunable tables, indexed by Size. Big slimes are the
        // size of a player AABB; Medium are half-scale; Small are
        // quarter-scale. HP / damage / walk speed all scale with size
        // per the Alpha rule (smaller = faster).
        public Slime(Vector3 spawnPos, int seed, int size) : base(spawnPos, seed)
        {
            Size = size < 0 ? 0 : (size > 2 ? 2 : size);
            switch (Size)
            {
                case 2: HalfWidth = 1.0f;  Height = 2.0f; break;
                case 1: HalfWidth = 0.5f;  Height = 1.0f; break;
                default: HalfWidth = 0.25f; Height = 0.5f; break;
            }
            // MaxHealth is read in base() before HalfWidth/Height are
            // assigned (Health = MaxHealth in the base ctor). MaxHealth
            // depends on Size which the derived ctor sets above, AFTER
            // base() has run. So we re-seed Health here from the now-
            // correct Size-driven MaxHealth.
            Health = MaxHealth;
        }

        public override int   MaxHealth
            => Size == 2 ? 16 : (Size == 1 ? 4 : 1);
        public override float WalkSpeed
            => Size == 2 ? 0.6f : (Size == 1 ? 0.9f : 1.2f);
        public override float DetectRange => 16f;
        public override float AttackRange
            => Size == 2 ? 1.6f : (Size == 1 ? 1.0f : 0.6f);
        public override int   AttackDamage
            => Size == 2 ? 4 : (Size == 1 ? 2 : 0);
        public override float AttackCooldownSeconds => 0.8f;

        // Per-instance bounce countdown — drives the "every ~1.2 s,
        // hop again" rhythm. Stays in sync across the three sizes so
        // a freshly-split slime cluster lands its first bounces in
        // unison (visually reads as a "shatter").
        private float _bounceTimer;

        // Squish wobble — incremented every Update so the renderer can
        // sample sin(_squashTimer) for a height-pulse on the cube.
        public float SquashTimer;

        public override void Update(float dt, World world, Vector3 playerPos, IPlayerDamageSink damageSink)
        {
            if (IsDead) return;

            if (HurtTimer > 0f)      { HurtTimer      -= dt; if (HurtTimer      < 0f) HurtTimer      = 0f; }
            if (AttackCooldown > 0f) { AttackCooldown -= dt; if (AttackCooldown < 0f) AttackCooldown = 0f; }
            SquashTimer += dt * 6f;

            float dx = playerPos.X - Position.X;
            float dy = playerPos.Y - Position.Y;
            float dz = playerPos.Z - Position.Z;
            float horizDist = (float)Math.Sqrt(dx * dx + dz * dz);

            // Bounce trigger — only on the ground, only when the
            // bounce-interval has elapsed. Mid-air the slime is purely
            // ballistic — gravity finishes the arc, the next bounce
            // arms once OnGround flips back to true.
            _bounceTimer -= dt;
            if (OnGround && _bounceTimer <= 0f)
            {
                _bounceTimer = BounceInterval;
                Velocity.Y = BounceJump;

                // Heading: chase the player if they're in detect range,
                // otherwise pick a random direction. The XZ speed is
                // applied as an impulse on this tick — gravity then
                // shapes the rest of the arc.
                float headingYaw;
                if (horizDist <= DetectRange && horizDist > 1e-3f)
                {
                    headingYaw = (float)Math.Atan2(dx, dz);
                }
                else
                {
                    headingYaw = (float)(_rng.NextDouble() * Math.PI * 2.0);
                }
                Yaw = headingYaw;
                Velocity.X = (float)Math.Sin(headingYaw) * WalkSpeed;
                Velocity.Z = (float)Math.Cos(headingYaw) * WalkSpeed;
            }

            // Melee — same Y-gate as the base chase-loop. Small slimes
            // have AttackDamage=0 so the call short-circuits inside
            // DamagePlayer (no-op), matching Alpha's "small slimes
            // don't damage" rule without a special branch here.
            if (AttackDamage > 0 && horizDist <= AttackRange + HalfWidth
                && Math.Abs(dy) <= 1.5f && AttackCooldown <= 0f)
            {
                damageSink.DamagePlayer(AttackDamage);
                AttackCooldown = AttackCooldownSeconds;
            }

            // Gravity + terminal velocity, identical to the base mob.
            Velocity.Y -= Gravity * dt;
            if (Velocity.Y < -MaxFallSpeed) Velocity.Y = -MaxFallSpeed;

            IntegrateMotion(dt, world);
        }

        public override void SpawnDeathDrops(IDropSink drops)
        {
            // Drops — only Small slimes (Size=0) drop slimeballs.
            // 0..2 per Alpha 1.1.2_01.
            if (Size == 0)
            {
                int balls = _rng.Next(0, 3);
                for (int i = 0; i < balls; i++)
                {
                    drops.SpawnDrop(
                        Position + new Vector3(0, 0.2f, 0),
                        BlockType.Slimeball, 1,
                        RandomScatterVelocity());
                }
                return;
            }

            // Split — Big (Size=2) → Medium (Size=1) × 2..4
            //        Medium (Size=1) → Small  (Size=0) × 2..4
            // Children spawn at the death position with a small
            // outward scatter so they don't all stack into one cell
            // and immediately collide-merge. Each child gets its own
            // RNG seed derived from the parent's so the scatter
            // pattern is deterministic for a given kill.
            int childCount = 2 + _rng.Next(0, 3); // 2..4 inclusive
            int childSize = Size - 1;
            for (int i = 0; i < childCount; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
                float radius = 0.3f;
                var spawnPos = Position + new Vector3(
                    (float)Math.Cos(angle) * radius,
                    0f,
                    (float)Math.Sin(angle) * radius);
                var child = new Slime(spawnPos, _rng.Next(), childSize);
                // Outward kick so the cluster reads as a shatter — the
                // children visibly fly apart instead of pile on the
                // parent's footprint. XZ proportional to the outward
                // radius vector, plus a tiny upward pop.
                child.Velocity = new Vector3(
                    (float)Math.Cos(angle) * 2.0f,
                    2.0f + (float)_rng.NextDouble() * 1.0f,
                    (float)Math.Sin(angle) * 2.0f);
                drops.SpawnHostile(child);
            }
        }

        private Vector3 RandomScatterVelocity()
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float speed = 1.0f + (float)_rng.NextDouble() * 0.6f;
            return new Vector3(
                (float)Math.Cos(angle) * speed,
                2.0f + (float)_rng.NextDouble() * 1.0f,
                (float)Math.Sin(angle) * speed);
        }
    }
}
