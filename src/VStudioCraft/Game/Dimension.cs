namespace VStudioCraft.Game
{
    // Tier 8 #51 V4 — Dimension identifier. The renderer holds two
    // World instances (overworld + nether, lazy-created); the active
    // dimension is whichever World `_world` currently points at.
    // The enum exists so the few dimension-aware systems (sky / fog
    // tint, mob-spawn rules, portal-touch return-position routing,
    // save format) can branch on a meaningful name rather than
    // comparing World references.
    internal enum Dimension : byte
    {
        Overworld = 0,
        Nether    = 1,
    }
}
