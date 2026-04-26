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

        public static bool IsAvailable => _initTried && !_muted;
        public static string InitFailureReason => _initFailureReason;

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
            set => _musicGain = value < 0f ? 0f : (value > 1f ? 1f : value);
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
                _muted = false;
                _initFailureReason = null;
            }
            catch (Exception ex)
            {
                // OpenAL not available on this host — likely missing
                // openal32.dll. Fall silent; the rest of the engine
                // doesn't need to know. Reason is exposed for the
                // options menu so the user can see why audio is dead.
                _muted = true;
                _initFailureReason = ex.Message;
                try { _context?.Dispose(); } catch { }
                _context = null;
            }
        }

        // Locate openal32.dll next to this assembly and LoadLibrary it.
        // Returns silently on failure — caller will see the AudioContext
        // ctor throw a moment later, which produces a more useful message.
        //
        // We check `Native\openal32.dll` first (where the csproj Content
        // entry deploys it — preserves the source folder structure under
        // `bin\Debug\` and inside the VSIX) then fall back to a flat
        // sibling, in case a future packaging change drops the subfolder.
        private static void TryPreloadOpenAL()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(
                    new Uri(typeof(AudioEngine).Assembly.CodeBase).LocalPath);
                if (string.IsNullOrEmpty(asmDir)) return;
                string[] candidates =
                {
                    Path.Combine(asmDir, "Native", "openal32.dll"),
                    Path.Combine(asmDir, "openal32.dll"),
                };
                foreach (string p in candidates)
                {
                    if (File.Exists(p))
                    {
                        LoadLibraryW(p);
                        return;
                    }
                }
            }
            catch
            {
                // Ignore — best-effort preload.
            }
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
                _context?.Dispose();
            }
            catch { }
            _context = null;
            _muted = true;
            _initTried = false;
        }
    }
}
