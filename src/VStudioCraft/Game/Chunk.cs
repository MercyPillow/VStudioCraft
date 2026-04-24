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

        public Chunk(int chunkX, int chunkZ)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
        }

        public byte[] RawBlocks => _blocks;

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
