using System;
using OpenTK;

namespace VStudioCraft.Game
{
    // First-person walker with AABB-vs-voxel collision. Position tracks the feet
    // (AABB min Y, centre of X/Z). The camera sits at Position + (0, EyeHeight, 0).
    //
    // The shared physics (AABB shape, sub-step integrator, MoveAxis, Collides,
    // OnGround tracking) lives on the Entity base — Pig and any future mob
    // reuses the same walker. Player layers swim physics, jump, fall tracking,
    // health / hunger / air, and the cosmetic hurt + swing timers on top.
    internal sealed class Player : Entity
    {
        // Player AABB shape constants — kept as public consts for the
        // call-sites that referenced `Player.HalfWidth` / `Player.Height`
        // before the Entity refactor. The Entity base also exposes them
        // as instance fields (default 0.3 / 1.8) so the shared physics
        // walker reads the same values; Player's constructor doesn't
        // need to override them since the defaults match.
        public new const float HalfWidth = 0.3f;   // AABB half-extent in X and Z
        public new const float Height = 1.8f;      // AABB Y extent
        public const float EyeHeight = 1.62f;      // camera offset above feet

        public const float WalkSpeed = 4.3f;
        public const float SprintSpeed = 7.0f;
        // Tier 5 #27 — Sneak speed. Canonical Alpha 1.1.2_01 sneak is
        // ~30% of walk; paired with the edge-stop rule in Update so the
        // player can't walk off a 1-block ledge while sneaking.
        public const float SneakSpeed = 1.295f;
        // Tier 10 follow-up — Sneak probe footprint half-extent. The
        // edge-stop walks every cell under a probe square of this
        // half-width centred on the projected position; making it
        // smaller than HalfWidth lets the visible AABB overhang the
        // block edge by (HalfWidth - SneakProbeHalf) before sneak
        // engages. A 0.10 probe lets the player creep ~0.2 blocks
        // past the edge — enough that the toes visibly hang over but
        // the body's centre of mass is still firmly on the block.
        public const float SneakProbeHalf = 0.10f;
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

        // Tier 8 #51 — Soul Sand horizontal slowdown. While the
        // player is standing ON a Soul Sand cell, their horizontal
        // velocity is scaled by this factor — canonical Alpha drops
        // movement to ~40% on soul sand, the signature gameplay
        // beat that pairs with the visual sub-cube subsidence.
        public const float SoulSandMoveScale = 0.4f;
        // Tier 8 #51 V2 — Fall-distance scale when landing on Soul
        // Sand. Halves the recorded fall distance, which feeds into
        // the existing damage formula — a 12-block fall reads as 6,
        // dropping the damage from ~9 hearts to ~3. Same gate as
        // the slowdown (cell directly under the foot AABB), shared
        // helper IsStandingOnSoulSand.
        public const float SoulSandFallDamageScale = 0.5f;

        // Tier 8 #46 — Ladder climb. Gravity is suppressed while the
        // player is on a ladder; vertical motion comes straight from
        // jump (up) / sneak (down) input at a constant climb speed.
        // With neither held, the player slides down slowly so they
        // don't get stuck mid-climb if they release the keys but
        // also don't free-fall off the ladder. Values are tuned so
        // a 16-block climb reads as a "deliberate climb" rather than
        // the modern Minecraft hold-to-warp speed.
        public const float LadderClimbSpeed = 3.0f;  // m/s up or down with input
        public const float LadderSlideSpeed = 1.5f;  // m/s passive descent (no input)

        // Bobbing: pure visual offset added to the camera Y when in water. The
        // amplitude is small (Alpha's bob is similarly subtle) and the cadence
        // is tied to the swim cycle so head-strokes read as the bob beats.
        public const float SwimBobAmplitude = 0.05f;
        public const float SwimBobFrequency = 4f;   // radians/sec

        // (MoveAxis sub-step cap lives on Entity as MaxSubStep.)

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

        // (Position / Velocity / OnGround inherited from Entity.)

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

        // Tier 4 #21 — Currently-ridden pig (Alpha 329 saddle). Non-null
        // means the player is mounted: physics is suspended, gravity
        // doesn't apply, WASD does NOTHING (Alpha pigs were unsteerable
        // — they just wandered with the player on top), and the
        // player's Position is glued to the pig each frame so the
        // camera tracks the pig. Space dismounts (sets Riding=null
        // and pushes the player up by 1 block).
        //
        // The reference is ephemeral: on world load it's always null
        // (matches Alpha — saving while mounted always dismounted on
        // load). The field is also force-cleared if the ridden pig
        // dies, so a killed mount can't strand the player in mid-air
        // physics-suspended forever.
        //
        // Why a Pig reference instead of a generic Entity: only pigs
        // are rideable in Alpha 1.1.2_01 (minecarts are a separate
        // entity, horses are post-Alpha), so the typed reference keeps
        // the mount/dismount logic from having to switch on entity
        // kind.
        public Pig Riding;

        // Tier 9 #54 V1 — Currently-mounted boat, or null. Same
        // ephemeral-reference contract as `Riding` above: cleared
        // on world load + on dimension swap (boats don't follow
        // the player across portals — they belong to the dim they
        // were placed in). RMB on a boat sets this; Sneak (Shift)
        // dismounts. While set, the player's WASD movement input
        // is suppressed and replaced by the boat's thrust path.
        public Boat MountedBoat;

        // Tier 9 #54 V2 — Currently-mounted minecart, or null. Same
        // ephemeral-reference contract as Riding / MountedBoat.
        // While set, the player's WASD is suppressed; W applies
        // forward thrust along the cart's rail axis via TickMinecarts.
        public Minecart MountedMinecart;

        // Tier 4 #23 — Currently-cast fishing bobber, or null if no
        // line is out. Set by GameRenderer.TryInteract on the cast
        // RMB and cleared when the player reels (or when the bobber
        // auto-despawns past its safety window). Single-bobber-per-
        // player matches Alpha 1.1.2_01 — you couldn't have two
        // simultaneous casts in flight.
        //
        // The reference is ephemeral the same way Riding is: on
        // world load it's always null, and a despawned bobber
        // force-clears it via the Tick pass so a stale reference
        // can't "ghost reel" empty world.
        public Bobber ActiveBobber;

        // Tier 5 #27 — Sneak modifier. Set by the host each frame from
        // _input.IsDown(Keys.ShiftKey); consumed inside Update for the
        // edge-stop pre-check below. Public field (vs property) keeps
        // the cross-thread write tear-free — single bool assignment.
        public bool IsSneaking;

        // Highest Y reached while airborne — the "peak" from which fall distance
        // is measured. Reset to current Y while on the ground so small hops
        // don't accumulate.
        private float _fallPeakY;

        public void Update(float dt, Vector3 wishHorizVel, bool wantJump, World world)
        {
            bool wasOnGround = OnGround;

            // Tick the cosmetic timers down (hurt flash + arm swing).
            // Both clamp at zero — TakeDamage / TriggerSwing refresh them
            // on demand. Doing it here means they auto-clear even if the
            // renderer ever forgets to read them.
            if (HurtTimer  > 0f) { HurtTimer  -= dt; if (HurtTimer  < 0f) HurtTimer  = 0f; }
            if (SwingTimer > 0f) { SwingTimer -= dt; if (SwingTimer < 0f) SwingTimer = 0f; }

            // Tier 4 #21 — Mounted pig override. While Riding != null
            // the player is glued to the pig's back: no gravity, no
            // wish-velocity integration, no AABB collision. Position is
            // teleported each frame to the pig's back-Y (pig.Height +
            // a small extra offset so the player AABB clears the
            // saddle pad, with the player's feet sitting on top of
            // the saddle). Velocity is zeroed so a dismount doesn't
            // inherit some stale walking velocity.
            //
            // Force-dismount if the ridden pig dies (the bestPassive in
            // TryHitMob would otherwise leave the player still pointed
            // at a dead-flagged Pig the chunk reaper is about to
            // remove). Pushing the player up by 1 block on dismount
            // matches the manual-dismount path so a death-dismount
            // doesn't drop them inside the pig's geometry.
            //
            // Space (wantJump) dismounts manually — Alpha bound
            // dismount to the same key as jump. We DON'T also gate on
            // wasOnGround because the pig could be airborne (e.g.
            // walked off a cliff); dismounting mid-fall is fine and
            // the player resumes normal physics from there.
            //
            // TODO: a cleaner refactor would split Player.Update into
            // a "physics tick" method gated on Riding == null and a
            // "common timers" method that always runs; for now the
            // early-return after the timer block keeps the change
            // surface small.
            if (Riding != null)
            {
                if (Riding.IsDead)
                {
                    // Force-dismount on death — push up so the player
                    // doesn't end up clipped into the dying pig.
                    Position = new Vector3(Position.X, Riding.Position.Y + 1.0f, Position.Z);
                    Velocity = Vector3.Zero;
                    Riding = null;
                    OnGround = false;
                }
                else if (wantJump)
                {
                    // Manual dismount — push up by 1 block so the
                    // player's feet land above the pig's back instead
                    // of inside the pig's body AABB.
                    Position = new Vector3(Position.X, Riding.Position.Y + Riding.Height + 1.0f, Position.Z);
                    Velocity = Vector3.Zero;
                    Riding = null;
                    OnGround = false;
                }
                else
                {
                    // Glued to the pig. Camera follows via
                    // SyncCameraToPlayer; eye is Position.Y +
                    // EyeHeight, so feet sit at pig back-Y and the
                    // player's view sits ~1.62 blocks above the pig.
                    Position = new Vector3(
                        Riding.Position.X,
                        Riding.Position.Y + Riding.Height + 0.4f,
                        Riding.Position.Z);
                    Velocity = Vector3.Zero;
                    OnGround = true;
                    // Sample water state for the HUD (dismounting into
                    // water is a thing) but skip swim physics — the pig
                    // is the one being affected by water, not us.
                    WasInWater = ScanInWater(world, fromY: Position.Y, toY: Position.Y + Height);
                    WasHeadInWater = ScanInWater(world,
                        fromY: Position.Y + EyeHeight - 0.1f,
                        toY:   Position.Y + EyeHeight + 0.1f);
                    return;
                }
            }

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
            // Tier 8 #51 — Soul Sand slowdown. Stacks multiplicatively
            // with WaterMoveScale (a soul-sand block submerged in
            // water scales by 0.5 × 0.4 = 0.2 — slower than either
            // alone, matching canonical Alpha). Probed only when the
            // player is on the ground; mid-air is unaffected so a
            // jumping player escapes the slowdown briefly, which is
            // the canonical "hop to outpace soul sand" trick.
            if (OnGround && IsStandingOnSoulSand(world))
                horizScale *= SoulSandMoveScale;
            Velocity.X = wishHorizVel.X * horizScale;
            Velocity.Z = wishHorizVel.Z * horizScale;

            // Vertical: water uses a smaller gravity and clamps both signs
            // (drag), so you sink slowly and can't free-fall through a deep
            // pool. Holding Space accelerates upward while submerged — the
            // continuous accel with a soft terminal feels closer to Alpha's
            // "tap-tap-tap to surface" than a single jump impulse would.
            //
            // Tier 8 #46 — Ladder takes priority over the standard
            // air branch (water still wins because ladders inside
            // water are unusual): when the player AABB overlaps a
            // ladder cell, gravity is suppressed and vertical motion
            // is driven directly by jump (climb up) / sneak (climb
            // down). With no input, the player descends at a slow
            // ladder-friction terminal so they don't get stuck mid-
            // climb but also don't fall off the moment they pause.
            bool onLadder = !inWater && IsOnLadder(world);
            if (inWater)
            {
                Velocity.Y -= WaterGravity * dt;
                if (wantJump) Velocity.Y += SwimUpAccel * dt;
                if (Velocity.Y < -WaterMaxFall) Velocity.Y = -WaterMaxFall;
                if (Velocity.Y >  WaterMaxRise) Velocity.Y =  WaterMaxRise;
            }
            else if (onLadder)
            {
                if (wantJump)         Velocity.Y =  LadderClimbSpeed;
                else if (IsSneaking)  Velocity.Y = -LadderClimbSpeed;
                else                  Velocity.Y = -LadderSlideSpeed;
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

            // Tier 5 #27 — Sneak edge-stop. While sneaking AND on the
            // ground, reject any horizontal step that would put any
            // part of the AABB over empty space at foot level — i.e.
            // you can walk along a ledge and stop with your toes flush
            // against the block boundary (the canonical "hang on the
            // edge" feel). Per-axis so a diagonal step toward an outer
            // corner can still be partially clamped (X zeroed, Z left
            // alone, etc.). Skipped while airborne (Alpha rule —
            // sneak doesn't catch you mid-fall) and while in water
            // (swim physics already gates falls through fluid).
            //
            // Tier 10 follow-up — the original implementation used
            // "any corner over solid" which let the player walk
            // until their entire AABB had cleared the block before
            // sneak engaged (so they'd visibly tip past the edge by
            // ~HalfWidth before stopping). Switching to "all cells
            // under AABB must be solid" makes sneak stop the player
            // before any part of the AABB hangs over the void —
            // matches the user's expectation of sneak keeping you
            // fully on the block.
            if (IsSneaking && OnGround && !inWater)
            {
                var probeX = new Vector3(Position.X + Velocity.X * dt, Position.Y, Position.Z);
                if (!IsAabbFullyOverGround(world, probeX)) Velocity.X = 0f;
                var probeZ = new Vector3(Position.X, Position.Y, Position.Z + Velocity.Z * dt);
                if (!IsAabbFullyOverGround(world, probeZ)) Velocity.Z = 0f;
            }

            // Tier 9 #54 V14 — Decay the auto-step visual lerp. The
            // Player uses MoveAxis directly instead of IntegrateMotion
            // (which has the decay built in for mobs/vehicles), so we
            // run the same decay manually here. Without this the
            // StepLerpY accumulates on every auto-step (snow / slab /
            // stair walk-up) and the camera sinks lower and lower
            // because there's no path back to 0.
            if (StepLerpY > 0f)
            {
                StepLerpY -= StepLerpDecayRate * dt;
                if (StepLerpY < 0f) StepLerpY = 0f;
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

        // Tier 5 #27 — True if the player AABB at `pos` (feet at pos.Y)
        // has solid ground under at least one corner. Probes one block-
        // thickness below the feet (Y - 0.05f) so the test reads the
        // floor block even when the AABB is exactly flush with the top.
        // Kept around for any future "any corner" use; the live sneak
        // path uses the stricter IsAabbFullyOverGround below.
        private bool HasGroundUnderAabb(World world, Vector3 pos)
        {
            int y = (int)Math.Floor(pos.Y - 0.05f);
            int x0 = (int)Math.Floor(pos.X - HalfWidth);
            int x1 = (int)Math.Floor(pos.X + HalfWidth);
            int z0 = (int)Math.Floor(pos.Z - HalfWidth);
            int z1 = (int)Math.Floor(pos.Z + HalfWidth);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (BlockData.IsSolid(world.GetBlock(x, y, z))) return true;
            return false;
        }

        // Tier 10 follow-up — strict version: every cell under the
        // AABB at `pos` must be solid. Used by the sneak edge-stop so
        // the player's projected footprint stays fully on the block —
        // the AABB never hangs over the void. The maxX / maxZ probes
        // include a 1e-5 epsilon so an AABB whose far edge sits
        // exactly on a cell boundary doesn't count the next cell over
        // (matches the same convention Collides() uses for its
        // upper-bound voxel walk).
        private bool IsAabbFullyOverGround(World world, Vector3 pos)
        {
            int y = (int)Math.Floor(pos.Y - 0.05f);
            int x0 = (int)Math.Floor(pos.X - SneakProbeHalf);
            int x1 = (int)Math.Floor(pos.X + SneakProbeHalf - 1e-5f);
            int z0 = (int)Math.Floor(pos.Z - SneakProbeHalf);
            int z1 = (int)Math.Floor(pos.Z + SneakProbeHalf - 1e-5f);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (!BlockData.IsSolid(world.GetBlock(x, y, z))) return false;
            return true;
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
                //
                // Tier 8 #51 V2 — Soul Sand softens fall damage. Half the
                // recorded distance when the supporting cell is Soul Sand
                // (canonical Alpha — landing on soul sand still hurts but
                // a 12-block fall reads as 6, dropping the damage from 9
                // hearts to 3). Same rule as water but a partial cushion
                // rather than a full cancel.
                float dist = _fallPeakY - Position.Y;
                if (dist > 0f && !IsInWater(world))
                {
                    if (IsStandingOnSoulSand(world)) dist *= SoulSandFallDamageScale;
                    LastFallDistance = dist;
                }
            }
            _fallPeakY = Position.Y;
        }

        public bool IsInWater(World world) =>
            ScanInWater(world, Position.Y, Position.Y + Height);

        // Tier 8 #46 — True when the player AABB overlaps any ladder
        // cell. Drives the climb-physics override in Update: gravity
        // is suppressed and vertical motion is set by jump/sneak
        // input rather than by the integrator. Same scan shape as
        // ScanInWater so both checks share the AABB-cell iteration
        // pattern; cheap (≤ ~8 cell reads on a standing player).
        public bool IsOnLadder(World world)
        {
            float minX = Position.X - HalfWidth, maxX = Position.X + HalfWidth;
            float minZ = Position.Z - HalfWidth, maxZ = Position.Z + HalfWidth;
            int bx0 = (int)Math.Floor(minX);
            int bx1 = (int)Math.Floor(maxX - 1e-5f);
            int by0 = (int)Math.Floor(Position.Y);
            int by1 = (int)Math.Floor(Position.Y + Height - 1e-5f);
            int bz0 = (int)Math.Floor(minZ);
            int bz1 = (int)Math.Floor(maxZ - 1e-5f);
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            for (int z = bz0; z <= bz1; z++)
            {
                if (world.GetBlock(x, y, z) == BlockType.Ladder) return true;
            }
            return false;
        }

        // Tier 8 #51 V3 — True when any cell within the player's
        // AABB is a NetherPortal block. Same scan shape as
        // IsOnLadder; sampled per-frame by the GameRenderer to
        // accumulate the portal-pulling timer (see RenderPortalTint /
        // _inPortalSeconds). Cheap (≤ ~8 cell reads on a standing
        // player); the eventual dimension-swap hook in V3 part 2
        // will key off the same predicate.
        public bool IsInNetherPortal(World world)
        {
            float minX = Position.X - HalfWidth, maxX = Position.X + HalfWidth;
            float minZ = Position.Z - HalfWidth, maxZ = Position.Z + HalfWidth;
            int bx0 = (int)Math.Floor(minX);
            int bx1 = (int)Math.Floor(maxX - 1e-5f);
            int by0 = (int)Math.Floor(Position.Y);
            int by1 = (int)Math.Floor(Position.Y + Height - 1e-5f);
            int bz0 = (int)Math.Floor(minZ);
            int bz1 = (int)Math.Floor(maxZ - 1e-5f);
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            for (int z = bz0; z <= bz1; z++)
            {
                if (world.GetBlock(x, y, z) == BlockType.NetherPortal) return true;
            }
            return false;
        }

        // Tier 8 #51 — True when the cell directly under the player's
        // foot AABB is Soul Sand. Sampled per-tick from Update so
        // the horizontal-velocity scale follows the player around;
        // cheap (≤ 4 cell reads on a wide-stance player). The probe
        // looks 1e-3 below the foot Y to land on the supporting cell
        // rather than the player's own cell.
        public bool IsStandingOnSoulSand(World world)
        {
            float minX = Position.X - HalfWidth, maxX = Position.X + HalfWidth;
            float minZ = Position.Z - HalfWidth, maxZ = Position.Z + HalfWidth;
            int bx0 = (int)Math.Floor(minX);
            int bx1 = (int)Math.Floor(maxX - 1e-5f);
            int bz0 = (int)Math.Floor(minZ);
            int bz1 = (int)Math.Floor(maxZ - 1e-5f);
            // Foot Y is where the player's collision AABB bottom sits.
            // The supporting cell is the one whose top face is at
            // Position.Y, i.e. the cell at floor(Position.Y - epsilon).
            int by = (int)Math.Floor(Position.Y - 1e-3f);
            for (int x = bx0; x <= bx1; x++)
            for (int z = bz0; z <= bz1; z++)
            {
                if (world.GetBlock(x, by, z) == BlockType.SoulSand) return true;
            }
            return false;
        }

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

        // Seconds remaining on the red hurt-flash overlay. TakeDamage
        // refreshes it to HurtFlashSeconds; the renderer reads it each
        // frame to draw a fading red full-screen wash. Decremented by the
        // per-frame Update so it auto-clears even if the player isn't
        // paying attention to TakeDamage callsites.
        public float HurtTimer { get; set; }
        public const float HurtFlashSeconds = 0.45f;

        // Seconds remaining on the arm-swing animation. Triggered on a
        // successful melee attack / break action via TriggerSwing. The
        // first-person held-item renderer reads this each frame to map
        // it to a swing-arc transform on the bottom-right gizmo. Like
        // HurtTimer, decremented in Update so a stuck non-zero value
        // resolves on its own.
        public float SwingTimer { get; set; }
        public const float SwingDurationSeconds = 0.30f;

        // Tier 4 #19 — Equipped armor inventory. The renderer sets
        // this once at world load so TakeDamage can consult the
        // equipped pieces without a per-call argument plumbing.
        // Nullable: a Player constructed before Inventory is wired
        // (e.g. in unit tests, or a mid-construction state) takes
        // raw damage with no reduction — the same behaviour the
        // pre-armor build had.
        public Inventory EquipmentInventory;

        // Take damage with the Alpha 1.1.2_01 armor reduction formula
        // applied if the player has equipped pieces. The formula is:
        //     reduced = max(1, originalDamage * (1 - sumReduction/25))
        // capped at 80 % reduction. With a full diamond set
        // (sum=20), 1 - 20/25 = 0.20, so the player still takes 20 %
        // of incoming damage (rounded up to at least 1 HP per hit
        // — full immunity wasn't a thing in Alpha and isn't here
        // either; bypassing the floor would let a full-armor player
        // ignore lava, suffocation and void damage entirely).
        //
        // Durability decrement is a TODO — the armor ladder follows
        // the same "decrement deferred" pattern the tool ladder
        // shipped with. A future pass will tick durability per hit
        // and break a piece when it hits 0.
        public void TakeDamage(int amount)
        {
            if (amount <= 0 || Health <= 0) return;
            int effective = amount;
            if (EquipmentInventory != null)
            {
                int reduction = EquipmentInventory.GetTotalArmorReduction();
                // Cap at 20 (the max a full diamond set provides) so
                // the formula stays bounded even if a future
                // enchanting / set-bonus path ever lifts individual
                // values above their Alpha defaults.
                if (reduction > 20) reduction = 20;
                if (reduction > 0)
                {
                    // Floor at 20 % of incoming damage, rounded up to
                    // at least 1. (1 - 20/25) = 0.20 = the 80 % cap
                    // the spec mandates.
                    double scale = 1.0 - reduction / 25.0;
                    if (scale < 0.20) scale = 0.20;
                    effective = (int)System.Math.Ceiling(amount * scale);
                    if (effective < 1) effective = 1;
                }
            }
            Health -= effective;
            if (Health < 0) Health = 0;
            // Flash even if the damage didn't kill — the red wash gives
            // the player feedback that something hurt them. Refresh on
            // every hit so multiple consecutive hits stay flashed.
            HurtTimer = HurtFlashSeconds;
        }

        // Kick off the held-item swing animation. Called from the game's
        // attack / break paths the moment LMB is pressed (or the
        // continuous break gets a fresh target). Refreshing the timer
        // lets a held-LMB sweep keep the arm swinging continuously
        // rather than holding mid-swing.
        public void TriggerSwing()
        {
            SwingTimer = SwingDurationSeconds;
        }

        // Heal counterpart to TakeDamage. Caps at MaxHealth and is a no-op
        // on a dead player (the respawn flow is the only path back to live
        // HP). Used by the hunger-driven slow regen and any future "ate food
        // with hunger off" instant heal.
        public void Heal(int amount)
        {
            if (amount <= 0 || Health <= 0) return;
            Health += amount;
            if (Health > MaxHealth) Health = MaxHealth;
        }

        // Refill hunger up to MaxHunger. The actual eat-food UX (slot use,
        // animation, sound) hasn't landed yet — this is the API the hunger
        // path will call from EatFood when it does.
        public void Eat(int amount)
        {
            if (amount <= 0) return;
            Hunger += amount;
            if (Hunger > MaxHunger) Hunger = MaxHunger;
        }

        public void HealFull()
        {
            Health = MaxHealth;
            Air = MaxAir;
            LastFallDistance = 0f;
            _fallPeakY = Position.Y;
        }

        // MoveAxis / IsStandingOnSomething / Collides / GetAxis / SetAxis
        // live on the Entity base — Player inherits them and Pig (and any
        // future mob) reuse the exact same walker.
    }
}
