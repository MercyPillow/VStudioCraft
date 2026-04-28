using System;
using System.IO;

namespace VStudioCraft.Net
{
    // Phase-2 packet types. Each is a small DTO struct/class with Read /
    // Write static methods that serialise the payload AFTER the 1-byte ID
    // (the ID itself is written by NetSession when it frames the packet,
    // not by these types — keeps the dispatcher in one place).
    //
    // I'm intentionally keeping each packet as its own small type rather
    // than a single union with a Kind enum: it lets the read-side dispatch
    // be a simple switch and the write-side a typed call, without case-by-
    // case "are these fields valid for this kind?" branches polluting
    // every packet. Phases 3+ add more types in this same file (or a
    // sibling for grouping) without disturbing the existing ones.

    // 0x00 — empty body. Sent both directions on a timer (~10 s) so the
    // socket layer detects a half-open connection without waiting for the
    // OS keepalive (default 2 hours, useless for a game). The presence of
    // the ID byte is the entire payload.
    internal struct KeepAlivePacket
    {
        public static void Write(PacketWriter w) { /* no fields */ }
        public static KeepAlivePacket Read(PacketReader r) => default;
    }

    // 0x01 — first packet a connecting client sends. The protocol version
    // gates incompatible clients: server rejects with a Disconnect packet
    // explaining the mismatch rather than silently mis-parsing later
    // packets. Username is 1..16 ASCII chars by convention; semantic
    // validation happens server-side.
    internal struct LoginRequestPacket
    {
        public int ProtocolVersion;
        public string Username;

        public void Write(PacketWriter w)
        {
            w.WriteInt(ProtocolVersion);
            w.WriteString(Username);
        }

        public static LoginRequestPacket Read(PacketReader r) => new LoginRequestPacket
        {
            ProtocolVersion = r.ReadInt(),
            Username        = r.ReadString(),
        };
    }

    // 0x02 — server's reply to a successful login. Carries the server-
    // assigned entity id for THIS client (so subsequent EntitySpawn /
    // EntityRelMove packets for other players can be distinguished from
    // the local-player's own snapshots), the world seed (so the client
    // can decorate sky/biome the same way), the gameMode the world is
    // running in, and the spawn coordinate the client should pre-position
    // its camera at while initial chunks stream in.
    internal struct LoginResponsePacket
    {
        public int EntityId;
        public int Seed;
        public byte GameMode;     // 0=Survival, 1=Creative, matches Game.GameMode enum
        public int SpawnX;
        public int SpawnY;
        public int SpawnZ;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteInt(Seed);
            w.WriteByte(GameMode);
            w.WriteInt(SpawnX);
            w.WriteInt(SpawnY);
            w.WriteInt(SpawnZ);
        }

        public static LoginResponsePacket Read(PacketReader r) => new LoginResponsePacket
        {
            EntityId = r.ReadInt(),
            Seed     = r.ReadInt(),
            GameMode = r.ReadByte(),
            SpawnX   = r.ReadInt(),
            SpawnY   = r.ReadInt(),
            SpawnZ   = r.ReadInt(),
        };
    }

    // 0x04 — orderly hangup. Sent by either side before closing the socket
    // so the peer sees a textual reason instead of a TCP RST. Reasons are
    // free-form English — only humans read them ("Server full", "Protocol
    // mismatch: expected 1 got 2", "Kicked by op").
    internal struct DisconnectPacket
    {
        public string Reason;

        public void Write(PacketWriter w) => w.WriteString(Reason ?? string.Empty);
        public static DisconnectPacket Read(PacketReader r) => new DisconnectPacket { Reason = r.ReadString() };
    }

    // 0x10 — client→server position + look. Sent every tick while the
    // client is in-world. Doubles for X/Z because Alpha worlds extend to
    // ±30M blocks and float precision is unsafe past ~16M. Y is also a
    // double for symmetry though it's bounded to 128. OnGround is a hint
    // the server uses to disambiguate jump vs fall — it doesn't trust the
    // client (server simulates physics authoritatively) but uses it as a
    // tiebreaker when the client's reported position is consistent with
    // either state. Yaw / pitch are degrees, range-normalised at the
    // sender side.
    internal struct PlayerPosLookPacket
    {
        public double X, Y, Z;
        public float Yaw, Pitch;
        public bool OnGround;

        public void Write(PacketWriter w)
        {
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteFloat(Yaw);
            w.WriteFloat(Pitch);
            w.WriteBool(OnGround);
        }

        public static PlayerPosLookPacket Read(PacketReader r) => new PlayerPosLookPacket
        {
            X        = r.ReadDouble(),
            Y        = r.ReadDouble(),
            Z        = r.ReadDouble(),
            Yaw      = r.ReadFloat(),
            Pitch    = r.ReadFloat(),
            OnGround = r.ReadBool(),
        };
    }

    // 0x30 — server→client chunk payload. CompressedBlocks is gzipped raw
    // block bytes (the same byte[] that WorldSaveFormat.WriteChunk emits
    // before compression); the client decompresses and hands the bytes
    // straight to a Chunk constructor. Light is recomputed client-side
    // (LightCalculator.RecomputeChunk), so we don't ship it on the wire —
    // it'd ~triple chunk size for no gain since the calc is O(chunk) and
    // sub-millisecond.
    //
    // The 4-byte length prefix on CompressedBlocks survives even when the
    // payload is empty (sentinel for "unload" — though ChunkUnload is the
    // proper packet for that). Phase 3 wires ChunkUnload (0x31).
    internal struct ChunkLoadPacket
    {
        public int ChunkX;
        public int ChunkZ;
        public byte[] CompressedBlocks;

        // Cap on the gzipped payload size we'll accept. A 16x16x128 chunk
        // is 32 KiB raw, ~6 KiB gzipped typical. 256 KiB gives us a 40x
        // safety margin and still bounds memory if a hostile client sends
        // a length-only packet.
        public const int MaxCompressedBytes = 256 * 1024;

        public void Write(PacketWriter w)
        {
            w.WriteInt(ChunkX);
            w.WriteInt(ChunkZ);
            w.WriteByteArray(CompressedBlocks);
        }

        public static ChunkLoadPacket Read(PacketReader r) => new ChunkLoadPacket
        {
            ChunkX           = r.ReadInt(),
            ChunkZ           = r.ReadInt(),
            CompressedBlocks = r.ReadByteArray(MaxCompressedBytes),
        };
    }
}
