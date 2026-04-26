using Microsoft.Win32;

namespace VStudioCraft.Game
{
    // Per-installation user preferences that aren't tied to a specific
    // world. Lives in HKCU\Software\VStudioCraft so it travels with the
    // user across worlds and persists between Visual Studio sessions.
    //
    // World-specific options (HungerEnabled, GameMode) stay in the world
    // save header — those are part of the world's identity, not the
    // user's environment. This file is for "how do I want the game to
    // look / feel everywhere I play" stuff.
    //
    // Registry was chosen over a JSON sidecar because:
    //   - we already reference System and System.Core (no extra deps),
    //   - VS extensions can't always assume %APPDATA% write access in
    //     locked-down developer machines, but HKCU is reliable,
    //   - single-value reads/writes are atomic — no torn config reads
    //     across the UI/render thread split.
    internal static class Settings
    {
        private const string KeyPath = @"Software\VStudioCraft";

        // True → load the embedded Alpha terrain.png at atlas build time
        // and slice it into per-tile layers. False → fall back to the
        // procedural noise palettes in BlockTextures (the original look,
        // shipped before this toggle existed).
        //
        // Default: false. The procedural look is the project's signature
        // and ships in clean installs; users who want the canonical Alpha
        // textures opt in via the Options menu.
        public static bool UseRealTextures
        {
            get => ReadBool("UseRealTextures", false);
            set => WriteBool("UseRealTextures", value);
        }

        private static bool ReadBool(string name, bool fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return fallback;
                    var v = key.GetValue(name);
                    if (v is int i) return i != 0;
                    return fallback;
                }
            }
            catch
            {
                // Registry access can fail under unusual ACLs; we never
                // want a settings read to take down the renderer.
                return fallback;
            }
        }

        private static void WriteBool(string name, bool value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Best-effort write — if HKCU is locked down, the toggle
                // still applies for the current session via the renderer
                // field; it just won't persist to the next launch.
            }
        }
    }
}
