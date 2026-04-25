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
        // v4 = HungerEnabled survival sub-setting.
        private const byte CurrentVersion = 4;

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
                return (header, world);
            }
        }
    }
}
