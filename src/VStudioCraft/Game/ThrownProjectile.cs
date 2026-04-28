using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 4 #20 — Snowball + Egg throwable projectiles. Parallels
    // ArrowProjectile (Tier 4 #17) — both share the renderer-owned-list,
    // gravity-bound-arc, swept-block-raycast + AABB-mob-test physics
    // pattern — but the on-hit / on-land semantics differ enough that a
    // dedicated class is clearer than a Kind enum bolted onto Arrow:
    //   * Arrow has a damage value and a 30-second LANDED state where
    //     it sticks visibly mid-air at the impact point until despawn.
    //   * Snowball / Egg deal 0 damage to almost everything (snowball
    //     hits a Blaze for 3 — dead-code branch, no Blazes today),
    //     leave nothing on a block hit, and despawn immediately on
    //     ANY contact (block, mob, max-range, sub-world). No landed
    //     phase, no recoverable item drop on land.
    //   * Egg specifically rolls a 5% chicken-spawn chance at the
    //     impact point on any landing (block hit OR mob hit) — Alpha
    //     1.1.2_01 behaviour. The roll is deterministic-but-reasonable
    //     via per-projectile seed; this matches the same RNG-as-state
    //     pattern PassiveMob uses.
    //
    // Why Option B (parallel class) and not Option A (refactor Arrow
    // into a Kind enum): the spec called out that either approach is
    // fine, and the Arrow class is structurally arrow-shaped (Damage
    // baked at fire time, LandedTimer / LandedDespawnSec / HasLanded
    // state machine, MaxDrawSeconds bow-charge constant) — generalising
    // those onto a Kind enum would require either parameterising every
    // constant on kind or carrying dead state for non-arrow kinds.
    // A parallel class with its own minimal state surface is shorter,
    // and each kind's hit logic stays in one place. Visual rendering
    // shares the same flat-cube path as arrows; only the body colour
    // varies (white for snowball, off-white for egg).
    internal sealed class ThrownProjectile
    {
        public enum Kind
        {
            Snowball,
            Egg,
        }

        // Phase 5d — server-assigned id for multiplayer replication.
        // See ArrowProjectile.NetworkId for the lifecycle contract.
        public int NetworkId;

        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Origin;
        public Kind ProjectileKind;

        // Per-projectile RNG. Seeded at fire time from a tick-derived
        // value so successive throws don't all roll the same chicken
        // outcome; held on the projectile rather than calling the
        // shared world RNG so the projectile's behaviour is local
        // and not coupled to whatever else is rolling. Used only by
        // the egg's chicken-spawn roll today.
        public System.Random Rng;

        // Same physics constants as the arrow — ballistic arc identical
        // to a tossed item drop. Kept as separate names so a future
        // tweak to one projectile family doesn't accidentally retune
        // the other.
        public const float MaxRange       = 64f;
        public const float HitRadius      = 0.15f;
        public const float RenderHalfSize = 0.08f;
        public const float GravityPerSec  = 20f;

        // Muzzle speed for both kinds. Alpha snowball muzzle was ~22 m/s
        // (1.5x bow-min, ~0.7x bow-max). Eggs use the same — both are
        // hand-thrown, no draw-charge; the spec called out 22f
        // explicitly. Kept as a constant so callers don't have to know
        // the magic number.
        public const float MuzzleSpeed = 22f;

        // 5% chicken-spawn chance per egg landing (Alpha 1.1.2_01).
        // Rolled once per projectile, on whatever the egg hits first
        // (block or mob).
        public const float EggChickenSpawnChance = 0.05f;

        // Render-tint colours. Snowball is bright white; egg is a
        // warm off-white-with-brown read (procedural cube body colour
        // — speckles aren't expressible in a flat-colour overlay
        // shader, so the off-white anchors the silhouette and the
        // tile-based icon in the inventory carries the speckle look).
        public static readonly Vector3 SnowballColor = new Vector3(0.95f, 0.95f, 1.00f);
        public static readonly Vector3 EggColor      = new Vector3(0.92f, 0.85f, 0.70f);

        public Vector3 GetRenderColor()
        {
            return ProjectileKind == Kind.Snowball ? SnowballColor : EggColor;
        }
    }
}
