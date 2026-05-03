using System;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Tier 8 #42 — Redstone power propagation. Per-tick BFS that:
    //   1. Resets all wire / sink power state.
    //   2. Walks every loaded chunk, finds power SOURCES (lit
    //      redstone torch, lever ON, stone button pressed, pressure
    //      plate pressed), seeds a BFS at strength 15.
    //   3. Spreads strength along adjacent RedstoneWire cells with
    //      a 1-per-step decay; stops at 0.
    //   4. Drives SINKS:
    //        - Wood Door / Iron Door: open-state on bit 4 of meta;
    //          if any neighbour cell powered AND not already open,
    //          flip to open. If no neighbour powered AND open AND
    //          the door isn't currently power-locked, flip to closed.
    //        - Redstone Torch: if the cell BENEATH it is powered,
    //          torch becomes OFF (Alpha inversion rule). Else ON.
    //        - Note Block: rising-edge trigger (was-not-powered →
    //          is-now-powered) plays the click placeholder sfx.
    //
    // The simulation runs from World.TickRedstone(dt) at a fixed
    // 5 Hz cadence — roughly Alpha's 20 Hz redstone tick rate
    // halved (good enough for visible cause-and-effect; precise
    // sub-game-tick timing isn't needed for the V1 simulation).
    //
    // NOTE: Power level is tracked in a per-cell HashMap rather
    // than the chunk meta byte because wire cells use their meta
    // for routed-tile state (future polish) and we don't want to
    // claim those bits permanently.
    internal static class RedstonePowerSystem
    {
        // Cadence — every N seconds. 0.1 s = 10 Hz, comfortable for
        // the player to see torches blink without burning CPU on
        // worlds with no redstone.
        public const float TickInterval = 0.1f;

        // Max signal level — Alpha canonical 15 cells of decay.
        public const int MaxPower = 15;

        // Hold ticks for buttons — incremented each Tick and the
        // pressed bit is cleared when the meta high-nibble counter
        // reaches zero. Stone-button canonical hold = 10 redstone
        // ticks; we run Tick() at 10 Hz so 10 ticks = 1 s.
        private const int ButtonHoldTicks = 10;

        // Per-cell power map — populated each Tick, read by sinks.
        // Keyed by (wx, wy, wz). Values 0..MaxPower; absence = 0.
        private static readonly Dictionary<(int x, int y, int z), int> _power
            = new Dictionary<(int, int, int), int>(1024);

        // Note-block rising-edge tracking. Holds the previous-tick
        // power state per note-block cell so we can detect 0 → >0
        // transitions and trigger the note placeholder sound.
        private static readonly Dictionary<(int x, int y, int z), bool> _wasNoteBlockPowered
            = new Dictionary<(int, int, int), bool>(64);

        // Tier 8 #42 perf — Sparse cell registry. World.SetBlock adds
        // a cell here whenever a redstone-relevant block is placed
        // (and removes it on break / replace). Tick iterates ONLY
        // these cells instead of walking every chunk's 32K cells —
        // without the registry the per-tick cost was a 22M-cell
        // sweep across all loaded chunks, dropping the game tick to
        // 4 fps even on worlds with no redstone placed.
        private static readonly HashSet<(int x, int y, int z)> Cells
            = new HashSet<(int, int, int)>();

        // True for every block type the redstone tick needs to
        // process — sources, wires, and sinks. Doors / note blocks
        // are sinks: they're not "redstone blocks" per se, but the
        // simulation has to inspect them each tick to drive their
        // open / play state, so they ride along in the same set.
        public static bool IsRedstoneRelevant(BlockType t)
        {
            switch (t)
            {
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                case BlockType.RedstoneWire:
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                case BlockType.NoteBlock:
                // Tier 8 #49 V1 — Dispenser is a redstone sink: when
                // any adjacent cell becomes powered, the rising edge
                // triggers a single-item ejection toward the
                // dispenser's facing direction.
                case BlockType.Dispenser:
                    return true;
                default:
                    return false;
            }
        }

        // Hooks called from World.SetBlock when a cell's block type
        // changes. The caller's already done the (oldT != newT)
        // check, so these only need to update the set.
        public static void OnBlockChanged(int wx, int wy, int wz, BlockType oldT, BlockType newT)
        {
            bool wasRel = IsRedstoneRelevant(oldT);
            bool isRel  = IsRedstoneRelevant(newT);
            if (wasRel && !isRel) Cells.Remove((wx, wy, wz));
            else if (!wasRel && isRel) Cells.Add((wx, wy, wz));
        }

        // Wholesale clear — used by World.Reset / world-load paths
        // that swap the chunk set out from under us.
        public static void ClearAll()
        {
            Cells.Clear();
            _power.Clear();
            _wasNoteBlockPowered.Clear();
        }

        // One-shot scan to populate the registry for a freshly-
        // loaded chunk. Called from the chunk-install path so worlds
        // loaded from disk get their existing redstone integrated
        // without walking every chunk on every tick.
        public static void RegisterChunk(Chunk chunk)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;
            for (int lx = 0; lx < Chunk.SizeX; lx++)
            for (int ly = 0; ly < Chunk.SizeY; ly++)
            for (int lz = 0; lz < Chunk.SizeZ; lz++)
            {
                var t = (BlockType)chunk.RawBlocks[Chunk.Index(lx, ly, lz)];
                if (IsRedstoneRelevant(t))
                    Cells.Add((baseX + lx, ly, baseZ + lz));
            }
        }

        // Run one redstone simulation tick. Caller is World.
        // Early-outs cleanly when no redstone-relevant blocks have
        // ever been placed (Cells.Count == 0), so worlds without
        // any redstone pay a single integer compare per tick.
        public static void Tick(World world)
        {
            if (Cells.Count == 0) return;
            _power.Clear();

            // Snapshot the cell list once so SetBlock-driven mutations
            // during sink processing (e.g. torch invert calls
            // SetBlock which fires OnBlockChanged → Cells.Add/Remove)
            // don't invalidate the iterator.
            var snapshot = new List<(int x, int y, int z)>(Cells.Count);
            foreach (var pos in Cells) snapshot.Add(pos);

            // Phase 1 — buttons: decrement hold counters.
            DecrementButtonHolds(world, snapshot);

            // Phase 2 — pressure plates: update pressed flags from
            // player overlap.
            UpdatePressurePlates(world, snapshot);

            // Phase 3 — seed BFS from all active sources. Walks the
            // sparse Cells list rather than every chunk. Stale cells
            // (block was changed via a code path that bypassed
            // OnBlockChanged) are silently skipped.
            var queue = new Queue<(int x, int y, int z)>();
            foreach (var (wx, wy, wz) in snapshot)
            {
                if (!TryGetMeta(world, wx, wy, wz, out var t, out byte meta)) continue;
                bool source = false;
                switch (t)
                {
                    case BlockType.RedstoneTorchOn:
                        source = true;
                        break;
                    case BlockType.Lever:
                    case BlockType.StoneButton:
                    case BlockType.StonePressurePlate:
                    case BlockType.WoodPressurePlate:
                        source = (meta & 0x01) != 0;
                        break;
                }
                if (source)
                {
                    var pos = (wx, wy, wz);
                    _power[pos] = MaxPower;
                    queue.Enqueue(pos);
                }
            }

            // Phase 4 — flood through wires with -1 decay per step.
            // Wire traversal still goes via world.GetBlock so wires
            // not (yet) in Cells get processed correctly — the BFS
            // is the source of truth for spread topology.
            while (queue.Count > 0)
            {
                var (px, py, pz) = queue.Dequeue();
                int level = _power[(px, py, pz)];
                int next = level - 1;
                if (next <= 0) continue;
                TrySpread(world, px - 1, py,     pz,     next, queue);
                TrySpread(world, px + 1, py,     pz,     next, queue);
                TrySpread(world, px,     py - 1, pz,     next, queue);
                TrySpread(world, px,     py + 1, pz,     next, queue);
                TrySpread(world, px,     py,     pz - 1, next, queue);
                TrySpread(world, px,     py,     pz + 1, next, queue);
            }

            // Phase 5 — drive sinks from the snapshot. Door / torch /
            // note-block cells are inspected for power-state changes.
            DriveSinks(world, snapshot);
        }

        // Helper: read both block type and metadata at a world pos
        // via the chunk that owns the cell. Returns false if the
        // chunk isn't loaded (cell is stale) — caller skips it.
        private static bool TryGetMeta(World world, int wx, int wy, int wz, out BlockType t, out byte meta)
        {
            t = BlockType.Air;
            meta = 0;
            if ((uint)wy >= Chunk.SizeY) return false;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = world.GetChunk(cx, cz);
            if (c == null) return false;
            int idx = Chunk.Index(lx, wy, lz);
            t = (BlockType)c.RawBlocks[idx];
            meta = c.RawMeta[idx];
            return true;
        }

        private static void TrySpread(World world, int wx, int wy, int wz,
            int level, Queue<(int x, int y, int z)> queue)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            // Only wire cells propagate; sinks read power but don't
            // pass it along (else a door would carry signal on its
            // back face into another wire, which Alpha doesn't do).
            var t = world.GetBlock(wx, wy, wz);
            if (t != BlockType.RedstoneWire) return;
            var key = (wx, wy, wz);
            if (_power.TryGetValue(key, out int existing) && existing >= level) return;
            _power[key] = level;
            queue.Enqueue(key);
        }

        private static void DecrementButtonHolds(World world, List<(int x, int y, int z)> snapshot)
        {
            foreach (var (wx, wy, wz) in snapshot)
            {
                if (!TryGetChunkLocal(world, wx, wy, wz, out var c, out int lx, out int ly, out int lz)) continue;
                int idx = Chunk.Index(lx, ly, lz);
                if (c.RawBlocks[idx] != (byte)BlockType.StoneButton) continue;
                byte meta = c.RawMeta[idx];
                int hold = (meta >> 4) & 0x0F;
                if (hold == 0) continue;
                hold--;
                byte pressed = (byte)(hold > 0 ? 1 : 0);
                c.RawMeta[idx] = (byte)((hold << 4) | pressed);
                if (pressed == 0) c.IsModified = true;
            }
        }

        // Release-delay window in redstone ticks (10 Hz). 2 extra
        // ticks ≈ 200 ms — long enough for the player to walk through
        // a door driven by the plate before the door snaps shut on
        // them, short enough that a quick "tap" on the plate doesn't
        // leave it stuck pressed for noticeable time afterwards.
        // Mirrors the canonical Alpha pressure-plate hold cadence.
        private const int PressurePlateReleaseHold = 10;

        private static void UpdatePressurePlates(World world, List<(int x, int y, int z)> snapshot)
        {
            int pwx = _playerCellX;
            int pwy = _playerCellY;
            int pwz = _playerCellZ;
            foreach (var (wx, wy, wz) in snapshot)
            {
                if (!TryGetChunkLocal(world, wx, wy, wz, out var c, out int lx, out int ly, out int lz)) continue;
                int idx = Chunk.Index(lx, ly, lz);
                var t = (BlockType)c.RawBlocks[idx];
                if (t != BlockType.StonePressurePlate && t != BlockType.WoodPressurePlate) continue;
                bool playerOn = (wx == pwx) && (wz == pwz) && (wy == pwy);
                byte meta = c.RawMeta[idx];
                int hold = (meta >> 4) & 0x0F;
                if (playerOn)
                {
                    // Refresh the release timer every tick the player
                    // stays on the plate, so the countdown only begins
                    // when they leave.
                    hold = PressurePlateReleaseHold;
                }
                else if (hold > 0)
                {
                    // Player has stepped off — count down toward 0.
                    // Pressed stays asserted while hold > 0 so the
                    // signal lingers past the player leaving the cell.
                    hold--;
                }
                byte pressed = (byte)((playerOn || hold > 0) ? 1 : 0);
                byte newMeta = (byte)((hold << 4) | (meta & 0x0E) | pressed);
                if (newMeta != meta)
                {
                    c.RawMeta[idx] = newMeta;
                    c.IsModified = true;
                }
            }
        }

        private static bool TryGetChunkLocal(World world, int wx, int wy, int wz,
            out Chunk c, out int lx, out int ly, out int lz)
        {
            c = null; lx = 0; ly = 0; lz = 0;
            if ((uint)wy >= Chunk.SizeY) return false;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            lx = wx - cx * Chunk.SizeX;
            lz = wz - cz * Chunk.SizeZ;
            ly = wy;
            c = world.GetChunk(cx, cz);
            return c != null;
        }

        // Set by World.TickRedstone before invoking Tick(world).
        private static int _playerCellX, _playerCellY, _playerCellZ;
        public static void SetPlayerCell(int wx, int wy, int wz)
        {
            _playerCellX = wx;
            _playerCellY = wy;
            _playerCellZ = wz;
        }

        private static void DriveSinks(World world, List<(int x, int y, int z)> snapshot)
        {
            foreach (var (wx, wy, wz) in snapshot)
            {
                if (!TryGetChunkLocal(world, wx, wy, wz, out var chunk, out int lx, out int ly, out int lz)) continue;
                int idx = Chunk.Index(lx, ly, lz);
                var t = (BlockType)chunk.RawBlocks[idx];
                switch (t)
                {
                    case BlockType.WoodDoorBlockBottom:
                    case BlockType.WoodDoorBlockTop:
                    case BlockType.IronDoorBlockBottom:
                    case BlockType.IronDoorBlockTop:
                        DriveDoorSink(world, chunk, lx, ly, lz, wx, wy, wz);
                        break;
                    case BlockType.RedstoneTorchOn:
                    case BlockType.RedstoneTorchOff:
                        DriveTorchSink(world, t, wx, wy, wz);
                        break;
                    case BlockType.NoteBlock:
                        DriveNoteBlockSink(world, wx, wy, wz);
                        break;
                    case BlockType.Dispenser:
                        DriveDispenserSink(world, wx, wy, wz);
                        break;
                }
            }
        }

        // Tier 8 #49 V1 — Dispenser activation. Rising-edge trigger:
        // we track the last-seen powered state per-cell in
        // _dispenserLastPowered, and only eject on the false → true
        // transition. Without this, holding a lever ON would dispense
        // an item every redstone tick (10 Hz) and drain the inventory
        // in seconds. Canonical Alpha behaviour is one eject per
        // signal pulse, matching what we get from rising-edge gating.
        private static readonly System.Collections.Generic.Dictionary<(int x, int y, int z), bool> _dispenserLastPowered
            = new System.Collections.Generic.Dictionary<(int x, int y, int z), bool>();

        private static void DriveDispenserSink(World world, int wx, int wy, int wz)
        {
            bool nowPowered = AnyAdjacentPowered(wx, wy, wz);
            var key = (wx, wy, wz);
            _dispenserLastPowered.TryGetValue(key, out bool wasPowered);
            _dispenserLastPowered[key] = nowPowered;
            if (!nowPowered || wasPowered) return; // not a rising edge

            var de = world.TryGetDispenserEntity(wx, wy, wz);
            if (de == null) return;
            var taken = de.TakeRandomOne(_dispenserRng);
            if (taken.IsEmpty) return;

            // Spawn the item one cell in front of the dispenser
            // (toward its facing direction) so it pops out the muzzle
            // rather than spawning inside the dispenser block. The
            // velocity also points along the muzzle so it visibly
            // shoots forward.
            float fx = 0f, fz = 0f;
            switch (de.Facing)
            {
                case BlockFacing.East:  fx = +1f; break;
                case BlockFacing.West:  fx = -1f; break;
                case BlockFacing.South: fz = +1f; break;
                default: /*North*/      fz = -1f; break;
            }
            world.SpawnDispenserDrop(wx + 0.5f + fx * 0.6f, wy + 0.5f, wz + 0.5f + fz * 0.6f,
                fx * 4f, 1f, fz * 4f, taken);
        }

        private static readonly System.Random _dispenserRng = new System.Random(0xD15);

        private static bool AnyAdjacentPowered(int wx, int wy, int wz)
        {
            return IsPowered(wx - 1, wy,     wz)
                || IsPowered(wx + 1, wy,     wz)
                || IsPowered(wx,     wy - 1, wz)
                || IsPowered(wx,     wy + 1, wz)
                || IsPowered(wx,     wy,     wz - 1)
                || IsPowered(wx,     wy,     wz + 1);
        }
        private static bool IsPowered(int wx, int wy, int wz)
            => _power.TryGetValue((wx, wy, wz), out int p) && p > 0;

        private static void DriveDoorSink(World world, Chunk chunk, int lx, int ly, int lz,
            int wx, int wy, int wz)
        {
            byte meta = chunk.RawMeta[Chunk.Index(lx, ly, lz)];
            bool currentlyOpen = BlockData.DoorIsOpen(meta);
            bool shouldOpen = AnyAdjacentPowered(wx, wy, wz);
            // Also propagate the powered check to the OTHER half so
            // a powered cell anywhere around either half toggles the
            // whole door.
            if (!shouldOpen)
            {
                int otherY = (BlockData.IsDoorBottom((BlockType)chunk.RawBlocks[Chunk.Index(lx, ly, lz)])) ? wy + 1 : wy - 1;
                shouldOpen = AnyAdjacentPowered(wx, otherY, wz);
            }
            if (shouldOpen != currentlyOpen)
            {
                // Re-pack meta with open bit flipped on both halves.
                BlockFacing f = BlockData.DoorFacing(meta);
                bool hingeRight = BlockData.DoorHingeRight(meta);
                byte newMeta = BlockData.DoorPackMeta(f, shouldOpen, hingeRight);
                chunk.RawMeta[Chunk.Index(lx, ly, lz)] = newMeta;
                // Stamp the partner half too so both doors animate
                // as one unit.
                bool isBottom = BlockData.IsDoorBottom(
                    (BlockType)chunk.RawBlocks[Chunk.Index(lx, ly, lz)]);
                int otherY = isBottom ? wy + 1 : wy - 1;
                if ((uint)otherY < Chunk.SizeY)
                {
                    chunk.RawMeta[Chunk.Index(lx, otherY, lz)] = newMeta;
                }
                chunk.IsModified = true;
                // Mark dirty so the mesher re-emits the door slab in
                // its new wall position next frame.
                world.DirtyChunks.Add((chunk.ChunkX, chunk.ChunkZ));
            }
        }

        private static void DriveTorchSink(World world, BlockType current, int wx, int wy, int wz)
        {
            // Inversion: torch is OFF when the block immediately
            // below it (the cell it would conceptually be mounted
            // on) is powered. Else ON. Without true mount-direction
            // tracking we use the cell below as the "input".
            bool input = IsPowered(wx, wy - 1, wz);
            BlockType target = input ? BlockType.RedstoneTorchOff : BlockType.RedstoneTorchOn;
            if (current != target)
            {
                world.SetBlock(wx, wy, wz, target);
            }
        }

        private static void DriveNoteBlockSink(World world, int wx, int wy, int wz)
        {
            bool poweredNow = AnyAdjacentPowered(wx, wy, wz);
            var key = (wx, wy, wz);
            _wasNoteBlockPowered.TryGetValue(key, out bool wasPowered);
            if (poweredNow && !wasPowered)
            {
                // Rising edge — play a note. Placeholder click for
                // now; pitched audio synthesis is a follow-up.
                SfxBank.PlayClick();
            }
            _wasNoteBlockPowered[key] = poweredNow;
        }
    }
}
