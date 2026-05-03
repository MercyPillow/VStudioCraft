using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 4 #17 — Bow + Arrow combat. An ArrowProjectile is the
    // in-flight entity spawned when the player releases a charged
    // bow shot. It travels along the camera-forward direction at a
    // muzzle velocity proportional to draw fraction, falls under
    // gravity, and despawns when it hits a solid block, a mob, or
    // flies past the max range.
    //
    // Modelled after DroppedItem (a flat bag-of-fields entity owned
    // by GameRenderer rather than going through a general entity
    // system) so it shares the same per-frame tick + render
    // lifecycle. Unlike drops, arrows are NOT persisted to save
    // files — Alpha 1.1.2_01 didn't preserve in-flight arrows
    // across save/load, only embedded ones (which we don't model
    // either; landed arrows just despawn after a short timer).
    //
    // Damage is computed at fire time from the muzzle velocity and
    // baked into Damage so a slow shot stays a weak hit even if it
    // happens to clip a mob at the end of its arc — matches Alpha's
    // "the bow's draw decides the damage, not the arrow's energy
    // when it strikes".
    internal sealed class ArrowProjectile
    {
        // Phase 5d — server-assigned id for multiplayer replication.
        // 0 = not yet networked; the host's hub diff pass assigns one
        // and ships a ProjectileSpawnPacket to each in-range friend.
        // Untouched in singleplayer.
        public int NetworkId;

        public Vector3 Position;
        public Vector3 Velocity;

        // World-space origin of the shot — used to gate the max-
        // range despawn. Arrows that fly more than MaxRange blocks
        // from origin (without hitting anything) just despawn so
        // the projectile list doesn't grow unbounded for missed
        // shots into the void.
        public Vector3 Origin;

        // Per-shot damage computed at fire time. drawFrac × 9
        // (clamped to a minimum of 1) — full draw lands the Alpha
        // 1.1.2 max bow damage of 9. Kept here so we don't have to
        // re-derive it from velocity at hit time, which would
        // double-count drag/gravity if we added them later.
        public int Damage;

        // True once the arrow has hit a solid block and stuck.
        // Stops the per-frame physics and starts the LandedTimer
        // despawn countdown. Visually the arrow stays still
        // mid-air at the impact point until despawn.
        public bool HasLanded;

        // Seconds since the arrow landed. Counts up while
        // HasLanded; arrow despawns once this exceeds
        // LandedDespawnSec. Reset is irrelevant — landed arrows
        // never un-land.
        public float LandedTimer;

        // Maximum world-space distance from Origin before the
        // projectile auto-despawns (in blocks). 64 matches Alpha's
        // bow effective range — past this the projectile is wasted
        // anyway and we don't need to keep ticking it.
        public const float MaxRange = 64f;

        // Seconds an embedded / landed arrow lingers before
        // despawning. Alpha kept arrows in the world for ~1 minute
        // visible-ish; we shorten to 30s so the projectile list
        // doesn't accumulate stuck arrows after a target practice
        // session — visual polish only, doesn't affect gameplay.
        public const float LandedDespawnSec = 30f;

        // Half-extent of the arrow's collision proxy used for
        // mob-AABB intersection tests. Small enough that a near-
        // miss doesn't count as a hit, large enough that a
        // graze on the AABB edge still registers.
        public const float HitRadius = 0.15f;

        // Visual cube half-size for the renderer fallback. Arrows
        // ship as a tiny grey cube for V1 — proper oriented-
        // billboard rendering is a polish TODO. The size is small
        // enough to read as a "bullet trail" rather than a flying
        // block.
        public const float RenderHalfSize = 0.08f;

        // Speed bounds at the muzzle. Min is the no-draw release
        // (drawFrac=0 → 3 m/s), max is full-draw (drawFrac=1.0 →
        // 30 m/s). Linear interpolation between the two.
        public const float MinMuzzleSpeed = 3f;
        public const float MaxMuzzleSpeed = 30f;

        // Damage at full draw. Scales linearly with draw fraction;
        // floor of 1 so a tap-fire still scratches a target.
        public const int MaxDamage = 9;

        // Bow draw cap in seconds. RMB-held duration past this
        // doesn't add any more power — matches Alpha's roughly
        // 1-second draw to max.
        public const float MaxDrawSeconds = 1.0f;

        // Gravity applied to in-flight arrows. Same value the
        // DroppedItem tick uses (-20 m/s²) so an arrow's arc reads
        // consistently with a tossed item drop. Player physics
        // uses a different number; matching the drop physics is
        // correct because both are loose entities subject to
        // simple ballistic gravity, while the player physics has
        // jump-curve tuning baked in.
        public const float GravityPerSec = 20f;
    }
}
