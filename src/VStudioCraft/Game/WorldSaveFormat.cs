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
        private const byte CurrentVersion = 11;

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
        }

        public static void Save(string path, Header header, World world)
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

        public static (Header header, World world) Load(string path)
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

                return (header, world);
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
