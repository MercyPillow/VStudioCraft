using System;

namespace VStudioCraft.Game
{
    // (BlockType, Count) pair. We're a block-only world right now — no
    // tools, no food — so the stack key is just BlockType. Empty slots
    // canonicalise to (Air, 0); the IsEmpty check below treats either
    // sentinel as empty so we don't have to be careful about which we
    // wrote. Cap is Alpha's vanilla 64; non-stackables (tools etc) don't
    // exist yet so there's nothing that opts out.
    internal struct ItemStack : IEquatable<ItemStack>
    {
        public const int MaxCount = 64;

        public BlockType Type;
        public int Count;

        public ItemStack(BlockType type, int count)
        {
            if (type == BlockType.Air || count <= 0)
            {
                Type = BlockType.Air;
                Count = 0;
                return;
            }
            Type = type;
            Count = count > MaxCount ? MaxCount : count;
        }

        public bool IsEmpty => Type == BlockType.Air || Count <= 0;

        public static ItemStack Empty => default;

        public bool Equals(ItemStack other) => Type == other.Type && Count == other.Count;
    }
}
