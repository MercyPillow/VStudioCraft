using System;
using System.Collections.Generic;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    internal sealed class GameRenderer : IDisposable
    {
        private const string VertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in float aLayer;
layout(location = 4) in float aLight;
out vec2 vUV;
out vec3 vNormal;
out float vViewDist;
out float vSkyLight;
out float vBlockLight;
flat out int vLayer;
uniform mat4 uProjection;
uniform mat4 uView;
void main()
{
    vec4 viewPos = uView * vec4(aPos, 1.0);
    gl_Position = uProjection * viewPos;
    vUV = aUV;
    vNormal = aNormal;
    vLayer = int(aLayer);
    // aLight is packed sky*16 + block. Unpack here so the fragment receives
    // separately-interpolated channels: interpolating the packed value would
    // smear sky into block at light boundaries (e.g. cave-mouth seams).
    float skyN   = floor(aLight / 16.0);
    float blockN = aLight - skyN * 16.0;
    vSkyLight   = skyN   / 15.0;
    vBlockLight = blockN / 15.0;
    // View-space -Z is distance into the scene; length(viewPos.xyz) makes
    // horizontal and vertical distance both contribute, so the fog ring
    // reads as a hemisphere around the camera, not just a flat band ahead.
    vViewDist = length(viewPos.xyz);
}
";

        private const string FragmentSrc = @"#version 330 core
in vec2 vUV;
in vec3 vNormal;
in float vViewDist;
in float vSkyLight;
in float vBlockLight;
flat in int vLayer;
out vec4 FragColor;
uniform sampler2DArray uAtlas;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uAmbient;
uniform float uSkyLightLevel;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
void main()
{
    // Greedy quads emit UVs that span the merged area (e.g. 0..w, 0..h); we
    // tile within each array layer by fract()ing. GL_REPEAT on the sampler
    // gives the same result but fract avoids any driver quirks at integer seams.
    vec2 tileUV = fract(vUV);
    vec4 tex = texture(uAtlas, vec3(tileUV, float(vLayer)));
    // Alpha-test for cross-sprite blocks (torches today; flowers/mushrooms
    // later). Torch tile leaves the surrounding texels at alpha=0 so they
    // discard here, leaving only the post and flame visible. Real translucent
    // blocks (water, alpha=160) survive the threshold and are drawn in the
    // separate transparent pass with blending.
    if (tex.a < 0.5) discard;

    // Combine sky + block contributions. uSkyLightLevel scales sky over the
    // day/night cycle (1.0 at noon, ~0.15 at midnight) so caves and night-time
    // both naturally darken to the block-light floor. Block light is warm-tinted
    // so torches/lava read distinct from sky-bright daylight.
    vec3 skyLit   = vSkyLight   * uSkyLightLevel * vec3(1.0, 0.97, 0.92);
    vec3 blockLit = vBlockLight * vec3(1.0, 0.78, 0.45);
    vec3 light = max(skyLit, blockLit);

    // Floor so totally dark areas aren't pure black (matches Alpha's
    // 'minimum brightness' minimum so you can still navigate caves dimly).
    light = max(light, vec3(0.06));

    // Light-coloured face shading: top brighter, bottom darker, sides middling.
    // Approximates Alpha's per-axis fixed shading without the sun-direction
    // dependence (the per-vertex sky term already encodes 'this face sees the sky').
    float dirBias = 0.78;
    if (vNormal.y >  0.5) dirBias = 1.0;
    else if (vNormal.y < -0.5) dirBias = 0.55;
    light *= dirBias;

    // Subtle directional sun warmth on top-facing faces, so dawn/dusk
    // reads as more than just a brightness change.
    float diff = max(dot(normalize(vNormal), normalize(uSunDir)), 0.0);
    light += uSunColor * diff * 0.08 * uSkyLightLevel;

    vec3 lit = tex.rgb * light;
    // Distance fog: blend toward horizon colour at the render edge so chunks
    // fade in/out instead of popping. uFogEnd is tuned to sit just inside the
    // chunk-unload radius so the terminating cliff never reveals itself.
    float fog = clamp((vViewDist - uFogStart) / max(uFogEnd - uFogStart, 0.0001), 0.0, 1.0);
    FragColor = vec4(mix(lit, uFogColor, fog), tex.a);
}
";

        private const string OverlayVertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
";

        private const string OverlayFragmentSrc = @"#version 330 core
out vec4 FragColor;
uniform vec3 uColor;
uniform float uAlpha;
void main() { FragColor = vec4(uColor, uAlpha); }
";

        // HUD sprite shader: samples a 2D texture with a UV sub-rect so one
        // sprite sheet can serve many HUD elements. Reuses the unit-quad mesh
        // (aPos in [0,1]^2 with z=0); the V axis doesn't need flipping because
        // we upload pixels py=0-first and OpenGL treats pixels[0] as the
        // UV=(0,0) texel — matching the block atlas convention in this codebase.
        private const string SpriteVertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
out vec2 vUV;
uniform mat4 uMVP;
uniform vec2 uUvOffset;
uniform vec2 uUvScale;
void main()
{
    gl_Position = uMVP * vec4(aPos, 1.0);
    vUV = uUvOffset + aPos.xy * uUvScale;
}
";

        private const string SpriteFragmentSrc = @"#version 330 core
in vec2 vUV;
out vec4 FragColor;
uniform sampler2D uSprite;
uniform vec4 uTint;
void main()
{
    vec4 t = texture(uSprite, vUV);
    if (t.a < 0.01) discard;
    FragColor = vec4(t.rgb * uTint.rgb, t.a * uTint.a);
}
";

        // Variant of the sprite shader that samples from the block-atlas
        // Texture2DArray. Same vertex shader; the fragment picks a layer
        // from a uniform so a single draw can pick out any tile in the atlas.
        // Used for rendering block icons inside hotbar slots.
        private const string SpriteArrayFragmentSrc = @"#version 330 core
in vec2 vUV;
out vec4 FragColor;
uniform sampler2DArray uAtlas;
uniform float uLayer;
uniform vec4 uTint;
void main()
{
    vec4 t = texture(uAtlas, vec3(vUV, uLayer));
    if (t.a < 0.01) discard;
    // Faux-3D shading: gradient from top-bright to bottom-dim so the flat
    // tile reads as a 'lit' face of a cube even without isometric geometry.
    // Not pixel-perfect Alpha (which renders an actual rotated cube), but
    // a clear visual cue at hotbar size and zero extra geometry.
    // The block atlas convention is v=0 at the BOTTOM of each tile (see
    // GenerateGrassSide — the green overhang lives at high y so it appears
    // along the top of the face). The hotbar render path V-flips the UVs
    // so the icon shows top-up; that means here vUV.y=1 is the top of the
    // icon. Gradient: bright at top (vUV.y=1), dim at bottom (vUV.y=0).
    float shade = mix(0.78, 1.05, vUV.y);
    FragColor = vec4(t.rgb * uTint.rgb * shade, t.a * uTint.a);
}
";

        private const float ReachDistance = 8f;
        public const int ViewDistanceChunks = 6;   // ~13x13 kept loaded around the player
        public const int UnloadDistanceChunks = 9; // 3 chunks of hysteresis beyond view distance
        private const int MaxInstallsPerFrame = 8; // completed-gen drains per frame
        private const int MaxUnloadsPerFrame = 3;
        private const int MaxMeshUploadsPerFrame = 4; // completed-mesh drains per frame

        private const float DayDuration = 300f;         // 5 min
        private const float TransitionDuration = 30f;   // 30 s (dawn and dusk each)
        private const float NightDuration = 60f;        // 1 min
        private const float TotalCycle = DayDuration + TransitionDuration + NightDuration + TransitionDuration;
        private const float DayEndFrac = DayDuration / TotalCycle;
        private const float DuskEndFrac = (DayDuration + TransitionDuration) / TotalCycle;
        private const float NightEndFrac = (DayDuration + TransitionDuration + NightDuration) / TotalCycle;

        private Shader _shader;
        private Shader _overlayShader;
        private Shader _spriteShader;
        private Shader _spriteArrayShader; // sampler2DArray variant for block-atlas icons
        private OverlayMesh _crosshairMesh;
        private OverlayMesh _wireCubeMesh;
        private OverlayMesh _unitQuadMesh; // [0,0]-[1,1] quad; scaled via MVP for full-screen tints + HUD sprites.
        private int _atlasTexture;
        private int _heartTexture;
        private int _drumstickTexture;
        private int _bubbleTexture;
        private int _hotbarBarTexture;
        private int _hotbarHighlightTexture;
        private int _fontTexture;

        // Input state shared with the host. The render thread reads
        // HotbarSlots / HotbarIndex from this every frame to paint the bar.
        // Set once by the host after construction and never reassigned, so
        // no synchronisation is required for the reference itself.
        public InputState Input { get; set; }
        private SkyRenderer _sky;
        private World _world;
        private ChunkJobSystem _jobs;
        private readonly Dictionary<(int x, int z), Mesh> _chunkMeshes = new Dictionary<(int x, int z), Mesh>();
        private bool _initialized;

        private Frustum _frustum;

        // Reusable sort scratch used by ProcessDirtyChunks / streaming so the
        // per-frame hot path doesn't allocate.
        private readonly List<(int x, int z, int distSq)> _scratchChunks = new List<(int, int, int)>(256);
        private static readonly Comparison<(int x, int z, int distSq)> CompareAsc =
            (a, b) => a.distSq.CompareTo(b.distSq);
        private static readonly Comparison<(int x, int z, int distSq)> CompareDesc =
            (a, b) => b.distSq.CompareTo(a.distSq);

        private float _timeOfDay = 0.25f;  // start at noon so first view is bright

        // Where the player snaps back to on death in survival. Set whenever a
        // world is loaded / started; respawn teleports here with full health.
        private Vector3 _spawnPos;

        // Accumulator for void-damage ticks. Ticks the player for a fixed
        // amount every half-second while they are below the kill plane.
        private float _voidTimer;

        // Drowning. Air loses 2 points (one bubble) every AirDecayInterval
        // while the head is submerged — 10 bubbles × 1.5 s = 15 s, matching
        // Alpha's air supply. Once Air hits zero the second timer takes over
        // and applies 2 HP of drowning damage every DrownDamageInterval.
        // Both reset to zero the moment the head emerges, and Air refills
        // instantly (Alpha behaviour — no sip-air-back-up sequence).
        private const float AirDecayInterval = 1.5f;
        private const float DrownDamageInterval = 1f;
        private float _airDecayTimer;
        private float _drownDamageTimer;

        // Fluid tick cadence. Alpha ticked water at 5 game-ticks (~0.25s) and
        // lava at 30 game-ticks (~1.5s). We share one cadence for both at
        // 0.25s and let the per-fluid reach difference (water=7, lava=3) do
        // most of the visual differentiation. The TickFluids call iterates
        // every loaded chunk; on the live workload it's well under a frame's
        // budget and we'll add a "has fluid" gate when it isn't.
        private const float FluidTickInterval = 0.25f;
        private float _fluidTickAccumulator;

        // Survival is opt-in; the existing game loop starts in Creative so we
        // don't break the creative-lite flow everybody already has. Toggled
        // from the UI thread via F3 — the single enum write is atomic on
        // x86/x64, so no lock is needed for cross-thread reads.
        public GameMode GameMode { get; set; } = GameMode.Creative;

        public Camera Camera { get; } = new Camera();
        public Player Player { get; } = new Player();
        public World World => _world;
        public float TimeOfDay
        {
            get => _timeOfDay;
            set => _timeOfDay = ((value % 1f) + 1f) % 1f;
        }

        public void InitializeGraphics()
        {
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.FrontFace(FrontFaceDirection.Ccw);

            _shader = new Shader(VertexSrc, FragmentSrc);
            _overlayShader = new Shader(OverlayVertexSrc, OverlayFragmentSrc);
            _spriteShader = new Shader(SpriteVertexSrc, SpriteFragmentSrc);
            _spriteArrayShader = new Shader(SpriteVertexSrc, SpriteArrayFragmentSrc);
            _crosshairMesh = BuildCrosshairMesh();
            _wireCubeMesh = BuildWireCubeMesh();
            _unitQuadMesh = BuildUnitQuadMesh();
            _atlasTexture = BlockTextures.CreateAtlas();
            _heartTexture = HudTextures.CreateHeartSheet();
            _drumstickTexture = HudTextures.CreateDrumstickSheet();
            _bubbleTexture = HudTextures.CreateBubbleSheet();
            _hotbarBarTexture = HotbarTextures.CreateBarTexture();
            _hotbarHighlightTexture = HotbarTextures.CreateSelectedHighlightTexture();
            _fontTexture = HotbarTextures.CreateFontTexture();
            _sky = new SkyRenderer();
            _sky.Initialize();
            _initialized = true;
        }

        private static OverlayMesh BuildUnitQuadMesh()
        {
            float[] v =
            {
                0, 0, 0,  1, 0, 0,  1, 1, 0,
                0, 0, 0,  1, 1, 0,  0, 1, 0,
            };
            var m = new OverlayMesh { Primitive = PrimitiveType.Triangles };
            m.Upload(v);
            return m;
        }

        private static OverlayMesh BuildCrosshairMesh()
        {
            // Two rectangles (4 tris = 12 verts) centered at origin, in pixel units.
            const float h = 8f;  // half-length of each arm
            const float w = 1f;  // half-thickness
            float[] v =
            {
                -h, -w, 0,  h, -w, 0,  h, w, 0,
                -h, -w, 0,  h,  w, 0, -h, w, 0,
                -w, -h, 0,  w, -h, 0,  w, h, 0,
                -w, -h, 0,  w,  h, 0, -w, h, 0,
            };
            var m = new OverlayMesh { Primitive = PrimitiveType.Triangles };
            m.Upload(v);
            return m;
        }

        private static OverlayMesh BuildWireCubeMesh()
        {
            // Unit cube, slightly inset so it floats just off the block surface.
            const float e = 0.003f;
            float a = -e, b = 1f + e;
            float[] v =
            {
                // bottom square
                a, a, a,  b, a, a,
                b, a, a,  b, a, b,
                b, a, b,  a, a, b,
                a, a, b,  a, a, a,
                // top square
                a, b, a,  b, b, a,
                b, b, a,  b, b, b,
                b, b, b,  a, b, b,
                a, b, b,  a, b, a,
                // verticals
                a, a, a,  a, b, a,
                b, a, a,  b, b, a,
                b, a, b,  b, b, b,
                a, a, b,  a, b, b,
            };
            var m = new OverlayMesh { Primitive = PrimitiveType.Lines };
            m.Upload(v);
            return m;
        }

        public void StartNewWorld(int seed)
        {
            SetWorld(World.Generate(seed));
            // Spawn above origin chunk; gravity drops player onto terrain on the first ticks.
            int spawnY = TerrainGenerator.BaseHeight + TerrainGenerator.HeightAmplitude + 2;
            Player.Position = new Vector3(0.5f, spawnY, 0.5f);
            Player.Velocity = Vector3.Zero;
            Player.OnGround = false;
            Player.HealFull();
            _spawnPos = Player.Position;
            _voidTimer = 0f;
            Camera.Yaw = 0f;
            Camera.Pitch = -0.1f;
            SyncCameraToPlayer();
        }

        public void LoadFromFile(string path)
        {
            var (header, world) = WorldSaveFormat.Load(path);
            SetWorld(world);
            // header.CameraPos is now the saved player feet position (format v2).
            Player.Position = header.CameraPos;
            Player.Velocity = Vector3.Zero;
            Player.OnGround = false;
            // Saved health + mode restore the exact survival state; if the save is
            // pre-v3 the loader fills them with Creative + full health defaults.
            GameMode = header.GameMode;
            Player.Health = header.Health > 0 ? header.Health : Player.MaxHealth;
            Player.LastFallDistance = 0f;
            _spawnPos = Player.Position;
            _voidTimer = 0f;
            Camera.Yaw = header.CameraYaw;
            Camera.Pitch = header.CameraPitch;
            Camera.ClampPitch();
            SyncCameraToPlayer();
        }

        public void SaveToFile(string path)
        {
            if (_world == null) return;
            var header = new WorldSaveFormat.Header
            {
                Seed = _world.Seed,
                CameraPos = Player.Position,
                CameraYaw = Camera.Yaw,
                CameraPitch = Camera.Pitch,
                GameMode = GameMode,
                Health = Player.Health,
            };
            WorldSaveFormat.Save(path, header, _world);
        }

        public void UpdatePlayer(float dt, Vector3 wishHorizVel, bool wantJump)
        {
            if (_world == null) return;
            Player.Update(dt, wishHorizVel, wantJump, _world);
            SyncCameraToPlayer();

            if (GameMode == GameMode.Survival)
            {
                ApplySurvivalDamage(dt);
            }
            else
            {
                // Creative always reads as full health so switching into
                // survival mid-session doesn't drop you to 0 HP from a stale read.
                if (Player.Health != Player.MaxHealth) Player.Health = Player.MaxHealth;
                if (Player.Air    != Player.MaxAir)    Player.Air    = Player.MaxAir;
                _voidTimer = 0f;
                _airDecayTimer = 0f;
                _drownDamageTimer = 0f;
            }

            // Consume any pending fall distance — survival already applied the
            // damage above; creative ignores it. Either way, clear so the next
            // landing starts fresh.
            Player.LastFallDistance = 0f;

            // Fluid tick — every FluidTickInterval seconds drive a single
            // pass of source-driven outflow. The tick itself self-gates on
            // each chunk's HasActiveFluid flag, so a steady-state ocean
            // costs nothing after the first scan. We only re-light chunks
            // whose lava (light-emitting) cells changed; pure water flow is
            // light-transparent and never alters the light field.
            _fluidTickAccumulator += dt;
            while (_fluidTickAccumulator >= FluidTickInterval)
            {
                _fluidTickAccumulator -= FluidTickInterval;
                var result = FluidTick.Tick(_world);
                // Lava emits 15 light, so a single advancing lava cell can
                // brighten adjacent chunks. Relight a 3×3 region around each
                // light-changed chunk so the glow doesn't stop at the border.
                // Region recomputes overlap when nearby chunks both reported
                // changes; the cost is bounded by edits-per-tick which is
                // small in practice (lava only spreads a few cells per tick).
                foreach (var key in result.LightChangedChunks)
                {
                    var touched = LightCalculator.RecomputeRegion(_world, key.x, key.z);
                    foreach (var k in touched) _world.DirtyChunks.Add(k);
                }
                foreach (var key in result.ChangedChunks)
                {
                    _world.DirtyChunks.Add(key);
                }
            }
        }

        // Survival damage sources wired up today: fall damage (Alpha formula
        // `max(0, distance - 3)`) and void damage (4 HP every 0.5 s below
        // y=-16) and drowning (after the 15 s air supply runs out, 2 HP
        // every 1 s). Fire / lava / cactus are deferred — they need
        // block-specific interaction hooks we don't have yet.
        private void ApplySurvivalDamage(float dt)
        {
            if (Player.LastFallDistance > 3f)
            {
                int dmg = (int)Math.Floor(Player.LastFallDistance - 3f);
                if (dmg > 0) Player.TakeDamage(dmg);
            }

            if (Player.Position.Y < -16f)
            {
                _voidTimer += dt;
                while (_voidTimer >= 0.5f)
                {
                    _voidTimer -= 0.5f;
                    Player.TakeDamage(4);
                }
            }
            else
            {
                _voidTimer = 0f;
            }

            // Drowning. WasHeadInWater is refreshed inside Player.Update
            // every tick, so we just consume it here. Air decays in 2-point
            // steps so the bubble row's "10 slots × 2 each" matches the
            // hearts/hunger pattern; once empty, drowning damage kicks in.
            if (Player.WasHeadInWater)
            {
                if (Player.Air > 0)
                {
                    _airDecayTimer += dt;
                    while (_airDecayTimer >= AirDecayInterval && Player.Air > 0)
                    {
                        _airDecayTimer -= AirDecayInterval;
                        Player.Air -= 2;
                        if (Player.Air <= 0)
                        {
                            Player.Air = 0;
                            _drownDamageTimer = 0f;
                        }
                    }
                }
                else
                {
                    _drownDamageTimer += dt;
                    while (_drownDamageTimer >= DrownDamageInterval)
                    {
                        _drownDamageTimer -= DrownDamageInterval;
                        Player.TakeDamage(2);
                    }
                }
            }
            else
            {
                Player.Air = Player.MaxAir;
                _airDecayTimer = 0f;
                _drownDamageTimer = 0f;
            }

            if (Player.IsDead)
            {
                Respawn();
            }
        }

        // Teleport to the remembered spawn and restore full HP. In a future
        // pass we'll add a death screen with a delay + respawn button; for now
        // it's instant so you don't feel stuck if you fall off the world.
        private void Respawn()
        {
            Player.Position = _spawnPos;
            Player.Velocity = Vector3.Zero;
            Player.OnGround = false;
            Player.HealFull();
            _voidTimer = 0f;
            _airDecayTimer = 0f;
            _drownDamageTimer = 0f;
            SyncCameraToPlayer();
        }

        private void SyncCameraToPlayer()
        {
            // Add the swim-bob Y offset to the eye position when submerged.
            // The offset is updated inside Player.Update and eases back to
            // zero on exit, so the camera glides rather than snaps.
            Camera.Position = Player.Position + new Vector3(0f, Player.EyeHeight + Player.SwimBobOffset, 0f);
        }

        private void SetWorld(World world)
        {
            // Tear down the old job system first so outstanding workers finish
            // against the old world and don't try to deliver stale results into
            // the new one.
            _jobs?.Dispose();
            _jobs = null;

            foreach (var m in _chunkMeshes.Values) m.Dispose();
            _chunkMeshes.Clear();

            _world = world;
            _world.MarkAllDirty();

            // 2–3 workers is a sweet spot: enough to keep the render thread fed
            // without oversubscribing against the UI + render threads. Capped so
            // a 32-thread box doesn't spin up a pile of chunk workers we can't
            // feed fast enough to matter.
            int workerCount = Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2));
            _jobs = new ChunkJobSystem(_world, workerCount);
        }

        // Drain completed mesh results — budgeted per frame so we don't spike GL
        // upload time when many workers complete at once. Chunks still in the
        // dirty set are pushed to the job system; successful enqueues clear the
        // dirty bit. If a chunk is re-dirtied while its job is in flight the bit
        // stays, and we'll enqueue a fresh job next frame after the worker clears.
        public void ProcessDirtyChunks(int maxUploadsPerFrame = MaxMeshUploadsPerFrame)
        {
            if (_world == null || _jobs == null) return;

            if (_world.DirtyChunks.Count > 0)
            {
                int pcx = (int)Math.Floor(Camera.Position.X / Chunk.SizeX);
                int pcz = (int)Math.Floor(Camera.Position.Z / Chunk.SizeZ);

                _scratchChunks.Clear();
                foreach (var k in _world.DirtyChunks)
                {
                    int dx = k.x - pcx, dz = k.z - pcz;
                    _scratchChunks.Add((k.x, k.z, dx * dx + dz * dz));
                }
                _scratchChunks.Sort(CompareAsc);

                for (int i = 0; i < _scratchChunks.Count; i++)
                {
                    var key = (_scratchChunks[i].x, _scratchChunks[i].z);
                    if (!_world.HasChunk(key.Item1, key.Item2))
                    {
                        // Chunk disappeared (e.g. unload); drop any stale mesh.
                        if (_chunkMeshes.TryGetValue(key, out var gone))
                        {
                            gone.Dispose();
                            _chunkMeshes.Remove(key);
                        }
                        _world.DirtyChunks.Remove(key);
                        continue;
                    }
                    if (_jobs.TryEnqueueMesh(key.Item1, key.Item2))
                    {
                        _world.DirtyChunks.Remove(key);
                    }
                    // else: already in-flight; keep the dirty bit, retry next frame.
                }
            }

            int applied = 0;
            while (applied < maxUploadsPerFrame && _jobs.TryDequeueMesh(out var r))
            {
                ApplyMeshResult(r);
                applied++;
            }
        }

        private void ApplyMeshResult(ChunkJobSystem.MeshResult r)
        {
            // Chunk may have been unloaded since the worker picked it up.
            if (!_world.HasChunk(r.X, r.Z))
            {
                if (_chunkMeshes.TryGetValue((r.X, r.Z), out var stale))
                {
                    stale.Dispose();
                    _chunkMeshes.Remove((r.X, r.Z));
                }
                return;
            }

            if (r.IndexCount == 0 && r.TIndexCount == 0)
            {
                if (_chunkMeshes.TryGetValue((r.X, r.Z), out var old))
                {
                    old.Dispose();
                    _chunkMeshes.Remove((r.X, r.Z));
                }
                return;
            }

            if (!_chunkMeshes.TryGetValue((r.X, r.Z), out var mesh))
            {
                mesh = new Mesh();
                _chunkMeshes[(r.X, r.Z)] = mesh;
            }
            mesh.Upload(
                r.Verts, r.VertFloatCount, r.Indices, r.IndexCount,
                r.TVerts, r.TVertFloatCount, r.TIndices, r.TIndexCount);
        }

        public void UpdateStreaming()
        {
            if (_world == null || _jobs == null) return;
            int pcx = (int)Math.Floor(Camera.Position.X / Chunk.SizeX);
            int pcz = (int)Math.Floor(Camera.Position.Z / Chunk.SizeZ);

            // Drain completed gen results and install them. Drop any that ended
            // up outside the current unload radius while they were in-flight.
            int installed = 0;
            int unloadR2 = UnloadDistanceChunks * UnloadDistanceChunks;
            while (installed < MaxInstallsPerFrame && _jobs.TryDequeueGen(out var r))
            {
                int dx = r.Chunk.ChunkX - pcx, dz = r.Chunk.ChunkZ - pcz;
                if (dx * dx + dz * dz <= unloadR2)
                {
                    _world.InstallGeneratedChunk(r.Chunk);
                }
                installed++;
            }

            GenerateNearMissing(pcx, pcz);
            UnloadFar(pcx, pcz);
        }

        private void GenerateNearMissing(int pcx, int pcz)
        {
            int r = ViewDistanceChunks;
            _scratchChunks.Clear();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int ds = dx * dx + dz * dz;
                if (ds > r * r) continue;
                int cx = pcx + dx, cz = pcz + dz;
                if (!_world.HasChunk(cx, cz))
                {
                    _scratchChunks.Add((cx, cz, ds));
                }
            }

            if (_scratchChunks.Count == 0) return;
            _scratchChunks.Sort(CompareAsc);
            // Enqueue the whole ring — the job system dedupes already-in-flight
            // keys. Workers pull from the queue in order, so chunks near the
            // camera get generated first.
            for (int i = 0; i < _scratchChunks.Count; i++)
            {
                _jobs.TryEnqueueGen(_scratchChunks[i].x, _scratchChunks[i].z);
            }
        }

        private void UnloadFar(int pcx, int pcz)
        {
            int unloadR2 = UnloadDistanceChunks * UnloadDistanceChunks;
            _scratchChunks.Clear();
            foreach (var chunk in _world.Chunks)
            {
                int dx = chunk.ChunkX - pcx, dz = chunk.ChunkZ - pcz;
                int ds = dx * dx + dz * dz;
                if (ds > unloadR2) _scratchChunks.Add((chunk.ChunkX, chunk.ChunkZ, ds));
            }
            if (_scratchChunks.Count == 0) return;

            _scratchChunks.Sort(CompareDesc);  // farthest first
            int limit = Math.Min(_scratchChunks.Count, MaxUnloadsPerFrame);
            for (int i = 0; i < limit; i++)
            {
                var key = (_scratchChunks[i].x, _scratchChunks[i].z);
                if (_chunkMeshes.TryGetValue(key, out var mesh))
                {
                    mesh.Dispose();
                    _chunkMeshes.Remove(key);
                }
                _world.UnloadChunk(_scratchChunks[i].x, _scratchChunks[i].z);
            }
        }

        public bool TryBreak()
        {
            if (_world == null) return false;
            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit)) return false;
            return _world.SetBlock(hit.X, hit.Y, hit.Z, BlockType.Air);
        }

        public bool TryPlace(BlockType t)
        {
            if (_world == null) return false;
            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit)) return false;
            int px = hit.X + hit.Nx;
            int py = hit.Y + hit.Ny;
            int pz = hit.Z + hit.Nz;
            // Allow placing into Air or Water (water gets replaced, classic
            // Alpha behaviour). Anything else — including torches and other
            // non-cube blocks the raycast can target — blocks the place.
            var existing = _world.GetBlock(px, py, pz);
            if (existing != BlockType.Air && existing != BlockType.Water && existing != BlockType.FlowingWater) return false;
            // Cross-sprite blocks need a solid block beneath them to attach
            // to. Torches: floor-only for now (wall attachment needs block
            // metadata). Flowers/mushrooms/tall grass: same rule.
            if (!BlockData.IsCubeShape(t) && !BlockData.IsSolid(_world.GetBlock(px, py - 1, pz)))
                return false;
            return _world.SetBlock(px, py, pz, t);
        }

        public void OnResize(int width, int height)
        {
            if (!_initialized || width <= 0 || height <= 0) return;
            GL.Viewport(0, 0, width, height);
        }

        public void AdvanceTime(float dt)
        {
            _timeOfDay = (_timeOfDay + dt / TotalCycle) % 1f;
            _sky?.Advance(dt);
        }

        // Piecewise angle so day/night/transitions each get their own share of the cycle.
        // Sun sweeps across the sky during day (stays above horizon), plunges during dusk,
        // rests below during night, and rises back during dawn.
        private float ComputeSunAngle()
        {
            float t = _timeOfDay;
            if (t < DayEndFrac)
            {
                float p = t / DayEndFrac;
                return MathHelper.Pi * (0.1f + 0.8f * p);  // 0.1π → 0.9π (east→west, above horizon)
            }
            if (t < DuskEndFrac)
            {
                float p = (t - DayEndFrac) / (DuskEndFrac - DayEndFrac);
                return MathHelper.Pi * (0.9f + 0.35f * p); // 0.9π → 1.25π (dip below)
            }
            if (t < NightEndFrac)
            {
                float p = (t - DuskEndFrac) / (NightEndFrac - DuskEndFrac);
                return MathHelper.Pi * (1.25f + 0.5f * p); // slow drift under the world
            }
            float q = (t - NightEndFrac) / (1f - NightEndFrac);
            return MathHelper.Pi * (1.75f + 0.35f * q);    // 1.75π → 2.1π (east horizon rise)
        }

        private Vector3 ComputeSunDirection()
        {
            float angle = ComputeSunAngle();
            return Vector3.Normalize(new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0.15f));
        }

        private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return a + (b - a) * t;
        }

        private Vector3 ComputeSkyColor(Vector3 sun)
        {
            float h = sun.Y;
            var day   = new Vector3(0.52f, 0.80f, 0.92f);
            var dusk  = new Vector3(0.95f, 0.45f, 0.25f);
            var night = new Vector3(0.02f, 0.04f, 0.10f);

            if (h >= 0.2f) return day;
            if (h >= 0f)   return Lerp(dusk, day, h / 0.2f);
            if (h >= -0.2f) return Lerp(night, dusk, (h + 0.2f) / 0.2f);
            return night;
        }

        private Vector3 ComputeSunColor(Vector3 sun)
        {
            float h = sun.Y;
            if (h <= 0) return Vector3.Zero;
            var warm = new Vector3(1.0f, 0.75f, 0.55f);
            var white = new Vector3(1.0f, 0.98f, 0.92f);
            return Lerp(warm, white, h / 0.5f);
        }

        public void Render(int width, int height)
        {
            if (!_initialized) return;

            // Keep viewport in lockstep with the width/height we use for crosshair math.
            // The WinForms Resize event may fire out of phase with the tick loop, so syncing
            // here every frame is cheap insurance against a stale viewport misplacing the HUD.
            GL.Viewport(0, 0, width, height);

            var sun = ComputeSunDirection();
            var sky = ComputeSkyColor(sun);
            var sunColor = ComputeSunColor(sun);
            float ambient = 0.22f + 0.18f * Math.Max(0f, sun.Y);
            // 0..1 scale on the per-block sky-light term. At noon (sun.Y ≈ 1)
            // it's 1.0; at midnight (sun.Y ≈ -1) it floors at 0.18 so a moonlit
            // night reads as dim but not pitch black, matching the sky's own
            // glow. Block light (torches/lava) is unaffected by this — it's
            // local emission, not driven by the sun.
            float skyLightLevel = 0.18f + 0.82f * Math.Max(0f, (sun.Y + 0.1f) / 1.1f);
            if (skyLightLevel > 1f) skyLightLevel = 1f;

            GL.ClearColor(sky.X, sky.Y, sky.Z, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var proj = Camera.GetProjection(width, height);
            var view = Camera.GetView();
            var vp = view * proj;
            _frustum.UpdateFromViewProj(ref vp);

            // Celestial bodies (stars, sun, moon) draw first with depth
            // disabled so the world pass overdraws them. Clouds draw after
            // the world — they need to respect terrain occlusion from below.
            float sunAngle = ComputeSunAngle();
            Vector3 antiSun = -sun;
            _sky.RenderCelestial(proj, view, Camera.Position, sunAngle, sun, antiSun);

            // Fog end sits 8 blocks inside the unload radius so the boundary
            // cliff is fully hidden even while a chunk is being streamed out.
            float fogEnd = (UnloadDistanceChunks * Chunk.SizeX) - 8f;
            float fogStart = fogEnd - 48f; // ~3 chunks of fade, matches Alpha feel

            _shader.Use();
            _shader.SetMatrix4("uProjection", proj);
            _shader.SetMatrix4("uView", view);
            _shader.SetVector3("uSunDir", sun);
            _shader.SetVector3("uSunColor", sunColor);
            _shader.SetFloat("uAmbient", ambient);
            _shader.SetFloat("uSkyLightLevel", skyLightLevel);
            _shader.SetVector3("uFogColor", sky);
            _shader.SetFloat("uFogStart", fogStart);
            _shader.SetFloat("uFogEnd", fogEnd);
            _shader.SetInt("uAtlas", 0);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

            // Pass 1 — opaques. Standard depth test + write, no blend, back-face cull.
            foreach (var kv in _chunkMeshes)
            {
                int cx = kv.Key.x, cz = kv.Key.z;
                float minX = cx * Chunk.SizeX;
                float minZ = cz * Chunk.SizeZ;
                if (!_frustum.Intersects(minX, 0, minZ, minX + Chunk.SizeX, Chunk.SizeY, minZ + Chunk.SizeZ))
                    continue;
                kv.Value.Draw();
            }

            // Pass 2 — transparents (water today). Blend on, depth write off so
            // surfaces behind multiple water faces still accumulate colour instead
            // of z-fighting. Face culling stays on so we don't double-shade the
            // underside of a water slab when looking down through it. Skip if the
            // player's camera is inside a water block — avoids the single big
            // near-plane quad covering the view.
            bool cameraInWater = false;
            if (_world != null)
            {
                int cx = (int)Math.Floor(Camera.Position.X);
                int cy = (int)Math.Floor(Camera.Position.Y);
                int cz = (int)Math.Floor(Camera.Position.Z);
                var inBlock = _world.GetBlock(cx, cy, cz);
                cameraInWater = inBlock == BlockType.Water || inBlock == BlockType.FlowingWater;
            }
            if (!cameraInWater)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                foreach (var kv in _chunkMeshes)
                {
                    if (kv.Value.TransparentIndexCount == 0) continue;
                    int cx = kv.Key.x, cz = kv.Key.z;
                    float minX = cx * Chunk.SizeX;
                    float minZ = cz * Chunk.SizeZ;
                    if (!_frustum.Intersects(minX, 0, minZ, minX + Chunk.SizeX, Chunk.SizeY, minZ + Chunk.SizeZ))
                        continue;
                    kv.Value.DrawTransparent();
                }
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }

            GL.BindTexture(TextureTarget.Texture2DArray, 0);

            // Clouds after the world passes — alpha blended against both the
            // sky behind them and any world geometry poking above their layer.
            // Skipped when submerged: they'd show through the blue tint anyway.
            if (!cameraInWater)
            {
                _sky.RenderClouds(proj, view, Camera.Position, sky, fogStart, fogEnd, sun.Y);
            }

            // Apply a watery tint as a full-screen overlay when the camera is submerged.
            if (cameraInWater)
            {
                RenderSubmergedOverlay(width, height);
            }

            RenderSelectionOutline(width, height);
            RenderCrosshair(width, height);

            if (GameMode == GameMode.Survival)
            {
                RenderSurvivalHud(width, height);
            }

            RenderHotbar(width, height);
        }

        // Survival HUD layout:
        //   |  hearts (10)  |  middle gap  |  hunger drumsticks (10)  |
        //   ←── left 1/3 ──→|←─ middle ───→|←──── right 1/3 ─────────→|
        //
        // The heart row is right-anchored to width/3 and the hunger row is
        // left-anchored to 2*width/3, so both rows sit inside their respective
        // thirds with a symmetric centre gap that scales with the window.
        // Hearts grow leftward and drumsticks grow rightward as the window
        // gets larger; on narrow windows the rows clip (but survival-mode text
        // UI survives because the status bar lives outside the GL viewport).
        private void RenderSurvivalHud(int width, int height)
        {
            const int iconPx = 20;           // on-screen size (Alpha's 9px upscaled for readability)
            const int spacing = 2;
            const int stride = iconPx + spacing;
            const int count = 10;
            int totalW = stride * count - spacing;
            int y0 = height - iconPx - 24;   // 24 px bottom margin

            // Right-edge of the heart row sits on the left third-line.
            int heartsRightEdge = width / 3;
            int heartsX0 = heartsRightEdge - totalW;
            // Left-edge of the hunger row sits on the right third-line.
            int hungerX0 = 2 * width / 3;

            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            _spriteShader.Use();
            _spriteShader.SetInt("uSprite", 0);
            _spriteShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            _spriteShader.SetVector2("uUvScale", new Vector2(HudTextures.UvWidth, 1f));

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            // Hearts row.
            int hp = Math.Max(0, Player.Health);
            int fullHearts = hp / 2;
            bool hpHalf = (hp & 1) != 0;
            DrawIconRow(_heartTexture, heartsX0, y0, iconPx, stride, count, fullHearts, hpHalf, ortho);

            // Hunger row.
            int hg = Math.Max(0, Player.Hunger);
            int fullHunger = hg / 2;
            bool hgHalf = (hg & 1) != 0;
            DrawIconRow(_drumstickTexture, hungerX0, y0, iconPx, stride, count, fullHunger, hgHalf, ortho);

            // Bubble row — sits one stride above the hunger row, sharing the
            // hunger row's left anchor. Only drawn when Air < MaxAir; while
            // surfaced (Air pinned at MaxAir) the bubbles are invisible to
            // match Alpha's "bubbles only show when you need them" rule.
            int air = Math.Max(0, Player.Air);
            if (air < Player.MaxAir)
            {
                int fullAir = air / 2;
                bool airHalf = (air & 1) != 0;
                int bubbleY = y0 - iconPx - spacing - 4; // 4 px gap above the hunger row
                DrawIconRow(_bubbleTexture, hungerX0, bubbleY, iconPx, stride, count, fullAir, airHalf, ortho);
            }

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // Draws a strip of N identical-width icons. Each slot picks full /
        // half / empty from the sprite sheet based on (fullCount, hasHalf), so
        // the same call pattern serves both hearts and drumsticks.
        private void DrawIconRow(int texture, int x0, int y0, int iconPx, int stride, int count,
            int fullCount, bool hasHalf, Matrix4 ortho)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            for (int i = 0; i < count; i++)
            {
                float uvOffX;
                if (i < fullCount) uvOffX = HudTextures.UvFullX;
                else if (i == fullCount && hasHalf) uvOffX = HudTextures.UvHalfX;
                else uvOffX = HudTextures.UvEmptyX;

                float xp = x0 + i * stride;
                float yp = y0;
                var model = Matrix4.CreateScale(iconPx, iconPx, 1f) * Matrix4.CreateTranslation(xp, yp, 0f);
                _spriteShader.SetMatrix4("uMVP", model * ortho);
                _spriteShader.SetVector2("uUvOffset", new Vector2(uvOffX, 0f));
                _unitQuadMesh.Draw();
            }
        }

        // Blue wash over the whole viewport — reads as "underwater". Uses the
        // overlay shader's uAlpha uniform rather than a blend-colour trick.
        private void RenderSubmergedOverlay(int width, int height)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);
            var scale = Matrix4.CreateScale(width, height, 1f);
            var mvp = scale * ortho;

            _overlayShader.Use();
            _overlayShader.SetMatrix4("uMVP", mvp);
            _overlayShader.SetVector3("uColor", new Vector3(0.15f, 0.28f, 0.55f));
            _overlayShader.SetFloat("uAlpha", 0.55f);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            _unitQuadMesh.Draw();
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
        }

        private void RenderSelectionOutline(int width, int height)
        {
            if (_world == null) return;
            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit)) return;

            var model = Matrix4.CreateTranslation(hit.X, hit.Y, hit.Z);
            var mvp = model * Camera.GetView() * Camera.GetProjection(width, height);

            _overlayShader.Use();
            _overlayShader.SetMatrix4("uMVP", mvp);
            _overlayShader.SetVector3("uColor", new Vector3(0.05f, 0.05f, 0.05f));
            _overlayShader.SetFloat("uAlpha", 1f);
            GL.LineWidth(2f);
            _wireCubeMesh.Draw();
        }

        private void RenderCrosshair(int width, int height)
        {
            var proj = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);
            var model = Matrix4.CreateTranslation(width * 0.5f, height * 0.5f, 0f);
            var mvp = model * proj;

            _overlayShader.Use();
            _overlayShader.SetMatrix4("uMVP", mvp);
            _overlayShader.SetVector3("uColor", new Vector3(1f, 1f, 1f));
            _overlayShader.SetFloat("uAlpha", 1f);

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            _crosshairMesh.Draw();
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
        }

        // Hotbar layout (~2.3× upscale of Alpha's chrome = 2× + 15%):
        //
        //   bar           : ~419 × 51, bottom-centred with a generous margin
        //   slot pitch    : ~46 px between slot centres
        //   icon size     : ~37 × 37 inside each slot
        //   highlight     : ~55 × 55 frame on the selected slot (overlaps bar)
        //
        // The block icon is the side-face tile from the atlas, sampled via
        // _spriteArrayShader (Texture2DArray). Reading the side rather than
        // the top makes Grass etc. recognisable at hotbar size — the top
        // tile is mostly green noise; the side shows the dirt + grass band.
        // Cross-sprite items (torch, flowers, tall grass) have a single
        // sprite tile so faceKind doesn't matter.
        //
        // Tooltip line: the selected block name in uppercase, centred above
        // the bar. Always-on for V1 so players have feedback on what they're
        // holding without consulting the WPF status strip.
        //
        // Non-integer Scale gives slightly uneven pixel doubling under the
        // sampler's Nearest filter; the chrome is simple enough that this
        // reads as 'a bit chunky' rather than blurry, which is the expected
        // look for retro pixel HUDs at non-integer zoom.
        private void RenderHotbar(int width, int height)
        {
            const float Scale = 2.3f;                                   // 2× + 15%
            int BarPx = (int)(HotbarTextures.BarWidth * Scale);         // ~419
            int BarH  = (int)(HotbarTextures.BarHeight * Scale);        // ~51
            int SlotPx = (int)(HotbarTextures.SlotInner * Scale);       // ~46
            int IconPx = (int)(HotbarTextures.IconInner * Scale);       // ~37
            int HighlightPx = (int)(HotbarTextures.HighlightSize * Scale); // ~55
            int FramePx = (int)(1 * Scale);                             // ~2 (1px frame scaled)
            const int BottomMargin = 28; // lifts the bar off the very edge of the window

            int barX = (width - BarPx) / 2;
            int barY = height - BarH - BottomMargin;

            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            // ---- bar background ------------------------------------------
            _spriteShader.Use();
            _spriteShader.SetInt("uSprite", 0);
            _spriteShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            _spriteShader.SetVector2("uUvOffset", new Vector2(0f, 0f));
            _spriteShader.SetVector2("uUvScale", new Vector2(1f, 1f));
            GL.BindTexture(TextureTarget.Texture2D, _hotbarBarTexture);
            DrawSpriteQuad(barX, barY, BarPx, BarH, ortho);

            // ---- block icons ---------------------------------------------
            _spriteArrayShader.Use();
            _spriteArrayShader.SetInt("uAtlas", 0);
            _spriteArrayShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            // V-flip: the block atlas was authored with v=0 at the bottom of
            // each tile (so greedy-mesher quads sample the green grass
            // overhang at the top of each face). The unit-quad's aPos.y
            // grows top-to-bottom in screen space under our ortho, so without
            // a flip the icon would show its bottom row at the top of the
            // slot (upside-down). We map aPos.y=0 → vUV.y=1 by pre-loading
            // the offset to 1 and scaling by -1.
            _spriteArrayShader.SetVector2("uUvOffset", new Vector2(0f, 1f));
            _spriteArrayShader.SetVector2("uUvScale", new Vector2(1f, -1f));
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

            BlockType[] slots = Input?.HotbarSlots;
            int selected = Input != null ? Input.HotbarIndex : 0;
            // Slot 0 starts inside the bar's 1-px frame (FramePx scales with the bar).
            int firstSlotX = barX + FramePx;
            int slotY = barY + FramePx;
            int iconPad = (SlotPx - IconPx) / 2;

            for (int i = 0; i < HotbarTextures.SlotCount; i++)
            {
                if (slots == null || i >= slots.Length) break;
                var t = slots[i];
                if (t == BlockType.Air) continue;
                int layer = BlockData.GetTileIndex(t, /*side*/2);
                _spriteArrayShader.SetFloat("uLayer", layer);
                int xp = firstSlotX + i * SlotPx + iconPad;
                int yp = slotY + iconPad;
                DrawSpriteQuadFor(_spriteArrayShader, xp, yp, IconPx, IconPx, ortho);
            }

            // ---- selected highlight --------------------------------------
            if (slots != null && selected >= 0 && selected < HotbarTextures.SlotCount)
            {
                _spriteShader.Use();
                _spriteShader.SetInt("uSprite", 0);
                _spriteShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
                _spriteShader.SetVector2("uUvOffset", new Vector2(0f, 0f));
                _spriteShader.SetVector2("uUvScale", new Vector2(1f, 1f));
                GL.BindTexture(TextureTarget.Texture2D, _hotbarHighlightTexture);

                // Centre the (slightly oversize) highlight on the slot's centre.
                int slotCx = firstSlotX + selected * SlotPx + SlotPx / 2;
                int slotCy = barY + BarH / 2;
                int hx = slotCx - HighlightPx / 2;
                int hy = slotCy - HighlightPx / 2;
                DrawSpriteQuad(hx, hy, HighlightPx, HighlightPx, ortho);
            }

            // ---- tooltip text --------------------------------------------
            if (slots != null && selected >= 0 && selected < slots.Length)
            {
                var t = slots[selected];
                if (t != BlockType.Air)
                {
                    string label = FriendlyName(t);
                    DrawString(label, /*scale*/2, /*centerX*/width / 2,
                        /*topY*/barY - HotbarTextures.GlyphCellH * 2 - 4,
                        new Vector4(1f, 1f, 1f, 1f), ortho);
                }
            }

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // CamelCase enum -> human-readable label. "FlowingWater" -> "Flowing Water".
        // Cheap one-pass split since this only runs once per frame for the tooltip.
        private static string FriendlyName(BlockType t)
        {
            string raw = t.ToString();
            if (raw.Length == 0) return raw;
            var sb = new System.Text.StringBuilder(raw.Length + 4);
            sb.Append(raw[0]);
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsUpper(c) && !char.IsUpper(raw[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Draws a string with the bitmap font at a given pixel scale,
        // horizontally centred on (centerX, topY). Each character occupies a
        // 6×8 cell in the font sheet; we draw a separate textured quad per
        // glyph so we can pick its UV sub-rect from the sheet.
        private void DrawString(string text, int scale, int centerX, int topY,
            Vector4 tint, Matrix4 ortho)
        {
            int glyphW = HotbarTextures.GlyphCellW * scale;
            int glyphH = HotbarTextures.GlyphCellH * scale;
            int total = text.Length * glyphW;
            int x = centerX - total / 2;

            _spriteShader.Use();
            _spriteShader.SetInt("uSprite", 0);
            _spriteShader.SetVector4("uTint", tint);
            _spriteShader.SetVector2("uUvScale",
                new Vector2(HotbarTextures.GlyphUvW, HotbarTextures.GlyphUvH));
            GL.BindTexture(TextureTarget.Texture2D, _fontTexture);

            for (int i = 0; i < text.Length; i++)
            {
                int gi = HotbarTextures.GlyphIndex(text[i]);
                if (gi < 0) { x += glyphW; continue; }
                HotbarTextures.GlyphUv(gi, out float u, out float v);
                _spriteShader.SetVector2("uUvOffset", new Vector2(u, v));
                DrawSpriteQuadFor(_spriteShader, x, topY, glyphW, glyphH, ortho);
                x += glyphW;
            }
        }

        private void DrawSpriteQuad(int x, int y, int w, int h, Matrix4 ortho)
            => DrawSpriteQuadFor(_spriteShader, x, y, w, h, ortho);

        // Generic version that lets a caller drive any sprite-shaped shader
        // (sampler2D or sampler2DArray) — the MVP layout is identical, only
        // the bound texture and uniform names differ.
        private void DrawSpriteQuadFor(Shader sh, int x, int y, int w, int h, Matrix4 ortho)
        {
            var model = Matrix4.CreateScale(w, h, 1f) * Matrix4.CreateTranslation(x, y, 0f);
            sh.SetMatrix4("uMVP", model * ortho);
            _unitQuadMesh.Draw();
        }

        public void Dispose()
        {
            _jobs?.Dispose();
            _jobs = null;
            foreach (var m in _chunkMeshes.Values) m.Dispose();
            _chunkMeshes.Clear();
            _crosshairMesh?.Dispose();
            _wireCubeMesh?.Dispose();
            _unitQuadMesh?.Dispose();
            _shader?.Dispose();
            _overlayShader?.Dispose();
            _spriteShader?.Dispose();
            _spriteArrayShader?.Dispose();
            _sky?.Dispose();
            _sky = null;
            if (_atlasTexture != 0)
            {
                GL.DeleteTexture(_atlasTexture);
                _atlasTexture = 0;
            }
            if (_heartTexture != 0)
            {
                GL.DeleteTexture(_heartTexture);
                _heartTexture = 0;
            }
            if (_drumstickTexture != 0)
            {
                GL.DeleteTexture(_drumstickTexture);
                _drumstickTexture = 0;
            }
            if (_bubbleTexture != 0)
            {
                GL.DeleteTexture(_bubbleTexture);
                _bubbleTexture = 0;
            }
            if (_hotbarBarTexture != 0)
            {
                GL.DeleteTexture(_hotbarBarTexture);
                _hotbarBarTexture = 0;
            }
            if (_hotbarHighlightTexture != 0)
            {
                GL.DeleteTexture(_hotbarHighlightTexture);
                _hotbarHighlightTexture = 0;
            }
            if (_fontTexture != 0)
            {
                GL.DeleteTexture(_fontTexture);
                _fontTexture = 0;
            }
        }
    }
}
