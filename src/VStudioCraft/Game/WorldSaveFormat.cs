using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using OpenTK;

namespace VStudioCraft.Game
{
    internal static class WorldSaveFormat
    {
        private const uint Magic = 0x31435356;  // 'VSC1' little-endian
        // v1 = initial, v2 = per-chunk IsModified byte, v3 = GameMode + Player.Health,
        // v4 = HungerEnabled survival sub-setting,
        // v5 = furnace tile entities (count + per-entity (x,y,z, input, fuel,
        //      output, burnTime, maxBurnTime, cookProgress)),
        // v6 = furnace facing byte appended to each entity (0=N, 1=S, 2=E, 3=W),
        // v7 = chest tile entities (count + per-entity (x,y,z, facing,
        //      27 ItemStacks)). Appended after the furnace block so a v6
        //      reader stops cleanly at the EOF.
        // v8 = Tier 4 #14 wheat metadata. Sparse per-chunk record block
        //      appended at the end (count + (index:int, meta:byte) pairs
        //      per chunk, in the same chunk order as the v2+ block writes
        //      above). Pre-v8 saves load with all wheat at meta=0 (stage
        //      0 sprout) — the random tick will re-grow them, costing a
        //      few real-world minutes per field but no data loss. The
        //      record sits past the chest block so a v7 reader stops
        //      cleanly at EOF without seeing the new section.
        // v9 = Tier 4 #22 world-spawn vector (3 floats appended at the
        //      tail of the file, AFTER the v8 wheat-metadata block so a
        //      v8 reader stops cleanly at the wheat record's EOF and
        //      doesn't see the new section). The spawn vector is the
        //      remembered respawn point — Alpha sets it to the player's
        //      first-tick position and never moves it (no bed yet); the
        //      compass arrow points at this. Pre-v9 saves load the spawn
        //      from the saved player position (the only sensible default
        //      — without a recorded spawn the closest stand-in is "where
        //      the player was when they saved", which matches what the
        //      renderer was already doing on load before this version).
        // v10 = Tier 4 #24 painting entities. Trailing block appended
        //       after the v9 spawn vector: int paintingCount, then per
        //       painting (int X, int Y, int Z, byte Facing, byte Width,
        //       byte Height, byte Variant). Pre-v10 saves had no
        //       paintings (the entity didn't exist), so legacy worlds
        //       load with Paintings = []. Stops cleanly at EOF for v9
        //       readers since the new block sits past the spawn-vector
        //       tail.
        // v11 = Tier 4 #25 jukebox tile entities. Trailing block
        //       appended after the v10 painting block: int jukeboxCount,
        //       then per jukebox (int X, int Y, int Z, byte Disc).
        //       Disc is the BlockType id of the inserted music disc
        //       (Disc13 / DiscCat) or BlockType.Air for a freshly-
        //       allocated entity that doesn't have a disc loaded
        //       (shouldn't happen in practice — empty entities are
        //       removed via RemoveJukeboxEntity, so the saved set is
        //       always loaded jukeboxes — but we serialise the byte
        //       defensively in case a future code path leaves an
        //       empty entity in the dict). Pre-v11 saves had no
        //       jukeboxes (the block didn't exist), so legacy worlds
        //       load with an empty entity table — same legacy-empty
        //       pattern as every preceding tile-entity block. Stops
        //       cleanly at EOF for v10 readers.
        // v12 = Tier 6 #47 — Time-of-day clock. Single float appended
        //       at the very end of the file after the v11 jukebox
        //       block so a v11 reader stops cleanly at EOF without
        //       seeing the new section. Range [0, 1) where 0.25 = noon
        //       (matches GameRenderer's _timeOfDay convention). Pre-v12
        //       saves load with the renderer's default (noon), so a
        //       round-trip on a legacy world simply resets the clock,
        //       same behaviour those saves had before this version.
        // v13 = Phase 8 multiplayer player table. Trailing block appended
        //       after the v12 time-of-day float so a v12 reader stops at
        //       EOF without seeing the new section. Layout:
        //
        //         int  playerCount
        //         per player:
        //           string username (length-prefixed UTF-8 — BinaryWriter's
        //                            7-bit-encoded length is fine here
        //                            since this matches BinaryReader.ReadString)
        //           double X, Y, Z
        //           float  Yaw, Pitch
        //           int    Health
        //           int    selectedHotbarSlot (0..8)
        //           49 ItemStacks (Inventory.TotalSlots) using WriteStack
        //
        //       Pre-v13 saves load with an empty player table — the
        //       single-player Header is still authoritative for the
        //       primary user (their CameraPos / Health / inventory load
        //       through the existing v9 path). MP-aware code (ServerHub
        //       login) consults the v13 table by username; missing
        //       entries spawn fresh.
        // v14 = Tier 8 #44 sign tile entities. Trailing block appended
        //       after the v13 player table so a v13 reader stops at
        //       EOF without seeing the new section. Layout:
        //
        //         int  signCount
        //         per sign:
        //           int    X, Y, Z
        //           string Line0, Line1, Line2, Line3 (length-prefixed
        //                                              UTF-8 — same shape
        //                                              the v13 username
        //                                              uses)
        //
        //       Pre-v14 saves had no signs in the BlockType enum, so
        //       legacy worlds load with an empty sign dict — same
        //       legacy-empty pattern furnaces (v5), chests (v7) and
        //       jukeboxes (v11) used. Facing isn't stored separately:
        //       it lives in the chunk's metadata byte alongside the
        //       block id, so it persists through the existing v2+
        //       chunk-byte block.
        private const byte CurrentVersion = 17;

        // Phase 8 — one entry per known player in the v13 multiplayer
        // player table. Captured at save time from `ServerHub` (or the
        // host's renderer for the loopback host); on load the server's
        // login handler looks up the username to restore inventory +
        // position. Pre-v13 worlds have an empty table — fresh logins
        // get default spawn / empty inventory in that case.
        public struct PersistedPlayer
        {
            public string Username;
            public double X, Y, Z;
            public float Yaw, Pitch;
            public int Health;
            public int HeldSlot;        // 0..8 hotbar selection
            public ItemStack[] Inventory; // length == Inventory.TotalSlots (49)
        }

        public struct Header
        {
            public int Seed;
            public Vector3 CameraPos;
            public float CameraYaw;
            public float CameraPitch;
            public GameMode GameMode;  // v3+
            public int Health;         // v3+ (1..MaxHealth; 0 means "load default")
            public bool HungerEnabled; // v4+ — survival sub-setting; default off
            // v9+ — world spawn (Tier 4 #22 / Compass). Remembered
            // respawn point and the target the compass arrow points
            // at. Pre-v9 saves return the player's saved feet position
            // here on load so the compass and respawn behave sanely on
            // legacy worlds (no separate spawn was tracked, so the
            // best stand-in is "wherever the player saved").
            public Vector3 SpawnPos;
            // v12+ — Time-of-day phase in [0, 1). 0 = midnight, 0.25 = noon.
            // Pre-v12 saves load with TimeOfDay = 0.25 (noon) so legacy
            // worlds keep the previous "always start at noon" behaviour.
            public float TimeOfDay;
        }

        // Singleplayer overload — keeps every existing call site working
        // without forcing them to think about the multiplayer player
        // table. Calls through to the MP-aware overload with a null
        // player list, which writes a 0-count v13 block.
        public static void Save(string path, Header header, World world)
            => Save(path, header, world, null, null);

        public static void Save(string path, Header header, World world, IList<PersistedPlayer> players)
            => Save(path, header, world, players, null, default);

        public static void Save(string path, Header header, World world, IList<PersistedPlayer> players, World netherWorld)
            => Save(path, header, world, players, netherWorld, default);

        // Tier 8 #51 V6 — Per-dimension player state. Round-trips the
        // overworld AND nether positions + camera angles so a save-
        // and-quit in either dimension reloads at the exact last-
        // known coords. Default-zeroed members mean "no record" —
        // the loader skips restoring whichever slot is unset.
        public struct DimensionState
        {
            public Vector3 OverworldPos;
            public float OverworldYaw, OverworldPitch;
            public Vector3 NetherPos;
            public float NetherYaw, NetherPitch;
            public Dimension CurrentDimension;
        }

        // Tier 8 #51 V5 — Save with an optional Nether world. The
        // overworld is always the primary file content (header + chunk
        // section + tile entities); the Nether goes in a v16 trailing
        // block so pre-v16 readers stop cleanly at the v15 dispenser
        // section. Pass null to skip Nether persistence — the player
        // never visited the Nether OR the host wants ephemeral Nether
        // (debug / test fixtures).
        //
        // V6 — additional v17 trailing block carrying per-dimension
        // player state (both positions + camera + currentDim) so the
        // player reloads in whichever dimension they saved in, at
        // their exact coords. Pre-v17 saves load with the existing
        // header.CameraPos as the overworld position and an empty
        // nether-position slot.
        public static void Save(string path, Header header, World world, IList<PersistedPlayer> players, World netherWorld, DimensionState dimState)
        {
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(Magic);
                w.Write(CurrentVersion);
                w.Write(header.Seed);
                w.Write(header.CameraPos.X);
                w.Write(header.CameraPos.Y);
                w.Write(header.CameraPos.Z);
                w.Write(header.CameraYaw);
                w.Write(header.CameraPitch);
                // v3: game mode + HP. Appended after the existing header fields so
                // a pre-v3 reader would never reach them.
                w.Write((byte)header.GameMode);
                w.Write(header.Health);
                // v4: hunger sub-setting flag. Appended again, same logic.
                w.Write((byte)(header.HungerEnabled ? 1 : 0));
                w.Write(world.PersistentChunkCount);
                foreach (var chunk in world.AllChunksForPersistence())
                {
                    w.Write(chunk.ChunkX);
                    w.Write(chunk.ChunkZ);
                    w.Write((byte)(chunk.IsModified ? 1 : 0));
                    chunk.WriteTo(w);
                }

                // v5: furnace tile entities. Counted at the world level
                // (not per-chunk) because the World owns the dictionary.
                // For each entry we write the absolute world coord and
                // the entity's three slots + two timers. ItemStacks are
                // (BlockType:int, Count:int, Durability:short) — same
                // shape used elsewhere; written as ushort/int/short to
                // keep the on-disk size compact.
                int feCount = 0;
                foreach (var _ in world.FurnaceEntities) feCount++;
                w.Write(feCount);
                foreach (var kv in world.FurnaceEntities)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Key.z);
                    WriteStack(w, kv.Value.Input);
                    WriteStack(w, kv.Value.Fuel);
                    WriteStack(w, kv.Value.Output);
                    w.Write(kv.Value.BurnTimeTicks);
                    w.Write(kv.Value.MaxBurnTimeTicks);
                    w.Write(kv.Value.CookProgressTicks);
                    // v6: orientation byte. Default for legacy v5 saves
                    // is North on load (see Load below).
                    w.Write((byte)kv.Value.Facing);
                }

                // v7: chest tile entities. Same structure as the furnace
                // block (count + per-entity record), but each record
                // carries a facing byte + 27 ItemStacks instead of the
                // furnace's three slots and timers. Empty chests are
                // still emitted — Save iterates everything in the dict
                // so the on-disk count matches the live count.
                int ceCount = 0;
                foreach (var _ in world.ChestEntities) ceCount++;
                w.Write(ceCount);
                foreach (var kv in world.ChestEntities)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Key.z);
                    w.Write((byte)kv.Value.Facing);
                    for (int i = 0; i < ChestTileEntity.SlotCount; i++)
                        WriteStack(w, kv.Value.Slots[i]);
                }

                // v8: Tier 4 #14 wheat metadata. Per-chunk sparse record
                // (count + (idx:int, meta:byte) pairs) keyed by (cx, cz)
                // so the loader can match the record to its chunk
                // regardless of the dictionary iteration order at
                // load time. Worlds with no wheat at all see this whole
                // block compress to essentially nothing — empty records
                // cost just 4 bytes per chunk.
                int chunkMetaCount = world.PersistentChunkCount;
                w.Write(chunkMetaCount);
                foreach (var chunk in world.AllChunksForPersistence())
                {
                    w.Write(chunk.ChunkX);
                    w.Write(chunk.ChunkZ);
                    chunk.WriteSparseMeta(w);
                }

                // v9: Tier 4 #22 — world-spawn vector. Three floats at
                // the tail of the stream so a v8 reader hits EOF after
                // the wheat record and never sees this. The compass
                // overlay reads SpawnPos to compute the bearing arrow;
                // respawn-on-death already used a renderer-private
                // _spawnPos with no persistence, so this also fixes
                // the long-standing bug where reloading a save would
                // reset the respawn point to the saved feet position
                // even if the player had wandered far from spawn.
                w.Write(header.SpawnPos.X);
                w.Write(header.SpawnPos.Y);
                w.Write(header.SpawnPos.Z);

                // v10: Tier 4 #24 — Painting entities. Append-only past
                // the v9 spawn vector so a v9 reader stops cleanly at
                // EOF. Empty Paintings list serialises as just an int 0
                // — costs 4 bytes per save, no paintings ever placed.
                var paintings = world.Paintings;
                w.Write(paintings.Count);
                for (int i = 0; i < paintings.Count; i++)
                {
                    var p = paintings[i];
                    w.Write(p.X);
                    w.Write(p.Y);
                    w.Write(p.Z);
                    w.Write((byte)p.Facing);
                    w.Write((byte)p.Width);
                    w.Write((byte)p.Height);
                    w.Write((byte)p.Variant);
                }

                // v11: Tier 4 #25 — Jukebox tile entities. Append-only
                // past the v10 painting block so a v10 reader stops
                // cleanly at EOF after consuming the paintings. Empty
                // jukebox table serialises as a bare int 0 (4 bytes).
                // We count by iterating because JukeboxEntities is an
                // IEnumerable surface for encapsulation, matching the
                // furnace/chest counting pattern above.
                int jeCount = 0;
                foreach (var _ in world.JukeboxEntities) jeCount++;
                w.Write(jeCount);
                foreach (var kv in world.JukeboxEntities)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Key.z);
                    w.Write((byte)kv.Value.Disc);
                }

                // v12: Tier 6 #47 — Time-of-day clock. Single float at
                // the very end of the file. Persists the day/night
                // phase so a saved-at-dusk world reloads at dusk
                // instead of snapping back to the renderer's default
                // (0.25 = noon). Pre-v12 readers stop after the
                // jukebox block above and never see this byte.
                w.Write(header.TimeOfDay);

                // v13: Phase 8 — multiplayer player table. Always
                // written in v13+ — single-player saves get a 0-count
                // block, which costs 4 bytes. Per-player payload is
                // ~370 bytes (49 stacks × 7 bytes + ~50 bytes for
                // pose/username), so 8 friends = ~3 KiB on disk —
                // negligible compared to the chunk byte cost.
                int playerCount = players?.Count ?? 0;
                w.Write(playerCount);
                if (players != null)
                {
                    for (int i = 0; i < players.Count; i++)
                    {
                        var p = players[i];
                        // BinaryWriter.Write(string) emits a 7-bit-encoded
                        // length prefix that BinaryReader.ReadString reads
                        // symmetrically — same as every other string in
                        // this format. Empty username is rejected at the
                        // call site (Server's login handler), but we
                        // write defensively in case a hand-edited save
                        // ends up here.
                        w.Write(p.Username ?? string.Empty);
                        w.Write(p.X);
                        w.Write(p.Y);
                        w.Write(p.Z);
                        w.Write(p.Yaw);
                        w.Write(p.Pitch);
                        w.Write(p.Health);
                        w.Write(p.HeldSlot);
                        // Inventory length is fixed at TotalSlots; we
                        // rewrite that constant here so a future bump
                        // (e.g. extra armor or off-hand) automatically
                        // propagates without an on-disk format change
                        // per-row. If TotalSlots ever grows, bump
                        // CurrentVersion and add a per-row
                        // backwards-compat decode in Load.
                        var inv = p.Inventory ?? Array.Empty<ItemStack>();
                        for (int s = 0; s < Inventory.TotalSlots; s++)
                        {
                            WriteStack(w, s < inv.Length ? inv[s] : ItemStack.Empty);
                        }
                    }
                }

                // v14: Tier 8 #44 — sign tile entities. Trailing block
                // appended past the v13 player table so a v13 reader
                // stops cleanly at EOF without seeing the new section.
                // Each sign carries its 4 lines of typed text; facing
                // is in the chunk meta (already persisted via the v2+
                // chunk byte block) so we don't write it twice. Empty
                // sign dict (no signs in the world) serialises as just
                // an int 0 — costs 4 bytes per save.
                int signCount = 0;
                foreach (var _ in world.SignEntities) signCount++;
                w.Write(signCount);
                foreach (var kv in world.SignEntities)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Key.z);
                    var lines = kv.Value.Lines ?? new string[4];
                    for (int i = 0; i < 4; i++)
                    {
                        // Defensive — a corrupt or hand-edited entity
                        // could have a null line; emit empty so
                        // ReadString sees a 0-length prefix.
                        w.Write(i < lines.Length && lines[i] != null ? lines[i] : string.Empty);
                    }
                }

                // v15: Tier 8 #49 — Dispenser tile entities. Trailing
                // block past v14 signs; same shape as the v7 chest
                // section but with 9 slots instead of 27. Pre-v15
                // saves had no dispensers in the BlockType enum so
                // legacy worlds load with an empty dispenser dict —
                // matches the legacy-empty pattern every appended
                // tile-entity section uses.
                int deCount = 0;
                foreach (var _ in world.DispenserEntities) deCount++;
                w.Write(deCount);
                foreach (var kv in world.DispenserEntities)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Key.z);
                    w.Write((byte)kv.Value.Facing);
                    for (int i = 0; i < DispenserTileEntity.SlotCount; i++)
                        WriteStack(w, kv.Value.Slots[i]);
                }

                // v16: Tier 8 #51 V5 — Nether dimension persistence.
                // Trailing block past the v15 dispenser section. A
                // single byte flag indicates whether the Nether
                // exists for this world (the player visited it at
                // least once). When present, we write the same
                // chunk-byte format the overworld uses + the
                // overworld tile-entity tail blocks (chest /
                // furnace / sign / etc.) for the Nether's parallel
                // dictionaries. Pre-v16 saves stop cleanly at the
                // v15 dispenser block — they never see this byte.
                bool hasNether = netherWorld != null && netherWorld.Dimension == Dimension.Nether;
                w.Write((byte)(hasNether ? 1 : 0));
                if (hasNether)
                {
                    w.Write(netherWorld.PersistentChunkCount);
                    foreach (var chunk in netherWorld.AllChunksForPersistence())
                    {
                        w.Write(chunk.ChunkX);
                        w.Write(chunk.ChunkZ);
                        w.Write((byte)(chunk.IsModified ? 1 : 0));
                        chunk.WriteTo(w);
                    }
                    // Nether-side tile entities. Currently any of the
                    // 5 entity types could exist in the Nether (the
                    // player can place chests + dispensers etc. on
                    // their netherrack platform), so we write all
                    // five dictionaries even if most are empty —
                    // matches the overworld's exhaustive tail-block
                    // approach and keeps the load symmetric.
                    int neFurnaceCount = 0;
                    foreach (var _ in netherWorld.FurnaceEntities) neFurnaceCount++;
                    w.Write(neFurnaceCount);
                    foreach (var kv in netherWorld.FurnaceEntities)
                    {
                        w.Write(kv.Key.x);
                        w.Write(kv.Key.y);
                        w.Write(kv.Key.z);
                        WriteStack(w, kv.Value.Input);
                        WriteStack(w, kv.Value.Fuel);
                        WriteStack(w, kv.Value.Output);
                        w.Write(kv.Value.BurnTimeTicks);
                        w.Write(kv.Value.MaxBurnTimeTicks);
                        w.Write(kv.Value.CookProgressTicks);
                    }
                    int neChestCount = 0;
                    foreach (var _ in netherWorld.ChestEntities) neChestCount++;
                    w.Write(neChestCount);
                    foreach (var kv in netherWorld.ChestEntities)
                    {
                        w.Write(kv.Key.x);
                        w.Write(kv.Key.y);
                        w.Write(kv.Key.z);
                        w.Write((byte)kv.Value.Facing);
                        for (int i = 0; i < ChestTileEntity.SlotCount; i++)
                            WriteStack(w, kv.Value.Slots[i]);
                    }
                    int neSignCount = 0;
                    foreach (var _ in netherWorld.SignEntities) neSignCount++;
                    w.Write(neSignCount);
                    foreach (var kv in netherWorld.SignEntities)
                    {
                        w.Write(kv.Key.x);
                        w.Write(kv.Key.y);
                        w.Write(kv.Key.z);
                        var lines = kv.Value.Lines ?? new string[4];
                        for (int i = 0; i < 4; i++)
                        {
                            w.Write(i < lines.Length && lines[i] != null ? lines[i] : string.Empty);
                        }
                    }
                    int neDispenserCount = 0;
                    foreach (var _ in netherWorld.DispenserEntities) neDispenserCount++;
                    w.Write(neDispenserCount);
                    foreach (var kv in netherWorld.DispenserEntities)
                    {
                        w.Write(kv.Key.x);
                        w.Write(kv.Key.y);
                        w.Write(kv.Key.z);
                        w.Write((byte)kv.Value.Facing);
                        for (int i = 0; i < DispenserTileEntity.SlotCount; i++)
                            WriteStack(w, kv.Value.Slots[i]);
                    }
                }

                // v17: Tier 8 #51 V6 — Per-dimension player state.
                // Trailing block past v16 nether section. Layout:
                //   3 floats overworld pos
                //   1 float  overworld yaw
                //   1 float  overworld pitch
                //   3 floats nether pos
                //   1 float  nether yaw
                //   1 float  nether pitch
                //   1 byte   currentDimension (0=Overworld, 1=Nether)
                // Pre-v17 readers stop after the v16 nether section
                // and restore overworld pos from header.CameraPos
                // with an empty nether-position slot, falling back
                // to the existing first-time-entry flow.
                w.Write(dimState.OverworldPos.X);
                w.Write(dimState.OverworldPos.Y);
                w.Write(dimState.OverworldPos.Z);
                w.Write(dimState.OverworldYaw);
                w.Write(dimState.OverworldPitch);
                w.Write(dimState.NetherPos.X);
                w.Write(dimState.NetherPos.Y);
                w.Write(dimState.NetherPos.Z);
                w.Write(dimState.NetherYaw);
                w.Write(dimState.NetherPitch);
                w.Write((byte)dimState.CurrentDimension);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Tier 6 #47 — Lightweight save-file summary used by the main
        // menu's World Select screen. Built from the header alone (no
        // chunk decode) so listing 100 saves is cheap. DisplayName
        // falls back to the file name for headers we couldn't read
        // cleanly; HeaderValid carries that distinction so the
        // renderer can grey-out broken entries.
        public struct SaveSummary
        {
            public string Path;
            public string DisplayName;
            public int Seed;
            public DateTime LastWriteUtc;
            public bool HeaderValid;
        }

        // Tier 6 #47 — Scan %APPDATA%\VStudioCraft\saves\*.voxworld and
        // return one summary per file, ordered most-recently-modified
        // first so the World Select screen reads as "your last world is
        // on top". Per-file errors are swallowed with a sentinel
        // (HeaderValid=false, Seed=0) so a single corrupt save doesn't
        // blank the whole list. The directory location matches the
        // existing NewWorldCommand / OpenWorldCommand convention.
        public static SaveSummary[] EnumerateSaves()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VStudioCraft", "saves");
            if (!Directory.Exists(dir)) return Array.Empty<SaveSummary>();

            var files = Directory.GetFiles(dir, "*.voxworld");
            var list = new List<SaveSummary>(files.Length);
            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(path); } catch { mtime = DateTime.MinValue; }
                try
                {
                    var hdr = ReadHeaderOnly(path);
                    list.Add(new SaveSummary
                    {
                        Path = path,
                        DisplayName = name,
                        Seed = hdr.Seed,
                        LastWriteUtc = mtime,
                        HeaderValid = true,
                    });
                }
                catch
                {
                    list.Add(new SaveSummary
                    {
                        Path = path,
                        DisplayName = name,
                        Seed = 0,
                        LastWriteUtc = mtime,
                        HeaderValid = false,
                    });
                }
            }
            list.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
            return list.ToArray();
        }

        // Tier 6 #47 — Read just the header fields from a save file
        // and stop. Used by EnumerateSaves to avoid a full chunk
        // decode for every file in the saves directory. Reads the
        // same magic + version + header layout as Load() but skips
        // everything past the v4 hunger byte (the chunks block); the
        // GZipStream is closed before any chunk data is touched, so
        // the cost is one decompression of ~tens of bytes. Throws on
        // any I/O / decode error so the caller can flag the file as
        // corrupt in the listing.
        public static Header ReadHeaderOnly(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                uint magic = r.ReadUInt32();
                if (magic != Magic)
                    throw new InvalidDataException("Not a VStudioCraft save file");
                byte version = r.ReadByte();
                if (version > CurrentVersion)
                    throw new InvalidDataException($"Save version {version} is newer than supported ({CurrentVersion})");

                var header = new Header
                {
                    Seed = r.ReadInt32(),
                    CameraPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    CameraYaw = r.ReadSingle(),
                    CameraPitch = r.ReadSingle(),
                    GameMode = GameMode.Creative,
                    Health = Player.MaxHealth,
                    HungerEnabled = false,
                };
                if (version == 1)
                {
                    header.CameraPos = new Vector3(
                        header.CameraPos.X,
                        header.CameraPos.Y - Player.EyeHeight,
                        header.CameraPos.Z);
                }
                if (version >= 3)
                {
                    header.GameMode = (GameMode)r.ReadByte();
                    int hp = r.ReadInt32();
                    if (hp <= 0 || hp > Player.MaxHealth) hp = Player.MaxHealth;
                    header.Health = hp;
                }
                if (version >= 4)
                {
                    header.HungerEnabled = r.ReadByte() != 0;
                }
                // Stop here — chunks come next and we don't need them.
                // SpawnPos (v9+) lives at the file tail, also skipped.
                return header;
            }
        }

        // Singleplayer overload — discards the v13 multiplayer player
        // table. Existing call sites (`var (header, world) =
        // WorldSaveFormat.Load(path)`) keep working without churn.
        public static (Header header, World world) Load(string path)
        {
            var (h, w, _) = LoadWithPlayers(path);
            return (h, w);
        }

        // Tier 9 #53 V1 — Backup-on-load-failure. When a parse
        // exception escapes LoadWithPlayersInner the catch block here
        // copies the corrupt save aside (renaming to
        // `<path>.corrupt-<timestamp>.bak` so the user can recover or
        // hand-inspect later) and re-throws. This way a save with one
        // bad chunk byte doesn't get silently overwritten by a
        // subsequent partial save — the original file is preserved
        // verbatim under a new name.
        //
        // The host's load path catches the rethrown exception and
        // surfaces it via the title-screen error line, same as it
        // already does for missing files / version mismatches.
        public static (Header header, World world, Dictionary<string, PersistedPlayer> players) LoadWithPlayers(string path)
        {
            var r = LoadWithPlayersAndNether(path);
            return (r.header, r.world, r.players);
        }

        // Tier 8 #51 V5 — Extended load that ALSO returns the Nether
        // world if one was persisted (v16+ saves only). Pre-v16 saves
        // and v16 saves where the player never visited the Nether
        // both return null for the nether tuple slot — caller is
        // expected to lazy-create on demand the same way it always
        // has.
        //
        // V6 — Also returns the per-dimension player state from the
        // v17 trailing block. Pre-v17 saves return a default (zeroed)
        // DimensionState so the renderer falls back to header.CameraPos
        // for the overworld slot and starts the nether at the
        // first-time-entry path.
        public static (Header header, World world, Dictionary<string, PersistedPlayer> players, World netherWorld, DimensionState dimState) LoadWithPlayersAndNether(string path)
        {
            try
            {
                return LoadWithPlayersInner(path);
            }
            catch (System.Exception ex) when (!(ex is FileNotFoundException))
            {
                TryQuarantineCorruptSave(path, ex);
                throw;
            }
        }

        // Best-effort copy of the corrupt save to a sibling .bak
        // file. Failure is silent — if the file lock or disk-full
        // condition stops us, the original Load exception is still
        // bubbled up unchanged. The timestamp suffix handles the
        // case of REPEATED corrupt loads on the same path so each
        // attempt produces its own quarantined snapshot.
        private static void TryQuarantineCorruptSave(string path, System.Exception originalException)
        {
            try
            {
                if (!File.Exists(path)) return;
                string stamp = System.DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                string bak = path + ".corrupt-" + stamp + ".bak";
                // Avoid overwriting an existing backup with the same
                // second-resolution stamp (rare but possible — same
                // file double-loaded in <1 s after a transient
                // failure).
                int dedupe = 0;
                while (File.Exists(bak))
                {
                    bak = path + ".corrupt-" + stamp + "-" + (++dedupe) + ".bak";
                    if (dedupe > 100) return; // give up — don't loop forever
                }
                File.Copy(path, bak);
            }
            catch
            {
                // Swallow — original Load exception will surface to
                // the caller anyway. We don't want a backup-failure
                // to mask the real load error.
                _ = originalException;
            }
        }

        private static (Header header, World world, Dictionary<string, PersistedPlayer> players, World netherWorld, DimensionState dimState) LoadWithPlayersInner(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                uint magic = r.ReadUInt32();
                if (magic != Magic)
                    throw new InvalidDataException("Not a VStudioCraft save file");
                byte version = r.ReadByte();
                if (version > CurrentVersion)
                    throw new InvalidDataException($"Save version {version} is newer than supported ({CurrentVersion})");

                var header = new Header
                {
                    Seed = r.ReadInt32(),
                    CameraPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    CameraYaw = r.ReadSingle(),
                    CameraPitch = r.ReadSingle(),
                    // Pre-v3 defaults: keep the creative-lite behaviour legacy saves had.
                    GameMode = GameMode.Creative,
                    Health = Player.MaxHealth,
                    // Pre-v4 worlds didn't know about the hunger sub-setting; default off
                    // so legacy saves match the new "no hunger bar by default" behaviour.
                    HungerEnabled = false,
                };

                // v1 stored the flying-camera eye position in this slot. From v2 on the slot
                // holds the player's feet. Drop it by an eye-height so the legacy saves spawn
                // the player roughly where they left off instead of up in the air.
                if (version == 1)
                {
                    header.CameraPos = new Vector3(
                        header.CameraPos.X,
                        header.CameraPos.Y - Player.EyeHeight,
                        header.CameraPos.Z);
                }

                if (version >= 3)
                {
                    header.GameMode = (GameMode)r.ReadByte();
                    int hp = r.ReadInt32();
                    // Clamp in case a save was hand-edited or truncated: negative HP
                    // would trigger instant respawn on load.
                    if (hp <= 0 || hp > Player.MaxHealth) hp = Player.MaxHealth;
                    header.Health = hp;
                }
                if (version >= 4)
                {
                    header.HungerEnabled = r.ReadByte() != 0;
                }

                var world = World.Empty(header.Seed);
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int cx = r.ReadInt32();
                    int cz = r.ReadInt32();
                    bool modified = version >= 2 && r.ReadByte() != 0;
                    var chunk = new Chunk(cx, cz) { IsModified = modified };
                    chunk.ReadFrom(r);
                    world.AddChunk(chunk);
                }

                // Tier 6 #47 — Re-propagate sky + block light on every
                // loaded chunk. The save format stores ONLY block ids
                // per chunk (Chunk.WriteTo writes _blocks; _light is
                // derived state and isn't persisted), so freshly-loaded
                // chunks have all-zero sky-light arrays. Without this
                // pass the shader's vSkyLight reads as 0 everywhere
                // and the world renders dark in broad daytime.
                //
                // Parallelised across cores (Phase A of the
                // parallelisation analysis) — the prior version's
                // sequential `foreach` was the dominant cost in
                // Load() per the dedicated server's startup log
                // (3.7 s on a 169-chunk world, with ~70 %+ of that
                // being relight). Each chunk's RecomputeChunk reads
                // and writes ONLY its own `_blocks` and `_light`
                // arrays — no cross-chunk dependencies in the
                // single-chunk recompute path (cross-chunk edge
                // bleed gets cleaned up the first time a player edits
                // near a seam, same as the gen-time seams the engine
                // already accepts; see LightCalculator.cs:141).
                // Materialising the chunk list lets Parallel.ForEach
                // partition cleanly without enumerator races.
                var chunkList = new List<Chunk>();
                foreach (var c in world.AllChunksForPersistence()) chunkList.Add(c);
                System.Threading.Tasks.Parallel.ForEach(chunkList, c =>
                {
                    LightCalculator.RecomputeChunk(c);
                });

                // Cross-chunk seam fix-up. The parallel per-chunk pass
                // above gives each chunk correct INTERNAL lighting but
                // leaves seams at every chunk boundary — a torch near
                // the seam doesn't bleed into the neighbour, sky-light
                // under an overhang doesn't fan across. This single
                // BFS pass walks every chunk-edge cell with positive
                // light and propagates outward across chunk boundaries
                // until the level decay (1 per cell) drops to 0.
                LightCalculator.PropagateAcrossSeams(world);

                // v5: furnace tile entities. Pre-v5 saves had no furnaces
                // (the block didn't exist), so legacy worlds load with
                // an empty entity table. The block layer in restored
                // chunks won't reference Furnace ids in those saves
                // either — the BlockType range simply wasn't populated.
                if (version >= 5)
                {
                    int feCount = r.ReadInt32();
                    for (int i = 0; i < feCount; i++)
                    {
                        int wx = r.ReadInt32();
                        int wy = r.ReadInt32();
                        int wz = r.ReadInt32();
                        var fe = world.GetOrCreateFurnaceEntity(wx, wy, wz);
                        fe.Input  = ReadStack(r);
                        fe.Fuel   = ReadStack(r);
                        fe.Output = ReadStack(r);
                        fe.BurnTimeTicks    = r.ReadInt32();
                        fe.MaxBurnTimeTicks = r.ReadInt32();
                        fe.CookProgressTicks = r.ReadInt32();
                        // v6 appended a single facing byte per entity.
                        // Pre-v6 entries default to North (the field's
                        // default value on a freshly-created
                        // FurnaceTileEntity). Pre-v6 worlds rendered
                        // every side of every furnace as the front
                        // face; under the new oriented mesher those
                        // load with the front pointing -Z and the other
                        // three sides showing the plain side tile. The
                        // player can break/replace the furnace to pick
                        // a new facing (or live with the default — it's
                        // a cosmetic-only difference, no gameplay
                        // impact).
                        if (version >= 6)
                            fe.Facing = (BlockFacing)r.ReadByte();
                    }
                }

                // v7: chest tile entities. Pre-v7 saves had no chests
                // (the block didn't exist), so legacy worlds load with
                // an empty chest table. The block layer in restored
                // chunks won't reference Chest ids in those saves
                // either — the BlockType range simply wasn't populated
                // before this version.
                if (version >= 7)
                {
                    int ceCount = r.ReadInt32();
                    for (int i = 0; i < ceCount; i++)
                    {
                        int wx = r.ReadInt32();
                        int wy = r.ReadInt32();
                        int wz = r.ReadInt32();
                        var ce = world.GetOrCreateChestEntity(wx, wy, wz);
                        ce.Facing = (BlockFacing)r.ReadByte();
                        for (int s = 0; s < ChestTileEntity.SlotCount; s++)
                            ce.Slots[s] = ReadStack(r);
                    }
                }

                // v8: Tier 4 #14 wheat metadata. Sparse record per chunk
                // keyed by (cx, cz). Pre-v8 saves had no wheat (the
                // block didn't exist), so legacy worlds skip this block
                // and load with all _meta bytes at zero — wheat planted
                // in a freshly-loaded post-v7-but-pre-v8 world doesn't
                // exist either, so there's nothing to lose. Records for
                // chunks not currently present are read past silently.
                if (version >= 8)
                {
                    int chunkMetaCount = r.ReadInt32();
                    for (int i = 0; i < chunkMetaCount; i++)
                    {
                        int cx = r.ReadInt32();
                        int cz = r.ReadInt32();
                        var chunk = world.GetChunk(cx, cz);
                        if (chunk != null)
                        {
                            chunk.ReadSparseMeta(r);
                        }
                        else
                        {
                            // Drain the record so the stream stays aligned
                            // even if the chunk it's keyed to wasn't
                            // present (shouldn't happen — Save iterates
                            // the same set we just loaded — but defensive
                            // because corrupt saves cost more than a
                            // ten-line read loop).
                            int dropCount = r.ReadInt32();
                            for (int j = 0; j < dropCount; j++) { r.ReadInt32(); r.ReadByte(); }
                        }
                    }
                }

                // v9: Tier 4 #22 — world-spawn vector. Pre-v9 saves
                // didn't track a separate spawn point (the renderer's
                // _spawnPos was reset to the just-loaded player
                // position on every load), so the compass needs a
                // sensible default for legacy worlds. The closest
                // stand-in is the saved feet position — same value
                // pre-v9 readers would have populated _spawnPos with,
                // so the runtime behaviour is unchanged on those
                // saves; the compass simply points at "where you last
                // saved" instead of "where you originally spawned".
                if (version >= 9)
                {
                    header.SpawnPos = new Vector3(
                        r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                else
                {
                    header.SpawnPos = header.CameraPos;
                }

                // v10: Tier 4 #24 — Painting entities. Pre-v10 saves
                // had no paintings (the entity didn't exist), so
                // legacy worlds skip this block and World's
                // Paintings list stays at its default empty state.
                if (version >= 10)
                {
                    int paintingCount = r.ReadInt32();
                    for (int i = 0; i < paintingCount; i++)
                    {
                        var p = new Painting
                        {
                            X = r.ReadInt32(),
                            Y = r.ReadInt32(),
                            Z = r.ReadInt32(),
                            Facing = (BlockFacing)r.ReadByte(),
                            Width  = r.ReadByte(),
                            Height = r.ReadByte(),
                            Variant = r.ReadByte(),
                        };
                        world.Paintings.Add(p);
                    }
                }

                // v11: Tier 4 #25 — Jukebox tile entities. Pre-v11
                // saves had no jukeboxes (the block didn't exist), so
                // legacy worlds skip this block and World's jukebox
                // dictionary stays empty. The block layer in restored
                // chunks won't reference Jukebox/Disc ids in those
                // saves either — the BlockType range simply wasn't
                // populated before this version.
                if (version >= 11)
                {
                    int jeCount = r.ReadInt32();
                    for (int i = 0; i < jeCount; i++)
                    {
                        int wx = r.ReadInt32();
                        int wy = r.ReadInt32();
                        int wz = r.ReadInt32();
                        var je = world.GetOrCreateJukeboxEntity(wx, wy, wz);
                        je.Disc = (BlockType)r.ReadByte();
                    }
                }

                // v12: Time-of-day clock. Pre-v12 saves don't carry it;
                // default to 0.25 (noon — the renderer's pre-existing
                // default) so legacy worlds keep their previous behaviour.
                header.TimeOfDay = 0.25f;
                if (version >= 12)
                {
                    header.TimeOfDay = r.ReadSingle();
                    // Wrap defensively in case a hand-edited save
                    // dropped a value outside [0, 1) — same wrap
                    // GameRenderer.TimeOfDay applies on assignment.
                    float t = header.TimeOfDay;
                    t = ((t % 1f) + 1f) % 1f;
                    header.TimeOfDay = t;
                }

                // v13: Multiplayer player table. Pre-v13 saves stop at
                // the time-of-day float; the empty dict returned to the
                // caller lets the SP path proceed unchanged. MP-aware
                // callers (ServerHub login) consult by username; missing
                // entries spawn fresh.
                var players = new Dictionary<string, PersistedPlayer>(StringComparer.Ordinal);
                if (version >= 13)
                {
                    int playerCount = r.ReadInt32();
                    if (playerCount < 0 || playerCount > 1024)
                        throw new InvalidDataException($"v13 playerCount {playerCount} out of expected range");
                    for (int i = 0; i < playerCount; i++)
                    {
                        var p = new PersistedPlayer
                        {
                            Username = r.ReadString(),
                            X = r.ReadDouble(),
                            Y = r.ReadDouble(),
                            Z = r.ReadDouble(),
                            Yaw = r.ReadSingle(),
                            Pitch = r.ReadSingle(),
                            Health = r.ReadInt32(),
                            HeldSlot = r.ReadInt32(),
                            Inventory = new ItemStack[Inventory.TotalSlots],
                        };
                        for (int s = 0; s < Inventory.TotalSlots; s++)
                            p.Inventory[s] = ReadStack(r);
                        // Last-write-wins on duplicate username — defensive
                        // against a hand-edited save with two records for
                        // the same user. The duplicate-handling here is
                        // cheaper than throwing because a benign cause is
                        // a future code path that double-emits during
                        // shutdown; we don't want save corruption to
                        // brick the world.
                        if (!string.IsNullOrEmpty(p.Username))
                        {
                            players[p.Username] = p;
                        }
                    }
                }

                // v14: Tier 8 #44 — sign tile entities. Pre-v14 saves
                // had no signs in the BlockType enum so legacy worlds
                // load with an empty sign dict and the (cx, cz) chunks
                // simply don't reference the SignPost / WallSign ids.
                // Each record carries the world coord + 4 typed lines;
                // facing comes from the chunk meta byte, which the
                // v2+ chunk-byte block already restored above before
                // we got here. If the entity's coord points at a cell
                // that ISN'T currently a sign block (e.g. a hand-edited
                // save), we still install the entity — the next remesh
                // simply won't display it, and a future placement at
                // that cell would inherit the orphan text. Cheaper than
                // a per-record block-id sanity check, and defensive
                // saves should always survive load.
                if (version >= 14)
                {
                    int signCount = r.ReadInt32();
                    if (signCount < 0 || signCount > 1_000_000)
                        throw new InvalidDataException($"v14 signCount {signCount} out of expected range");
                    for (int i = 0; i < signCount; i++)
                    {
                        int wx = r.ReadInt32();
                        int wy = r.ReadInt32();
                        int wz = r.ReadInt32();
                        var se = world.GetOrCreateSignEntity(wx, wy, wz);
                        for (int line = 0; line < 4; line++)
                        {
                            se.Lines[line] = r.ReadString();
                        }
                    }
                }

                // v15: Tier 8 #49 — Dispenser tile entities. Pre-v15
                // saves had no dispensers in the BlockType enum so
                // legacy worlds load with an empty dispenser dict.
                // Same shape as the v7 chest section (coord + facing
                // + slots) just with the smaller 9-slot count.
                if (version >= 15)
                {
                    int deCount = r.ReadInt32();
                    if (deCount < 0 || deCount > 1_000_000)
                        throw new InvalidDataException($"v15 dispenserCount {deCount} out of expected range");
                    for (int i = 0; i < deCount; i++)
                    {
                        int wx = r.ReadInt32();
                        int wy = r.ReadInt32();
                        int wz = r.ReadInt32();
                        byte facing = r.ReadByte();
                        var de = world.GetOrCreateDispenserEntity(wx, wy, wz);
                        de.Facing = (BlockFacing)facing;
                        for (int s = 0; s < DispenserTileEntity.SlotCount; s++)
                            de.Slots[s] = ReadStack(r);
                    }
                }

                // v16: Tier 8 #51 V5 — Nether world. Pre-v16 saves
                // had no Nether block, so a load returns null and
                // the renderer lazy-creates the Nether on the next
                // portal entry the same way it did pre-V5. v16+
                // saves with hasNether=0 (player never visited the
                // Nether before saving) also return null.
                World netherWorld = null;
                if (version >= 16)
                {
                    byte hasNether = r.ReadByte();
                    if (hasNether != 0)
                    {
                        netherWorld = World.Empty(header.Seed);
                        netherWorld.Dimension = Dimension.Nether;
                        int neChunkCount = r.ReadInt32();
                        if (neChunkCount < 0 || neChunkCount > 1_000_000)
                            throw new InvalidDataException($"v16 nether chunkCount {neChunkCount} out of expected range");
                        for (int i = 0; i < neChunkCount; i++)
                        {
                            int cx = r.ReadInt32();
                            int cz = r.ReadInt32();
                            bool modified = r.ReadByte() != 0;
                            var c = new Chunk(cx, cz) { IsModified = modified };
                            c.ReadFrom(r);
                            netherWorld.AddChunk(c);
                        }
                        // Recompute lighting for every nether chunk so
                        // the per-cell light values are consistent with
                        // the loaded block data.
                        foreach (var c in netherWorld.AllChunksForPersistence())
                        {
                            LightCalculator.RecomputeChunk(c);
                        }
                        // Nether-side tile entities — same shape as
                        // the overworld tail blocks above, but writing
                        // into netherWorld's parallel dictionaries.
                        int neFurnaceCount = r.ReadInt32();
                        for (int i = 0; i < neFurnaceCount; i++)
                        {
                            int wx = r.ReadInt32();
                            int wy = r.ReadInt32();
                            int wz = r.ReadInt32();
                            var fe = netherWorld.GetOrCreateFurnaceEntity(wx, wy, wz);
                            fe.Input = ReadStack(r);
                            fe.Fuel = ReadStack(r);
                            fe.Output = ReadStack(r);
                            fe.BurnTimeTicks = r.ReadInt32();
                            fe.MaxBurnTimeTicks = r.ReadInt32();
                            fe.CookProgressTicks = r.ReadInt32();
                        }
                        int neChestCount = r.ReadInt32();
                        for (int i = 0; i < neChestCount; i++)
                        {
                            int wx = r.ReadInt32();
                            int wy = r.ReadInt32();
                            int wz = r.ReadInt32();
                            byte facing = r.ReadByte();
                            var ce = netherWorld.GetOrCreateChestEntity(wx, wy, wz);
                            ce.Facing = (BlockFacing)facing;
                            for (int s = 0; s < ChestTileEntity.SlotCount; s++)
                                ce.Slots[s] = ReadStack(r);
                        }
                        int neSignCount = r.ReadInt32();
                        for (int i = 0; i < neSignCount; i++)
                        {
                            int wx = r.ReadInt32();
                            int wy = r.ReadInt32();
                            int wz = r.ReadInt32();
                            var se = netherWorld.GetOrCreateSignEntity(wx, wy, wz);
                            for (int line = 0; line < 4; line++)
                            {
                                se.Lines[line] = r.ReadString();
                            }
                        }
                        int neDispenserCount = r.ReadInt32();
                        for (int i = 0; i < neDispenserCount; i++)
                        {
                            int wx = r.ReadInt32();
                            int wy = r.ReadInt32();
                            int wz = r.ReadInt32();
                            byte facing = r.ReadByte();
                            var de = netherWorld.GetOrCreateDispenserEntity(wx, wy, wz);
                            de.Facing = (BlockFacing)facing;
                            for (int s = 0; s < DispenserTileEntity.SlotCount; s++)
                                de.Slots[s] = ReadStack(r);
                        }
                    }
                }

                // v17: Tier 8 #51 V6 — Per-dimension player state.
                // Pre-v17 saves return a default DimensionState; the
                // renderer falls back to header.CameraPos for the
                // overworld slot.
                DimensionState dimState = default;
                if (version >= 17)
                {
                    dimState.OverworldPos   = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    dimState.OverworldYaw   = r.ReadSingle();
                    dimState.OverworldPitch = r.ReadSingle();
                    dimState.NetherPos      = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    dimState.NetherYaw      = r.ReadSingle();
                    dimState.NetherPitch    = r.ReadSingle();
                    byte dim = r.ReadByte();
                    dimState.CurrentDimension = dim == (byte)Dimension.Nether
                        ? Dimension.Nether
                        : Dimension.Overworld;
                }

                return (header, world, players, netherWorld, dimState);
            }
        }

        // ItemStack on-disk shape: 2 bytes type id + 4 bytes count + 2
        // bytes durability. Empty stacks serialise as type=Air with
        // zero count/durability.
        private static void WriteStack(BinaryWriter w, ItemStack s)
        {
            w.Write((ushort)s.Type);
            w.Write(s.Count);
            w.Write(s.Durability);
        }

        private static ItemStack ReadStack(BinaryReader r)
        {
            var type = (BlockType)r.ReadUInt16();
            int count = r.ReadInt32();
            short dur = r.ReadInt16();
            if (type == BlockType.Air || count <= 0) return ItemStack.Empty;
            return new ItemStack(type, count, dur);
        }
    }
}
