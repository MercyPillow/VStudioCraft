using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 8 #43 — Primed TNT. Spawned when the player right-clicks
    // a placed Tnt block with Flint & Steel held; the cell becomes
    // Air, a PrimedTntEntity replaces it at the cell centre, and a
    // 4-second fuse counts down. On expiry the host renderer runs
    // the explosion algorithm (Explode below) which scans a sphere
    // around the entity, breaks every block whose blast resistance
    // is below the local intensity, and (probabilistically) drops
    // the broken blocks as items.
    //
    // The fuse visual is a colour pulse — the entity flashes white
    // every ~0.2 s so the player can see it priming. No physics
    // (yet) — the entity stays at its spawn cell. Future polish
    // could give it a small upward kick + gravity so a bottom-up
    // chain looks more like Alpha; for V1 stationary works.
    internal sealed class PrimedTntEntity
    {
        public Vector3 Position;     // cell centre when spawned
        public float FuseSeconds;    // counts down from FuseStart

        // Alpha canonical fuse: 80 redstone ticks = 4.0 s real time.
        public const float FuseStart = 4.0f;

        // Explosion radius for primed TNT. Alpha's TNT has a base
        // strength of 4, which produces a blast radius of roughly
        // 4 cells along each axis (the actual sphere has 5-cell
        // reach at corners due to the strength-vs-resistance ray
        // attenuation; we approximate with a 4-cell euclidean
        // radius which is close enough at the player's typical
        // viewing scale).
        public const float ExplosionRadius = 4.0f;

        // Render scale — slightly under 1.0 so the cube doesn't
        // z-fight the air cell its position is centred in.
        public const float RenderScale = 0.95f;

        public PrimedTntEntity(Vector3 cellCentre)
        {
            Position = cellCentre;
            FuseSeconds = FuseStart;
        }

        // Returns true when the fuse has expired and the host should
        // explode + remove this entity. dt is the per-frame delta;
        // FuseSeconds counts down each call.
        public bool Update(float dt)
        {
            FuseSeconds -= dt;
            return FuseSeconds <= 0f;
        }

        // Per-frame render colour. Pulses between dark red and a
        // brighter wash as the fuse counts down — short flash window
        // every ~0.2 s so the cube reads as "primed" at any fuse
        // remaining time.
        public Vector3 GetRenderColor()
        {
            // Pulse phase: 0..1 over a 0.2 s window.
            float phase = (FuseSeconds * 5f) % 1f;
            bool flash = phase < 0.25f;
            return flash
                ? new Vector3(1.00f, 0.95f, 0.85f)   // bright wash
                : new Vector3(0.85f, 0.20f, 0.10f); // dim TNT-red
        }

        public Vector3 RenderPosition
            => new Vector3(Position.X - 0.5f * RenderScale,
                           Position.Y - 0.5f * RenderScale,
                           Position.Z - 0.5f * RenderScale);
    }
}
