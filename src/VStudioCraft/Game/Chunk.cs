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
        }
    }
}
