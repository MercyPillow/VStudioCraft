using OpenTK;

namespace VStudioCraft.Game
{
    // Tier 4 #23 — Fishing Rod cast endpoint. A Bobber is the world-
    // space marker the player drops with a RMB cast and reels back in
    // with a second RMB. Unlike the arrow / thrown-projectile entities
    // it has NO physics: the bobber sits at wherever the cast raycast
    // landed and just ticks a per-bobber catch timer. The visual
    // (small white cuboid + line back to the rod tip) is drawn from
    // GameRenderer.RenderBobbers each frame.
    //
    // Bobbers are NOT persisted — Alpha 1.1.2_01 didn't preserve cast
    // lines across save/load (matches the arrow / thrown-projectile
    // policy here too). World load always starts with an empty bobber
    // list, and the auto-despawn timer protects against orphaned
    // entries when the player wanders out of reach without reeling.
    //
    // Modelled after ArrowProjectile (a flat bag-of-fields entity
    // owned by GameRenderer rather than going through a general
    // entity system) so it shares the same per-frame tick + render
    // lifecycle as the other ephemeral projectile-like entities.
    internal sealed class Bobber
    {
        // Phase 5d — server-assigned id for multiplayer replication.
        // See ArrowProjectile.NetworkId for the lifecycle contract.
        public int NetworkId;

        // World-space position of the bobber. Set at cast time and
        // never changes — the bobber is essentially static for the
        // life of the cast.
        public Vector3 Position;

        // Seconds remaining before the cast catches a fish. Set at
        // cast time to a uniform random value in
        // [MinCatchSec, MaxCatchSec]; ticks down each frame. Once it
        // reaches zero, Caught flips to true and the next reel pulls
        // a Raw Porkchop. Reel BEFORE the timer hits zero pulls
        // nothing — matches Alpha behaviour where you have to wait
        // for the bobber to dip before reeling.
        public float CatchTimer;

        // True once CatchTimer has reached zero and a fish is on the
        // line. The renderer uses this to add a tiny vertical wiggle
        // to the bobber so the player has a visual cue to reel.
        public bool Caught;

        // Total seconds since the cast started. Used for both the
        // catch-wiggle phase (sin(time * wiggleFreq)) and for the
        // safety despawn (a bobber that's never reeled gives up
        // after AutoDespawnSec to keep the bobber list bounded).
        public float AgeSec;

        // Back-reference to the player who cast this bobber. Lets the
        // renderer draw the line from the rod tip (player eye + a bit
        // of forward) back to the bobber position without having to
        // pass Player into the render pass separately. Single-player
        // scope keeps this trivial; in a multiplayer port the field
        // would key into a player-id table instead.
        public Player Owner;

        // Catch timer bounds (in seconds). Alpha 1.1.2_01 used a
        // ~5..30s window for a cast to land a fish; preserved
        // verbatim so the gameplay rhythm matches the era.
        public const float MinCatchSec = 5f;
        public const float MaxCatchSec = 30f;

        // Safety despawn for a cast the player never reels in.
        // Alpha didn't have one — bobbers persisted indefinitely —
        // but without it a player who casts and walks away leaks
        // bobbers into the renderer's list every cast. 60s is well
        // past the worst-case catch wait (30s) plus a generous
        // grace period for the player to reel after the dip.
        public const float AutoDespawnSec = 60f;

        // Visual cube half-size for the renderer. Small enough to
        // read as a "bobber on the water" rather than a flying
        // block, matching the visual scale used by ArrowProjectile
        // and ThrownProjectile.
        public const float RenderHalfSize = 0.05f;

        // Wiggle amplitude (in blocks) and frequency (in radians/s)
        // applied to the bobber's Y once Caught flips. Both small —
        // the wiggle is a polish cue, not a load-bearing visual.
        public const float WiggleAmplitude = 0.08f;
        public const float WiggleFrequency = 6f;
    }
}
