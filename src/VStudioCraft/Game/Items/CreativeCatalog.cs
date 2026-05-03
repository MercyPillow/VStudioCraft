using System.Collections.Generic;
using System.Text;

namespace VStudioCraft.Game
{
    // Creative-mode "give me anything" catalog. Lists every placeable
    // BlockType so the inventory's main grid can be replaced with a
    // searchable, scrollable picker when the player is in creative.
    //
    // The list is built once at static-init time from the BlockType enum,
    // filtering out Air and the flowing-fluid variants (those don't have
    // a "stack the player picks up" form). Order is the enum's declaration
    // order so familiar blocks (grass, dirt, stone) cluster at the top —
    // matches Alpha's creative tab feel without us having to maintain a
    // hand-curated category list.
    //
    // Search is a case-insensitive substring match against the block's
    // FriendlyName ("Flowing Water" etc.). The renderer calls Filter once
    // per frame the inventory is open with the current search text and
    // scroll offset; result is a small List<BlockType> (capped at the
    // catalog's full length) so per-frame allocation is fine for a
    // user-driven UI.
    internal static class CreativeCatalog
    {
        // Master list of every catalog entry, in enum declaration order.
        public static readonly BlockType[] All = BuildAll();

        private static BlockType[] BuildAll()
        {
            var values = (BlockType[])System.Enum.GetValues(typeof(BlockType));
            var list = new List<BlockType>(values.Length);
            foreach (var t in values)
            {
                if (!IsCatalogEntry(t)) continue;
                list.Add(t);
            }
            return list.ToArray();
        }

        // Catalog inclusion rule. Air isn't a placeable; FlowingWater /
        // FlowingLava are runtime-only fluid states the engine generates
        // from the source variants, so a player picking them up makes no
        // sense. Tools are included — creative players can grab a fresh
        // pickaxe / shovel / axe / sword from the catalog the same way
        // they'd pick a block. Everything else (including the source
        // Water / Lava blocks) gets a tile.
        public static bool IsCatalogEntry(BlockType t)
        {
            switch (t)
            {
                case BlockType.Air:
                case BlockType.FlowingWater:
                case BlockType.FlowingLava:
                // LitFurnace is the runtime "actively burning" state of a
                // Furnace cell — the tile-entity tick swaps a placed
                // Furnace into LitFurnace whenever its fuel slot is
                // active, and back out when the burn timer drains. The
                // player shouldn't ever hold a LitFurnace stack; the
                // catalog entry is Furnace.
                case BlockType.LitFurnace:
                // Wall-torch BlockTypes (TorchEast/West/South/North) are
                // runtime-only orientation variants of the floor torch —
                // the placement code picks the variant from the raycast
                // hit's face normal. The player should only see / hold
                // BlockType.Torch in inventories; breaking a wall torch
                // also drops generic Torch (see BlockData.DropFor).
                case BlockType.TorchEast:
                case BlockType.TorchWest:
                case BlockType.TorchSouth:
                case BlockType.TorchNorth:
                // Tier 4 #16 — Door BLOCK halves are runtime-only — the
                // player's catalog pick / hotbar slot must be the ITEM
                // form (WoodDoorItem / IronDoorItem). RMB-placing the
                // item spawns the matching pair of block halves with
                // metadata (handled in GameRenderer.TryInteract). The
                // catalog already includes the WoodDoorItem and
                // IronDoorItem ids via the default branch below; here
                // we just exclude the four block halves so they don't
                // show up alongside (which would produce broken half-
                // door entries the player couldn't place properly).
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                // Tier 6 #47 — Wheat is a planted-only world block;
                // the player only ever holds the WheatSeeds (to plant)
                // or WheatItem (harvested grain) forms, never the
                // crop block itself. Excluding it keeps the catalog
                // showing one "Wheat" entry (the WheatItem) instead
                // of two (the world block + the item, both labelled
                // "Wheat" via BlockData.Name).
                case BlockType.Wheat:
                // Tier 8 #42 — Redstone runtime-only variants. Player
                // should only see / hold:
                //   - RedstoneTorchOn (the canonical lit form; the
                //     OFF variant is a power-driven runtime state).
                //   - RedstoneDust (the held / dropped item form;
                //     RedstoneWire is the placed-block form, spawned
                //     when RedstoneDust is RMB'd onto a solid top
                //     face — same shape as the door item-vs-blocks
                //     pattern).
                case BlockType.RedstoneTorchOff:
                case BlockType.RedstoneWire:
                // Tier 8 #44 — Sign block variants (SignPost / WallSign)
                // are runtime-only — the player crafts and holds the
                // SignItem id, and the placement path picks the post
                // vs wall variant from the hit-face normal. The
                // SignItem itself is included via the default branch
                // below; excluding the two block variants here keeps
                // the catalog from showing three sign entries (the
                // item + two block forms) when only the item is the
                // valid hold-and-place form.
                case BlockType.SignPost:
                case BlockType.WallSign:
                    return false;
                default:
                    return true;
            }
        }

        // Filter the catalog by a free-text query. Empty / whitespace
        // queries return the full list. Match is case-insensitive substring
        // against the friendly name ("flowing water" matches "FlowingWater"
        // — though FlowingWater itself is filtered above; useful for
        // future tools / items that share a friendly-name word).
        public static List<BlockType> Filter(string query)
        {
            var result = new List<BlockType>(All.Length);
            if (string.IsNullOrWhiteSpace(query))
            {
                result.AddRange(All);
                return result;
            }

            string needle = query.Trim().ToLowerInvariant();
            foreach (var t in All)
            {
                string name = FriendlyName(t).ToLowerInvariant();
                if (name.IndexOf(needle, System.StringComparison.Ordinal) >= 0)
                {
                    result.Add(t);
                }
            }
            return result;
        }

        // Friendly display label. Consults BlockData.Name first so any
        // explicit override there ("Wheat" for WheatItem, "Iron Ingot"
        // for IronIngot, etc.) wins. When BlockData.Name returns the
        // raw enum ToString (the fall-through default), apply the
        // CamelCase split so "GrassTop" reads as "Grass Top". This
        // keeps the catalog, hotbar tooltip, and item-name popout in
        // lockstep on a single source of truth.
        public static string FriendlyName(BlockType t)
        {
            string raw = t.ToString();
            string named = ItemType.Name(t);
            if (named != null && named != raw) return named;
            if (raw.Length == 0) return raw;
            var sb = new StringBuilder(raw.Length + 4);
            sb.Append(raw[0]);
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsUpper(c) && !char.IsUpper(raw[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
