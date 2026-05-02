using System;
using System.IO;
using VStudioCraft.Game; // ItemStack, BlockType — ItemSpawnPacket / TileEntityDataPacket carry these

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

        // Phase 5c — dropped items (block-break drops, mob death drops,
        // Q-tossed items). Sent via ItemSpawnPacket (0x27) which carries
        // the ItemStack payload alongside position + velocity; subsequent
        // RelMove / Despawn use the existing 0x21 / 0x25 packets.
        public const byte DroppedItem  = 32;

        // Phase 5d — projectiles. All four share ProjectileSpawnPacket
        // (0x28) — type byte distinguishes them. Subsequent motion
        // through EntityRelMove (0x21) and EntityDespawn (0x25), same
        // shape as drops. Bobbers are unusual in that they sit still
        // most of their life (waiting for a fish) but the same packet
        // shape works fine — the RelMove deltas just stay near zero.
        public const byte Arrow        = 48;
        public const byte Snowball     = 49;
        public const byte Egg          = 50;
        public const byte Bobber       = 51;
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

    // 0x27 — server→client dropped-item spawn. Distinct from EntitySpawn
    // because dropped items carry an ItemStack payload (item type +
    // count) plus a velocity vector for the toss arc; encoding both in
    // EntitySpawn would have ballooned every player/mob spawn packet
    // for two fields they never use.
    //
    // Velocity is included so the client's local DroppedItem can render
    // a brief toss arc instead of teleporting to the resting position.
    // Subsequent updates flow through EntityRelMove (cheap delta packets
    // as gravity pulls the drop down to its rest position) and
    // EntityDespawn (when the host picks it up or the 5-minute Alpha
    // lifetime expires).
    internal struct ItemSpawnPacket
    {
        public int EntityId;
        public double X, Y, Z;
        public float Vx, Vy, Vz;
        public byte ItemType;   // BlockType byte
        public byte ItemCount;  // 1..64

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteFloat(Vx);
            w.WriteFloat(Vy);
            w.WriteFloat(Vz);
            w.WriteByte(ItemType);
            w.WriteByte(ItemCount);
        }

        public static ItemSpawnPacket Read(PacketReader r) => new ItemSpawnPacket
        {
            EntityId  = r.ReadInt(),
            X         = r.ReadDouble(),
            Y         = r.ReadDouble(),
            Z         = r.ReadDouble(),
            Vx        = r.ReadFloat(),
            Vy        = r.ReadFloat(),
            Vz        = r.ReadFloat(),
            ItemType  = r.ReadByte(),
            ItemCount = r.ReadByte(),
        };
    }

    // 0x28 — server→client projectile spawn (Arrow / Snowball / Egg /
    // Bobber). Same shape as ItemSpawn minus the ItemStack payload:
    // eid + type + pos + vel. Type byte uses EntityType.Arrow / Snowball
    // / Egg / Bobber. Subsequent motion + despawn flow through the
    // existing EntityRelMove (0x21) and EntityDespawn (0x25) packets;
    // the friend's render path dispatches on the type tag from the
    // initial spawn so an arrow keeps drawing as an arrow even after
    // ten RelMove updates.
    internal struct ProjectileSpawnPacket
    {
        public int EntityId;
        public byte ProjectileType; // EntityType.Arrow/Snowball/Egg/Bobber
        public double X, Y, Z;
        public float Vx, Vy, Vz;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteByte(ProjectileType);
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteFloat(Vx);
            w.WriteFloat(Vy);
            w.WriteFloat(Vz);
        }

        public static ProjectileSpawnPacket Read(PacketReader r) => new ProjectileSpawnPacket
        {
            EntityId       = r.ReadInt(),
            ProjectileType = r.ReadByte(),
            X = r.ReadDouble(),
            Y = r.ReadDouble(),
            Z = r.ReadDouble(),
            Vx = r.ReadFloat(),
            Vy = r.ReadFloat(),
            Vz = r.ReadFloat(),
        };
    }

    // 0x43 — friend→server "I right-clicked while holding the item in
    // my selected hotbar slot". Server resolves what to do based on
    // the slot's content: snowball / egg → spawn ThrownProjectile;
    // bow → would need draw-charge state (deferred); bucket → fluid
    // place / scoop (deferred); fishing rod → bobber cast (deferred).
    // Phase 6b ships only the snowball + egg paths because they're
    // single-shot intent (no charge timer) and exercise the full
    // inventory-decrement → projectile-spawn loop end-to-end.
    //
    // No payload — the server uses the friend's last-known position +
    // yaw + held slot. Phase 6b extension could carry a "click target"
    // for bucket-on-water etc.
    internal struct PlayerUseItemPacket
    {
        public void Write(PacketWriter w) { /* no fields */ }
        public static PlayerUseItemPacket Read(PacketReader r) => default;
    }

    // 0x44 — friend→server hotbar slot selection. 0..8. Server stores
    // it on ServerClient.HeldSlot so future PlayerUseItem / PlayerDrop
    // can ask "what's the friend holding right now". Cheap (1 byte body)
    // — clients can spam wheel-scroll without flooding the wire.
    internal struct PlayerHeldSlotPacket
    {
        public byte Slot; // 0..8

        public void Write(PacketWriter w) => w.WriteByte(Slot);
        public static PlayerHeldSlotPacket Read(PacketReader r) => new PlayerHeldSlotPacket
        {
            Slot = r.ReadByte(),
        };
    }

    // 0x45 — friend→server Q-drop intent. Mode 0 = drop one item from
    // the currently-held hotbar slot, mode 1 = drop the whole stack
    // (Shift+Q on the host). Server resolves the held slot from
    // ServerClient.HeldSlot, mutates ServerInventory, spawns a
    // DroppedItem at the friend's position with the standard toss
    // velocity, and broadcasts ItemSpawn + InventoryUpdate.
    internal struct PlayerDropItemPacket
    {
        public byte Mode; // 0 = single, 1 = whole stack

        public void Write(PacketWriter w) => w.WriteByte(Mode);
        public static PlayerDropItemPacket Read(PacketReader r) => new PlayerDropItemPacket
        {
            Mode = r.ReadByte(),
        };
    }

    // ===========================================================================
    // 0x4_ / 0x5_ / 0x6_ — windowed inventories (Phase 6c)
    //
    // The "window" abstraction handles every UI panel that's NOT the
    // player's persistent inventory: chest, furnace, crafting table.
    // Each open window is identified by a per-client `byte windowId`
    // (1..255 — id 0 is the implicit player inventory, never sent
    // through these packets). Friend interaction:
    //
    //   1. Friend right-clicks a chest/furnace/crafting block
    //      → ships PlayerInteractBlockPacket(x,y,z) (0x46)
    //   2. Server validates the block, allocates windowId, opens a
    //      window state record holding the backing tile entity
    //      (chest entity, furnace entity, ephemeral crafting grid).
    //      Sends OpenWindowPacket(0x52, windowId, kind, slotCount, x,y,z)
    //      then TileEntityDataPacket(0x60, windowId, slots[, kind tail]).
    //   3. Friend clicks a slot inside the window
    //      → ships WindowClickPacket(0x54, windowId, slot, button, shift)
    //   4. Server applies via Inventory.Handle*ClickSlot on the window's
    //      backing inventory + replies with TileEntityDataPacket for the
    //      window's slots and InventoryUpdatePacket for any cursor / main
    //      inventory changes.
    //   5. Friend closes the window (Esc / outside-click)
    //      → ships CloseWindowPacket(0x53, windowId).
    //   6. Server commits any cursor stack to the player's main grid,
    //      drops leftover crafting input at the player's feet, removes
    //      the window state record.
    // ===========================================================================

    // Window kinds — one byte per OpenWindow / TileEntityData. Not the
    // same enum as EntityType; windows live in a different namespace
    // (block-attached, not entity-attached).
    internal static class WindowKind
    {
        public const byte PlayerInventory = 0; // implicit; never sent
        public const byte Chest           = 1; // 27 slots
        public const byte Furnace         = 2; // 3 slots + cook progress + burn time tail
        public const byte CraftingTable   = 3; // 9 input + 1 output (output not networked separately — server
                                               // recomputes from inputs each click)
    }

    // 0x46 — friend→server "I right-clicked the cell at (x,y,z) and
    // it's a tile-entity-bearing block I want to open the UI for."
    // Server validates: cell exists, block is a recognised
    // window-bearing type (Chest, Furnace, LitFurnace, CraftingTable),
    // friend is within 6-block reach (same tolerance as dig/place).
    // On success, server allocates a windowId and ships OpenWindow +
    // TileEntityData. On failure, silent — friend's local UI doesn't
    // open and they can try again.
    internal struct PlayerInteractBlockPacket
    {
        public int X, Y, Z;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
        }

        public static PlayerInteractBlockPacket Read(PacketReader r) => new PlayerInteractBlockPacket
        {
            X = r.ReadInt(), Y = r.ReadInt(), Z = r.ReadInt(),
        };
    }

    // 0x47 — Tier 8 #44 V3 — client→server "I just finished editing
    // the sign at (X,Y,Z) and these are my four lines of text." The
    // server validates that there's actually a SignTileEntity at the
    // coord (rejects writes to non-sign cells from a hostile or
    // glitched client), copies the strings in, and broadcasts a
    // SignText (0x61) to every client tracking the chunk so the
    // typing appears on every viewer's copy of the world.
    //
    // No client-side prediction: the local renderer stamps the text
    // into its own SignTileEntity at commit time so the player sees
    // their typing immediately, but on multiplayer the SERVER's
    // SignText echo is what every other client (and the originating
    // client's authoritative state, after a round trip) sees. If
    // the server rejects the edit (corrupt coord, race with a
    // breaker, etc.) the originator's display stays out of sync for
    // one chunk-reload-cycle — acceptable for the cooperative-LAN
    // design point.
    internal struct PlayerEditSignPacket
    {
        public int X, Y, Z;
        public string Line0, Line1, Line2, Line3;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteString(Line0 ?? string.Empty);
            w.WriteString(Line1 ?? string.Empty);
            w.WriteString(Line2 ?? string.Empty);
            w.WriteString(Line3 ?? string.Empty);
        }

        public static PlayerEditSignPacket Read(PacketReader r) => new PlayerEditSignPacket
        {
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            Line0 = r.ReadString(),
            Line1 = r.ReadString(),
            Line2 = r.ReadString(),
            Line3 = r.ReadString(),
        };
    }

    // 0x61 — Tier 8 #44 V3 — server→client sign-text broadcast.
    // Same payload shape as PlayerEditSign minus the implied "this
    // came from me" semantics (a SignText sent from server to client
    // is authoritative; the receiving client overwrites its local
    // SignTileEntity.Lines with the four strings here).
    //
    // Sent in two scenarios:
    //   1. After a successful PlayerEditSign — every client tracking
    //      the chunk gets the new text, including the originator
    //      (so the local-prediction stamp gets a server-confirmed
    //      version on top, which converges if both agree and
    //      corrects mismatches if the server rejected fields).
    //   2. During ChunkLoad — for every sign already in the chunk
    //      being shipped, the server emits one SignText so the
    //      late-joining client sees existing writing as soon as the
    //      sign block renders.
    internal struct SignTextPacket
    {
        public int X, Y, Z;
        public string Line0, Line1, Line2, Line3;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteString(Line0 ?? string.Empty);
            w.WriteString(Line1 ?? string.Empty);
            w.WriteString(Line2 ?? string.Empty);
            w.WriteString(Line3 ?? string.Empty);
        }

        public static SignTextPacket Read(PacketReader r) => new SignTextPacket
        {
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            Line0 = r.ReadString(),
            Line1 = r.ReadString(),
            Line2 = r.ReadString(),
            Line3 = r.ReadString(),
        };
    }

    // 0x52 — server→client "I'm opening window <id> of kind <kind>
    // for you, the cell at (x,y,z) is its backing tile entity, and
    // it has <slotCount> slots". Slot population follows in a
    // separate TileEntityDataPacket so the client can pre-allocate
    // its slot array before the data arrives.
    internal struct OpenWindowPacket
    {
        public byte WindowId;
        public byte Kind;
        public byte SlotCount;
        public int X, Y, Z;

        public void Write(PacketWriter w)
        {
            w.WriteByte(WindowId);
            w.WriteByte(Kind);
            w.WriteByte(SlotCount);
            w.WriteInt(X);
            w.WriteInt(Y);
            w.WriteInt(Z);
        }

        public static OpenWindowPacket Read(PacketReader r) => new OpenWindowPacket
        {
            WindowId = r.ReadByte(),
            Kind = r.ReadByte(),
            SlotCount = r.ReadByte(),
            X = r.ReadInt(), Y = r.ReadInt(), Z = r.ReadInt(),
        };
    }

    // 0x53 — close-window. Sent both directions:
    //   - Server → client: server forced the window closed (e.g. block
    //     was broken while open).
    //   - Client → server: friend hit Esc or clicked away. Server
    //     commits the window state and removes the record.
    internal struct CloseWindowPacket
    {
        public byte WindowId;

        public void Write(PacketWriter w) => w.WriteByte(WindowId);
        public static CloseWindowPacket Read(PacketReader r) => new CloseWindowPacket
        {
            WindowId = r.ReadByte(),
        };
    }

    // 0x54 — friend→server slot click inside an open window. Slot
    // indexing is window-local (chest 0..26, furnace 0..2, crafting
    // 0..8 input + slot 9 = output). Buttons + shift match
    // InventoryClickPacket.
    internal struct WindowClickPacket
    {
        public byte WindowId;
        public byte Slot;
        public byte Button;   // 0=LMB, 1=RMB
        public byte Shift;    // 0/1

        public void Write(PacketWriter w)
        {
            w.WriteByte(WindowId);
            w.WriteByte(Slot);
            w.WriteByte(Button);
            w.WriteByte(Shift);
        }

        public static WindowClickPacket Read(PacketReader r) => new WindowClickPacket
        {
            WindowId = r.ReadByte(),
            Slot = r.ReadByte(),
            Button = r.ReadByte(),
            Shift = r.ReadByte(),
        };
    }

    // 0x60 — server→client "here's the current state of window <id>".
    // SlotCount × ItemStack covers the window's full slot array;
    // furnace adds 8 bytes of tail (BurnTime + MaxBurnTime + CookProgress
    // packed as int+int+int, but we use 2 ints here for simplicity —
    // matches the v5+ save format encoding).
    //
    // Sent at OpenWindow time and on every server-side mutation of the
    // window (other-player click on the same chest, server-side
    // crafting recipe match, server-side furnace cook tick).
    internal struct TileEntityDataPacket
    {
        public byte WindowId;
        public byte Kind;       // copy of OpenWindowPacket.Kind for self-validation
        public byte SlotCount;
        public ItemStack[] Slots;
        // Furnace-only tail (kind == WindowKind.Furnace):
        public int FurnaceBurnTime;
        public int FurnaceMaxBurnTime;
        public int FurnaceCookProgress;

        public void Write(PacketWriter w)
        {
            w.WriteByte(WindowId);
            w.WriteByte(Kind);
            w.WriteByte(SlotCount);
            for (int i = 0; i < SlotCount && i < (Slots?.Length ?? 0); i++)
            {
                var s = Slots[i];
                w.WriteByte((byte)s.Type);
                w.WriteByte((byte)(s.IsEmpty ? 0 : s.Count));
            }
            // Pad if Slots is shorter than declared count (defensive —
            // shouldn't happen on the write side but lets us send a
            // partial array without breaking the wire framing).
            for (int i = (Slots?.Length ?? 0); i < SlotCount; i++)
            {
                w.WriteByte(0);
                w.WriteByte(0);
            }
            if (Kind == WindowKind.Furnace)
            {
                w.WriteInt(FurnaceBurnTime);
                w.WriteInt(FurnaceMaxBurnTime);
                w.WriteInt(FurnaceCookProgress);
            }
        }

        public static TileEntityDataPacket Read(PacketReader r)
        {
            var p = new TileEntityDataPacket
            {
                WindowId = r.ReadByte(),
                Kind = r.ReadByte(),
                SlotCount = r.ReadByte(),
            };
            p.Slots = new ItemStack[p.SlotCount];
            for (int i = 0; i < p.SlotCount; i++)
            {
                byte type = r.ReadByte();
                byte count = r.ReadByte();
                p.Slots[i] = count == 0 ? ItemStack.Empty : new ItemStack((BlockType)type, count);
            }
            if (p.Kind == WindowKind.Furnace)
            {
                p.FurnaceBurnTime = r.ReadInt();
                p.FurnaceMaxBurnTime = r.ReadInt();
                p.FurnaceCookProgress = r.ReadInt();
            }
            return p;
        }
    }

    // 0x50 — friend→server "I clicked slot N with button B (with/without
    // shift)". Local hit-test resolves the slot index client-side; the
    // server doesn't need to know about screen layout. Two slot
    // sentinels:
    //
    //   0..48 — main inventory slots (matches Inventory.Slots[] index)
    //   0xFF  — clicked OUTSIDE the panel while holding cursor;
    //           server tosses Cursor as a DroppedItem at the friend's
    //           position. (Same effect as Q-drop on the cursor stack;
    //           reuses the SpawnDropHook the host already wired.)
    //
    // Button: 0=LMB, 1=RMB. Shift: 0/1.
    //
    // Server applies via Inventory.HandleLeftClickSlot /
    // HandleRightClickSlot / HandleShiftClickSlot on ServerInventory
    // (the same path the host's local UI uses), then ships the full
    // 49-slot inventory + cursor back. The bandwidth cost is fine —
    // clicks are infrequent and the full burst is ~150 bytes.
    internal struct InventoryClickPacket
    {
        public byte Slot;     // 0..48, or 0xFF for outside-drop
        public byte Button;   // 0=LMB, 1=RMB
        public byte Shift;    // 0/1

        public void Write(PacketWriter w)
        {
            w.WriteByte(Slot);
            w.WriteByte(Button);
            w.WriteByte(Shift);
        }

        public static InventoryClickPacket Read(PacketReader r) => new InventoryClickPacket
        {
            Slot = r.ReadByte(),
            Button = r.ReadByte(),
            Shift = r.ReadByte(),
        };
    }

    // 0x51 — server→client inventory slot update. Sent when the server's
    // authoritative inventory for a client changes (drop pickup, mob death
    // drop pickup, future crafting / chest interaction). Single-slot packet
    // because most updates are 1-slot (one drop picked up = one slot
    // changes); a multi-slot variant can come if profiling shows the
    // overhead matters.
    //
    // Slot indexing matches Inventory.Slots[]:
    //   0..35  — main grid (4×9, top-down row-major)
    //   36..44 — hotbar
    //   45..48 — armor (helmet, chest, leggings, boots)
    //   0xFF   — cursor stack (Phase 6b-extended) — the floating stack
    //            held while the inventory panel is open.
    //
    // ItemStack on the wire is (type byte, count byte). Empty stack is
    // type=Air (0), count=0; the reader maps that back to ItemStack.Empty.
    internal struct InventoryUpdatePacket
    {
        public byte Slot;       // 0..48 inclusive
        public byte ItemType;   // BlockType byte; 0 = empty
        public byte ItemCount;  // 1..64; 0 alongside type=0 = empty

        public void Write(PacketWriter w)
        {
            w.WriteByte(Slot);
            w.WriteByte(ItemType);
            w.WriteByte(ItemCount);
        }

        public static InventoryUpdatePacket Read(PacketReader r) => new InventoryUpdatePacket
        {
            Slot = r.ReadByte(),
            ItemType = r.ReadByte(),
            ItemCount = r.ReadByte(),
        };
    }

    // 0x26 — server→client entity health update. Sent when a tracked
    // entity's health changes (decreases — health gain isn't currently
    // signalled separately because Alpha doesn't show heal flashes to
    // other players). Friend's render path uses the change to drive the
    // existing hurt-flash lerp on the local replica without needing to
    // run TakeDamage logic itself.
    //
    // Health is short (-32768..32767) — mob health caps at low integers
    // (~20 for mobs, 20 for player) but the wider field leaves room
    // for future damage scales without a protocol bump.
    internal struct EntityHealthPacket
    {
        public int EntityId;
        public short Health;

        public void Write(PacketWriter w)
        {
            w.WriteInt(EntityId);
            w.WriteShort(Health);
        }

        public static EntityHealthPacket Read(PacketReader r) => new EntityHealthPacket
        {
            EntityId = r.ReadInt(),
            Health = r.ReadShort(),
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
    // world flips type or metadata. The server batches these inside a
    // tick (one packet per dirtied cell at the end of the tick) and
    // only sends to clients whose tracked-chunks set contains the
    // cell's chunk — a client that hasn't been streamed (cx, cz) yet
    // doesn't need to know about edits there. Used for player-driven
    // dig/place, future fluid spread / sugar-cane growth, and door
    // toggle (which mutates Meta but not BlockType — KI-3).
    //
    // Coordinates are absolute world coords. The cell's chunk is
    // computed as (X >> 4, Z >> 4) on receive, mirroring World.SetBlock.
    //
    // v2 (current): Meta byte appended after BlockType. v1 → v2 is a
    // hard wire-format break, so ProtocolVersion was bumped to 2; a
    // v1 client gets a clean Disconnect at login.
    internal struct BlockChangePacket
    {
        public int X;
        public int Y;
        public int Z;
        public byte BlockType;
        public byte Meta;

        public void Write(PacketWriter w)
        {
            w.WriteInt(X);
            // Y is < 128 always — could pack to byte; using int keeps
            // the packet shape symmetrical with X/Z and trivially
            // future-proof if vertical world size ever grows.
            w.WriteInt(Y);
            w.WriteInt(Z);
            w.WriteByte(BlockType);
            w.WriteByte(Meta);
        }

        public static BlockChangePacket Read(PacketReader r) => new BlockChangePacket
        {
            X = r.ReadInt(),
            Y = r.ReadInt(),
            Z = r.ReadInt(),
            BlockType = r.ReadByte(),
            Meta = r.ReadByte(),
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
