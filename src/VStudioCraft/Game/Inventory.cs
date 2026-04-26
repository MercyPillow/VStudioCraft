using System;

namespace VStudioCraft.Game
{
    // 45-slot player inventory + 1 cursor. Slot indexing matches the
    // InventoryScreen panel layout exactly so a slot rect from
    // InventoryScreen.GetSlotRect maps directly to Slots[i] without
    // a translation table:
    //   0..35  — main grid (4×9, top-down row-major)
    //   36..44 — hotbar row
    //
    // Constants here MUST stay in lock-step with InventoryScreen.MainRows /
    // Cols / TotalSlots — the renderer indexes Slots[] using slot rectangles
    // pulled from the screen layout, and a mismatch would walk past the end
    // of Slots[].
    //
    // The cursor stack lives outside the slot range and follows the
    // mouse while the inventory screen is open. Click handling routes
    // through HandleLeftClickSlot / HandleLeftClickOutside on the
    // render thread (the host queues clicks via InputState; the renderer
    // consumes them once per frame).
    internal sealed class Inventory
    {
        public const int MainCount = 36;   // 4 rows × 9 cols
        public const int HotbarCount = 9;
        public const int TotalSlots = MainCount + HotbarCount;  // 45
        public const int HotbarStart = MainCount;

        public readonly ItemStack[] Slots = new ItemStack[TotalSlots];
        public ItemStack Cursor;

        // Seed the hotbar with a starter loadout — used at world start so
        // creative-mode play has the same blocks-on-the-bar feel it had
        // before the ItemStack refactor. Survival uses the same defaults
        // for now (the player can clear them by dropping, or just use
        // them as a head-start). Main grid stays empty either way.
        public void FillHotbar(BlockType[] types, int countPerStack)
        {
            int n = Math.Min(types.Length, HotbarCount);
            for (int i = 0; i < n; i++)
            {
                Slots[HotbarStart + i] = new ItemStack(types[i], countPerStack);
            }
        }

        public ItemStack GetHotbar(int hotbarIndex)
        {
            if (hotbarIndex < 0 || hotbarIndex >= HotbarCount) return ItemStack.Empty;
            return Slots[HotbarStart + hotbarIndex];
        }

        // Decrement the hotbar slot at `hotbarIndex` by 1. Used by the
        // survival place-on-RMB path. Returns true if at least one item
        // was removed (i.e. the place is allowed to proceed). The slot
        // is canonicalised to Empty when it hits zero so renderers
        // don't have to special-case Count=0 ItemStacks.
        public bool DecrementHotbar(int hotbarIndex)
        {
            if (hotbarIndex < 0 || hotbarIndex >= HotbarCount) return false;
            ref var s = ref Slots[HotbarStart + hotbarIndex];
            if (s.IsEmpty) return false;
            s.Count--;
            if (s.Count <= 0) s = ItemStack.Empty;
            return true;
        }

        // Try to absorb `stack` into the inventory. Two-pass policy
        // matching Alpha's pickup behaviour:
        //   1. Top up existing same-type stacks that have room. Hotbar
        //      first so picked-up items appear where the player looks.
        //   2. Drop any leftover into the first empty slot (hotbar then main).
        // Returns whatever didn't fit (Empty when fully absorbed).
        public ItemStack TryAdd(ItemStack stack)
        {
            if (stack.IsEmpty) return ItemStack.Empty;

            // Pass 1 — merge into matching partial stacks.
            for (int phase = 0; phase < 2; phase++)
            {
                int start = phase == 0 ? HotbarStart : 0;
                int end   = phase == 0 ? HotbarStart + HotbarCount : MainCount;
                for (int i = start; i < end; i++)
                {
                    ref var s = ref Slots[i];
                    // SameKindAs (not Type==) so a damaged pickaxe doesn't
                    // absorb a pristine one. Tools have MaxStackSize=1 too,
                    // so even matching same-kind stacks get no room — the
                    // pickup falls through to the empty-slot pass.
                    if (s.IsEmpty || !s.SameKindAs(stack)) continue;
                    int room = s.MaxStackSize - s.Count;
                    if (room <= 0) continue;
                    int take = Math.Min(room, stack.Count);
                    s.Count += take;
                    stack.Count -= take;
                    if (stack.Count == 0) return ItemStack.Empty;
                }
            }

            // Pass 2 — drop into the first empty slot.
            for (int phase = 0; phase < 2; phase++)
            {
                int start = phase == 0 ? HotbarStart : 0;
                int end   = phase == 0 ? HotbarStart + HotbarCount : MainCount;
                for (int i = start; i < end; i++)
                {
                    ref var s = ref Slots[i];
                    if (!s.IsEmpty) continue;
                    s = stack;
                    return ItemStack.Empty;
                }
            }
            return stack; // out of room
        }

        // Standard left-click slot exchange (Alpha rules):
        //   cursor empty + slot empty  → no-op
        //   cursor empty + slot full   → pick entire slot up to cursor
        //   cursor full  + slot empty  → drop cursor into slot
        //   cursor full  + slot, same type → top up slot, leftover stays in cursor
        //   cursor full  + slot, different type → swap (atomic)
        public void HandleLeftClickSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TotalSlots) return;
            ref var slot = ref Slots[slotIndex];
            var cursor = Cursor;

            if (cursor.IsEmpty)
            {
                if (slot.IsEmpty) return;
                Cursor = slot;
                slot = ItemStack.Empty;
                return;
            }

            if (slot.IsEmpty)
            {
                slot = cursor;
                Cursor = ItemStack.Empty;
                return;
            }

            if (slot.SameKindAs(cursor))
            {
                int room = slot.MaxStackSize - slot.Count;
                if (room <= 0) return; // slot is already capped — no-op
                int take = Math.Min(room, cursor.Count);
                slot.Count += take;
                cursor.Count -= take;
                Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }

            // Different types — swap.
            Cursor = slot;
            slot = cursor;
        }

        // Shift+click transfer (Alpha quick-move). If the clicked slot is on
        // the hotbar, the stack is pushed into the main grid; if it's on the
        // main grid, it's pushed onto the hotbar. The destination range is
        // scanned in order:
        //   1. Same-type partial stacks first (merge until full).
        //   2. First empty slot.
        // For hotbar→main we walk the main grid top-left to bottom-right
        // (slots 0..35); for main→hotbar we walk the hotbar 1→9 (slots
        // 36..44). Anything that doesn't fit stays in the source slot —
        // canonical Alpha behaviour and matches the user's "move to first
        // unused slot" request.
        public void HandleShiftClickSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TotalSlots) return;
            ref var src = ref Slots[slotIndex];
            if (src.IsEmpty) return;

            int destStart, destEnd;
            if (slotIndex >= HotbarStart)
            {
                // Hotbar → main grid (top-left scan)
                destStart = 0;
                destEnd   = MainCount;
            }
            else
            {
                // Main grid → hotbar (1..9 scan)
                destStart = HotbarStart;
                destEnd   = HotbarStart + HotbarCount;
            }

            // Pass 1 — top up matching partial stacks in destination range.
            for (int i = destStart; i < destEnd; i++)
            {
                ref var d = ref Slots[i];
                if (d.IsEmpty || !d.SameKindAs(src)) continue;
                int room = d.MaxStackSize - d.Count;
                if (room <= 0) continue;
                int take = Math.Min(room, src.Count);
                d.Count += take;
                src.Count -= take;
                if (src.Count == 0) { src = ItemStack.Empty; return; }
            }

            // Pass 2 — first empty slot in destination range.
            for (int i = destStart; i < destEnd; i++)
            {
                ref var d = ref Slots[i];
                if (!d.IsEmpty) continue;
                d = src;
                src = ItemStack.Empty;
                return;
            }
            // Out of room in the destination range — leave the source slot
            // untouched (no half-moves: if the player wanted a partial they
            // can left-click).
        }
    }
}
