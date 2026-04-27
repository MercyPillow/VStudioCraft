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
        {
            if (BlockData.IsTool(type)) return 1;
            // Tier 4 #20 — Snowball stacks to 16 in Alpha 1.1.2_01
            // (vs the default 64). Eggs also cap at 16 in canonical
            // Alpha — included here so the stack-cap surface stays
            // canonically correct alongside the new Snowball entry,
            // even though Tier 3 #12's chicken-lay path only ever
            // drops 1 at a time. Inventory merge respects this cap
            // because it reads MaxStackSize on every merge attempt.
            if (type == BlockType.Snowball || type == BlockType.Egg) return 16;
            // Tier 4 #15 — Bucket family. Empty bucket caps at 16
            // (matches Alpha 1.1.2_01 — empty buckets stack so the
            // player can carry a small pile without burning a hotbar
            // slot per pail), but the THREE filled variants are
            // unstackable. The Alpha rule for filled buckets is "one
            // per slot" — a single pail of water-or-lava is heavy
            // enough that it can't share a stack with another. Milk
            // joins the unstackable set for parity.
            if (type == BlockType.BucketEmpty) return 16;
            if (type == BlockType.BucketWater
             || type == BlockType.BucketLava
             || type == BlockType.BucketMilk) return 1;
            // Tier 4 #21 — Saddles are unstackable in Alpha 1.1.2_01.
            // One saddle per slot; the player can carry multiple by
            // burning multiple slots.
            if (type == BlockType.Saddle) return 1;
            // Tier 4 #23 — Fishing rods are unstackable in Alpha
            // 1.1.2_01 (each rod has its own durability metadata, so
            // they couldn't share a slot even if stacking were
            // permitted; the cap is the explicit Alpha rule).
            if (type == BlockType.FishingRod) return 1;
            return MaxCount;
        }

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
