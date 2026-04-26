using System;
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
        private const byte CurrentVersion = 7;

        public struct Header
        {
            public int Seed;
            public Vector3 CameraPos;
            public float CameraYaw;
            public float CameraPitch;
            public GameMode GameMode;  // v3+
            public int Health;         // v3+ (1..MaxHealth; 0 means "load default")
            public bool HungerEnabled; // v4+ — survival sub-setting; default off
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
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
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
