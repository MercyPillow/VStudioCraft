namespace VStudioCraft.Game
{
    // Tier 4 #24 — Painting entity. RMB-on-wall placement spawns one
    // of these into World.Paintings; the renderer (RenderPaintings)
    // draws it as a flat textured rectangle inset slightly into the
    // wall plane. Like a torch, the painting does NOT occupy a block
    // cell — physics ignores it and the player walks straight through.
    //
    // Position model: (X, Y, Z) is the cell BEHIND the painting (the
    // solid wall block the painting hangs on). Facing is the wall's
    // normal direction — the side of (X, Y, Z) the painting faces.
    // Width × Height is in BLOCKS (1×1 / 1×2 / 2×1 / 2×2 / 4×3 in V1);
    // the rectangle extends in the wall plane to the +X / +Y direction
    // from the anchor cell when Facing = North/South, and to the +Z /
    // +Y direction when Facing = East/West.
    //
    // Variant indexes the art tile (0..4 → TilePainting1x1 ..
    // TilePainting4x3). The variant also implies the size — V1 uses a
    // 1-to-1 variant→size table since "auto-size to largest available
    // rectangle" is a deferred polish feature; for now placement
    // always produces a 1×1 entry (Variant=0) regardless of free-wall
    // shape. The data model is sized for the full variant set so the
    // future polish path doesn't need a save-format bump.
    internal sealed class Painting
    {
        public int X, Y, Z;
        public BlockFacing Facing;
        public int Width;
        public int Height;
        public int Variant;
    }
}
