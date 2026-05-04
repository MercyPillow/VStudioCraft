using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Audio;
using OpenTK.Audio.OpenAL;

namespace VStudioCraft.Game
{
    // Thin OpenAL wrapper. Manages a single AudioContext + a pool of
    // SourcePoolSize reusable AL sources. PlayOneShot is the only public
    // verb — picks a Stopped/Initial source from the pool (or rotates
    // through to evict the oldest if all are still playing) and queues a
    // pre-built buffer for playback.
    //
    // **Defensive init**: AudioContext.ctor throws if openal32.dll is
    // missing on the host (no OpenAL Soft installed, locked-down VS
    // extension host, etc.). We catch every exception and fall into a
    // permanently-muted state where PlayOneShot becomes a no-op — the
    // renderer must keep working without sound.
    //
    // **No spatialisation**: every source is set to SourceRelative and
    // anchored at (0,0,0). That's fine for V1 — every SFX we play (block
    // break, footstep, UI click, water splash) is conceptually on the
    // player. Ambient cave drips (Tier 1 #2 wishlist) would want true
    // 3D positioning; we'll add that when they ship.
    internal static class AudioEngine
    {
        // 16 sources is generous: most ticks fire 0–1 SFX, and the worst
        // case (a cluster of dropped items getting picked up at once) is
        // bounded by the inventory loop. Cheap to keep them all alive
        // since each AL source is a few hundred bytes of state.
        private const int SourcePoolSize = 16;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        private static AudioContext _context;
        private static readonly int[] _sources = new int[SourcePoolSize];
        private static int _evictCursor;
        private static bool _initTried;
        private static bool _muted = true;
        private static float _masterGain = 1.0f;
        private static string _initFailureReason; // null when init succeeded
        private static string _preloadDiagnostic;  // last preload attempt summary

        // Tier 4 #25 — Dedicated music source. Allocated alongside the
        // SFX pool but kept separate so a music track can be started /
        // stopped independently of the one-shot SFX traffic. The pool's
        // round-robin eviction would otherwise cut a song mid-playback
        // the moment the player walks across grass and racks up 16
        // footstep one-shots; isolating music to its own source dodges
        // that. Music gain is multiplied by MusicGain * MasterGain at
        // PlayMusic time so the options menu's two sliders both apply.
        private static int _musicSource;
        private static bool _musicSourceCreated;

        public static bool IsAvailable => _initTried && !_muted;
        public static string InitFailureReason => _initFailureReason;
        public static string PreloadDiagnostic  => _preloadDiagnostic;

        public static float MasterGain
        {
            get => _masterGain;
            set => _masterGain = value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        // Music volume — kept here so a future jukebox / music streaming
        // path has a single, settings-bound knob to read. SFX paths
        // (PlayOneShot) ignore this; they only obey MasterGain. Music
        // playback is expected to multiply MusicGain * MasterGain so
        // dropping master also drops music.
        private static float _musicGain = 1.0f;
        public static float MusicGain
        {
            get => _musicGain;
            set
            {
                _musicGain = value < 0f ? 0f : (value > 1f ? 1f : value);
                // Tier 10 #51 — Push the new volume into any in-flight
                // MCI music stream so the options-menu music slider
                // affects the currently-playing disc immediately.
                RefreshMusicFileVolume();
            }
        }

        // Idempotent. Safe to call from the renderer's init path; any
        // subsequent call is a cheap early-out.
        public static void Initialize()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                // Pre-load the bundled openal32.dll from this assembly's
                // directory. The VSIX install path isn't on Windows' DLL
                // search path, so a naked DllImport("openal32") would fail
                // with "module not found". LoadLibraryW with a full path
                // pins the binary in the process — every subsequent
                // [DllImport("openal32")] call inside OpenTK then resolves
                // to the loaded module without hitting the search path
                // again. Failure here is non-fatal; we fall through and
                // let `new AudioContext()` throw, then capture the reason.
                TryPreloadOpenAL();

                _context = new AudioContext();
                AL.GenSources(_sources);
                for (int i = 0; i < SourcePoolSize; i++)
                {
                    AL.Source(_sources[i], ALSourcef.Gain, 1f);
                    AL.Source(_sources[i], ALSourcef.Pitch, 1f);
                    AL.Source(_sources[i], ALSourceb.SourceRelative, true);
                    AL.Source(_sources[i], ALSource3f.Position, 0f, 0f, 0f);
                }
                // Tier 4 #25 — Dedicated music source. One AL source
                // separate from the SFX pool so PlayMusic / StopMusic
                // don't compete with the round-robin one-shot eviction.
                _musicSource = AL.GenSource();
                AL.Source(_musicSource, ALSourcef.Gain, 1f);
                AL.Source(_musicSource, ALSourcef.Pitch, 1f);
                AL.Source(_musicSource, ALSourceb.SourceRelative, true);
                AL.Source(_musicSource, ALSource3f.Position, 0f, 0f, 0f);
                _musicSourceCreated = true;
                _muted = false;
                _initFailureReason = null;
                System.Diagnostics.Debug.WriteLine("[VStudioCraft.AudioEngine] init OK; preload: " + (_preloadDiagnostic ?? "(none)"));
            }
            catch (Exception ex)
            {
                // OpenAL not available on this host — likely missing
                // openal32.dll. Fall silent; the rest of the engine
                // doesn't need to know. Reason is exposed for the
                // options menu so the user can see why audio is dead.
                _muted = true;
                // Concatenate context-ctor reason + preload diagnostic so
                // the options menu shows the WHOLE story in one row. The
                // common failure mode is "DLL not found at <path>" — that
                // tells us deployment is broken in a way the bare
                // exception message ("module could not be loaded") never
                // would.
                string preload = _preloadDiagnostic ?? "(no preload diagnostic)";
                _initFailureReason = ex.GetType().Name + ": " + ex.Message + " | " + preload;
                System.Diagnostics.Debug.WriteLine("[VStudioCraft.AudioEngine] init failed: " + _initFailureReason);
                try { _context?.Dispose(); } catch { }
                _context = null;
            }
        }

        // Locate openal32.dll next to this assembly and LoadLibrary it.
        // Returns a status string (also stashed in _preloadDiagnostic) so
        // the options menu can surface the real reason audio is dead —
        // "DLL not found in <path>", "LoadLibrary failed: 0x...", etc.
        //
        // We try a small set of likely directories rather than just the
        // assembly's CodeBase, because VSIX deployment can take a few
        // forms:
        //   1. Direct VS run (F5 + experimental hive): assembly loads from
        //      the Extensions\<Random>\ folder; Native\openal32.dll sits
        //      under that same folder.
        //   2. VSIX-installed for the user: same layout but at a different
        //      LocalAppData path.
        //   3. Local bin\Debug build with a shadow-copied assembly: the
        //      shadow copy has no Native\ subfolder, so we fall back to
        //      Assembly.CodeBase (the original DLL location).
        // Plus a flat-sibling fallback in case packaging ever changes.
        private static void TryPreloadOpenAL()
        {
            var attempts = new System.Collections.Generic.List<string>();
            try
            {
                var asm = typeof(AudioEngine).Assembly;
                string locDir = SafeDir(asm.Location);
                string codeDir;
                try
                {
                    codeDir = SafeDir(new Uri(asm.CodeBase).LocalPath);
                }
                catch { codeDir = null; }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                var dirs = new System.Collections.Generic.List<string>();
                AddIfNew(dirs, locDir);
                AddIfNew(dirs, codeDir);
                AddIfNew(dirs, baseDir);

                foreach (string d in dirs)
                {
                    foreach (string rel in new[] { @"Native\openal32.dll", "openal32.dll" })
                    {
                        string p = Path.Combine(d, rel);
                        attempts.Add(p);
                        if (!File.Exists(p)) continue;
                        IntPtr h = LoadLibraryW(p);
                        if (h != IntPtr.Zero)
                        {
                            _preloadDiagnostic = "loaded " + p;
                            return;
                        }
                        int err = Marshal.GetLastWin32Error();
                        _preloadDiagnostic = "LoadLibrary failed (0x" + err.ToString("X") + ") for " + p;
                        return;
                    }
                }
                _preloadDiagnostic = "openal32.dll not found; searched: " + string.Join(" ; ", attempts);
            }
            catch (Exception ex)
            {
                _preloadDiagnostic = "preload threw: " + ex.GetType().Name + " " + ex.Message;
            }
        }

        private static string SafeDir(string p)
        {
            try { return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p); }
            catch { return null; }
        }

        private static void AddIfNew(System.Collections.Generic.List<string> list, string d)
        {
            if (string.IsNullOrEmpty(d)) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], d, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(d);
        }

        // Upload a PCM buffer (mono 16-bit) and return its AL buffer
        // handle. Returns 0 if the engine is muted or if AL rejected the
        // upload — the caller should treat 0 as "no sound" and ignore
        // it (PlayOneShot also ignores 0).
        public static int CreateBuffer(short[] pcm, int sampleRate)
        {
            if (_muted || pcm == null || pcm.Length == 0) return 0;
            try
            {
                int buf = AL.GenBuffer();
                AL.BufferData(buf, ALFormat.Mono16, pcm, pcm.Length * sizeof(short), sampleRate);
                return buf;
            }
            catch
            {
                return 0;
            }
        }

        // Play a pre-built buffer once. gain is 0..1 (multiplied by
        // MasterGain); pitch is a multiplier on the buffer's natural
        // playback rate (1.0 = neutral, 0.5 = octave down, 2.0 = octave up).
        public static void PlayOneShot(int buffer, float gain = 1f, float pitch = 1f)
        {
            if (_muted || buffer == 0) return;
            if (gain < 0f) gain = 0f;
            if (pitch < 0.5f) pitch = 0.5f;
            else if (pitch > 2.0f) pitch = 2.0f;
            try
            {
                int src = AcquireSource();
                AL.Source(src, ALSourcei.Buffer, buffer);
                AL.Source(src, ALSourcef.Gain, gain * _masterGain);
                AL.Source(src, ALSourcef.Pitch, pitch);
                AL.SourcePlay(src);
            }
            catch
            {
                // Source state can drift if the AL context is lost. Don't
                // crash the render thread over it.
            }
        }

        // Tier 4 #25 — Start streaming a music buffer on the dedicated
        // music source. Stops any music currently playing first so the
        // caller doesn't have to bracket the call with StopMusic — a
        // jukebox eject-then-insert sequence simply calls PlayMusic
        // again with the new buffer.
        //
        // buffer==0 is treated as "no audio asset for this disc" (see
        // class doc) and is a soft no-op rather than an error. The
        // jukebox interact path will still flip the entity's Disc field
        // and consume the disc from the player's hand — only the audio
        // is silent. This is intentional: V1 ships disc data flow +
        // structured PlayMusic API + null-safe stub buffers, and real
        // disc audio (PCM-uploaded WAV / OGG) is a roadmap-deferred
        // polish item until disc-asset wrangling is in scope.
        public static void PlayMusic(int buffer, float gain = 1f)
        {
            if (_muted || !_musicSourceCreated || buffer == 0) return;
            if (gain < 0f) gain = 0f;
            try
            {
                AL.SourceStop(_musicSource);
                AL.Source(_musicSource, ALSourcei.Buffer, buffer);
                AL.Source(_musicSource, ALSourcef.Gain, gain * _musicGain * _masterGain);
                AL.Source(_musicSource, ALSourcef.Pitch, 1f);
                AL.SourcePlay(_musicSource);
            }
            catch
            {
                // Source state can drift if the AL context is lost. Don't
                // crash the render thread over it — same defensive
                // pattern as PlayOneShot.
            }
        }

        // Tier 4 #25 — Stop the music source. Idempotent (a Stop on an
        // already-stopped source is a no-op in OpenAL). Called from the
        // jukebox eject path and on world unload. Also tears down the
        // MCI file-stream path (Tier 10 #51) so callers don't have to
        // know which playback mode is active.
        public static void StopMusic()
        {
            if (_musicSourceCreated)
            {
                try { AL.SourceStop(_musicSource); } catch { }
            }
            StopMusicFile();
        }

        // ---- Tier 10 #51 — Music file playback (MCI) -------------------
        // OpenAL needs raw PCM to play a buffer, and decoding MP3 / Vorbis
        // would mean adding a dependency. The Windows Media Control
        // Interface (`mciSendString`) is built into Windows since
        // Win2K, plays MP3 files directly via the system codec, and
        // doesn't need a window handle for audio-only playback. So we
        // route disc music through MCI on a single named alias and
        // expose the same Play/Stop verbs the OpenAL music path uses.
        // The OpenAL music source still exists for any future PCM
        // music; the two paths coexist without conflict because
        // StopMusic stops both.

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, System.Text.StringBuilder buffer, int bufferSize, IntPtr hwndCallback);

        // The currently-open MCI alias. "vsccmusic" is unique enough to
        // avoid colliding with any other MCI-using process. Tracked so
        // we know whether to issue a `close` before opening a new file
        // (consecutive PlayMusicFile calls without a StopMusicFile
        // between them).
        private const string McMusicAlias = "vsccmusic";
        private static bool _mciMusicOpen;
        private static string _mciCurrentPath;

        // Best-effort path lookup helper. Looks for the music file in
        // the executable's directory (and a `Music` subfolder), then in
        // the user's Downloads. Returns null if no candidate exists.
        // Caller is expected to handle null silently — the disc still
        // inserts/ejects normally with no audio.
        public static string FindMusicAsset(string fileNameWithoutExt)
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
                string user   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;
                string[] exts = { ".mp3", ".ogg", ".wav", ".m4a", ".wma" };
                string[] roots =
                {
                    Path.Combine(exeDir, "Music"),
                    exeDir,
                    Path.Combine(user, "Downloads"),
                };
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    foreach (var ext in exts)
                    {
                        string candidate = Path.Combine(root, fileNameWithoutExt + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }
            return null;
        }

        // Play a music file via MCI. Stops any in-flight music first
        // (whether MCI- or OpenAL-sourced) so the caller doesn't have
        // to bracket the call. Path can be MP3 / WAV / OGG / etc. —
        // anything Windows has a codec for. A null or missing file is
        // a soft no-op so the disc still inserts cleanly.
        public static void PlayMusicFile(string path, float gain = 1f)
        {
            if (_muted) return;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            // Stop OpenAL music if any (the two music paths share the
            // jukebox's "currently playing" semantics).
            if (_musicSourceCreated)
            {
                try { AL.SourceStop(_musicSource); } catch { }
            }
            StopMusicFile();
            try
            {
                // MCI can't take certain characters in unquoted paths
                // (spaces, parentheses) — wrap the path in double quotes.
                string openCmd = "open \"" + path + "\" type mpegvideo alias " + McMusicAlias;
                int rc = mciSendString(openCmd, null, 0, IntPtr.Zero);
                if (rc != 0)
                {
                    // Fall back to letting MCI infer the type — works
                    // for WAV / OGG without the explicit type alias.
                    openCmd = "open \"" + path + "\" alias " + McMusicAlias;
                    rc = mciSendString(openCmd, null, 0, IntPtr.Zero);
                }
                if (rc != 0) return;
                _mciMusicOpen = true;
                _mciCurrentPath = path;
                if (gain < 0f) gain = 0f;
                if (gain > 1f) gain = 1f;
                int volume = (int)(gain * _musicGain * _masterGain * 1000f);
                if (volume < 0)    volume = 0;
                if (volume > 1000) volume = 1000;
                mciSendString("setaudio " + McMusicAlias + " volume to " + volume, null, 0, IntPtr.Zero);
                mciSendString("play " + McMusicAlias, null, 0, IntPtr.Zero);
            }
            catch { }
        }

        // Stop and close the MCI alias. Idempotent.
        public static void StopMusicFile()
        {
            if (!_mciMusicOpen) return;
            try { mciSendString("stop " + McMusicAlias, null, 0, IntPtr.Zero); } catch { }
            try { mciSendString("close " + McMusicAlias, null, 0, IntPtr.Zero); } catch { }
            _mciMusicOpen = false;
            _mciCurrentPath = null;
        }

        // Apply the live music volume (master × music slider) to the
        // currently-playing MCI stream. Called by the options-menu
        // sliders so dragging the music slider takes effect mid-track
        // instead of waiting for the next disc swap.
        public static void RefreshMusicFileVolume()
        {
            if (!_mciMusicOpen) return;
            try
            {
                int volume = (int)(_musicGain * _masterGain * 1000f);
                if (volume < 0)    volume = 0;
                if (volume > 1000) volume = 1000;
                mciSendString("setaudio " + McMusicAlias + " volume to " + volume, null, 0, IntPtr.Zero);
            }
            catch { }
        }

        // Tier 4 #25 — True while the music source has a buffer that's
        // actively playing. Useful for the renderer / future UI to
        // know whether a "now playing" indicator should show.
        public static bool IsMusicPlaying
        {
            get
            {
                if (_muted || !_musicSourceCreated) return false;
                try
                {
                    AL.GetSource(_musicSource, ALGetSourcei.SourceState, out int state);
                    return state == (int)ALSourceState.Playing;
                }
                catch { return false; }
            }
        }

        // Find a source not currently playing. Falls back to evicting the
        // oldest (round-robin via _evictCursor) when the whole pool is
        // busy — caller's sound becomes the freshest one and the displaced
        // one cuts off. With SourcePoolSize=16 that's vanishingly rare.
        private static int AcquireSource()
        {
            for (int i = 0; i < SourcePoolSize; i++)
            {
                int s = _sources[i];
                AL.GetSource(s, ALGetSourcei.SourceState, out int state);
                if (state == (int)ALSourceState.Stopped || state == (int)ALSourceState.Initial)
                {
                    _evictCursor = (i + 1) % SourcePoolSize;
                    return s;
                }
            }
            int evict = _sources[_evictCursor];
            _evictCursor = (_evictCursor + 1) % SourcePoolSize;
            AL.SourceStop(evict);
            return evict;
        }

        // Tear down. Idempotent; safe to call from a finally block. Once
        // shut down, Initialize() can be called again to bring the engine
        // back (e.g. between worlds) — we reset _initTried so the next
        // call retries device acquisition from scratch.
        public static void Shutdown()
        {
            // Tier 10 #51 — Close any in-flight MCI music stream before
            // tearing down the AL context. MCI playback is independent
            // of OpenAL but lives on the same "music" semantic, so it
            // gets cleaned up alongside the rest of the audio engine.
            StopMusicFile();
            if (_context == null && !_initTried) return;
            try
            {
                for (int i = 0; i < SourcePoolSize; i++)
                {
                    if (_sources[i] != 0)
                    {
                        try { AL.SourceStop(_sources[i]); } catch { }
                        try { AL.DeleteSource(_sources[i]); } catch { }
                        _sources[i] = 0;
                    }
                }
                // Tier 4 #25 — Tear down the music source alongside the
                // SFX pool. Same defensive try/catch wrapping — a lost
                // AL context shouldn't take down the renderer.
                if (_musicSourceCreated)
                {
                    try { AL.SourceStop(_musicSource); } catch { }
                    try { AL.DeleteSource(_musicSource); } catch { }
                    _musicSource = 0;
                    _musicSourceCreated = false;
                }
                _context?.Dispose();
            }
            catch { }
            _context = null;
            _muted = true;
            _initTried = false;
        }
    }
}
