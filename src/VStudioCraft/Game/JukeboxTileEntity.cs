namespace VStudioCraft.Game
{
    // Per-block persistent state for a Jukebox placed in the world.
    //
    // A jukebox cell stores AT MOST ONE inserted music disc — there's
    // no "queue" of discs. Disc==Air means the slot is empty (no music
    // playing); Disc13 / DiscCat means that disc is loaded and the
    // matching track is conceptually playing. The world owns a
    // Dictionary<(int wx, int wy, int wz), JukeboxTileEntity> (see
    // World.JukeboxEntities) so jukebox state survives chunk load /
    // unload — chunks don't carry tile-entity sidecar data.
    //
    // Why a tile-entity at all (vs encoding the disc in metadata):
    //   - Other tile-entity blocks (Furnace, Chest) already use this
    //     pattern, so the save / interact / break paths have shared
    //     plumbing to extend.
    //   - Future extension (volume, pitch, looping) lands on the entity
    //     without burning per-cell metadata bits.
    //   - Persistence is uniform: the JukeboxEntities dict slots in
    //     after the chest dict in WorldSaveFormat the same way every
    //     other tile-entity block does.
    //
    // No tick: jukeboxes are passive. The renderer doesn't iterate
    // JukeboxEntities each frame — only when the player RMBs to insert
    // / eject a disc, or breaks the block (eject + drop). The actual
    // audio playback runs on the dedicated MusicSource in AudioEngine
    // and is triggered at insert/eject time, not per-tick.
    internal sealed class JukeboxTileEntity
    {
        // The currently-inserted disc, or BlockType.Air if empty. Only
        // Disc13 / DiscCat are valid non-empty values; the insert path
        // gates on that range so a corrupt save can't poison the entity
        // with a bogus type.
        public BlockType Disc;

        // True when no disc is loaded. Used by the save-compaction path
        // so a freshly-placed jukebox the player hasn't touched doesn't
        // need to persist (the entity dict just doesn't get an entry
        // for it until insert).
        public bool IsEmpty => Disc == BlockType.Air;
    }
}
