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

    // 0x32 — server→client single-block change. Sent when any cell in the
    // world flips type. The server batches these inside a tick (one
    // packet per dirtied cell at the end of the tick) and only sends to
    // clients whose tracked-chunks set contains the cell's chunk —
    // a client that hasn't been streamed (cx, cz) yet doesn't need to
    // know about edits there. Phase 3+ uses this for player-driven dig/
    // place; Phase 5+ also uses it for fluid spread, sugar-cane growth,
    // and door toggle.
    //
    // Coordinates are absolute world coords. The cell's chunk is
    // computed as (X >> 4, Z >> 4) on receive, mirroring World.SetBlock.
    // Block type is a byte (matches BlockType enum width) — meta is
    // separate from type and not yet on this packet because Phase 3
    // doesn't carry meta-bearing edits (door state, wheat stage). When
    // those arrive the packet will gain a 1-byte meta field; bumping
    // ProtocolVersion at that point gates old clients out cleanly.
    internal struct BlockChangePacket
    {
        public int X;
        public int Y;
        public int Z;
        public byte BlockType;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            // Y is < 128 always — could pack to byte; using int keeps
            // the packet shape symmetrical with X/Z and trivially
            // future-proof if vertical world size ever grows.
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteByte(BlockType);
        }

        public static BlockChangePacket Read(PacketReader r) => new BlockChangePacket
        {
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            BlockType = r.ReadByte(),
        };
    }

    // 0x31 — server→client chunk drop. Sent when a player walks far
    // enough that a previously-streamed chunk has fallen out of their
    // view radius. The client mirrors by calling World.UnloadChunk.
    // The server's tracked-chunks set drops the entry as it sends, so
    // any subsequent BlockChange in that chunk won't ship.
    internal struct ChunkUnloadPacket
    {
        public int ChunkX;
        public int ChunkZ;

        public void Write(PacketWriter w)
        {
            w.WriteInt(ChunkX);
            w.WriteInt(ChunkZ);
        }

        public static ChunkUnloadPacket Read(PacketReader r) => new ChunkUnloadPacket
        {
            ChunkX = r.ReadInt(),
            ChunkZ = r.ReadInt(),
        };
    }

    // 0x40 — client→server "I want to break this block".
    //
    // Status:
    //   0 = start  — Phase 3+ uses this for creative-mode instant break
    //                (server validates raycast + immediately applies SetBlock).
    //                In survival mode (Phase 5+) the server also kicks off
    //                the per-player break-progress timer here.
    //   1 = cancel — player released LMB before the timer completed.
    //   2 = finish — player held LMB long enough for the survival timer to
    //                hit zero on the client. Server cross-checks against
    //                its own timer and applies SetBlock if valid.
    //
    // Face is the block face the player was looking at when they clicked,
    // packed as 0..5 (0=-Y, 1=+Y, 2=-Z, 3=+Z, 4=-X, 5=+X) — same indexing
    // the client's raycast uses. Lets the server reject digs with an
    // implausible face (player on the wrong side of the block).
    internal struct PlayerDigPacket
    {
        public byte Status;    // 0=start, 1=cancel, 2=finish
        public int X, Y, Z;
        public byte Face;

        public void Write(PacketWriter w)
        {
            w.WriteByte(Status);
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteByte(Face);
        }

        public static PlayerDigPacket Read(PacketReader r) => new PlayerDigPacket
        {
            Status = r.ReadByte(),
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            Face = r.ReadByte(),
        };
    }

    // 0x42 — client→server "I want to place a block here".
    //
    // X/Y/Z is the cell the player CLICKED ON (the existing solid that
    // gets the new block placed against). Face indicates which side they
    // hit; the server places into (X+dx, Y+dy, Z+dz) where d* is the face
    // normal. This matches Alpha's Place packet shape.
    //
    // BlockType is the cell-type the client believes is in their hand.
    // Server cross-checks against the inventory it holds for this client
    // — a desync (client's selected slot differs from server's idea)
    // results in the place being rejected and the inventory packet that
    // refreshes the client's slot also fires (Phase 6 for the inventory
    // sync; Phase 3 just trusts the client value).
    internal struct PlayerPlacePacket
    {
        public int X, Y, Z;
        public byte Face;
        public byte BlockType;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteByte(Face);
            w.WriteByte(BlockType);
        }

        public static PlayerPlacePacket Read(PacketReader r) => new PlayerPlacePacket
        {
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            Face = r.ReadByte(),
            BlockType = r.ReadByte(),
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
