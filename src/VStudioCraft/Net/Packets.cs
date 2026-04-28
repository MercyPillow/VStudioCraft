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

    // ===========================================================================
    // 0x2_ — entity replication (Phase 4)
    //
    // The server tells clients about other entities — for now just other
    // players (EntityType.Player). Phase 5 reuses the same packet shapes
    // for mobs / drops / projectiles by widening EntityType.
    //
    // Position encoding choice: full doubles for absolute (Spawn,
    // Teleport), full floats for deltas (RelMove, Look, RelMoveLook).
    // Alpha used 1/32-block fixed-point bytes for deltas to fit four
    // entity moves into a 64-byte MTU; we don't care — local-area MP
    // is the design target, bandwidth is plentiful, and the simpler
    // wire format is easier to debug. ~17 bytes per moving entity per
    // tick × 20 Hz × 8 players = ~3 KiB/s aggregate, trivial.
    //
    // EntityRelMove is emitted for the common case (small position
    // change since last broadcast). When the delta exceeds the float
    // precision the renderer uses for interpolation OR when the server
    // hasn't broadcast for ≥ 20 ticks (drift-correction window),
    // EntityTeleport is sent instead and resets the client's last-seen
    // anchor. EntityLook is the rarer "rotated in place" case.
    // ===========================================================================

    // Entity-type tag carried in EntitySpawn. Single byte so future types
    // (mobs, drops, projectiles) just claim a value without disturbing
    // the on-wire layout. Player=0 because it's the only one that lands
    // in Phase 4; Phase 5 wires Pig/Cow/Sheep/Chicken/Zombie/Skeleton/
    // Spider/Creeper next.
    internal static class EntityType
    {
        public const byte Player       = 0;

        // Phase 5 — passives. Bucket starting at 1 so a future reorder
        // doesn't disturb Player=0; gaps are deliberate to leave room
        // for future passives without rebucketing.
        public const byte Pig          = 1;
        public const byte Cow          = 2;
        public const byte Sheep        = 3;
        public const byte Chicken      = 4;

        // Phase 5b — hostiles. Bucket starts at 16 to leave headroom
        // above the passive range for new passives without renumbering.
        public const byte Zombie       = 16;
        public const byte Skeleton     = 17;
        public const byte Spider       = 18;
        public const byte Creeper      = 19;
        // public const byte DroppedItem  = 32;
        // public const byte Arrow        = 48;
        // public const byte Snowball     = 49;
        // public const byte Egg          = 50;
        // public const byte Bobber       = 51;
    }

    // 0x20 — a new entity entered this client's awareness. The server
    // sends one to a client when:
    //   - Another player joined, and we already have the (cx,cz) the
    //     joiner spawned in.
    //   - We just joined ourselves; the server enumerates all currently-
    //     in-range entities and sends a spawn for each.
    // The DisplayName is included for player nametags and admin logs;
    // for non-Player types it can be empty string.
    internal struct EntitySpawnPacket
    {
        public int EntityId;
        public byte EntityType;
        public double X, Y, Z;
        public float Yaw, Pitch;
        public string DisplayName;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteByte(EntityType);
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteFloat(Yaw);
            w.WriteFloat(Pitch);
            w.WriteString(DisplayName ?? string.Empty);
        }

        public static EntitySpawnPacket Read(PacketReader r) => new EntitySpawnPacket
        {
            EntityId    = r.ReadInt(),
            EntityType  = r.ReadByte(),
            X           = r.ReadDouble(),
            Y           = r.ReadDouble(),
            Z           = r.ReadDouble(),
            Yaw         = r.ReadFloat(),
            Pitch       = r.ReadFloat(),
            DisplayName = r.ReadString(),
        };
    }

    // 0x21 — entity moved by (dx, dy, dz) from its last broadcast
    // anchor. Client adds this to the buffered "previous" snapshot
    // and uses (now, anchor+delta) as the new "current" snapshot
    // for interpolation.
    internal struct EntityRelMovePacket
    {
        public int EntityId;
        public float Dx, Dy, Dz;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteFloat(Dx);
            w.WriteFloat(Dy);
            w.WriteFloat(Dz);
        }

        public static EntityRelMovePacket Read(PacketReader r) => new EntityRelMovePacket
        {
            EntityId = r.ReadInt(),
            Dx       = r.ReadFloat(),
            Dy       = r.ReadFloat(),
            Dz       = r.ReadFloat(),
        };
    }

    // 0x22 — entity rotated in place. Pos didn't move enough to be
    // worth a delta packet, but yaw/pitch changed (player turning to
    // look at something while standing still).
    internal struct EntityLookPacket
    {
        public int EntityId;
        public float Yaw, Pitch;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteFloat(Yaw);
            w.WriteFloat(Pitch);
        }

        public static EntityLookPacket Read(PacketReader r) => new EntityLookPacket
        {
            EntityId = r.ReadInt(),
            Yaw      = r.ReadFloat(),
            Pitch    = r.ReadFloat(),
        };
    }

    // 0x23 — common case: entity moved AND rotated in the same tick.
    // Combined to halve the per-tick packet count for active players.
    internal struct EntityRelMoveLookPacket
    {
        public int EntityId;
        public float Dx, Dy, Dz;
        public float Yaw, Pitch;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteFloat(Dx);
            w.WriteFloat(Dy);
            w.WriteFloat(Dz);
            w.WriteFloat(Yaw);
            w.WriteFloat(Pitch);
        }

        public static EntityRelMoveLookPacket Read(PacketReader r) => new EntityRelMoveLookPacket
        {
            EntityId = r.ReadInt(),
            Dx       = r.ReadFloat(),
            Dy       = r.ReadFloat(),
            Dz       = r.ReadFloat(),
            Yaw      = r.ReadFloat(),
            Pitch    = r.ReadFloat(),
        };
    }

    // 0x24 — absolute reposition. Sent for:
    //   - First broadcast after EntitySpawn (so the client gets a clean
    //     anchor regardless of what it did with the spawn position).
    //   - Drift correction every ~20 ticks (1 s) so accumulated rounding
    //     in chained EntityRelMove deltas can't slowly walk the
    //     replica off the server's authoritative position.
    //   - Long-distance movement (teleport, login spawn, fall through
    //     the void) where a single delta would exceed the precision
    //     of the renderer's interpolator.
    internal struct EntityTeleportPacket
    {
        public int EntityId;
        public double X, Y, Z;
        public float Yaw, Pitch;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteFloat(Yaw);
            w.WriteFloat(Pitch);
        }

        public static EntityTeleportPacket Read(PacketReader r) => new EntityTeleportPacket
        {
            EntityId = r.ReadInt(),
            X        = r.ReadDouble(),
            Y        = r.ReadDouble(),
            Z        = r.ReadDouble(),
            Yaw      = r.ReadFloat(),
            Pitch    = r.ReadFloat(),
        };
    }

    // 0x25 — entity left this client's awareness. Reasons:
    //   - Owning player disconnected.
    //   - Entity moved out of this client's view radius (their tracked-
    //     entities set drops it).
    //   - Mob died (Phase 5).
    // Client deletes its replica; future packets referencing this
    // EntityId are ignored until a fresh EntitySpawn re-introduces it.
    internal struct EntityDespawnPacket
    {
        public int EntityId;

        public void Write(PacketWriter w) => w.WriteInt(EntityId);
        public static EntityDespawnPacket Read(PacketReader r) => new EntityDespawnPacket { EntityId = r.ReadInt() };
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
