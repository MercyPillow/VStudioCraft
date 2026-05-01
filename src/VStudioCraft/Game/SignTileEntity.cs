namespace VStudioCraft.Game
{
    // Tier 8 #44 — Per-block persistent state for a Sign (post or wall
    // variant) placed in the world.
    //
    // A sign carries up to 4 lines of player-typed text. The text is
    // sealed at placement time (Alpha-faithful behaviour — there is no
    // "edit existing sign" path; to retype, break and replace the
    // sign).
    //
    // Why two block types but ONE tile entity class:
    //   - SignPost (free-standing on top of a block) and WallSign
    //     (mounted on the side of a block) differ only in geometry +
    //     placement face. Their persisted payload is identical — 4
    //     lines of text — so a single entity class covers both. The
    //     block id at the same (x,y,z) tells the mesher which geometry
    //     to emit; the chunk metadata byte at the same cell encodes
    //     the facing (low 2 bits = N/S/E/W). The tile entity only
    //     carries the text.
    //
    // No tick: signs are passive. The renderer doesn't iterate
    // SignEntities each frame — only when a sign is placed (the editor
    // commits text into the entity), broken (entity removed), or
    // rendered (the overlay text pass reads each visible sign's lines).
    //
    // Persistence: WorldSaveFormat v14 appends a per-entity record
    // (x, y, z, then 4 length-prefixed strings). Pre-v14 saves had no
    // sign block in the BlockType enum, so legacy worlds load with an
    // empty sign dict and the BlockType range simply isn't populated.
    internal sealed class SignTileEntity
    {
        // The four lines of text, in top-to-bottom order. Empty
        // strings render as blank lines. The editor caps each line
        // length and the total payload, but the entity stores
        // whatever was given — defensive against a corrupt save or a
        // future longer-line config. Indices 0..3 = top→bottom on the
        // sign face.
        public string[] Lines = new string[4] { "", "", "", "" };

        // True if every line is empty / null. The save-compaction
        // path could use this to skip persisting blank signs that the
        // player placed and immediately broke before typing — the
        // entity dict's record won't exist until the editor commits
        // the first non-empty line, so in practice IsEmpty is mostly
        // a safety hatch.
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (!string.IsNullOrEmpty(Lines[i])) return false;
                }
                return true;
            }
        }
    }
}
