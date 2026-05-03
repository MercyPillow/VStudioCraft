using System;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Tier 7 #41 — Weather: rain / snow particles + lightning flashes.
    //
    // Two modes alternating on a timer: Clear (default) and Raining.
    // The cycle alternates every WeatherCycleSeconds so worlds get a
    // mix of dry and wet stretches without any persistent weather
    // state (no save-format change). Per-biome behaviour: Plains /
    // Forest get rain; Snow gets snow flakes; Desert never sees
    // precipitation regardless of the global state.
    //
    // Particle pool is allocated once and recycled — falling
    // particles wrap to the top when they pass below the player.
    // The cylinder is centred on the camera and re-anchored each
    // frame so walking doesn't outrun the rain.
    //
    // Lightning fires randomly while raining: brief sky-brightness
    // boost lasting LightningFlashSeconds, with average frequency
    // ~1 strike every LightningInterval seconds.
    internal sealed class WeatherSystem : IDisposable
    {
        public enum Mode { Clear, Raining }

        // Cycle length of the global on/off oscillator. 240 s = 4
        // real-time minutes, ~half an in-game day each side.
        private const float WeatherCycleSeconds = 240f;

        // Per-particle constants. Rain falls fast, snow slow.
        private const int    ParticleCount   = 600;
        private const float  CylinderRadius  = 14f;
        private const float  CylinderHeight  = 18f;   // -8..+10 around player
        private const float  RainFallSpeed   = 22f;   // m/s
        private const float  SnowFallSpeed   = 3.5f;  // m/s
        private const float  WindX           = 1.5f;  // light horizontal drift

        // Lightning timing. Avg one flash per ~LightningInterval s.
        private const float  LightningInterval     = 30f;
        private const float  LightningFlashSeconds = 0.18f;

        public Mode Current { get; private set; } = Mode.Clear;

        // Per-frame brightness multiplier exposed to the chunk-light
        // pass. 1.0 = no flash, ~2.0 during a strike.
        public float LightningBoost { get; private set; } = 1f;

        private float _modeTimer;
        private float _flashTimer;          // counts down each strike
        private float _nextStrikeTimer;     // counts down to next strike (Poisson-ish)
        private readonly Random _rng = new Random(0xC107D);

        // Per-particle state. Layout: world position + per-particle
        // randomised X/Z offset for natural drift jitter.
        private struct Particle
        {
            public Vector3 Pos;
            public float JitterPhase;       // for slight per-flake oscillation
        }
        private Particle[] _particles;

        // Render mesh — points in world space, drawn with a dedicated
        // shader. The vertex buffer is updated each frame by uploading
        // the particle positions raw (3 floats per vertex).
        private int _vao, _vbo;
        private float[] _vertScratch;
        private Shader _shader;

        // Vertex shader: passes through world pos and computes a per-
        // vertex screen-space size (point size) scaling with distance.
        private const string ParticleVS = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uViewProj;
uniform float uPointSize;
void main()
{
    gl_Position = uViewProj * vec4(aPos, 1.0);
    gl_PointSize = uPointSize;
}
";
        private const string ParticleFS = @"#version 330 core
out vec4 FragColor;
uniform vec4 uColor;
void main()
{
    // Anti-alias the point so streaks/flakes have soft edges.
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = length(d);
    if (r > 0.5) discard;
    FragColor = uColor;
}
";

        public void Initialize()
        {
            _particles = new Particle[ParticleCount];
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i] = new Particle
                {
                    Pos = RandomCylinderPoint(Vector3.Zero),
                    JitterPhase = (float)_rng.NextDouble() * (float)Math.PI * 2f,
                };
            }
            _vertScratch = new float[ParticleCount * 3];
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertScratch.Length * sizeof(float),
                _vertScratch, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
            _shader = new Shader(ParticleVS, ParticleFS);
            // Enable point-size in vertex shader (GL3.2+).
            GL.Enable(EnableCap.ProgramPointSize);
        }

        public void Update(float dt, Vector3 cameraPos)
        {
            // Cycle the global mode — half cycle clear, half raining.
            _modeTimer += dt;
            if (_modeTimer > WeatherCycleSeconds) _modeTimer -= WeatherCycleSeconds;
            Current = _modeTimer > WeatherCycleSeconds * 0.5f ? Mode.Raining : Mode.Clear;

            if (Current != Mode.Raining)
            {
                // No precipitation, no flashes. Reset boost cleanly.
                LightningBoost = 1f;
                return;
            }

            // Advance particle positions. Wrap when below the player.
            float fallSpeed = SnowOrRainFallSpeed();
            for (int i = 0; i < _particles.Length; i++)
            {
                var p = _particles[i];
                p.Pos.Y -= fallSpeed * dt;
                p.Pos.X += WindX * dt;
                if (p.Pos.Y < cameraPos.Y - 8f
                    || Math.Abs(p.Pos.X - cameraPos.X) > CylinderRadius * 1.5f
                    || Math.Abs(p.Pos.Z - cameraPos.Z) > CylinderRadius * 1.5f)
                {
                    p.Pos = RandomCylinderPoint(cameraPos);
                    p.Pos.Y = cameraPos.Y + (float)_rng.NextDouble() * 6f + 6f; // top portion
                }
                _particles[i] = p;
            }

            // Lightning strikes — Poisson-ish: countdown timer with
            // exponential-ish reset on fire.
            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f) { _flashTimer = 0f; LightningBoost = 1f; }
            }
            _nextStrikeTimer -= dt;
            if (_nextStrikeTimer <= 0f)
            {
                _flashTimer = LightningFlashSeconds;
                LightningBoost = 2.0f;
                // Reset interval with ±50 % jitter so strikes don't
                // look perfectly periodic.
                _nextStrikeTimer = LightningInterval * (0.5f + (float)_rng.NextDouble());
            }
        }

        // Snow vs rain decision based on the player's current biome.
        // Defaults to rain (the typical case).
        private static Biome _lastBiome = Biome.Plains;
        public void SetPlayerBiome(Biome b) { _lastBiome = b; }

        private float SnowOrRainFallSpeed()
            => _lastBiome == Biome.Snow ? SnowFallSpeed : RainFallSpeed;

        // Whether precipitation is rendering at the player's column.
        // Desert is a permanent dry biome; even in Raining mode, no
        // particles are drawn there.
        public bool IsPrecipitatingForBiome(Biome b)
            => Current == Mode.Raining && b != Biome.Desert;

        public void Render(Matrix4 viewProj, Vector3 cameraPos, Biome playerBiome)
        {
            if (!IsPrecipitatingForBiome(playerBiome)) return;

            // Pack particles into the VBO — only x/y/z per vertex.
            for (int i = 0; i < _particles.Length; i++)
            {
                int o = i * 3;
                _vertScratch[o + 0] = _particles[i].Pos.X;
                _vertScratch[o + 1] = _particles[i].Pos.Y;
                _vertScratch[o + 2] = _particles[i].Pos.Z;
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                _vertScratch.Length * sizeof(float), _vertScratch);

            _shader.Use();
            _shader.SetMatrix4("uViewProj", viewProj);
            // Snow flakes are bigger + opaque-white; rain streaks are
            // narrower + translucent grey-blue.
            bool snow = playerBiome == Biome.Snow;
            _shader.SetFloat("uPointSize", snow ? 4.0f : 2.5f);
            _shader.SetVector4("uColor", snow
                ? new Vector4(0.95f, 0.97f, 1.0f, 0.9f)
                : new Vector4(0.55f, 0.65f, 0.85f, 0.6f));

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Points, 0, ParticleCount);
            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        private Vector3 RandomCylinderPoint(Vector3 centre)
        {
            // Uniform disc + uniform Y in the cylinder.
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double r = Math.Sqrt(_rng.NextDouble()) * CylinderRadius;
            float dy = (float)(_rng.NextDouble() * CylinderHeight - CylinderHeight * 0.4);
            return new Vector3(
                centre.X + (float)(Math.Cos(angle) * r),
                centre.Y + dy,
                centre.Z + (float)(Math.Sin(angle) * r));
        }

        public void Dispose()
        {
            if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
            if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
            _shader?.Dispose();
        }
    }
}
