using System;

namespace VStudioCraft.Game
{
    // (BlockType, Count, Durability) triple. BlockType doubles as the item
    // identifier — tool ids occupy the contiguous slice [WoodSword..GoldAxe]
    // in the same enum, and BlockData.IsTool routes them down the tool
    // branches in placement / rendering / inventory code.
    //
    // Durability is the in-stack damage counter for tool stacks: 0 means
    // "fresh" (or N/A for non-tools); each successful break increments it
    // by 1 until it reaches the tool's MaxDurability, at which point the
    // stack is consumed. Stacking equality includes Durability so two
    // partially-damaged pickaxes don't auto-merge into one stack with a
    // single durability value silently averaged or chosen — they stay as
    // two separate slots, matching Alpha behaviour. Tools also have
    // MaxStackSize=1 (see below) so the stacking question is moot in
    // practice; the equality is defensive against future bugs.
    //
    // Cap is Alpha's vanilla 64 for blocks; tools cap at 1 (non-stackable).
    // MaxStackSize per-stack lets the merge code in Inventory handle both.
    internal struct ItemStack : IEquatable<ItemStack>
    {
        public const int MaxCount = 64;

        public BlockType Type;
        public int Count;
        // 0 = pristine for tools; ignored / 0 for blocks. Stored as short
        // so a max-durability diamond pickaxe (1562) fits without wasting
        // a full int per slot — every Inventory.Slots[] cell carries one
        // of these and the array is hot in cache.
        public short Durability;

        public ItemStack(BlockType type, int count)
        {
            if (type == BlockType.Air || count <= 0)
            {
                Type = BlockType.Air;
                Count = 0;
                Durability = 0;
                return;
            }
            Type = type;
            int cap = MaxStackSizeFor(type);
            Count = count > cap ? cap : count;
            Durability = 0;
        }

        public ItemStack(BlockType type, int count, short durability) : this(type, count)
        {
            // Constructor preserves explicit durability for stacks that
            // are being reconstituted (e.g. picked up from the world,
            // restored from save). Non-tool stacks discard it — defensive
            // against accidentally serialising/transferring a stale
            // value into a block stack.
            if (BlockData.IsTool(type)) Durability = durability;
        }

        public bool IsEmpty => Type == BlockType.Air || Count <= 0;

        // Per-stack stack-size cap. Tools never stack. Everything else
        // uses the vanilla 64. Inventory merge logic reads this through
        // the static helper so empty cursor stacks (Type=Air) don't
        // produce a divide-by-zero or zero-cap edge case.
        public int MaxStackSize => MaxStackSizeFor(Type);

        public static int MaxStackSizeFor(BlockType type)
            => BlockData.IsTool(type) ? 1 : MaxCount;

        public static ItemStack Empty => default;

        // Two stacks merge only when type AND durability match. Count is
        // not part of identity — a partial stack and a full stack of the
        // same block are "the same item kind", and the merge code
        // computes the new count from both. Durability inclusion stops
        // a damaged tool from blob-merging with a pristine one.
        public bool Equals(ItemStack other)
            => Type == other.Type && Count == other.Count && Durability == other.Durability;

        // Same-kind test for stacking — count is irrelevant here.
        public bool SameKindAs(ItemStack other)
            => Type == other.Type && Durability == other.Durability;
    }
}
