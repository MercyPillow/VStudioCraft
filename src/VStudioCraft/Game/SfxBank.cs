using System;

namespace VStudioCraft.Game
{
    // Procedurally-synthesized SFX. We don't ship audio assets — every
    // sample here is generated at startup from oscillators + filtered
    // noise + envelopes, exactly the same approach BlockTextures /
    // HotbarTextures take for graphics. Same upside (no embedded blobs,
    // no asset pipeline), same downside (sounds are only as good as our
    // synthesis is).
    //
    // All samples are mono 16-bit PCM at 22050 Hz. That's audibly fine
    // for short transient SFX (steps, breaks, clicks) and halves memory
    // / upload time vs 44.1 kHz. Each Sfx slot owns one AL buffer; pitch
    // variation is applied at PlayOneShot time so a single buffer covers
    // many subtly-different plays.
    //
    // The synthesis uses a fixed-seed Random so the bank sounds the same
    // every run. The seed is per-buffer so changes in one synthesizer
    // don't ripple into the others and force the player to relearn the
    // soundscape.
    internal static class SfxBank
    {
        private const int SampleRate = 22050;
        private const float TwoPi = (float)(2.0 * Math.PI);

        // ---- Sfx slots ----------------------------------------------------
        // One buffer per material per category, plus a handful of
        // category-less effects. The Sfx enum is the indexing key into
        // _buffers; PlayBreak/PlayPlace/PlayStep map (BlockMaterial -> Sfx).
        public enum Sfx
        {
            BreakStone, BreakWood, BreakDirt, BreakSand, BreakGlass, BreakCloth, BreakLeaves,
            PlaceStone, PlaceWood, PlaceDirt, PlaceSand, PlaceGlass, PlaceCloth, PlaceLeaves,
            StepStone,  StepWood,  StepDirt,  StepSand,  StepGlass,  StepCloth,  StepLeaves,
            UiClick,
            FallThud,
            WaterEnter,
            Pickup,
            COUNT
        }

        private static readonly int[] _buffers = new int[(int)Sfx.COUNT];
        private static bool _initialized;

        // Cheap per-call jitter so repeated plays of the same buffer don't
        // sound mechanical. Not seeded — we want true variation between
        // identical events.
        private static readonly Random _playJitter = new Random();

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // AudioEngine.Initialize is idempotent; safe to call again
            // here so SfxBank can be standalone (synthesise + upload
            // without the caller having to remember the order).
            AudioEngine.Initialize();
            if (!AudioEngine.IsAvailable) return; // silent fallback

            _buffers[(int)Sfx.BreakStone]  = Up(SynthBreakStone(seed: 1001));
            _buffers[(int)Sfx.BreakWood]   = Up(SynthBreakWood(seed: 1002));
            _buffers[(int)Sfx.BreakDirt]   = Up(SynthBreakDirt(seed: 1003));
            _buffers[(int)Sfx.BreakSand]   = Up(SynthBreakSand(seed: 1004));
            _buffers[(int)Sfx.BreakGlass]  = Up(SynthBreakGlass(seed: 1005));
            _buffers[(int)Sfx.BreakCloth]  = Up(SynthBreakCloth(seed: 1006));
            _buffers[(int)Sfx.BreakLeaves] = Up(SynthBreakLeaves(seed: 1007));

            _buffers[(int)Sfx.PlaceStone]  = Up(SynthPlaceStone(seed: 2001));
            _buffers[(int)Sfx.PlaceWood]   = Up(SynthPlaceWood(seed: 2002));
            _buffers[(int)Sfx.PlaceDirt]   = Up(SynthPlaceDirt(seed: 2003));
            _buffers[(int)Sfx.PlaceSand]   = Up(SynthPlaceSand(seed: 2004));
            _buffers[(int)Sfx.PlaceGlass]  = Up(SynthPlaceGlass(seed: 2005));
            _buffers[(int)Sfx.PlaceCloth]  = Up(SynthPlaceCloth(seed: 2006));
            _buffers[(int)Sfx.PlaceLeaves] = Up(SynthPlaceLeaves(seed: 2007));

            _buffers[(int)Sfx.StepStone]   = Up(SynthStepStone(seed: 3001));
            _buffers[(int)Sfx.StepWood]    = Up(SynthStepWood(seed: 3002));
            _buffers[(int)Sfx.StepDirt]    = Up(SynthStepDirt(seed: 3003));
            _buffers[(int)Sfx.StepSand]    = Up(SynthStepSand(seed: 3004));
            _buffers[(int)Sfx.StepGlass]   = Up(SynthStepGlass(seed: 3005));
            _buffers[(int)Sfx.StepCloth]   = Up(SynthStepCloth(seed: 3006));
            _buffers[(int)Sfx.StepLeaves]  = Up(SynthStepLeaves(seed: 3007));

            _buffers[(int)Sfx.UiClick]     = Up(SynthUiClick(seed: 4001));
            _buffers[(int)Sfx.FallThud]    = Up(SynthFallThud(seed: 4002));
            _buffers[(int)Sfx.WaterEnter]  = Up(SynthWaterEnter(seed: 4003));
            _buffers[(int)Sfx.Pickup]      = Up(SynthPickup(seed: 4004));
        }

        private static int Up(short[] pcm) => AudioEngine.CreateBuffer(pcm, SampleRate);

        // ---- Public play helpers -----------------------------------------

        // Pitch-jitter range that feels natural for repeated plays of the
        // same sample. ±6% is roughly a quarter-semitone — not enough to
        // sound "off-pitch", but enough that ten breaks in a row don't
        // feel like a single repeating tape loop.
        private const float DefaultPitchJitter = 0.06f;

        public static void PlayBreak(BlockType t, float gain = 0.7f)
            => PlayMaterial(BreakSlot(BlockMaterialData.For(t)), gain, DefaultPitchJitter);

        public static void PlayPlace(BlockType t, float gain = 0.55f)
            => PlayMaterial(PlaceSlot(BlockMaterialData.For(t)), gain, DefaultPitchJitter);

        public static void PlayStep(BlockType underfoot, float gain = 0.35f)
            => PlayMaterial(StepSlot(BlockMaterialData.For(underfoot)), gain, 0.10f);

        public static void PlayClick(float gain = 0.45f)
            => PlayJittered(Sfx.UiClick, gain, 0.02f);

        // Fall thud volume scales with the impact distance: a 5-block drop
        // is barely audible, a 20-block drop should slam. We accept the
        // raw fall distance and compress to a 0.3..1.0 gain band.
        public static void PlayFall(float fallBlocks, float gain = 0.8f)
        {
            float t = fallBlocks / 12f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float g = gain * (0.3f + 0.7f * t);
            PlayJittered(Sfx.FallThud, g, 0.05f);
        }

        public static void PlayWaterEnter(float gain = 0.55f)
            => PlayJittered(Sfx.WaterEnter, gain, 0.06f);

        public static void PlayPickup(float gain = 0.45f)
            => PlayJittered(Sfx.Pickup, gain, 0.10f);

        // Slot lookups. Returning -1 means "no audio for this material",
        // which PlayMaterial handles by simply not playing anything (the
        // step-on-air case in particular).
        private static int BreakSlot(BlockMaterial m)
        {
            switch (m)
            {
                case BlockMaterial.Stone:  return (int)Sfx.BreakStone;
                case BlockMaterial.Wood:   return (int)Sfx.BreakWood;
                case BlockMaterial.Dirt:   return (int)Sfx.BreakDirt;
                case BlockMaterial.Sand:   return (int)Sfx.BreakSand;
                case BlockMaterial.Glass:  return (int)Sfx.BreakGlass;
                case BlockMaterial.Cloth:  return (int)Sfx.BreakCloth;
                case BlockMaterial.Leaves: return (int)Sfx.BreakLeaves;
                default: return -1;
            }
        }
        private static int PlaceSlot(BlockMaterial m)
        {
            switch (m)
            {
                case BlockMaterial.Stone:  return (int)Sfx.PlaceStone;
                case BlockMaterial.Wood:   return (int)Sfx.PlaceWood;
                case BlockMaterial.Dirt:   return (int)Sfx.PlaceDirt;
                case BlockMaterial.Sand:   return (int)Sfx.PlaceSand;
                case BlockMaterial.Glass:  return (int)Sfx.PlaceGlass;
                case BlockMaterial.Cloth:  return (int)Sfx.PlaceCloth;
                case BlockMaterial.Leaves: return (int)Sfx.PlaceLeaves;
                default: return -1;
            }
        }
        private static int StepSlot(BlockMaterial m)
        {
            switch (m)
            {
                case BlockMaterial.Stone:  return (int)Sfx.StepStone;
                case BlockMaterial.Wood:   return (int)Sfx.StepWood;
                case BlockMaterial.Dirt:   return (int)Sfx.StepDirt;
                case BlockMaterial.Sand:   return (int)Sfx.StepSand;
                case BlockMaterial.Glass:  return (int)Sfx.StepGlass;
                case BlockMaterial.Cloth:  return (int)Sfx.StepCloth;
                case BlockMaterial.Leaves: return (int)Sfx.StepLeaves;
                default: return -1;
            }
        }

        private static void PlayMaterial(int slot, float gain, float pitchJitter)
        {
            if (slot < 0) return;
            PlayJittered((Sfx)slot, gain, pitchJitter);
        }

        private static void PlayJittered(Sfx s, float gain, float pitchJitter)
        {
            int buf = _buffers[(int)s];
            if (buf == 0) return;
            float pitch = 1f;
            if (pitchJitter > 0f)
            {
                // Uniform [-pitchJitter, +pitchJitter] around 1.0
                pitch = 1f + ((float)_playJitter.NextDouble() * 2f - 1f) * pitchJitter;
            }
            AudioEngine.PlayOneShot(buf, gain, pitch);
        }

        // ---- DSP primitives ----------------------------------------------

        // One-pole low-pass IIR. cutoffHz is the -3dB corner. Returns a
        // function-like struct via mutable state — call Step(x) per sample.
        private struct LowPass
        {
            private readonly float _a;
            private float _y;
            public LowPass(float cutoffHz)
            {
                // RC-equivalent coefficient. For a one-pole LP, a = dt / (RC + dt)
                // where dt = 1/SampleRate and RC = 1 / (2π * cutoff).
                float dt = 1f / SampleRate;
                float rc = 1f / (TwoPi * cutoffHz);
                _a = dt / (rc + dt);
                _y = 0f;
            }
            public float Step(float x)
            {
                _y += _a * (x - _y);
                return _y;
            }
        }

        // One-pole high-pass IIR derived from the same cutoff. y' = a*(y + x - x_prev).
        private struct HighPass
        {
            private readonly float _a;
            private float _y, _xPrev;
            public HighPass(float cutoffHz)
            {
                float dt = 1f / SampleRate;
                float rc = 1f / (TwoPi * cutoffHz);
                _a = rc / (rc + dt);
                _y = 0f; _xPrev = 0f;
            }
            public float Step(float x)
            {
                _y = _a * (_y + x - _xPrev);
                _xPrev = x;
                return _y;
            }
        }

        // Band-pass = LP after HP. Cheap and good enough for SFX colouring.
        private struct BandPass
        {
            private HighPass _hp;
            private LowPass _lp;
            public BandPass(float lowCutHz, float highCutHz)
            {
                _hp = new HighPass(lowCutHz);
                _lp = new LowPass(highCutHz);
            }
            public float Step(float x) => _lp.Step(_hp.Step(x));
        }

        // Exponential decay envelope. Returns 1.0 at t=0, decaying to ~0
        // at duration. Steeper k means faster die-off; k=4 puts the
        // envelope at ~2% of peak by `duration`.
        private static float ExpDecay(float tSec, float durSec, float k = 4f)
        {
            if (tSec >= durSec) return 0f;
            float n = tSec / durSec;
            return (float)Math.Exp(-k * n);
        }

        // Fast linear attack (5–10 ms) joined to an exp decay. Avoids the
        // click that a pure exp-decay envelope produces at sample 0
        // because the very first sample jumps from 0 to peak instantly.
        private static float AttackDecay(float tSec, float attackSec, float decaySec, float k = 4f)
        {
            if (tSec < 0f) return 0f;
            if (tSec < attackSec)
            {
                if (attackSec <= 0f) return 1f;
                return tSec / attackSec;
            }
            return ExpDecay(tSec - attackSec, decaySec, k);
        }

        // Allocate a buffer of the given duration and return (buffer, len).
        private static short[] NewBuffer(float seconds)
        {
            int len = (int)(SampleRate * seconds);
            return new short[len];
        }

        // Convert -1..+1 sample to int16, clipping at ±32767.
        private static short Clip(float x)
        {
            int v = (int)(x * 32760f);
            if (v >  32767) v =  32767;
            if (v < -32768) v = -32768;
            return (short)v;
        }

        // ---- Synthesis: BREAK -------------------------------------------

        // Stone break: short noise burst + low thud transient. The thud
        // gives the "weight"; the noise adds the gritty detail.
        private static short[] SynthBreakStone(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.18f;
            var buf = NewBuffer(dur);
            var hp = new HighPass(800f);
            var lp = new LowPass(2200f);
            float dt = 1f / SampleRate;
            float thudPhase = 0f;
            float thudFreq = 70f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.005f, dur - 0.005f, 5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = lp.Step(hp.Step(noise));
                float thudEnv = ExpDecay(t, 0.06f, 6f);
                thudPhase += TwoPi * thudFreq * dt;
                float thud = (float)Math.Sin(thudPhase) * thudEnv * 0.8f;
                float sample = (coloured * 0.7f + thud * 0.6f) * env;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakWood(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.22f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(300f, 1600f);
            float dt = 1f / SampleRate;
            float toneFreq = 280f, tonePhase = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.005f, dur - 0.005f, 4.5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                tonePhase += TwoPi * toneFreq * dt;
                float tone = (float)Math.Sin(tonePhase) * ExpDecay(t, 0.10f, 5f) * 0.5f;
                float sample = (coloured * 0.6f + tone * 0.4f) * env;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakDirt(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.16f;
            var buf = NewBuffer(dur);
            var lp = new LowPass(900f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.008f, dur - 0.008f, 4.5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = lp.Step(noise);
                float sample = coloured * env * 0.85f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakSand(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.18f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(1000f, 4000f);
            float dt = 1f / SampleRate;
            // Granular jitter: re-seed the noise gain every ~5ms so the
            // sample has a slightly rattly "grains-against-grains" feel
            // instead of a uniform hiss.
            int jitterPeriod = SampleRate / 200; // 5 ms
            float jitter = 1f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                if (i % jitterPeriod == 0) jitter = 0.6f + 0.4f * (float)rng.NextDouble();
                float env = AttackDecay(t, 0.005f, dur - 0.005f, 5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                float sample = coloured * env * jitter * 0.9f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakGlass(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.30f;
            var buf = NewBuffer(dur);
            var hp = new HighPass(2500f);
            float dt = 1f / SampleRate;
            // Three close inharmonic partials make the "tinkle" — real
            // breaking glass spreads energy across many resonances; three
            // is a good cheap approximation.
            float[] freqs = { 2400f, 3700f, 5100f };
            float[] phases = { 0f, 0f, 0f };
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.002f, dur - 0.002f, 6f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = hp.Step(noise) * 0.6f;
                float tones = 0f;
                for (int k = 0; k < freqs.Length; k++)
                {
                    phases[k] += TwoPi * freqs[k] * dt;
                    tones += (float)Math.Sin(phases[k]);
                }
                tones /= freqs.Length;
                tones *= ExpDecay(t, 0.20f, 7f) * 0.5f;
                float sample = (coloured + tones) * env;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakCloth(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.14f;
            var buf = NewBuffer(dur);
            var lp = new LowPass(700f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.010f, dur - 0.010f, 5.5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = lp.Step(noise);
                float sample = coloured * env * 0.7f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthBreakLeaves(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.20f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(1500f, 5000f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.008f, dur - 0.008f, 4f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                float sample = coloured * env * 0.55f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        // ---- Synthesis: PLACE -------------------------------------------
        // Place sounds are softer + shorter cousins of break. Less
        // transient, less high-end, no thud. Same material colour.

        private static short[] SynthPlaceStone(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.12f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(400f, 1500f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.008f, dur - 0.008f, 5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = bp.Step(noise) * env * 0.7f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceWood(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.14f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(250f, 1000f);
            float dt = 1f / SampleRate;
            float tonePhase = 0f, toneFreq = 220f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.010f, dur - 0.010f, 4f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                tonePhase += TwoPi * toneFreq * dt;
                float tone = (float)Math.Sin(tonePhase) * ExpDecay(t, 0.08f, 5f) * 0.4f;
                float sample = (coloured * 0.6f + tone) * env;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceDirt(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.10f;
            var buf = NewBuffer(dur);
            var lp = new LowPass(700f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.012f, dur - 0.012f, 4f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = lp.Step(noise) * env * 0.7f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceSand(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.12f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(800f, 3000f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.008f, dur - 0.008f, 4.5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = bp.Step(noise) * env * 0.7f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceGlass(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.16f;
            var buf = NewBuffer(dur);
            var hp = new HighPass(2000f);
            float dt = 1f / SampleRate;
            float tonePhase = 0f, toneFreq = 2200f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.003f, dur - 0.003f, 5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = hp.Step(noise) * 0.5f;
                tonePhase += TwoPi * toneFreq * dt;
                float tone = (float)Math.Sin(tonePhase) * ExpDecay(t, 0.10f, 6f) * 0.4f;
                float sample = (coloured + tone) * env;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceCloth(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.08f;
            var buf = NewBuffer(dur);
            var lp = new LowPass(500f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.012f, dur - 0.012f, 5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = lp.Step(noise) * env * 0.55f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthPlaceLeaves(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.12f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(1200f, 4500f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.008f, dur - 0.008f, 4f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = bp.Step(noise) * env * 0.45f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        // ---- Synthesis: STEP --------------------------------------------
        // Steps are short, soft, and material-tinted. Two close-spaced
        // micro-bursts ("heel + toe") at the start sell the footstep
        // illusion better than a single uniform pulse.

        private static short[] SynthStep(int seed, float dur, float bandLo, float bandHi, float gainBias, bool hasThud)
        {
            var rng = new Random(seed);
            var buf = NewBuffer(dur);
            var bp = new BandPass(bandLo, bandHi);
            float dt = 1f / SampleRate;
            float thudPhase = 0f, thudFreq = 90f;
            // Heel-toe envelope: two attack peaks 18ms apart, with the
            // toe peak slightly softer.
            float heelPeak = 0.005f, toePeak = 0.023f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float heel = (float)Math.Exp(-((t - heelPeak) * (t - heelPeak)) / (2f * 0.005f * 0.005f));
                float toe  = 0.65f * (float)Math.Exp(-((t - toePeak)  * (t - toePeak))  / (2f * 0.006f * 0.006f));
                float env = heel + toe;
                if (env > 1f) env = 1f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                float sample = coloured * env * gainBias;
                if (hasThud)
                {
                    thudPhase += TwoPi * thudFreq * dt;
                    sample += (float)Math.Sin(thudPhase) * ExpDecay(t, 0.04f, 8f) * 0.35f;
                }
                buf[i] = Clip(sample);
            }
            return buf;
        }

        private static short[] SynthStepStone(int seed)  => SynthStep(seed, 0.10f,  600f, 2200f, 0.50f, true);
        private static short[] SynthStepWood(int seed)   => SynthStep(seed, 0.10f,  300f, 1500f, 0.55f, true);
        private static short[] SynthStepDirt(int seed)   => SynthStep(seed, 0.09f,  150f,  900f, 0.55f, false);
        private static short[] SynthStepSand(int seed)   => SynthStep(seed, 0.09f,  800f, 3000f, 0.45f, false);
        private static short[] SynthStepGlass(int seed)  => SynthStep(seed, 0.08f, 1500f, 4000f, 0.40f, false);
        private static short[] SynthStepCloth(int seed)  => SynthStep(seed, 0.08f,  200f,  700f, 0.40f, false);
        private static short[] SynthStepLeaves(int seed) => SynthStep(seed, 0.09f, 1200f, 4500f, 0.38f, false);

        // ---- Synthesis: misc --------------------------------------------

        // UI click — extremely short HP-noise transient. The brief duration
        // is what makes it feel "click" rather than "thud".
        private static short[] SynthUiClick(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.02f;
            var buf = NewBuffer(dur);
            var hp = new HighPass(1500f);
            float dt = 1f / SampleRate;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.0008f, dur - 0.0008f, 8f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float sample = hp.Step(noise) * env * 0.85f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        // Fall thud — low sine + noise transient. Want it to register as
        // a body impact, so the noise has a quick attack and the sine
        // sustains a touch longer.
        private static short[] SynthFallThud(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.30f;
            var buf = NewBuffer(dur);
            var lp = new LowPass(400f);
            float dt = 1f / SampleRate;
            float bodyFreq = 60f, bodyPhase = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float impact = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = lp.Step(impact);
                float impactEnv = AttackDecay(t, 0.005f, 0.08f, 5f);
                bodyPhase += TwoPi * bodyFreq * dt;
                float body = (float)Math.Sin(bodyPhase) * ExpDecay(t, 0.25f, 4.5f);
                float sample = coloured * impactEnv * 0.7f + body * 0.6f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        // Water enter / splash — band-passed noise with an 8 Hz amplitude
        // warble. The warble is what reads as "wet" — without it the
        // sample sounds like static.
        private static short[] SynthWaterEnter(int seed)
        {
            var rng = new Random(seed);
            float dur = 0.45f;
            var buf = NewBuffer(dur);
            var bp = new BandPass(300f, 2500f);
            float dt = 1f / SampleRate;
            float lfoFreq = 8f, lfoPhase = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.005f, dur - 0.005f, 3.5f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float coloured = bp.Step(noise);
                lfoPhase += TwoPi * lfoFreq * dt;
                float warble = 0.7f + 0.3f * (float)Math.Sin(lfoPhase);
                float sample = coloured * env * warble * 0.85f;
                buf[i] = Clip(sample);
            }
            return buf;
        }

        // Pickup — short rising sine chirp 600→900 Hz, very fast decay.
        // The rising glide is what telegraphs "got it" rather than just
        // "something happened".
        private static short[] SynthPickup(int seed)
        {
            float dur = 0.10f;
            var buf = NewBuffer(dur);
            float dt = 1f / SampleRate;
            float startFreq = 600f, endFreq = 950f;
            float phase = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i * dt;
                float env = AttackDecay(t, 0.003f, dur - 0.003f, 4f);
                float u = t / dur;
                float freq = startFreq + (endFreq - startFreq) * u;
                phase += TwoPi * freq * dt;
                float sample = (float)Math.Sin(phase) * env * 0.5f;
                buf[i] = Clip(sample);
            }
            return buf;
        }
    }
}
