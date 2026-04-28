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

        // 0x5_ — window / inventory (Phase 6) ------------------------------
        public const byte InventoryClick      = 0x50; // C->S  — Phase 6b
        public const byte InventoryUpdate     = 0x51; // S->C  — Phase 6a (single-slot updates from server-authoritative inventory)
        public const byte OpenWindow          = 0x52; // S->C
        public const byte CloseWindow         = 0x53; // both

        // 0x6_ — tile entity blobs (Phase 6) -------------------------------
        public const byte TileEntityData      = 0x60; // S->C

        // Protocol version. Bumped when packet shapes change so a mismatched
        // client gets a clean Disconnect at login rather than misinterpreting
        // bytes mid-session. Incremented every time the wire format breaks
        // backward compatibility (NOT every time a new packet is added —
        // additive changes leave existing clients alone).
        public const int ProtocolVersion = 1;
    }
}
