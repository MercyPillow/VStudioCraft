namespace VStudioCraft.Net
{
    // The wire packet identifiers. Single byte on wire — first byte of every
    // framed packet — followed by the packet-specific payload. Numbering is
    // grouped by 0x10 buckets so new packets in a category land next to their
    // siblings:
    //
    //   0x0_  Session-level (handshake, keepalive, disconnect, chat)
    //   0x1_  Local-player position / look / look-correction
    //   0x2_  Other-entity replication (spawn, move, despawn)  — Phase 4+
    //   0x3_  Chunk + block (chunk load / unload / single-block change) — Phase 3+
    //   0x4_  Player-action intents (dig, place, use)         — Phase 3+
    //   0x5_  Window / inventory                              — Phase 6+
    //   0x6_  Tile-entity blobs (furnace progress, chest)     — Phase 6+
    //
    // Phase 2 implements only the IDs listed below; the rest are reserved
    // here as named constants so that adding them in later phases doesn't
    // accidentally pick a colliding number, and so the dispatcher can
    // exhaustively switch on PacketId without falling through.
    //
    // We intentionally use a const-byte class (rather than an enum) because
    // the wire format uses single-byte IDs and casting between byte and a
    // typed enum at every read/write site adds noise without improving
    // clarity. The fixed integer literals also make the table at the top
    // of the multiplayer plan file read directly as code.
    internal static class PacketIds
    {
        // 0x0_ — session ----------------------------------------------------
        public const byte KeepAlive       = 0x00;
        public const byte LoginRequest    = 0x01; // C->S
        public const byte LoginResponse   = 0x02; // S->C
        public const byte Chat            = 0x03; // both       — Phase 8
        public const byte Disconnect      = 0x04; // both

        // 0x1_ — local-player pos/look -------------------------------------
        public const byte PlayerPosLook        = 0x10; // C->S
        public const byte PlayerPosLookCorrect = 0x11; // S->C  — Phase 4

        // 0x2_ — entity replication (Phase 4+) -----------------------------
        public const byte EntitySpawn         = 0x20; // S->C
        public const byte EntityRelMove       = 0x21; // S->C
        public const byte EntityLook          = 0x22; // S->C
        public const byte EntityRelMoveLook   = 0x23; // S->C
        public const byte EntityTeleport      = 0x24; // S->C
        public const byte EntityDespawn       = 0x25; // S->C
        public const byte EntityHealth        = 0x26; // S->C
        // Phase 5c — DroppedItem replication. EntitySpawn (0x20) doesn't
        // carry an item payload, so dropped items use a dedicated spawn
        // packet that includes the ItemStack tuple. Subsequent motion +
        // despawn use the existing EntityRelMove (0x21) and
        // EntityDespawn (0x25) packets — those are payload-agnostic.
        public const byte ItemSpawn           = 0x27; // S->C
        // Phase 5d — projectile spawn (Arrow, Snowball, Egg, Bobber).
        // Distinct from EntitySpawn (0x20) because the wire payload
        // carries velocity for the visible toss arc — players need
        // to see an arrow ARC across a room rather than teleport
        // toward its rest pose.
        public const byte ProjectileSpawn     = 0x28; // S->C

        // 0x3_ — world streaming -------------------------------------------
        public const byte ChunkLoad           = 0x30; // S->C
        public const byte ChunkUnload         = 0x31; // S->C  — Phase 3
        public const byte BlockChange         = 0x32; // S->C  — Phase 3
        public const byte MultiBlockChange    = 0x33; // S->C  — Phase 3

        // 0x4_ — player intents (Phase 3+) ---------------------------------
        public const byte PlayerDigStart      = 0x40; // C->S
        public const byte PlayerDigStop       = 0x41; // C->S
        public const byte PlayerPlace         = 0x42; // C->S
        public const byte PlayerUseItem       = 0x43; // C->S
        // Phase 6b — friend hotbar selection (1..9 / scroll wheel).
        // Server tracks the held slot per ServerClient so future
        // PlayerUseItem can resolve "what is the friend holding" and
        // PlayerDropItem can drop from the right slot.
        public const byte PlayerHeldSlot      = 0x44; // C->S
        // Phase 6b — friend Q-drop intent. Server removes the item
        // from ServerInventory and spawns a DroppedItem in the world
        // at the friend's position with the host's standard toss
        // velocity, mirroring what the host's local Q-drop path does.
        public const byte PlayerDropItem      = 0x45; // C->S
        // Phase 6c — friend RMB on a tile-entity-bearing block (chest /
        // furnace / crafting table / jukebox). Distinct from
        // PlayerUseItem (which acts on the held item) because this
        // intent identifies the world cell the player wants to open.
        public const byte PlayerInteractBlock = 0x46; // C->S
        // Tier 8 #44 V3 — Sign edit commit. Sent by the client when
        // the local player closes the sign editor (Enter on the last
        // line or Escape). Carries (x, y, z, line0..line3); the
        // server is authoritative on persisted text — it copies the
        // 4 strings into the SignTileEntity and broadcasts the new
        // text to every client tracking the chunk via SignText
        // (0x61). Server-side validation is intentionally lax (any
        // string up to a defensive cap) — Alpha-faithful design
        // point is cooperative LAN play, no anti-grief on text.
        public const byte PlayerEditSign      = 0x47; // C->S

        // 0x5_ — window / inventory (Phase 6) ------------------------------
        public const byte InventoryClick      = 0x50; // C->S  — Phase 6b
        public const byte InventoryUpdate     = 0x51; // S->C  — Phase 6a (single-slot updates from server-authoritative inventory)
        // Phase 6c — windowed inventories (chest / furnace / crafting).
        // Window 0 is the implicit player inventory and is never
        // explicitly opened/closed; windowIds 1..255 are per-client
        // allocations for tile-entity sessions.
        public const byte OpenWindow          = 0x52; // S->C
        public const byte CloseWindow         = 0x53; // both
        // Phase 6c — click on a slot inside an open window. Distinct
        // from InventoryClick because the slot-space differs (chest
        // window has its own 0..26 slots) and the server-side handler
        // routes against the window's backing Inventory rather than
        // ServerInventory directly.
        public const byte WindowClick         = 0x54; // C->S

        // 0x6_ — tile entity blobs (Phase 6) -------------------------------
        public const byte TileEntityData      = 0x60; // S->C
        // Tier 8 #44 V3 — Sign text broadcast. Carries (x, y, z,
        // line0..line3). Server emits this to every client tracking
        // the chunk: (a) once per sign in the chunk on ChunkLoad, so
        // late-joiners see existing writing; (b) once per sign on
        // PlayerEditSign accept, so live edits propagate to all
        // viewers. Distinct from TileEntityData (chest/furnace) so
        // the wire shape stays minimal — sign payload is just 4
        // strings plus the world coord.
        public const byte SignText            = 0x61; // S->C

        // Protocol version. Bumped when packet shapes change so a mismatched
        // client gets a clean Disconnect at login rather than misinterpreting
        // bytes mid-session. Incremented every time the wire format breaks
        // backward compatibility (NOT every time a new packet is added —
        // additive changes leave existing clients alone).
        // v1: initial Phase 2 wire shape.
        // v2: BlockChangePacket gains a `byte Meta` field for door
        //     toggle (KI-3 fix). All v1 packets unchanged on the wire,
        //     so a v2 server replying to a v1 client would mis-frame
        //     the next packet — bumping the version cuts that off
        //     with a clean Disconnect at login.
        // v3: Tier 8 #44 V3 — adds PlayerEditSign (0x47) and SignText
        //     (0x61). New packets only; existing packet shapes
        //     unchanged. We bump anyway because a v2 server would
        //     not know how to decode an inbound 0x47 from a v3
        //     client, and a v2 client wouldn't recognise 0x61 from
        //     a v3 server — both would throw at the dispatcher and
        //     drop the connection mid-session, which is worse than
        //     a clean Disconnect at login.
        public const int ProtocolVersion = 3;
    }
}
