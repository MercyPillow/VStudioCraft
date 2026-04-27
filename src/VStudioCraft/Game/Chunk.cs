using System;
using System.IO;

namespace VStudioCraft.Game
{
    internal sealed class Chunk
    {
        public const int SizeX = 16;
        public const int SizeY = 128;
        public const int SizeZ = 16;
        public const int BlockCount = SizeX * SizeY * SizeZ;

        public int ChunkX { get; }
        public int ChunkZ { get; }

        public bool IsModified { get; set; }

        // True if this chunk has fluid cells that might still propagate. Set
        // when terrain gen places water/lava, when the player edits near a
        // fluid, or when a neighbour-chunk fluid tick writes into us. Cleared
        // when a tick scans the chunk and finds no air-bordered fluid (i.e.
        // every source has reached steady state). Lets the tick skip the
        // bulk of the world cheaply once flow settles.
        public bool HasActiveFluid { get; set; }

        // Flat byte storage, indexed as (x * SizeY + y) * SizeZ + z so Z is the fastest-
        // varying axis — matches the mesher's innermost loop for cache-friendly sweeps.
        private readonly byte[] _blocks = new byte[BlockCount];

        // Per-block lighting, packed as (sky << 4) | block — 4 bits each, 0..15.
        // Sky = exposure to the sky (full = 15 above ground); block = emission from
        // luminous blocks (lava, torches later). Recomputed by LightCalculator after
        // generation and after edits; not persisted (round-tripped to disk would just
        // be redundant since lighting is a deterministic function of blocks).
        private readonly byte[] _light = new byte[BlockCount];

        // Per-cell metadata. Today only fluid cells use it: low 4 bits = remaining
        // horizontal spread reach, bit 4 (0x10) = falling marker. Other cells leave
        // it 0. Not persisted (re-derived from blocks on world entry; a fresh tick
        // will reach the steady state within ~7 ticks for water columns).
        private readonly byte[] _meta = new byte[BlockCount];

        public Chunk(int chunkX, int chunkZ)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
        }

        public byte[] RawBlocks => _blocks;
        public byte[] RawLight => _light;
        public byte[] RawMeta => _meta;

        public static int Index(int x, int y, int z) => (x * SizeY + y) * SizeZ + z;

        public BlockType Get(int x, int y, int z)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ)
                return BlockType.Air;
            return (BlockType)_blocks[Index(x, y, z)];
        }

        public void Set(int x, int y, int z, BlockType t)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return;
            _blocks[Index(x, y, z)] = (byte)t;
        }

        // Tier 4 #14 — Wheat growth uses the per-cell metadata byte to
        // hold the growth stage in the low 4 bits (clamped 0..7). The
        // mesher reads this through Block.GetWheatTileForStage so each
        // wheat block in a chunk picks its own per-stage tile, and the
        // random-tick promotes meta+1 once enough light is present.
        // The accessor is bounds-checked to mirror Get/Set; out-of-range
        // reads return 0 (a freshly-planted sprout if the caller is the
        // mesher, harmless if it's the tick).
        public byte GetMeta(int x, int y, int z)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return 0;
            return _meta[Index(x, y, z)];
        }

        public void SetMeta(int x, int y, int z, byte value)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return;
            _meta[Index(x, y, z)] = value;
        }

        public byte GetSkyLight(int x, int y, int z)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return 15;
            return (byte)((_light[Index(x, y, z)] >> 4) & 0xF);
        }

        public void SetSkyLight(int x, int y, int z, byte value)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return;
            int i = Index(x, y, z);
            _light[i] = (byte)((_light[i] & 0x0F) | ((value & 0xF) << 4));
        }

        public byte GetBlockLight(int x, int y, int z)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return 0;
            return (byte)(_light[Index(x, y, z)] & 0xF);
        }

        public void SetBlockLight(int x, int y, int z, byte value)
        {
            if ((uint)x >= SizeX || (uint)y >= SizeY || (uint)z >= SizeZ) return;
            int i = Index(x, y, z);
            _light[i] = (byte)((_light[i] & 0xF0) | (value & 0xF));
        }

        public void WriteTo(BinaryWriter w)
        {
            w.Write(_blocks, 0, _blocks.Length);
        }

        public void ReadFrom(BinaryReader r)
        {
            int read = 0;
            while (read < _blocks.Length)
            {
                int n = r.Read(_blocks, read, _blocks.Length - read);
                if (n <= 0) break;
                read += n;
            }
            // Sanitize legacy block IDs that no longer exist in the enum
            // (e.g. TallGrass=35 was removed once we narrowed scope to
            // Alpha 1.1.2_01, which had no tall grass). Without this,
            // older saves load fine but the mesher hits the default
            // branch in BlockData and renders the stale ID as opaque
            // stone-textured cubes wherever the foliage used to be.
            // Mapping legacy flora to Air leaves a tidy scatter of empty
            // cells the player can walk through, matching what they'd
            // see if they re-generated the world today.
            for (int i = 0; i < _blocks.Length; i++)
            {
                byte b = _blocks[i];
                if (!Enum.IsDefined(typeof(BlockType), b))
                    _blocks[i] = (byte)BlockType.Air;
            }
        }

        // Tier 4 #14 (v8) — Sparse wheat-metadata persistence. The full
        // 32 KB _meta buffer is 99.9% zero in any non-fluid-tick world,
        // so writing every byte would balloon save sizes. Instead we
        // walk the block array, find Wheat cells, and emit just
        // (index:int, meta:byte) pairs for those cells. Fluid metadata
        // is still re-derived from a fresh tick on load (its low-4-bits
        // spread reach reaches steady state within a few ticks), so the
        // save format only needs to round-trip cells whose meta is
        // genuinely state and not derived. Today that's wheat plus the
        // four door halves (Tier 4 #16) — both halves carry an identical
        // facing/open/hinge byte that drives the mesher and the toggle
        // path, and losing it would render every saved door closed-and-
        // facing-north on reload. The wire format is unchanged: pairs of
        // (index:int, meta:byte). The reader (ReadSparseMeta) doesn't
        // care WHICH block produced the meta — it just stamps the byte
        // back into the slot — so no version bump is needed.
        public void WriteSparseMeta(BinaryWriter w)
        {
            int count = 0;
            for (int i = 0; i < _blocks.Length; i++)
                if (IsSparseMetaCell(_blocks[i])) count++;
            w.Write(count);
            for (int i = 0; i < _blocks.Length; i++)
                if (IsSparseMetaCell(_blocks[i]))
                {
                    w.Write(i);
                    w.Write(_meta[i]);
                }
        }

        // Predicate for the sparse-meta filter — kept as a tiny helper
        // so the read/write loops stay readable AND so adding a new
        // metadata-bearing block (saplings, cake bites, …) is a one-
        // line change. Pure id check; we don't peek at the meta byte
        // because a zero meta is a valid persisted value (e.g. wheat
        // stage 0, door facing-north-closed-left-hinge).
        private static bool IsSparseMetaCell(byte id)
        {
            BlockType t = (BlockType)id;
            if (t == BlockType.Wheat) return true;
            if (BlockData.IsDoor(t)) return true;
            return false;
        }

        public void ReadSparseMeta(BinaryReader r)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int idx = r.ReadInt32();
                byte m = r.ReadByte();
                if ((uint)idx < (uint)_meta.Length)
                    _meta[idx] = m;
            }
        }
    }
}
