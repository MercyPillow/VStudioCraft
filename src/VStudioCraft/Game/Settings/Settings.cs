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

        // Audio mixer values, both 0..1. Persisted as DWord (value × 1000)
        // so HKCU integer storage round-trips losslessly at the resolution
        // the slider exposes (0.1 % per click step is far finer than ear
        // can hear). Default: full volume — first launch should be audible
        // unless the user opts down.
        //
        // MasterVolume scales every SFX (and any future music) — it's the
        // global mute. MusicVolume scales only music tracks; the options
        // slider for music multiplies onto the master, matching user
        // expectation ("if I drop master to 50%, music drops too").
        public static float MasterVolume
        {
            get => ReadFloat01("MasterVolume", 1.0f);
            set => WriteFloat01("MasterVolume", value);
        }

        public static float MusicVolume
        {
            get => ReadFloat01("MusicVolume", 1.0f);
            set => WriteFloat01("MusicVolume", value);
        }

        // Tier 10 follow-up — Render distance in chunks. Default 6
        // matches the legacy compile-time constant; range 4..16 covers
        // "tiny window" to "stress test the chunk job pool". Stored as
        // an int in the registry — clamped on read so a corrupt value
        // can't bring chunk streaming to its knees.
        public const int RenderDistanceMin     = 4;
        public const int RenderDistanceMax     = 16;
        public const int RenderDistanceDefault = 6;
        public static int RenderDistance
        {
            get => ReadInt("RenderDistance", RenderDistanceDefault, RenderDistanceMin, RenderDistanceMax);
            set => WriteInt("RenderDistance", value, RenderDistanceMin, RenderDistanceMax);
        }

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return fallback;
                    var v = key.GetValue(name);
                    if (v is int i)
                    {
                        if (i < min) i = min;
                        else if (i > max) i = max;
                        return i;
                    }
                    return fallback;
                }
            }
            catch { return fallback; }
        }

        private static void WriteInt(string name, int value, int min, int max)
        {
            try
            {
                if (value < min) value = min;
                else if (value > max) value = max;
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static float ReadFloat01(string name, float fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return fallback;
                    var v = key.GetValue(name);
                    if (v is int i)
                    {
                        if (i < 0) i = 0;
                        else if (i > 1000) i = 1000;
                        return i / 1000f;
                    }
                    return fallback;
                }
            }
            catch { return fallback; }
        }

        private static void WriteFloat01(string name, float value)
        {
            try
            {
                if (value < 0f) value = 0f;
                else if (value > 1f) value = 1f;
                int stored = (int)(value * 1000f + 0.5f);
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, stored, RegistryValueKind.DWord);
                }
            }
            catch { }
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
