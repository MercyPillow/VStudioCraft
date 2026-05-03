using System.IO;
using System.Windows.Forms;

namespace VStudioCraft.Game
{
    // Tier 9 #53 V2 — Configurable key bindings. The 8 in-game
    // gameplay keys (movement / jump / sneak / inventory / drop) are
    // routed through this static surface so the runtime input handler
    // reads `KeyBindings.MoveForward` instead of a hardcoded `Keys.W`.
    // The Controls sub-screen in the options menu lets the player
    // rebind each one; bindings persist to %APPDATA%\VStudioCraft\
    // keybindings.cfg as a tiny key=value text file.
    //
    // F-keys (F3 debug, F5 toggle perspective, F8 cinematic) and the
    // number-row 1..9 hotbar selectors stay hardcoded — they're not
    // canonical "user-configurable" keys in Alpha 1.1.2_01 and the
    // rebinding UI only meaningfully helps players who use a non-
    // QWERTY layout (Dvorak, AZERTY) where the movement cluster is
    // in a different position.
    internal static class KeyBindings
    {
        public static Keys MoveForward = Keys.W;
        public static Keys MoveBackward = Keys.S;
        public static Keys MoveLeft     = Keys.A;
        public static Keys MoveRight    = Keys.D;
        public static Keys Jump         = Keys.Space;
        public static Keys Sneak        = Keys.ShiftKey;
        public static Keys Inventory    = Keys.E;
        public static Keys DropItem     = Keys.Q;

        // Bindings exposed as an ordered list so the Controls screen
        // can render rows generically. Each entry is the display name
        // + a getter / setter pair for the keys field.
        public sealed class Binding
        {
            public readonly string Label;
            public System.Func<Keys> Get;
            public System.Action<Keys> Set;
            public Binding(string label, System.Func<Keys> g, System.Action<Keys> s)
            {
                Label = label; Get = g; Set = s;
            }
        }
        public static readonly Binding[] All =
        {
            new Binding("Forward",     () => MoveForward,  k => MoveForward  = k),
            new Binding("Back",        () => MoveBackward, k => MoveBackward = k),
            new Binding("Left",        () => MoveLeft,     k => MoveLeft     = k),
            new Binding("Right",       () => MoveRight,    k => MoveRight    = k),
            new Binding("Jump",        () => Jump,         k => Jump         = k),
            new Binding("Sneak",       () => Sneak,        k => Sneak        = k),
            new Binding("Inventory",   () => Inventory,    k => Inventory    = k),
            new Binding("Drop",        () => DropItem,     k => DropItem     = k),
        };

        // Path under %APPDATA%\VStudioCraft\keybindings.cfg. Same
        // parent directory the save folder uses, so a player who
        // wipes their config can find both data buckets in one
        // place. The directory is created on demand.
        private static string ConfigPath
        {
            get
            {
                string appdata = System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appdata, "VStudioCraft", "keybindings.cfg");
            }
        }

        // Load bindings from disk if the config file exists. Skipped
        // silently when the file is absent (fresh install — defaults
        // apply) or unreadable. Each non-blank, non-`#`-prefixed line
        // is `Name=KeyName` (e.g. "Forward=W"). Unknown names are
        // ignored so a config from a future build doesn't break
        // loading on an older one.
        public static void LoadFromDisk()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq >= line.Length - 1) continue;
                    string name = line.Substring(0, eq).Trim();
                    string val  = line.Substring(eq + 1).Trim();
                    if (!System.Enum.TryParse(val, out Keys parsed)) continue;
                    foreach (var b in All)
                    {
                        if (b.Label.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            b.Set(parsed);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Best-effort: a corrupted / locked config doesn't
                // crash the game — the player just runs with
                // defaults until they re-save.
            }
        }

        // Persist current bindings. Called on close-from-options-
        // menu and on game shutdown so a crash doesn't lose the
        // last-edited bindings.
        public static void SaveToDisk()
        {
            try
            {
                string path = ConfigPath;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(path))
                {
                    w.WriteLine("# VStudioCraft key bindings — edit names match the in-game labels.");
                    w.WriteLine("# Values use System.Windows.Forms.Keys enum names (e.g. W, ShiftKey, Space).");
                    foreach (var b in All)
                    {
                        w.WriteLine($"{b.Label}={b.Get()}");
                    }
                }
            }
            catch
            {
                // Best-effort: failure to persist is silent. The
                // in-memory bindings still apply for the rest of the
                // session.
            }
        }

        // Reset all bindings to canonical Alpha defaults. Used by the
        // "Defaults" button on the Controls screen.
        public static void ResetToDefaults()
        {
            MoveForward  = Keys.W;
            MoveBackward = Keys.S;
            MoveLeft     = Keys.A;
            MoveRight    = Keys.D;
            Jump         = Keys.Space;
            Sneak        = Keys.ShiftKey;
            Inventory    = Keys.E;
            DropItem     = Keys.Q;
        }
    }
}
