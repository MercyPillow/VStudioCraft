using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V7 — Ghast fireball projectile. Parallels
    // ThrownProjectile (snowball/egg) and ArrowProjectile in lifecycle
    // shape (renderer-owned list, swept block-raycast + AABB-mob hit
    // test, despawn on first contact), but the physics + on-hit
    // semantics differ:
    //
    //   * No gravity — the fireball travels in a straight line. Alpha
    //     ghasts shoot a slow no-gravity glow ball; same here.
    //   * Slower than arrows / snowballs (10 m/s vs 22-30) so the
    //     player can dodge it on visible reaction; this is canonical
    //     and the slow speed plus the visible orange trail is what
    //     makes Ghast combat feel like dodgeball rather than reflex.
    //   * On contact with a block OR a mob OR the player AABB, calls
    //     into the renderer's shared Explode(center, radius=2) — same
    //     blast pass TNT uses, just a smaller radius. That handles
    //     terrain damage, drop rolls, and player damage falloff.
    //   * No item drop on impact. Fireballs vaporise.
    //
    // Reused by V8 (Blaze): same projectile, different muzzle (3-shot
    // volley with a small spread) — Blaze constructs FireballProjectile
    // instances directly and adds them to the same _fireballs list.
    internal sealed class FireballProjectile
    {
        // Server-assigned id for multiplayer replication. Kept here
        // alongside the other projectile classes' NetworkId fields so
        // Phase-5 sync code can dispatch on type uniformly.
        public int NetworkId;

        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Origin;

        // Lifetime cap so a fireball that misses everything (clear
        // Nether sky, no terrain in the path) eventually self-despawns
        // instead of streaming forever. 5 s @ 10 m/s = 50 blocks of
        // travel before timeout — roughly matches MaxRange.
        public float TimeAliveSeconds;

        // Physics constants. Speed is the canonical Alpha ghast value;
        // hit radius is small (the visible body is bigger, so the
        // collision capsule reads like the inner core of the visible
        // glow); render half-size matches the visible blob.
        public const float MaxRange       = 60f;
        public const float MaxLifetime    = 5.5f;
        public const float HitRadius      = 0.20f;
        public const float RenderHalfSize = 0.40f;
        public const float MuzzleSpeed    = 10f;

        // Explosion radius when a fireball lands. Smaller than TNT
        // (which is 4) so a Ghast at long range can chip terrain
        // without reshaping the cavern. Player damage falloff is
        // handled by the shared Explode helper.
        public const float ExplosionRadius = 2.0f;

        // Render tint — bright orange-yellow core. The renderer draws
        // a single solid-coloured cube; fancier flame wisps would need
        // a particle attachment and aren't required for V7 polish.
        public static readonly Vector3 BodyColor = new Vector3(1.00f, 0.55f, 0.10f);
    }
}
