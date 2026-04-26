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

        // CamelCase enum -> human-readable label. Mirrors the helper in
        // GameRenderer (kept duplicated here so the catalog has no
        // dependency back into the renderer).
        public static string FriendlyName(BlockType t)
        {
            string raw = t.ToString();
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
