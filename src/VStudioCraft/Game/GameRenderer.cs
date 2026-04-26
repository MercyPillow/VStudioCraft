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

        // Crack-overlay shader. Draws a slightly-inflated cube around the
        // block currently being broken; samples the 10-frame crack atlas and
        // alpha-tests so only the dark crack pixels show. The shader is intentionally
        // tiny — the artwork is in the texture, not in the lighting model.
        private const string CrackVertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
uniform mat4 uMVP;
void main()
{
    gl_Position = uMVP * vec4(aPos, 1.0);
    vUV = aUV;
}
";

        private const string CrackFragmentSrc = @"#version 330 core
in vec2 vUV;
out vec4 FragColor;
uniform sampler2DArray uCrack;
uniform float uLayer;
void main()
{
    vec4 t = texture(uCrack, vec3(vUV, uLayer));
    if (t.a < 0.5) discard;
    FragColor = t;
}
";

        // Multi-face cube shader. Same Texture2DArray sampling as the crack
        // shader, but each cube face picks a different layer from a 6-element
        // uniform array. Indexed off `gl_VertexID / 6` since the break-cube
        // mesh lays out exactly 6 verts per face in the order:
        //   0=-X, 1=+X, 2=-Y(bottom), 3=+Y(top), 4=-Z, 5=+Z.
        // Used for both world-space drops (so a dropped grass cube shows
        // grass-top + grass-side like a placed block) and the inventory's
        // 3-face isometric icons. uTint lets the HUD path pre-shade faces
        // (top brighter, sides dimmer) without authoring three tile copies.
        // uFaceShade indexes the same way as uLayers — supplied per face.
        private const string MultiFaceCubeVertexSrc = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
flat out float vLayer;
flat out float vShade;
uniform mat4 uMVP;
uniform float uLayers[6];
uniform float uFaceShade[6];
void main()
{
    gl_Position = uMVP * vec4(aPos, 1.0);
    vUV = aUV;
    int face = gl_VertexID / 6;
    vLayer = uLayers[face];
    vShade = uFaceShade[face];
}
";

        private const string MultiFaceCubeFragmentSrc = @"#version 330 core
in vec2 vUV;
flat in float vLayer;
flat in float vShade;
out vec4 FragColor;
uniform sampler2DArray uAtlas;
uniform vec4 uTint;
void main()
{
    vec4 t = texture(uAtlas, vec3(vUV, vLayer));
    if (t.a < 0.5) discard;
    FragColor = vec4(t.rgb * uTint.rgb * vShade, t.a * uTint.a);
}
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
        private Shader _crackShader;       // pos+uv -> sampler2DArray for break overlay
        private Shader _multiFaceCubeShader; // pos+uv -> per-face sampler2DArray (drops + iso icons)
        private OverlayMesh _crosshairMesh;
        private OverlayMesh _wireCubeMesh;
        private OverlayMesh _unitQuadMesh; // [0,0]-[1,1] quad; scaled via MVP for full-screen tints + HUD sprites.
        private TexturedCubeMesh _breakCubeMesh;
        private int _atlasTexture;
        private int _heartTexture;
        private int _drumstickTexture;
        private int _bubbleTexture;
        private int _hotbarBarTexture;
        private int _hotbarHighlightTexture;
        private int _fontTexture;
        private int _crackTexture;

        // Block-break progress (survival only). Tracks the cell currently being
        // broken plus a 0..1 progress accumulator. Reset whenever the player
        // releases LMB, looks at a different cell, or the cell goes away.
        // Hardness < 0 (bedrock) is unbreakable so we never accumulate; hardness
        // 0 (flowers, torches, TNT) breaks instantly on the first held frame.
        // The renderer reads InputState.BreakHeld each frame to drive this.
        private bool _breakHasTarget;
        private int _breakTargetX, _breakTargetY, _breakTargetZ;
        private BlockType _breakTargetType;
        private float _breakProgress;

        // Dropped item entities. Survival breaks spawn one of these at the
        // broken block's centre; TickDrops integrates physics + pickup each
        // frame the world isn't halted; RenderDrops draws each as a small
        // spinning textured cube. There is no general entity system yet —
        // this is a flat list owned directly by the renderer. Drops are
        // not persisted to save files (yet); SetWorld clears them so a
        // load doesn't inherit drops from the previous session.
        private readonly System.Collections.Generic.List<DroppedItem> _drops
            = new System.Collections.Generic.List<DroppedItem>();

        // Input state shared with the host. The render thread reads
        // Inventory / HotbarIndex from this every frame to paint the bar.
        // Set once by the host after construction and never reassigned, so
        // no synchronisation is required for the reference itself.
        public InputState Input { get; set; }

        // True while the game is paused (Esc / pause menu open). The render
        // loop skips world updates (day cycle, fluid ticks, player movement,
        // survival timers) but still draws the world + pause overlay so the
        // menu can be drawn on top. Volatile so the render thread sees the
        // UI thread's flip without locking.
        private volatile bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set => _isPaused = value;
        }

        // True while the inventory screen is open (E key). Same world-halt
        // semantics as IsPaused — the host gates AdvanceTime / UpdatePlayer
        // / fluid ticks / break-progress on the combined IsWorldHalted flag
        // so opening the inventory mid-mine cancels the in-flight break and
        // freezes the world until it closes. Volatile for the same UI/render
        // thread reasons as _isPaused.
        private volatile bool _isInventoryOpen;
        public bool IsInventoryOpen
        {
            get => _isInventoryOpen;
            set => _isInventoryOpen = value;
        }

        // True while the Options sub-menu (opened from the pause menu) is
        // showing. Implies _isPaused — the host only opens it on top of an
        // already-paused state, and BACK/Esc returns to the pause menu.
        // Volatile for the UI/render thread sync, same as the others.
        private volatile bool _isOptionsOpen;
        public bool IsOptionsOpen
        {
            get => _isOptionsOpen;
            set => _isOptionsOpen = value;
        }

        // True while the crafting screen is open (RMB on a CraftingTable
        // block). Same world-halt semantics as IsInventoryOpen — host
        // gates ticks on IsWorldHalted, render path draws the panel on
        // top. Volatile for UI/render thread sync.
        private volatile bool _isCraftingOpen;
        public bool IsCraftingOpen
        {
            get => _isCraftingOpen;
            set => _isCraftingOpen = value;
        }

        // The 3×3 crafting grid + output slot, owned by the renderer
        // because the screen is a transient overlay (no save/multi-
        // session lifecycle). When the screen closes, anything left in
        // the grid is shoved back into the player's inventory; leftovers
        // toss into the world like a closed-with-cursor inventory does.
        // The output slot is ALWAYS rebuilt from CraftingRecipes.Match
        // each time the grid changes, so we never write directly to it
        // outside of the take-from-output path.
        private readonly ItemStack[] _craftingGrid = new ItemStack[CraftingScreen.GridSlotCount];
        private ItemStack _craftingOutput;

        // Per-world setting: show the hunger drumstick row + drive
        // hunger-based slow regen. Off by default (the user explicitly
        // wanted the bar gone unless they opt in). Persisted in the world
        // save header (v4+); creative mode ignores the flag entirely
        // because the survival HUD doesn't render then.
        private volatile bool _hungerEnabled;
        public bool HungerEnabled
        {
            get => _hungerEnabled;
            set => _hungerEnabled = value;
        }

        // Convenience for the host: any modal UI that should freeze the
        // world. New modals (chat overlay, world-creation dialog…) just
        // OR themselves in here and the rest of the loop gates on this
        // single flag. Options doesn't add to this list because it only
        // ever opens on top of the pause menu (which is already halted).
        public bool IsWorldHalted => _isPaused || _isInventoryOpen || _isCraftingOpen;

        // Rebuild the block atlas from whichever source the user has
        // currently selected (procedural or embedded Alpha terrain.png).
        // Called from the render thread when the Options toggle flips —
        // we delete the previous GL texture handle, generate a fresh
        // one, and let the existing shader uniform binding pick it up
        // on the next frame (the uniform always samples texture unit 0,
        // and we re-bind whatever lives in _atlasTexture there each
        // draw, so swapping the handle is enough).
        //
        // Must run on the GL thread; the host queues a render-thread
        // delegate when handling the Options click.
        public void RebuildBlockAtlas()
        {
            // Build the replacement first, *then* swap. If we deleted the
            // existing texture before creation and the new build failed
            // (e.g. the embedded Alpha terrain.png is missing in a stale
            // hive deployment) we'd leave the renderer pointing at handle
            // 0 — sampler reads return black and the world goes dark on
            // the next frame. By staging into `next` we keep the current
            // atlas alive until we're sure of a working replacement.
            int next;
            if (Settings.UseRealTextures)
            {
                next = BlockTextures.CreateAtlasFromAlphaTerrain();
                // Real-textures path can return 0 when the embedded PNG
                // isn't present in this build — fall back to procedural
                // so the toggle still produces *some* visible atlas.
                if (next == 0) next = BlockTextures.CreateAtlas();
            }
            else
            {
                next = BlockTextures.CreateAtlas();
            }
            // Defensive: if even the procedural builder failed (extremely
            // unlikely — it's all in-process pixel synthesis) keep the
            // existing atlas rather than going black.
            if (next == 0) return;
            int old = _atlasTexture;
            _atlasTexture = next;
            if (old != 0) GL.DeleteTexture(old);
        }
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

        // Hunger-driven slow health regen. Active only when HungerEnabled
        // is on and Hunger >= 70% of max (14/20). Heals 1 HP per tick; the
        // interval (4 s) is conservative — Alpha didn't have this loop, so
        // we picked a cadence that feels healing-but-not-trivializing.
        private const int   HungerRegenThreshold  = 14;       // 70% of MaxHunger
        private const float HungerRegenInterval   = 4f;       // seconds per +1 HP
        private float _hungerRegenTimer;

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
            _crackShader = new Shader(CrackVertexSrc, CrackFragmentSrc);
            _multiFaceCubeShader = new Shader(MultiFaceCubeVertexSrc, MultiFaceCubeFragmentSrc);
            _crosshairMesh = BuildCrosshairMesh();
            _wireCubeMesh = BuildWireCubeMesh();
            _unitQuadMesh = BuildUnitQuadMesh();
            _breakCubeMesh = BuildBreakCubeMesh();
            // Atlas source picked from the persisted user setting. The
            // procedural atlas is the default; opt-in via the Options menu
            // swaps to the embedded Alpha terrain.png slice. Both sources
            // produce the same Texture2DArray shape so nothing else
            // downstream needs to know which one is active.
            //
            // CreateAtlasFromAlphaTerrain returns 0 if the embedded PNG
            // isn't in the assembly (older deployed build, missing
            // resource on disk) — we silently fall back to the procedural
            // atlas in that case so the game keeps rendering rather than
            // going black-screen.
            if (Settings.UseRealTextures)
            {
                _atlasTexture = BlockTextures.CreateAtlasFromAlphaTerrain();
                if (_atlasTexture == 0)
                    _atlasTexture = BlockTextures.CreateAtlas();
            }
            else
            {
                _atlasTexture = BlockTextures.CreateAtlas();
            }
            _crackTexture = CrackTextures.CreateAtlas();
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

        // Slightly inflated unit cube with per-face UVs in [0,1]. Sits 0.003 of
        // a block outside the target cell so the crack texture floats just
        // above the block surface and never z-fights with the underlying chunk
        // mesh. Same inflation factor as the selection wire-cube — keeps both
        // overlays sitting at the same visual depth.
        //
        // 36 vertices, 5 floats each (pos.xyz, uv.xy). Every face is wound
        // CCW as seen from OUTSIDE the cube so back-face culling keeps all
        // six visible (an earlier version had -X and +X reversed, which
        // dropped them under GL_CULL_FACE — leaving the crack overlay only on
        // the four Y/Z faces). Each face's normal has been verified by edge
        // cross-product. UVs span [0,1] across each face; the crack texture
        // is abstract enough that exact orientation per-face doesn't matter
        // visually, just that all six faces sample the active frame.
        private static TexturedCubeMesh BuildBreakCubeMesh()
        {
            const float e = 0.003f;
            float a = -e, b = 1f + e;
            // Per face: tri 1 (bl, br, tr), tri 2 (bl, tr, tl).
            float[] v =
            {
                // -X face (normal -X). Viewed from -X with Y up, +Z right.
                //   bl=(a,a,a), br=(a,a,b), tr=(a,b,b), tl=(a,b,a)
                a, a, a, 0, 0,   a, a, b, 1, 0,   a, b, b, 1, 1,
                a, a, a, 0, 0,   a, b, b, 1, 1,   a, b, a, 0, 1,

                // +X face (normal +X). Viewed from +X with Y up, -Z right.
                //   bl=(b,a,b), br=(b,a,a), tr=(b,b,a), tl=(b,b,b)
                b, a, b, 0, 0,   b, a, a, 1, 0,   b, b, a, 1, 1,
                b, a, b, 0, 0,   b, b, a, 1, 1,   b, b, b, 0, 1,

                // -Y face (bottom, normal -Y). Viewed from below, +X right, +Z up.
                a, a, a, 0, 0,   b, a, a, 1, 0,   b, a, b, 1, 1,
                a, a, a, 0, 0,   b, a, b, 1, 1,   a, a, b, 0, 1,

                // +Y face (top, normal +Y). Viewed from above, +X right, -Z up.
                a, b, b, 0, 0,   b, b, b, 1, 0,   b, b, a, 1, 1,
                a, b, b, 0, 0,   b, b, a, 1, 1,   a, b, a, 0, 1,

                // -Z face (back, normal -Z). Viewed from -Z, -X right, +Y up.
                b, a, a, 0, 0,   a, a, a, 1, 0,   a, b, a, 1, 1,
                b, a, a, 0, 0,   a, b, a, 1, 1,   b, b, a, 0, 1,

                // +Z face (front, normal +Z). Viewed from +Z, +X right, +Y up.
                a, a, b, 0, 0,   b, a, b, 1, 0,   b, b, b, 1, 1,
                a, a, b, 0, 0,   b, b, b, 1, 1,   a, b, b, 0, 1,
            };
            var m = new TexturedCubeMesh();
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
            HungerEnabled = header.HungerEnabled;
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
                HungerEnabled = HungerEnabled,
            };
            WorldSaveFormat.Save(path, header, _world);
        }

        public void UpdatePlayer(float dt, Vector3 wishHorizVel, bool wantJump)
        {
            if (_world == null) return;
            Player.Update(dt, wishHorizVel, wantJump, _world);
            SyncCameraToPlayer();

            // Per-frame break-progress accumulation. Survival uses hold-LMB
            // gated by hardness; creative ignores progress and breaks instantly
            // via the existing BreakPressed path. Ticks even on the same frame
            // as a creative-instant break — the survival branch will reset on
            // its own next frame, no harm done.
            UpdateBreakProgress(dt);

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
                _hungerRegenTimer = 0f;
            }

            // Consume any pending fall distance — survival already applied the
            // damage above; creative ignores it. Either way, clear so the next
            // landing starts fresh.
            Player.LastFallDistance = 0f;

            // Fluid tick — every FluidTickInterval seconds drive a single
            // pass of source-driven outflow. The tick itself self-gates on
            // each chunk's HasActiveFluid flag, so a steady-state ocean
            // costs nothing after the first scan. Both water and lava are
            // light-transparent and emit 0, so the tick never has to drive
            // a re-light pass — only a remesh of the chunks whose blocks
            // changed. (Earlier revisions had lava emit 15, which forced a
            // 3×3-chunk RecomputeRegion every spread step and stuttered the
            // render thread; flowing lava and flowing water now share the
            // same one-cost-per-tick model.)
            _fluidTickAccumulator += dt;
            while (_fluidTickAccumulator >= FluidTickInterval)
            {
                _fluidTickAccumulator -= FluidTickInterval;
                var result = FluidTick.Tick(_world);
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

            // Hunger-driven slow regen. Only when the option is on and the
            // player isn't already topped up; the threshold is the same 70%
            // rule the user asked for. Reset the timer the moment the
            // condition drops so a brief hunger dip doesn't accidentally
            // accumulate a heal-tick.
            if (HungerEnabled
                && Player.Hunger >= HungerRegenThreshold
                && Player.Health < Player.MaxHealth
                && !Player.IsDead)
            {
                _hungerRegenTimer += dt;
                while (_hungerRegenTimer >= HungerRegenInterval
                       && Player.Health < Player.MaxHealth)
                {
                    _hungerRegenTimer -= HungerRegenInterval;
                    Player.Heal(1);
                }
            }
            else
            {
                _hungerRegenTimer = 0f;
            }

            if (Player.IsDead)
            {
                Respawn();
            }
        }

        // The eat-food entry point — items aren't wired to it yet (the eat
        // animation + slot consumption are deferred). Behaviour:
        //   HungerEnabled ON  → restore Hunger by `amount`. Health regens
        //                       passively in ApplySurvivalDamage when Hunger
        //                       is high enough.
        //   HungerEnabled OFF → restore Health directly by `amount`, since
        //                       there's no hunger pool to refill.
        // Called by future food items / cheats / debug hooks.
        public void EatFood(int amount)
        {
            if (amount <= 0) return;
            if (HungerEnabled) Player.Eat(amount);
            else               Player.Heal(amount);
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
            _hungerRegenTimer = 0f;
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

            // Drops belong to the previous world and reference its block
            // coordinates — drop them so a world swap doesn't leave stale
            // floating items at coordinates that may no longer be loaded.
            _drops.Clear();

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
            // Bedrock (and any future hardness<0 block) is unbreakable in
            // both modes — the click is silently ignored. Creative still
            // breaks everything else instantly; survival lets the click
            // through but the actual break is gated by hold-progress and
            // happens inside UpdateBreakProgress.
            var t = _world.GetBlock(hit.X, hit.Y, hit.Z);
            if (BlockData.Hardness(t) < 0f) return false;
            if (GameMode == GameMode.Survival) return false;
            return _world.SetBlock(hit.X, hit.Y, hit.Z, BlockType.Air);
        }

        // Drives the survival break-progress timer. Called every frame from
        // UpdatePlayer (which only runs while !paused). Resets progress on
        // any of:
        //   - LMB released (BreakHeld false)
        //   - Raycast misses (looking at sky / out of reach)
        //   - Target cell or block-type changed since last frame
        //   - Game flipped to creative
        // Otherwise accumulates dt/hardness; once progress >= 1 the block
        // breaks and the state resets so the next break-cycle starts clean.
        private void UpdateBreakProgress(float dt)
        {
            if (Input == null || _world == null)
            {
                _breakHasTarget = false;
                _breakProgress = 0f;
                return;
            }

            // Creative skips the timer entirely and uses the one-shot
            // BreakPressed path. Drop any in-flight progress so a mode flip
            // mid-break doesn't leave a stale crack overlay floating.
            if (GameMode != GameMode.Survival || !Input.BreakHeld)
            {
                _breakHasTarget = false;
                _breakProgress = 0f;
                return;
            }

            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit))
            {
                _breakHasTarget = false;
                _breakProgress = 0f;
                return;
            }

            var t = _world.GetBlock(hit.X, hit.Y, hit.Z);
            float hardness = BlockData.Hardness(t);
            if (hardness < 0f)
            {
                // Bedrock / unbreakable — show no progress.
                _breakHasTarget = false;
                _breakProgress = 0f;
                return;
            }

            bool sameTarget = _breakHasTarget &&
                              _breakTargetX == hit.X &&
                              _breakTargetY == hit.Y &&
                              _breakTargetZ == hit.Z &&
                              _breakTargetType == t;

            if (!sameTarget)
            {
                _breakHasTarget = true;
                _breakTargetX = hit.X;
                _breakTargetY = hit.Y;
                _breakTargetZ = hit.Z;
                _breakTargetType = t;
                _breakProgress = 0f;
            }

            // Tool-aware speed: held tool's effectiveness multiplier
            // applies when the right kind of tool is used against the
            // right block. SpeedMultiplier returns 1f for bare-hand /
            // ineffective combinations, so the survival "punch through
            // anything" baseline is preserved.
            BlockType heldType = BlockType.Air;
            var heldStack = Input.Inventory.GetHotbar(Input.HotbarIndex);
            if (!heldStack.IsEmpty) heldType = heldStack.Type;
            float speedMult = ToolData.SpeedMultiplier(heldType, t);

            if (hardness <= 0f)
            {
                // Hardness zero — flowers, torches, TNT — chip through immediately.
                _breakProgress = 1f;
            }
            else
            {
                _breakProgress += dt * speedMult / hardness;
            }

            if (_breakProgress >= 1f)
            {
                // Snapshot the broken cell + type before the SetBlock so
                // the spawn position uses block coordinates, not stale
                // raycast coords if the player happens to be looking
                // somewhere else by the time the next frame runs.
                int bx = hit.X, by = hit.Y, bz = hit.Z;
                var brokenType = t;
                _world.SetBlock(bx, by, bz, BlockType.Air);
                SpawnBreakDrop(bx, by, bz, brokenType, heldType);
                // Tool durability tick: every successful break consumes 1
                // point; when the tool runs out it's removed from the
                // hotbar. Bare-hand and non-tool stacks (blocks held in
                // the hotbar) are skipped.
                if (BlockData.IsTool(heldType))
                {
                    DamageHeldTool(1);
                }
                _breakHasTarget = false;
                _breakProgress = 0f;
            }
        }

        // Apply `amount` durability damage to the player's currently-
        // selected hotbar slot. If the tool reaches its MaxDurability the
        // stack is cleared. Safe to call when nothing's held — the
        // IsEmpty / IsTool guards keep it a no-op.
        private void DamageHeldTool(int amount)
        {
            if (Input == null) return;
            var inv = Input.Inventory;
            if (inv == null) return;
            int idx = Inventory.HotbarStart + Input.HotbarIndex;
            if (idx < 0 || idx >= inv.Slots.Length) return;
            ref var s = ref inv.Slots[idx];
            if (s.IsEmpty || !BlockData.IsTool(s.Type)) return;
            int newDur = s.Durability + amount;
            int max = ToolData.MaxDurability(s.Type);
            if (newDur >= max)
            {
                // Tool snapped — clear the slot. Alpha plays a sound here
                // (the wooden break-snap); we'll wire that in once the
                // audio pipeline lands.
                s = ItemStack.Empty;
            }
            else
            {
                s.Durability = (short)newDur;
            }
        }

        // RMB-on-block interaction dispatch. Runs BEFORE TryPlace so
        // an interactive block (crafting table today; furnace, chest,
        // door later) can swallow the right-click without it being
        // interpreted as a placement attempt. Returns true if the
        // interaction was consumed — the caller should not also call
        // TryPlace in that case.
        //
        // Currently handles only CraftingTable: RMB opens the crafting
        // screen. The raycast must land on an actual CraftingTable cell
        // (not just any solid block) for the open to fire. If the
        // player isn't aiming at anything, or is aiming at a non-
        // interactive block, this returns false and the place flow
        // proceeds normally.
        public bool TryInteract()
        {
            if (_world == null) return false;
            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit))
                return false;
            var target = _world.GetBlock(hit.X, hit.Y, hit.Z);
            switch (target)
            {
                case BlockType.CraftingTable:
                    // Crafting screen acts as a session overlay — the
                    // grid resets to empty on every open. Future
                    // expansion (chest / furnace) will key per-block-
                    // position state on tile entities; crafting has no
                    // persistent contents in Alpha (close-with-stuff-in-
                    // grid → it tosses out, so empty-on-open is correct).
                    for (int i = 0; i < _craftingGrid.Length; i++)
                        _craftingGrid[i] = ItemStack.Empty;
                    _craftingOutput = ItemStack.Empty;
                    _isCraftingOpen = true;
                    return true;
                default:
                    return false;
            }
        }

        public bool TryPlace(BlockType t)
        {
            if (_world == null) return false;
            // Tools and items can't be placed — RMB on either is a
            // no-op. Guard runs before the survival check so creative-
            // mode RMB on a tool / stick / ingot also does nothing
            // (otherwise the cube shape branch below would attempt to
            // place the non-block id as a world cell).
            if (BlockData.IsTool(t) || BlockData.IsItem(t)) return false;
            // In survival, you can only place blocks you actually have. The
            // call site already passes Input.SelectedBlock as `t`, so this
            // is mainly belt-and-braces against an empty hotbar slot
            // (SelectedBlock returns Air there) and against a stack count
            // that's hit zero between this check and the previous frame.
            if (GameMode == GameMode.Survival)
            {
                if (Input == null || t == BlockType.Air) return false;
                var heldStack = Input.Inventory.GetHotbar(Input.HotbarIndex);
                if (heldStack.IsEmpty || heldStack.Type != t) return false;
            }
            if (!Raycast.Cast(_world, Camera.Position, Camera.Forward, ReachDistance, out var hit)) return false;
            int px = hit.X + hit.Nx;
            int py = hit.Y + hit.Ny;
            int pz = hit.Z + hit.Nz;
            // Allow placing into Air or any fluid cell (the fluid gets
            // replaced, classic Alpha behaviour — works for both water and
            // lava families, source and flowing alike). Anything else —
            // including torches and other non-cube blocks the raycast can
            // target — blocks the place.
            var existing = _world.GetBlock(px, py, pz);
            if (existing != BlockType.Air
                && existing != BlockType.Water && existing != BlockType.FlowingWater
                && existing != BlockType.Lava  && existing != BlockType.FlowingLava) return false;
            // Cross-sprite blocks need a solid block beneath them to attach
            // to. Torches: floor-only for now (wall attachment needs block
            // metadata). Flowers/mushrooms/tall grass: same rule.
            if (!BlockData.IsCubeShape(t) && !BlockData.IsSolid(_world.GetBlock(px, py - 1, pz)))
                return false;
            bool placed = _world.SetBlock(px, py, pz, t);
            if (placed && GameMode == GameMode.Survival)
            {
                // Decrement the held stack — once the slot empties, the
                // SelectedBlock guard above prevents subsequent placements.
                Input.Inventory.DecrementHotbar(Input.HotbarIndex);
            }
            return placed;
        }

        // Spawn a dropped-item entity for a block broken in survival. A
        // small upward + slightly-randomised lateral kick is added so the
        // cube hops out of the just-broken cell rather than spawning
        // already-resting on the floor below — reads as a real ejection
        // and lets the player see it. Position is the cell centre.
        // Creative breaks (TryBreak) intentionally don't call this — the
        // creative loop is "block-replace mode" and would otherwise litter
        // the world with drops the player didn't want.
        private void SpawnBreakDrop(int bx, int by, int bz, BlockType type, BlockType tool)
        {
            if (type == BlockType.Air) return;
            // Skip items we can't yet pick up cleanly: fluid sources just
            // disappear (matches Alpha — broken water doesn't drop).
            if (type == BlockType.Water || type == BlockType.FlowingWater
                || type == BlockType.Lava  || type == BlockType.FlowingLava) return;

            // Tier / kind gate: stone broken with bare hands or a wooden
            // shovel breaks but yields nothing. Ores require a pickaxe
            // of the right material tier (CanHarvest matches Alpha rules).
            if (!ToolData.CanHarvest(tool, type)) return;

            // Stone → cobblestone, CoalOre → Coal item, DiamondOre →
            // Diamond gem (handled in DropFor). Two blocks have
            // probabilistic / multi-count drops that don't fit the
            // single-output DropFor signature, so we resolve them here:
            //
            //   * Gravel — 10% chance to drop Flint (matching Alpha's
            //     1-in-10 flint odds), otherwise drops the gravel block
            //     itself. Flint is the only item drop; the other 90%
            //     stays a normal gravel block.
            //   * Clay — drops 4 ClayBall items per block, never the
            //     clay block itself (matching Alpha — clay blocks are
            //     consumed into ingredients on harvest). Iron / gold
            //     ores still drop the ore block until furnace smelting
            //     lands in Tier 1 #1.
            BlockType dropType;
            int dropCount = 1;
            if (type == BlockType.Gravel)
            {
                dropType = (_dropRng.Next(10) == 0) ? BlockType.Flint : BlockType.Gravel;
            }
            else if (type == BlockType.Clay)
            {
                dropType = BlockType.ClayBall;
                dropCount = 4;
            }
            else
            {
                dropType = ToolData.DropFor(type);
            }
            if (dropType == BlockType.Air) return;

            var rng = _dropRng;
            // Spawn one DroppedItem per unit so stacks fan out a bit
            // when broken (matches Alpha — clay blocks scatter their 4
            // balls instead of dropping them as a single merged stack).
            for (int n = 0; n < dropCount; n++)
            {
                float jx = ((float)rng.NextDouble() - 0.5f) * 2f;   // -1..1
                float jz = ((float)rng.NextDouble() - 0.5f) * 2f;
                var d = new DroppedItem
                {
                    Position = new Vector3(bx + 0.5f, by + 0.5f, bz + 0.5f),
                    Velocity = new Vector3(jx, 3.5f, jz),
                    Stack = new ItemStack(dropType, 1),
                    AgeSec = 0f,
                    PickupCooldownSec = DroppedItem.SpawnPickupCooldown,
                };
                _drops.Add(d);
            }
        }

        private readonly System.Random _dropRng = new System.Random(0xD0E5);

        // Per-frame physics + pickup pass over every loose drop. Caller
        // (the host's render loop) only invokes this when the world is
        // unpaused, so drops freeze in place during pause / inventory.
        public void TickDrops(float dt)
        {
            if (_world == null || Input == null) return;
            if (_drops.Count == 0) return;

            // Player body centre — drop pickup uses sphere distance from
            // the drop's centre to this point (feet + half-height puts us
            // at the AABB centre, which feels right for "did I brush it").
            var playerCenter = Player.Position + new Vector3(0f, Player.Height * 0.5f, 0f);
            var inv = Input.Inventory;
            float pr2 = DroppedItem.PickupRadius * DroppedItem.PickupRadius;

            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var d = _drops[i];
                d.AgeSec += dt;
                if (d.PickupCooldownSec > 0f) d.PickupCooldownSec -= dt;

                if (d.AgeSec > DroppedItem.MaxLifetimeSec)
                {
                    _drops.RemoveAt(i);
                    continue;
                }

                // Gravity + integrate. Cap fall speed so a drop in the void
                // doesn't accumulate ridiculous velocity. Drag x/z lightly
                // every frame so airborne drops decelerate and floor drops
                // come to rest within a fraction of a second.
                //
                // Drag is rate-converted to be framerate-independent: the
                // reference is 0.98^frame at 60 fps, so a high-fps machine
                // doesn't decelerate the drop dramatically faster (which
                // used to make tossed items dribble out at the player's
                // feet). Math: drag-per-second = 0.98^60 ≈ 0.30, which
                // means a thrown drop retains ~30% of its velocity after
                // 1s and lands several blocks away.
                d.Velocity.Y -= 20f * dt;
                if (d.Velocity.Y < -20f) d.Velocity.Y = -20f;
                d.Position += d.Velocity * dt;
                float airDrag = (float)System.Math.Pow(0.98, dt * 60.0);
                d.Velocity.X *= airDrag;
                d.Velocity.Z *= airDrag;

                // Single-cell ground test: if the cell underneath the
                // drop's bottom is solid, snap the drop up so its bottom
                // sits flush with the cell top. No wall sweep — drops
                // thrown into a wall just slide along the floor once
                // gravity wins. Good enough for scattered loose items.
                int cx = (int)System.Math.Floor(d.Position.X);
                int cyBot = (int)System.Math.Floor(d.Position.Y - DroppedItem.HalfSize);
                int cz = (int)System.Math.Floor(d.Position.Z);
                if (BlockData.IsSolid(_world.GetBlock(cx, cyBot, cz)))
                {
                    d.Position.Y = cyBot + 1f + DroppedItem.HalfSize;
                    if (d.Velocity.Y < 0f) d.Velocity.Y = 0f;
                    // Stronger horizontal drag once on ground so the drop
                    // settles instead of skating. Same framerate-
                    // independent treatment as the air drag.
                    float groundDrag = (float)System.Math.Pow(0.6, dt * 60.0);
                    d.Velocity.X *= groundDrag;
                    d.Velocity.Z *= groundDrag;
                }

                // Pickup. Spawn-cooldown gates a 1-frame re-grab from the
                // miner's body brushing past the just-spawned drop.
                if (d.PickupCooldownSec <= 0f)
                {
                    float dx = d.Position.X - playerCenter.X;
                    float dy = d.Position.Y - playerCenter.Y;
                    float dz = d.Position.Z - playerCenter.Z;
                    if (dx * dx + dy * dy + dz * dz <= pr2)
                    {
                        var leftover = inv.TryAdd(d.Stack);
                        if (leftover.IsEmpty)
                        {
                            _drops.RemoveAt(i);
                            continue;
                        }
                        // Partial pickup — the absorbed portion is gone,
                        // remainder stays in the world for someone with
                        // empty slots to grab later.
                        d.Stack = leftover;
                    }
                }
            }
        }

        // Inventory click dispatcher. Survival routes through the slot
        // exchange rules; creative replaces the main grid with a catalog
        // tile-pick that fills the cursor with a full stack of the picked
        // block. Both modes still toss the cursor on outside-panel clicks
        // (Alpha-style discard).
        public void HandleInventoryClick(int button, int mx, int my, int screenW, int screenH)
        {
            HandleInventoryClick(button, mx, my, screenW, screenH, false);
        }

        public void HandleInventoryClick(int button, int mx, int my, int screenW, int screenH, bool shift)
        {
            if (Input == null) return;
            var inv = Input.Inventory;

            if (GameMode == GameMode.Creative)
            {
                // Hotbar row is still a real slot — click it to drop the
                // cursor / pick the slot up / swap, same rules as survival.
                int hotSlot = InventoryScreen.HitTestHotbar(screenW, screenH, mx, my, /*creative*/true);
                if (hotSlot >= 0)
                {
                    if (shift)            inv.HandleShiftClickSlot(hotSlot);
                    else if (button == 2) inv.HandleRightClickSlot(hotSlot);
                    else                  inv.HandleLeftClickSlot(hotSlot);
                    return;
                }

                // Catalog tile click → fill cursor with a full stack.
                int tile = InventoryScreen.HitTestCatalogTile(screenW, screenH, mx, my);
                if (tile >= 0)
                {
                    var filtered = CreativeCatalog.Filter(Input.InventorySearchText ?? string.Empty);
                    int absolute = Input.InventoryScrollRows * InventoryScreen.Cols + tile;
                    if (absolute >= 0 && absolute < filtered.Count)
                    {
                        // RMB picks half a stack so the player can split a
                        // catalog grab on the fly; LMB grabs the cap.
                        int count = button == 2 ? ItemStack.MaxCount / 2 : ItemStack.MaxCount;
                        inv.Cursor = new ItemStack(filtered[absolute], count);
                    }
                    return;
                }

                // Search bar click is a no-op for now — focus is implicit
                // (any key press while the inventory is open types into the
                // bar). Swallow it so it doesn't toss the cursor stack.
                if (InventoryScreen.HitTestSearchBar(screenW, screenH, mx, my))
                    return;

                // Outside everything → toss the cursor.
                if (!inv.Cursor.IsEmpty) TossCursorStack();
                return;
            }

            // Survival: full slot exchange against main + hotbar.
            int slot = InventoryScreen.HitTest(screenW, screenH, mx, my);
            if (slot >= 0)
            {
                // LMB = full pick / drop / swap / merge.
                // RMB = pick-half / drop-one (Alpha rules — see
                //       Inventory.HandleRightClickSlot for the table).
                // Shift+click bypasses both and quick-moves to the
                // opposite range (hotbar ↔ main grid).
                if (shift)            inv.HandleShiftClickSlot(slot);
                else if (button == 2) inv.HandleRightClickSlot(slot);
                else                  inv.HandleLeftClickSlot(slot);
                return;
            }
            // Outside the panel: drop the cursor stack into the world.
            // Same effect as a Q-toss but originated from the GUI.
            if (!inv.Cursor.IsEmpty)
            {
                TossCursorStack();
            }
        }

        // RMB-drag deposit into an inventory slot. Drops one item from
        // the cursor stack if the destination is empty or holds the
        // same kind; foreign-type slots are skipped (no swap during
        // drag — that's only the single-RMB rule). Slot index space
        // matches whichever code path queued it: survival passes a raw
        // Inventory.Slots index, creative passes a hotbar slot index
        // (HitTestHotbar already returns Inventory.Slots-space, so no
        // remap needed).
        public void HandleInventoryDragDeposit(int slotIndex)
        {
            if (Input == null) return;
            var inv = Input.Inventory;
            if (inv.Cursor.IsEmpty) return;
            if (slotIndex < 0 || slotIndex >= Inventory.TotalSlots) return;
            DepositOneFromCursor(ref inv.Slots[slotIndex], inv);
        }

        // RMB-drag deposit into a crafting-panel slot. Slot 0..8 hits
        // the input grid; 9 (output) is skipped (read-only); 10..54
        // routes to the player inventory via InventoryIndexFor. After
        // any grid mutation the output stack is recomputed from the
        // recipe registry.
        public void HandleCraftingDragDeposit(int slotIndex)
        {
            if (Input == null) return;
            var inv = Input.Inventory;
            if (inv.Cursor.IsEmpty) return;

            if (slotIndex >= 0 && slotIndex < CraftingScreen.GridSlotCount)
            {
                DepositOneFromCursor(ref _craftingGrid[slotIndex], inv);
                _craftingOutput = CraftingRecipes.Match(_craftingGrid);
                return;
            }
            if (slotIndex == CraftingScreen.OutputSlot) return;

            int invIdx = CraftingScreen.InventoryIndexFor(slotIndex);
            if (invIdx < 0 || invIdx >= Inventory.TotalSlots) return;
            DepositOneFromCursor(ref inv.Slots[invIdx], inv);
        }

        // Shared "drop one from cursor" primitive used by both drag-
        // deposit handlers. Empty slot → place a single item; same-
        // type slot under cap → increment; foreign-type or capped slot
        // → no-op. Preserves cursor durability when seeding a new
        // slot so a damaged tool drag still carries wear.
        private static void DepositOneFromCursor(ref ItemStack slot, Inventory inv)
        {
            var cursor = inv.Cursor;
            if (cursor.IsEmpty) return;
            if (slot.IsEmpty)
            {
                slot = new ItemStack(cursor.Type, 1, cursor.Durability);
                cursor.Count--;
                inv.Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }
            if (slot.SameKindAs(cursor))
            {
                if (slot.Count >= slot.MaxStackSize) return;
                slot.Count++;
                cursor.Count--;
                inv.Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }
            // Foreign type — skip; drag never swaps.
        }

        // ----- Crafting screen click handling --------------------------
        // Mirrors HandleInventoryClick's contract: the host queues clicks
        // via _input on MouseDown, the render thread drains the queue
        // here once per frame so all crafting-grid + cursor mutation
        // stays single-threaded. Slot space is the 0..54 range described
        // in CraftingScreen.cs:
        //   0..8   — 3×3 input grid (mutates _craftingGrid + recomputes output)
        //   9      — output (read-only result of CraftingRecipes.Match)
        //   10..54 — player main + hotbar (mirrors HandleInventoryClick)
        // After every grid mutation _craftingOutput is rebuilt from the
        // recipe registry so the panel always shows what the current
        // contents resolve to.
        public void HandleCraftingClick(int button, int mx, int my, int screenW, int screenH, bool shift)
        {
            if (Input == null) return;
            var inv = Input.Inventory;

            int slot = CraftingScreen.HitTest(screenW, screenH, mx, my);
            if (slot < 0)
            {
                // Click outside the panel: same toss-cursor behaviour as
                // the inventory screen so the player can dump a stack by
                // clicking the dim wash.
                if (!inv.Cursor.IsEmpty) TossCursorStack();
                return;
            }

            // Crafting input grid (3×3) — exchange with cursor like a
            // normal slot, then rebuild the output slot from the recipe
            // table. Shift-click on a grid cell quick-moves the stack
            // back into the player inventory (mirrors Alpha behaviour).
            if (slot >= 0 && slot < CraftingScreen.GridSlotCount)
            {
                if (shift)
                {
                    // Push the grid cell into the player inventory
                    // (TryAdd already prefers hotbar then main grid).
                    var leftover = inv.TryAdd(_craftingGrid[slot]);
                    _craftingGrid[slot] = leftover;
                }
                else if (button == 2)
                {
                    HandleRightClickSlotRef(ref _craftingGrid[slot], inv);
                }
                else
                {
                    HandleLeftClickSlotRef(ref _craftingGrid[slot], inv);
                }
                _craftingOutput = CraftingRecipes.Match(_craftingGrid);
                return;
            }

            // Output slot — read-only "result" cell. LMB picks up (or tops
            // up if the cursor already holds the same item) one batch and
            // consumes one of every input. Shift+click crafts as many
            // batches as fit in the player inventory in a single click.
            if (slot == CraftingScreen.OutputSlot)
            {
                if (_craftingOutput.IsEmpty) return;

                if (shift)
                {
                    // Repeat-craft until either the recipe stops
                    // matching (e.g. ran out of an ingredient) or the
                    // inventory has no room for another batch.
                    while (!_craftingOutput.IsEmpty)
                    {
                        var batch = _craftingOutput;
                        var leftover = inv.TryAdd(batch);
                        if (!leftover.IsEmpty)
                        {
                            // No room — stop crafting; we don't half-
                            // consume an input. Anything that DID fit
                            // already landed in the inventory; the
                            // leftover is forfeit (no input was
                            // consumed for it).
                            break;
                        }
                        CraftingRecipes.ConsumeOne(_craftingGrid);
                        _craftingOutput = CraftingRecipes.Match(_craftingGrid);
                    }
                    return;
                }

                // Plain LMB: into the cursor.
                if (inv.Cursor.IsEmpty)
                {
                    inv.Cursor = _craftingOutput;
                    CraftingRecipes.ConsumeOne(_craftingGrid);
                    _craftingOutput = CraftingRecipes.Match(_craftingGrid);
                    return;
                }
                if (inv.Cursor.SameKindAs(_craftingOutput))
                {
                    int room = inv.Cursor.MaxStackSize - inv.Cursor.Count;
                    if (room < _craftingOutput.Count) return; // can't fit a full batch — Alpha doesn't partial-craft into the cursor
                    var c = inv.Cursor;
                    c.Count += _craftingOutput.Count;
                    inv.Cursor = c;
                    CraftingRecipes.ConsumeOne(_craftingGrid);
                    _craftingOutput = CraftingRecipes.Match(_craftingGrid);
                    return;
                }
                // Different type on cursor: ignore (no swap into output).
                return;
            }

            // Player main + hotbar slots — same dispatch as the regular
            // inventory screen, just with our InventoryIndexFor mapping
            // since the slot space here starts at 10.
            int invIdx = CraftingScreen.InventoryIndexFor(slot);
            if (invIdx < 0 || invIdx >= Inventory.TotalSlots) return;
            if (shift)
            {
                // Shift+click on an inventory slot pushes into the
                // crafting grid first if there's room of the same type
                // or an empty cell, otherwise falls back to the regular
                // inventory cross-section quick-move. Keeps the grid as
                // the natural target while the panel is open.
                ref var src = ref inv.Slots[invIdx];
                if (!src.IsEmpty)
                {
                    bool moved = TryShiftIntoGrid(ref src);
                    if (!moved) inv.HandleShiftClickSlot(invIdx);
                }
            }
            else if (button == 2)
            {
                inv.HandleRightClickSlot(invIdx);
            }
            else
            {
                inv.HandleLeftClickSlot(invIdx);
            }
            // Inventory ↔ grid shifts can change a grid cell — recompute
            // the output. (HandleLeftClickSlot only touches inventory +
            // cursor, but it's cheap to re-resolve.)
            _craftingOutput = CraftingRecipes.Match(_craftingGrid);
        }

        // Same Alpha left-click rules as Inventory.HandleLeftClickSlot,
        // but the "slot" lives outside the inventory's slot array (in
        // _craftingGrid). Sharing the cursor with the inventory keeps
        // the player's hand consistent across crafting and main-bag.
        private static void HandleLeftClickSlotRef(ref ItemStack slot, Inventory inv)
        {
            var cursor = inv.Cursor;
            if (cursor.IsEmpty)
            {
                if (slot.IsEmpty) return;
                inv.Cursor = slot;
                slot = ItemStack.Empty;
                return;
            }
            if (slot.IsEmpty)
            {
                slot = cursor;
                inv.Cursor = ItemStack.Empty;
                return;
            }
            if (slot.SameKindAs(cursor))
            {
                int room = slot.MaxStackSize - slot.Count;
                if (room <= 0) return;
                int take = System.Math.Min(room, cursor.Count);
                slot.Count += take;
                cursor.Count -= take;
                inv.Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }
            // Different type — swap.
            inv.Cursor = slot;
            slot = cursor;
        }

        // Right-click counterpart for crafting-grid cells. Same table
        // as Inventory.HandleRightClickSlot — pick half on empty cursor,
        // drop one on held cursor (or swap on different-type held).
        // Kept in sync with the inventory version so a player's RMB
        // intuition reads the same in either panel.
        private static void HandleRightClickSlotRef(ref ItemStack slot, Inventory inv)
        {
            var cursor = inv.Cursor;
            if (cursor.IsEmpty)
            {
                if (slot.IsEmpty) return;
                int half = (slot.Count + 1) / 2;
                inv.Cursor = new ItemStack(slot.Type, half, slot.Durability);
                slot.Count -= half;
                if (slot.Count <= 0) slot = ItemStack.Empty;
                return;
            }
            if (slot.IsEmpty)
            {
                slot = new ItemStack(cursor.Type, 1, cursor.Durability);
                cursor.Count--;
                inv.Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }
            if (slot.SameKindAs(cursor))
            {
                if (slot.Count >= slot.MaxStackSize) return;
                slot.Count++;
                cursor.Count--;
                inv.Cursor = cursor.Count > 0 ? cursor : ItemStack.Empty;
                return;
            }
            // Different type — swap (matches LMB so RMB on a foreign
            // slot still extracts its contents in one click).
            inv.Cursor = slot;
            slot = cursor;
        }

        // Push a player-inventory stack into the crafting grid: top up
        // matching partial cells first, then fill the first empty cell.
        // Returns true if any items moved (so the caller can skip the
        // regular Inventory shift-click path). Same two-pass policy as
        // Inventory.TryAdd, scoped to _craftingGrid.
        private bool TryShiftIntoGrid(ref ItemStack src)
        {
            bool moved = false;
            // Pass 1 — top up matching partial cells.
            for (int i = 0; i < _craftingGrid.Length; i++)
            {
                if (src.IsEmpty) break;
                ref var c = ref _craftingGrid[i];
                if (c.IsEmpty || !c.SameKindAs(src)) continue;
                int room = c.MaxStackSize - c.Count;
                if (room <= 0) continue;
                int take = System.Math.Min(room, src.Count);
                c.Count += take;
                src.Count -= take;
                moved = true;
                if (src.Count == 0) src = ItemStack.Empty;
            }
            // Pass 2 — first empty cell.
            if (!src.IsEmpty)
            {
                for (int i = 0; i < _craftingGrid.Length; i++)
                {
                    ref var c = ref _craftingGrid[i];
                    if (!c.IsEmpty) continue;
                    c = src;
                    src = ItemStack.Empty;
                    moved = true;
                    break;
                }
            }
            return moved;
        }

        // Close the crafting screen. Anything left in the 3×3 grid is
        // pushed back into the player's inventory; whatever doesn't
        // fit is tossed into the world (matches the comment in
        // CraftingScreen.cs and Alpha's "close-with-stuff-in-grid"
        // semantics). The cursor is treated the same way — leaving the
        // crafting screen with a stack on the cursor would otherwise
        // strand it on the next-opened panel.
        public void CloseCrafting()
        {
            if (Input == null)
            {
                _isCraftingOpen = false;
                return;
            }
            var inv = Input.Inventory;
            for (int i = 0; i < _craftingGrid.Length; i++)
            {
                if (_craftingGrid[i].IsEmpty) continue;
                var leftover = inv.TryAdd(_craftingGrid[i]);
                _craftingGrid[i] = ItemStack.Empty;
                if (!leftover.IsEmpty) ThrowStack(leftover);
            }
            if (!inv.Cursor.IsEmpty)
            {
                var leftover = inv.TryAdd(inv.Cursor);
                inv.Cursor = ItemStack.Empty;
                if (!leftover.IsEmpty) ThrowStack(leftover);
            }
            _craftingOutput = ItemStack.Empty;
            _isCraftingOpen = false;
        }

        // Toss the cursor's stack into the world in front of the player.
        // Used by clicks outside the inventory panel and by Q-drop on the
        // hotbar (via DropFromHotbar). Both paths route through ThrowStack
        // so the "oomf" feel is identical.
        private void TossCursorStack()
        {
            if (Input == null) return;
            var stack = Input.Inventory.Cursor;
            if (stack.IsEmpty) return;
            Input.Inventory.Cursor = ItemStack.Empty;
            ThrowStack(stack);
        }

        // Q-drop from the currently-selected hotbar slot. wholeStack=true
        // (Shift+Q) ejects the whole stack; wholeStack=false (Q) splits one
        // item off. No-op if the slot is empty. Same physics as the GUI
        // toss — the drop spawns ~2 blocks in front of the player with a
        // forward + upward kick so it actually flies away.
        public void DropFromHotbar(bool wholeStack)
        {
            if (Input == null) return;
            int idx = Input.HotbarIndex;
            if (idx < 0 || idx >= Inventory.HotbarCount) return;
            int slotIndex = Inventory.HotbarStart + idx;
            DropFromSlotRef(ref Input.Inventory.Slots[slotIndex], wholeStack);
        }

        // Q-drop while the inventory screen is open. Mirrors hotbar Q-drop
        // but resolves the source from the cursor stack (if non-empty) or
        // the slot under the mouse pointer. Creative-mode catalog tiles
        // aren't real slots — Q over a catalog tile is a no-op (the tile
        // is an infinite source, dropping from it makes no sense). Catalog
        // is read-only here.
        public void DropFromInventoryHover(bool wholeStack, int screenW, int screenH)
        {
            if (Input == null) return;
            var inv = Input.Inventory;

            // Cursor stack always wins — it's the most explicit "I'm
            // holding this" intent.
            if (!inv.Cursor.IsEmpty)
            {
                if (wholeStack)
                {
                    var s = inv.Cursor;
                    inv.Cursor = ItemStack.Empty;
                    ThrowStack(s);
                }
                else
                {
                    // Preserve durability when splitting a tool stack — a
                    // half-broken pickaxe dropped from the cursor should
                    // arrive in the world with the same wear it had on
                    // the mouse pointer.
                    var s = new ItemStack(inv.Cursor.Type, 1, inv.Cursor.Durability);
                    var c = inv.Cursor; c.Count--;
                    inv.Cursor = c.Count > 0 ? c : ItemStack.Empty;
                    ThrowStack(s);
                }
                return;
            }

            int mx = Input.MenuMouseX;
            int my = Input.MenuMouseY;

            if (GameMode == GameMode.Creative)
            {
                // Only the hotbar row is a real slot in creative — main
                // grid is the catalog. Q over the catalog is a no-op.
                int hot = InventoryScreen.HitTestHotbar(screenW, screenH, mx, my, /*creative*/true);
                if (hot < 0) return;
                DropFromSlotRef(ref inv.Slots[hot], wholeStack);
                return;
            }

            // Survival: any of the 45 inventory slots are fair game.
            int slot = InventoryScreen.HitTest(screenW, screenH, mx, my);
            if (slot < 0) return;
            DropFromSlotRef(ref inv.Slots[slot], wholeStack);
        }

        // Shared "pop 1 or all from this slot and throw it" primitive.
        private void DropFromSlotRef(ref ItemStack slot, bool wholeStack)
        {
            if (slot.IsEmpty) return;
            ItemStack toss;
            if (wholeStack)
            {
                toss = slot;
                slot = ItemStack.Empty;
            }
            else
            {
                toss = new ItemStack(slot.Type, 1);
                slot.Count--;
                if (slot.Count <= 0) slot = ItemStack.Empty;
            }
            ThrowStack(toss);
        }

        // Shared throw primitive: spawn a DroppedItem at the player's hand
        // (just outside the body, at eye height) and propel it along the
        // camera-forward vector with a small upward arc. The drop visibly
        // launches *from* the player and clearly leaves their pickup
        // radius — not pre-teleported there, and not stopping at their
        // feet either.
        //
        // Tuning: forward velocity 14 + framerate-independent air drag
        // (0.98/frame at 60 fps) ⇒ several blocks of horizontal travel
        // before the drop settles. Up-velocity 3.5 with gravity -20 gives
        // a perceptible arc (peaks ~0.3 above eye, falls back to eye in
        // ~0.35s, lands at feet around 0.75s).
        // Pickup cooldown is generous so the player can keep walking after
        // the throw without re-grabbing.
        private void ThrowStack(ItemStack stack)
        {
            if (stack.IsEmpty) return;
            Vector3 fwd = Camera.Forward;
            // Just outside the player's body so the drop doesn't clip
            // through the head model on spawn but still reads as "flying
            // from me". If a wall is in our face, fall back to the camera
            // origin so the drop spawns inside our own air rather than the
            // wall.
            Vector3 spawn = Camera.Position + fwd * 0.4f;
            if (_world != null)
            {
                int sx = (int)System.Math.Floor(spawn.X);
                int sy = (int)System.Math.Floor(spawn.Y);
                int sz = (int)System.Math.Floor(spawn.Z);
                if (BlockData.IsSolid(_world.GetBlock(sx, sy, sz)))
                    spawn = Camera.Position;
            }
            var d = new DroppedItem
            {
                Position = spawn,
                // Velocity = (forward * speed) + small upward kick. The
                // forward speed is what gives the throw its "vector feel":
                // pitch the camera up and the drop arcs higher, look down
                // and it slams into the ground at your feet — same shape
                // the player intuitively expects from a thrown item.
                Velocity = fwd * 14f + new Vector3(0f, 3.5f, 0f),
                Stack = stack,
                AgeSec = 0f,
                // Longer cooldown than break-spawned drops so the player
                // can throw items without immediately re-picking them.
                PickupCooldownSec = 1f,
            };
            _drops.Add(d);
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

            // Pass 2 — transparents (water + lava). Blend on, depth write off
            // so surfaces behind multiple translucent faces still accumulate
            // colour instead of z-fighting. Face culling stays on so we don't
            // double-shade the underside of a fluid slab when looking down
            // through it. Skip if the player's camera is inside a fluid cell
            // of either family — avoids the single big near-plane quad
            // covering the view.
            bool cameraInFluid = false;
            bool cameraInLava = false;
            if (_world != null)
            {
                int cx = (int)Math.Floor(Camera.Position.X);
                int cy = (int)Math.Floor(Camera.Position.Y);
                int cz = (int)Math.Floor(Camera.Position.Z);
                var inBlock = _world.GetBlock(cx, cy, cz);
                cameraInLava = inBlock == BlockType.Lava || inBlock == BlockType.FlowingLava;
                cameraInFluid = cameraInLava
                    || inBlock == BlockType.Water || inBlock == BlockType.FlowingWater;
            }
            if (!cameraInFluid)
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
            // Skipped when submerged in either fluid: the tint would wash them
            // out anyway.
            if (!cameraInFluid)
            {
                _sky.RenderClouds(proj, view, Camera.Position, sky, fogStart, fogEnd, sun.Y);
            }

            // Full-screen tint when submerged. Water = blue / 0.55 alpha;
            // lava = thick orange / 0.80 alpha (Alpha 1.1.2 made lava nearly
            // opaque so you could barely see swimming through it).
            if (cameraInFluid)
            {
                RenderSubmergedOverlay(width, height, cameraInLava);
            }

            RenderDrops(width, height);
            RenderBreakOverlay(width, height);
            RenderSelectionOutline(width, height);
            RenderCrosshair(width, height);

            if (GameMode == GameMode.Survival)
            {
                RenderSurvivalHud(width, height);
            }

            RenderHotbar(width, height);

            // Modal overlays. Only one is shown at a time — the host
            // never opens the inventory over an active pause menu, but
            // we still gate on _isInventoryOpen first so a stuck flag
            // can't double-stack the dim wash.
            if (_isInventoryOpen) RenderInventory(width, height);
            else if (_isCraftingOpen) RenderCrafting(width, height);
            else if (_isPaused)
            {
                // Options is layered on top of the pause menu — draw the
                // pause backdrop first so dismissing options reveals it
                // without a one-frame flicker.
                if (_isOptionsOpen) RenderOptionsMenu(width, height);
                else                RenderPauseMenu(width, height);
            }
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
            // Base sizes match Alpha's 9-px source upscaled to a 20-px tile
            // for readability. Both icon size and spacing ride UiScale so a
            // fullscreen window doesn't strand the heart row at the same
            // pixel size as a small one.
            int iconPx = UiScale.S(20, width, height);
            int spacing = UiScale.S(2, width, height);
            int stride = iconPx + spacing;
            const int count = 10;
            int totalW = stride * count - spacing;
            int y0 = height - iconPx - UiScale.S(24, width, height);   // bottom margin scales too

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

            // Hunger row — gated behind the per-world HungerEnabled toggle.
            // Default off (the user asked for the bar to be hidden unless
            // explicitly opted in via Options → Survival → Hunger Bar).
            if (HungerEnabled)
            {
                int hg = Math.Max(0, Player.Hunger);
                int fullHunger = hg / 2;
                bool hgHalf = (hg & 1) != 0;
                DrawIconRow(_drumstickTexture, hungerX0, y0, iconPx, stride, count, fullHunger, hgHalf, ortho);
            }

            // Bubble row — sits one stride above the hunger row's anchor
            // (or the hearts' baseline if hunger is hidden). Only drawn
            // when Air < MaxAir; while surfaced the bubbles are invisible
            // to match Alpha's "bubbles only show when you need them" rule.
            int air = Math.Max(0, Player.Air);
            if (air < Player.MaxAir)
            {
                int fullAir = air / 2;
                bool airHalf = (air & 1) != 0;
                int bubbleY = y0 - iconPx - spacing - UiScale.S(4, width, height); // small gap above the hunger row, scaled
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

        // Full-screen wash that reads as "submerged". Blue+0.55 for water;
        // thick orange+0.80 for lava (Alpha 1.1.2 made lava almost opaque so
        // you could barely see swimming through it). Uses the overlay
        // shader's uAlpha uniform rather than a blend-colour trick.
        private void RenderSubmergedOverlay(int width, int height, bool inLava)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);
            var scale = Matrix4.CreateScale(width, height, 1f);
            var mvp = scale * ortho;

            _overlayShader.Use();
            _overlayShader.SetMatrix4("uMVP", mvp);
            if (inLava)
            {
                _overlayShader.SetVector3("uColor", new Vector3(0.85f, 0.30f, 0.05f));
                _overlayShader.SetFloat("uAlpha", 0.80f);
            }
            else
            {
                _overlayShader.SetVector3("uColor", new Vector3(0.15f, 0.28f, 0.55f));
                _overlayShader.SetFloat("uAlpha", 0.55f);
            }

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            _unitQuadMesh.Draw();
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
        }

        // Draws the 10-frame crack overlay around the block currently being
        // broken. Frame index = floor(progress * 10) clamped to [0, 9] so the
        // last frame holds for one tick before the block actually breaks.
        // Uses alpha-test (no blending) so the cracks read as crisp dark lines
        // over the underlying block face — matches the torch-tile convention.
        // Skipped entirely when no break is in progress.
        private void RenderBreakOverlay(int width, int height)
        {
            if (!_breakHasTarget || _breakProgress <= 0f) return;

            int frame = (int)(_breakProgress * CrackTextures.FrameCount);
            if (frame < 0) frame = 0;
            else if (frame >= CrackTextures.FrameCount) frame = CrackTextures.FrameCount - 1;

            var model = Matrix4.CreateTranslation(_breakTargetX, _breakTargetY, _breakTargetZ);
            var mvp = model * Camera.GetView() * Camera.GetProjection(width, height);

            _crackShader.Use();
            _crackShader.SetMatrix4("uMVP", mvp);
            _crackShader.SetInt("uCrack", 0);
            _crackShader.SetFloat("uLayer", frame);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _crackTexture);
            // Slight inflation (built into the mesh) keeps us out of z-fight
            // with the underlying chunk face. Cull is left on — back faces of
            // the inflated cube would just sit behind the front faces anyway.
            _breakCubeMesh.Draw();
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // Draw every loose dropped item as a small spinning textured cube
        // at its world position. Uses the break-overlay's pos+UV cube mesh
        // with the multi-face cube shader so a dropped grass cube shows
        // grass-top + grass-side + dirt-bottom (matching how the same block
        // looks when placed). World shading is uniform across faces — the
        // chunk's ambient lighting gives placed blocks their per-face
        // brightness, but a 0.25-block drop in mid-air doesn't need that
        // and a flat 1.0 shade keeps it readable from any angle.
        private void RenderDrops(int width, int height)
        {
            if (_drops.Count == 0) return;

            _multiFaceCubeShader.Use();
            _multiFaceCubeShader.SetInt("uAtlas", 0);
            _multiFaceCubeShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            SetCubeFaceShade(_multiFaceCubeShader,
                /*top*/1f, /*side*/1f, /*bottom*/1f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

            var view = Camera.GetView();
            var proj = Camera.GetProjection(width, height);

            // Constant 0.25-block cube around drop centre. The base mesh
            // (_breakCubeMesh) spans [0,1]^3 (with tiny inflation), so we
            // first translate by -0.5 to centre on origin, then scale.
            var localCentre = Matrix4.CreateTranslation(-0.5f, -0.5f, -0.5f);
            var sizeScale   = Matrix4.CreateScale(0.25f);

            BlockType lastType = BlockType.Air;
            for (int i = 0; i < _drops.Count; i++)
            {
                var d = _drops[i];
                if (d.Stack.Type != lastType)
                {
                    SetCubeFaceLayers(_multiFaceCubeShader, d.Stack.Type);
                    lastType = d.Stack.Type;
                }

                // Cute Alpha-style spin around vertical + tiny vertical bob.
                float spin = d.AgeSec * 1.5f;
                float bob  = (float)System.Math.Sin(d.AgeSec * 2.0) * 0.05f;

                var rotY  = Matrix4.CreateRotationY(spin);
                var trans = Matrix4.CreateTranslation(d.Position.X,
                                                      d.Position.Y + bob,
                                                      d.Position.Z);
                var model = localCentre * sizeScale * rotY * trans;
                var mvp = model * view * proj;
                _multiFaceCubeShader.SetMatrix4("uMVP", mvp);
                _breakCubeMesh.Draw();
            }

            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // Set per-face atlas layer indices on the multi-face cube shader.
        // Mesh face order is fixed by BuildBreakCubeMesh:
        //   0 = -X, 1 = +X, 2 = -Y (bottom), 3 = +Y (top), 4 = -Z, 5 = +Z.
        // BlockData.GetTileIndex's faceKind is 0=top, 1=bottom, 2=side; we
        // remap accordingly so a grass cube renders grass-top up top, dirt
        // down below, and grass-side around — same as a placed block.
        private static void SetCubeFaceLayers(Shader sh, BlockType type)
        {
            int side = BlockData.GetTileIndex(type, /*side*/2);
            int top  = BlockData.GetTileIndex(type, /*top*/0);
            int bot  = BlockData.GetTileIndex(type, /*bottom*/1);
            sh.SetFloat("uLayers[0]", side); // -X
            sh.SetFloat("uLayers[1]", side); // +X
            sh.SetFloat("uLayers[2]", bot);  // -Y bottom
            sh.SetFloat("uLayers[3]", top);  // +Y top
            sh.SetFloat("uLayers[4]", side); // -Z
            sh.SetFloat("uLayers[5]", side); // +Z
        }

        // Set per-face shade multipliers on the multi-face cube shader.
        // Same mesh face order as SetCubeFaceLayers. Used by the inventory
        // icon path to fake directional light on the iso-rotated cube
        // (top brightest, sides slightly dimmer) so the three visible
        // faces read as distinct surfaces even when their tiles are the
        // same colour (e.g. cobblestone).
        private static void SetCubeFaceShade(Shader sh, float top, float side, float bottom)
        {
            sh.SetFloat("uFaceShade[0]", side);
            sh.SetFloat("uFaceShade[1]", side);
            sh.SetFloat("uFaceShade[2]", bottom);
            sh.SetFloat("uFaceShade[3]", top);
            sh.SetFloat("uFaceShade[4]", side);
            sh.SetFloat("uFaceShade[5]", side);
        }

        // Render a 3-face block icon (top + two sides) into a pixel
        // rectangle on the HUD. Uses the exact same multi-face cube
        // shader + mesh as the world drops, with the same uniform
        // shading (no faux directional light), so a hotbar icon looks
        // like a "paused drop" rather than a separate iso illustration.
        // The camera is a small ortho-ish perspective that matches the
        // angle a player typically views a drop on the ground at.
        // Cross-sprite blocks (torch, flowers, tall grass) are NOT routed
        // through here — the 3D cube wouldn't look right for an "X"-shaped
        // sprite; they continue to use the flat sprite path with the side tile.
        private void RenderBlockIcon3D(BlockType type, int slotX, int slotY,
            int slotW, int slotH, Matrix4 ortho)
        {
            // Classic Minecraft inventory orientation:
            //   1. Yaw -45° around Y so a corner of the cube faces the
            //      camera. The front edge (between two side faces) is now
            //      vertical, sitting dead-centre in screen X.
            //   2. Pitch +30° around X. The top of the now corner-on cube
            //      tips toward the viewer, exposing the top face as a
            //      flat diamond above the two side faces. Bottom tucks
            //      back behind the front edge and gets back-face culled.
            //
            // CRITICAL: yaw FIRST, then pitch. The reverse order
            // (pitch * yaw) yaws an already-tilted cube around the world
            // Y axis — the pitched cube swings, leaving the front seam
            // tilted rather than vertical and the top diamond rotated
            // off-axis. Yaw-then-pitch yaws the upright cube and then
            // pitches the resulting (still-axis-aligned-in-Y) corner-on
            // shape, so the seam stays vertical.
            var localCentre = Matrix4.CreateTranslation(-0.5f, -0.5f, -0.5f);
            var rotY = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-45f));
            var rotX = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(30f));

            // After yaw 45° + pitch 30° the cube projects to a hexagon
            // roughly 1.42 wide and 1.58 tall (slightly taller than wide
            // because the back-top and front-bottom corners stick further
            // out vertically once the cube is tipped). Fit so the icon
            // touches the smaller of the two slot bounds — width-only
            // would overflow the slot vertically; height-only would leave
            // a wide gap on the sides.
            const float ProjectedW = 1.42f;
            const float ProjectedH = 1.58f;
            const float SlotPad    = 0.92f;
            float fitPx = System.Math.Min(
                slotW * SlotPad / ProjectedW,
                slotH * SlotPad / ProjectedH);

            // Y is negated because the screen ortho has y growing
            // downward (top-left origin) but our model's +Y points up.
            //
            // CRITICAL: Z stays at 1, NOT fitPx. The HUD ortho uses
            // near=-1/far=1, and the hardware clips against those planes
            // regardless of whether the depth test is enabled. Scaling Z
            // by fitPx (~30-50) would push the rotated cube's depth
            // extent out to ±~40, far past the [-1,1] near/far range, so
            // ~98% of the cube gets clipped and only a thin slice through
            // z=0 survives — which renders as just the cube's edges.
            // Ortho projection ignores Z for screen position, so a small
            // Z scale produces an identical 2D silhouette while keeping
            // every vertex inside the depth bounds.
            var scale = Matrix4.CreateScale(fitPx, -fitPx, 1f);
            var trans = Matrix4.CreateTranslation(
                slotX + slotW * 0.5f, slotY + slotH * 0.5f, 0f);

            var mvp = localCentre * rotY * rotX * scale * trans * ortho;

            _multiFaceCubeShader.Use();
            _multiFaceCubeShader.SetInt("uAtlas", 0);
            _multiFaceCubeShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            SetCubeFaceLayers(_multiFaceCubeShader, type);
            // Per-face shading so the three visible faces of the iso
            // cube read as distinct surfaces even on uniform-tile blocks
            // like cobblestone or planks. After yaw -45° + pitch +30°
            // the visible faces are:
            //   index 3 (+Y) → TOP   — brightest
            //   index 5 (+Z) → LEFT  — middle
            //   index 1 (+X) → RIGHT — darkest
            // The other three faces (-X / -Z / -Y) are back-face culled
            // and never sampled, so their shade values don't matter.
            // Drops keep uniform 1.0 shading because they spin and would
            // otherwise flicker brightness as faces rotate.
            _multiFaceCubeShader.SetFloat("uFaceShade[0]", 1f);   // -X (hidden)
            _multiFaceCubeShader.SetFloat("uFaceShade[1]", 0.6f); // +X right
            _multiFaceCubeShader.SetFloat("uFaceShade[2]", 1f);   // -Y (hidden)
            _multiFaceCubeShader.SetFloat("uFaceShade[3]", 1f);   // +Y top
            _multiFaceCubeShader.SetFloat("uFaceShade[4]", 1f);   // -Z (hidden)
            _multiFaceCubeShader.SetFloat("uFaceShade[5]", 0.8f); // +Z left
            _multiFaceCubeShader.SetMatrix4("uMVP", mvp);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

            // Cull to hide the 3 back-facing faces; without this the
            // far-side faces would draw on top of the near ones because
            // the HUD pass runs with depth test off.
            //
            // Winding sanity: the negative Y in `scale` flips winding once,
            // but the HUD ortho (CreateOrthographicOffCenter with top=0,
            // bottom=height) already encodes a Y flip too. Two flips cancel,
            // so the final geometry is still CCW from the eye — i.e. the
            // default FrontFace=Ccw is correct. The earlier FrontFace=Cw
            // override hid the FRONT faces and left only the back-facing
            // (away-from-camera) faces visible, which read as just edges
            // / inside-of-cube seams. Don't touch FrontFace here.
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            _breakCubeMesh.Draw();
            // Restore the HUD pass baseline. The hotbar / inventory loops
            // mix cube-shape blocks (which need cull) with cross-sprite
            // items (torches, flowers, tall grass — which use a unit quad
            // through DrawFlatSpriteIcon). Leaving cull enabled here would
            // back-face-cull the next iteration's flat sprite quad and
            // make the torch / flower invisible. Self-contained state in
            // and out keeps the caller free of cleanup.
            GL.Disable(EnableCap.CullFace);

            GL.BindTexture(TextureTarget.Texture2DArray, 0);
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
            // Crosshair mesh is built once at h=8/w=1 in pixel units. We
            // deliberately do NOT pipe this through UiScale — the crosshair
            // is an aiming reticle, not chrome, and players want it the
            // same pixel size regardless of viewport / fullscreen state.
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
            // All bar / slot / icon / highlight rectangles come from a
            // single source-pixel scale via HotbarLayout. That keeps the
            // bar's 182:22 aspect locked, and keeps the icons aligned
            // with the slot wells visible on the bar texture at *any*
            // viewport size — the previous code rounded each dimension
            // independently and so the slot pitch drifted out of sync
            // with the bar's actual width at non-integer scales.
            int BarPx = HotbarLayout.BarPx(width, height);
            int BarH  = HotbarLayout.BarH(width, height);
            int barX  = HotbarLayout.BarX(width, height);
            int barY  = HotbarLayout.BarTopY(width, height);

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
            // Cube blocks render as a 3-face isometric (top + 2 sides) via
            // RenderBlockIcon3D — same look as a placed block, so the
            // hotbar tile matches what the player will actually place.
            // Cross-sprite items (torch, flowers, tall grass) keep the
            // flat sprite path since their tile is an X, not a cube.
            var inv = Input?.Inventory;
            int selected = Input != null ? Input.HotbarIndex : 0;

            for (int i = 0; i < HotbarTextures.SlotCount; i++)
            {
                if (inv == null) break;
                var stack = inv.Slots[Inventory.HotbarStart + i];
                if (stack.IsEmpty) continue;
                HotbarLayout.GetIconRect(i, width, height,
                    out int xp, out int yp, out int iw, out int ih);
                if (BlockData.IsCubeShape(stack.Type))
                {
                    RenderBlockIcon3D(stack.Type, xp, yp, iw, ih, ortho);
                }
                else
                {
                    DrawFlatSpriteIcon(stack.Type, xp, yp, iw, ih, ortho);
                }
            }
            // RenderBlockIcon3D toggles CullFace; restore the HUD pass
            // baseline (cull off, depth off) before the next sprite draws.
            GL.Disable(EnableCap.CullFace);

            // ---- selected highlight --------------------------------------
            if (inv != null && selected >= 0 && selected < HotbarTextures.SlotCount)
            {
                _spriteShader.Use();
                _spriteShader.SetInt("uSprite", 0);
                _spriteShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
                _spriteShader.SetVector2("uUvOffset", new Vector2(0f, 0f));
                _spriteShader.SetVector2("uUvScale", new Vector2(1f, 1f));
                GL.BindTexture(TextureTarget.Texture2D, _hotbarHighlightTexture);

                HotbarLayout.GetHighlightRect(selected, width, height,
                    out int hx, out int hy, out int hw, out int hh);
                DrawSpriteQuad(hx, hy, hw, hh, ortho);
            }

            // ---- stack-count digits --------------------------------------
            // Drawn after the highlight so the digit sits above the frame
            // outline. Single-item stacks skip the count to keep the bar
            // visually clean.
            if (inv != null)
            {
                for (int i = 0; i < HotbarTextures.SlotCount; i++)
                {
                    var stack = inv.Slots[Inventory.HotbarStart + i];
                    if (stack.IsEmpty || stack.Count <= 1) continue;
                    HotbarLayout.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawStackCount(stack.Count, sx, sy, sw, sh, ortho);
                }

                // Durability bars on damaged tool stacks. Walks the same
                // hotbar slots; DrawDurabilityBar is a no-op for non-tool
                // and undamaged tool stacks so the work is bounded.
                for (int i = 0; i < HotbarTextures.SlotCount; i++)
                {
                    var stack = inv.Slots[Inventory.HotbarStart + i];
                    if (stack.IsEmpty) continue;
                    HotbarLayout.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawDurabilityBar(stack, sx, sy, sw, sh, width, height, ortho);
                }
            }

            // ---- tooltip text --------------------------------------------
            if (inv != null && selected >= 0 && selected < HotbarTextures.SlotCount)
            {
                var stack = inv.Slots[Inventory.HotbarStart + selected];
                if (!stack.IsEmpty)
                {
                    string label = FriendlyName(stack.Type);
                    int labelScale = System.Math.Max(1, UiScale.S(2, width, height));
                    DrawString(label, /*scale*/labelScale, /*centerX*/width / 2,
                        /*topY*/barY - HotbarTextures.GlyphCellH * labelScale - UiScale.S(4, width, height),
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

        // Draw an inventory-stack count (e.g. "64") right-aligned to the
        // bottom-right corner of a slot rect. Uses scale-2 glyphs (~12×16
        // px) so they read clearly without dominating the icon. A 1-px
        // dark-grey drop-shadow gives the digits contrast against pale
        // block icons (sand, planks). Caller must have blend on; the
        // sprite shader is rebound each call so it can interleave with
        // other draw passes (block-icon array shader, highlight, etc.).
        private void DrawStackCount(int count, int slotX, int slotY,
            int slotW, int slotH, Matrix4 ortho)
        {
            string text = count.ToString();
            int scale = 2;
            int glyphW = HotbarTextures.GlyphCellW * scale;
            int glyphH = HotbarTextures.GlyphCellH * scale;
            int total = text.Length * glyphW;

            // Right-bottom anchor with a small inset so the digits sit
            // inside the slot border instead of clipping the corner.
            const int InsetX = 3;
            const int InsetY = 3;
            int rightX = slotX + slotW - InsetX;
            int topY = slotY + slotH - InsetY - glyphH;
            int leftX = rightX - total;

            // Drop-shadow pass (offset +1,+1) — same string, dark tint.
            DrawDigits(text, leftX + 1, topY + 1, glyphW, glyphH,
                new Vector4(0f, 0f, 0f, 0.85f), ortho);
            // Foreground pass — bright white.
            DrawDigits(text, leftX, topY, glyphW, glyphH,
                new Vector4(1f, 1f, 1f, 1f), ortho);
        }

        // Inner glyph-loop helper shared by DrawStackCount's shadow + fg
        // passes. Mirrors DrawString but takes a left-anchored x instead
        // of a centre, since stack counts are right-anchored to the slot.
        private void DrawDigits(string text, int leftX, int topY,
            int glyphW, int glyphH, Vector4 tint, Matrix4 ortho)
        {
            _spriteShader.Use();
            _spriteShader.SetInt("uSprite", 0);
            _spriteShader.SetVector4("uTint", tint);
            _spriteShader.SetVector2("uUvScale",
                new Vector2(HotbarTextures.GlyphUvW, HotbarTextures.GlyphUvH));
            GL.BindTexture(TextureTarget.Texture2D, _fontTexture);

            int x = leftX;
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

        // Pause overlay — dim wash + 4 buttons + title. Drawn last each frame
        // when _isPaused is set, so the world + hotbar still render behind.
        // Hover highlight is driven by Input.MenuMouseX/Y, which the host
        // updates on every WPF mouse-move while paused (in physical pixels).
        private void RenderPauseMenu(int width, int height)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            // Dim background — semi-transparent black wash over the whole
            // viewport. Reads as 'paused' without hiding the world entirely.
            DrawSolidQuad(0, 0, width, height,
                new Vector3(0f, 0f, 0f), 0.55f, ortho);

            int mx = Input?.MenuMouseX ?? -1;
            int my = Input?.MenuMouseY ?? -1;

            // Buttons. Border thickness and label font scale ride UiScale
            // so the chrome reads as one tier with the button rect itself.
            int btnBorder = UiScale.S(2, width, height);
            int btnLabelScale = System.Math.Max(1, UiScale.S(2, width, height));
            for (int i = 0; i < PauseMenu.Count; i++)
            {
                var b = PauseMenu.GetButton(i, width, height);
                bool hover = mx >= b.X && mx < b.X + b.W && my >= b.Y && my < b.Y + b.H;

                Vector3 fill = hover
                    ? new Vector3(0.42f, 0.55f, 0.72f)
                    : new Vector3(0.16f, 0.20f, 0.26f);
                DrawSolidQuad(b.X, b.Y, b.W, b.H, fill, 0.95f, ortho);

                // Frame around the button — light edge so the button reads
                // as a tile even when not hovered. Thickness scales with UI.
                Vector3 border = hover
                    ? new Vector3(1f, 1f, 1f)
                    : new Vector3(0.78f, 0.82f, 0.88f);
                DrawSolidQuad(b.X, b.Y, b.W, btnBorder, border, 1f, ortho);                            // top
                DrawSolidQuad(b.X, b.Y + b.H - btnBorder, b.W, btnBorder, border, 1f, ortho);          // bottom
                DrawSolidQuad(b.X, b.Y, btnBorder, b.H, border, 1f, ortho);                            // left
                DrawSolidQuad(b.X + b.W - btnBorder, b.Y, btnBorder, b.H, border, 1f, ortho);          // right

                // Centred label inside the button. Glyphs are 6×8 cells.
                int labelTopY = b.Y + (b.H - HotbarTextures.GlyphCellH * btnLabelScale) / 2;
                DrawString(b.Label, /*scale*/btnLabelScale, /*centerX*/b.X + b.W / 2,
                    /*topY*/labelTopY, new Vector4(1f, 1f, 1f, 1f), ortho);
            }

            // Title above the button stack.
            DrawString("GAME MENU", /*scale*/PauseMenu.TitleFontScale(width, height),
                /*centerX*/width / 2,
                /*topY*/PauseMenu.TitleY(width, height),
                new Vector4(1f, 1f, 1f, 1f), ortho);

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
        }

        // Options sub-menu. Same dim wash + frame palette as the pause
        // menu so the two read as one UI family. Section headings draw
        // dimmer (no fill, no border) so they look like labels rather
        // than buttons; disabled rows draw greyed and ignore hover.
        private void RenderOptionsMenu(int width, int height)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            // Dim background — same wash as the pause menu (slightly darker
            // because we're layered on top of an already-dim world).
            DrawSolidQuad(0, 0, width, height,
                new Vector3(0f, 0f, 0f), 0.55f, ortho);

            int mx = Input?.MenuMouseX ?? -1;
            int my = Input?.MenuMouseY ?? -1;

            bool isSurvival = GameMode == GameMode.Survival;
            bool useReal = Settings.UseRealTextures;
            var rows = OptionsMenu.BuildRows(width, height, HungerEnabled, isSurvival, useReal);
            int rowBorder = UiScale.S(2, width, height);
            int rowLabelScale = System.Math.Max(1, UiScale.S(2, width, height));

            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];

                if (r.IsSection)
                {
                    // Section heading — no fill, no border. Slightly muted
                    // grey so it reads as a label distinct from buttons.
                    int labelTopY = r.Y + (r.H - HotbarTextures.GlyphCellH * rowLabelScale) / 2;
                    DrawString(r.Label, /*scale*/rowLabelScale,
                        /*centerX*/r.X + r.W / 2, labelTopY,
                        new Vector4(0.78f, 0.82f, 0.88f, 1f), ortho);
                    continue;
                }

                bool hover = !r.IsDisabled
                    && mx >= r.X && mx < r.X + r.W
                    && my >= r.Y && my < r.Y + r.H;

                Vector3 fill;
                if (r.IsDisabled)       fill = new Vector3(0.10f, 0.12f, 0.16f);
                else if (hover)         fill = new Vector3(0.42f, 0.55f, 0.72f);
                else                    fill = new Vector3(0.16f, 0.20f, 0.26f);
                float alpha = r.IsDisabled ? 0.7f : 0.95f;
                DrawSolidQuad(r.X, r.Y, r.W, r.H, fill, alpha, ortho);

                // Frame around the button — thickness scales with UI.
                Vector3 border;
                if (r.IsDisabled)       border = new Vector3(0.40f, 0.42f, 0.46f);
                else if (hover)         border = new Vector3(1f, 1f, 1f);
                else                    border = new Vector3(0.78f, 0.82f, 0.88f);
                DrawSolidQuad(r.X, r.Y, r.W, rowBorder, border, 1f, ortho);
                DrawSolidQuad(r.X, r.Y + r.H - rowBorder, r.W, rowBorder, border, 1f, ortho);
                DrawSolidQuad(r.X, r.Y, rowBorder, r.H, border, 1f, ortho);
                DrawSolidQuad(r.X + r.W - rowBorder, r.Y, rowBorder, r.H, border, 1f, ortho);

                Vector4 textCol = r.IsDisabled
                    ? new Vector4(0.55f, 0.58f, 0.62f, 1f)
                    : new Vector4(1f, 1f, 1f, 1f);
                int btnLabelTopY = r.Y + (r.H - HotbarTextures.GlyphCellH * rowLabelScale) / 2;
                DrawString(r.Label, /*scale*/rowLabelScale,
                    /*centerX*/r.X + r.W / 2, btnLabelTopY,
                    textCol, ortho);
            }

            // Title above the row stack.
            DrawString("OPTIONS", /*scale*/OptionsMenu.TitleFontScale(width, height),
                /*centerX*/width / 2,
                /*topY*/OptionsMenu.TitleY(width, height, HungerEnabled, isSurvival, useReal),
                new Vector4(1f, 1f, 1f, 1f), ortho);

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
        }

        // Inventory overlay — dim wash + panel + main grid + hotbar row.
        // Survival mode draws a 4×9 main grid + the hotbar; creative mode
        // replaces the main grid with a search bar + scrollable catalog of
        // every placeable BlockType (see RenderCreativeInventoryBody). The
        // hotbar row stays live in both modes. Every survival slot reads
        // from Input.Inventory.Slots and renders the block icon + stack-
        // count digits; creative catalog tiles read from CreativeCatalog.
        // The cursor stack (held while moving items) follows MenuMouseX/Y so
        // the player can see what they're carrying mid-drag.
        private void RenderInventory(int width, int height)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            // Dim background — same wash as the pause menu so the two
            // modals feel like they belong to the same UI family.
            DrawSolidQuad(0, 0, width, height,
                new Vector3(0f, 0f, 0f), 0.55f, ortho);

            // ---- panel ---------------------------------------------------
            bool creativePanel = GameMode == GameMode.Creative;
            InventoryScreen.GetPanelRect(width, height, creativePanel,
                out int panelX, out int panelY, out int panelW, out int panelH);

            // Two-tone panel: dark fill + 2-px lighter border. Same palette
            // as the hotbar bar so the inventory reads as the bar's bigger
            // sibling.
            DrawSolidQuad(panelX, panelY, panelW, panelH,
                new Vector3(0.16f, 0.16f, 0.18f), 0.95f, ortho);
            var border = new Vector3(0.78f, 0.82f, 0.88f);
            int pb = InventoryScreen.SlotBorderPx(width, height);
            DrawSolidQuad(panelX, panelY, panelW, pb, border, 1f, ortho);                       // top
            DrawSolidQuad(panelX, panelY + panelH - pb, panelW, pb, border, 1f, ortho);         // bottom
            DrawSolidQuad(panelX, panelY, pb, panelH, border, 1f, ortho);                       // left
            DrawSolidQuad(panelX + panelW - pb, panelY, pb, panelH, border, 1f, ortho);         // right

            // ---- title ---------------------------------------------------
            string title = GameMode == GameMode.Creative ? "CREATIVE INVENTORY" : InventoryScreen.Title;
            DrawString(title, /*scale*/InventoryScreen.TitleScale(width, height),
                /*centerX*/width / 2,
                /*topY*/InventoryScreen.TitleY(width, height, creativePanel),
                new Vector4(1f, 1f, 1f, 1f), ortho);

            if (GameMode == GameMode.Creative)
                RenderCreativeInventoryBody(width, height, ortho);
            else
                RenderSurvivalInventoryBody(width, height, ortho);

            // ---- cursor stack (follows the mouse) ----------------------
            // Rendered last so it floats above every slot. Position is
            // anchored to the cursor centre so the icon doesn't lurch
            // when the player drags from a slot's edge to its centre.
            var inv = Input?.Inventory;
            if (inv != null && !inv.Cursor.IsEmpty && Input != null)
            {
                int cx = Input.MenuMouseX;
                int cy = Input.MenuMouseY;
                int iconSize = InventoryScreen.IconPx(width, height);
                int ix = cx - iconSize / 2;
                int iy = cy - iconSize / 2;

                if (BlockData.IsCubeShape(inv.Cursor.Type))
                {
                    RenderBlockIcon3D(inv.Cursor.Type, ix, iy, iconSize, iconSize, ortho);
                    GL.Disable(EnableCap.CullFace);
                }
                else
                {
                    DrawFlatSpriteIcon(inv.Cursor.Type, ix, iy, iconSize, iconSize, ortho);
                }

                // Count badge in the same bottom-right anchor as a slot
                // would use. Using SlotPx as the synthetic frame keeps
                // visual parity with in-slot counts.
                int cursorFrame = InventoryScreen.SlotPx(width, height);
                int cursorFrameX = cx - cursorFrame / 2;
                int cursorFrameY = cy - cursorFrame / 2;
                if (inv.Cursor.Count > 1)
                {
                    DrawStackCount(inv.Cursor.Count, cursorFrameX, cursorFrameY,
                        cursorFrame, cursorFrame, ortho);
                }
                // Durability bar on cursor — moves with the mouse so the
                // player can see how worn the tool they're dragging is.
                DrawDurabilityBar(inv.Cursor, cursorFrameX, cursorFrameY,
                    cursorFrame, cursorFrame, width, height, ortho);
            }

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // Crafting-table screen. Mirrors RenderInventory's layout but
        // with the 3×3 input grid + arrow + output slot stacked on top
        // of the player's main + hotbar grids. The same dim-wash + slot
        // wells + cursor stack draw so the panel reads as part of the
        // same family of modal screens. All slot positions come from
        // CraftingScreen.GetSlotRect — the renderer never duplicates
        // geometry math the click router doesn't also use.
        private void RenderCrafting(int width, int height)
        {
            var ortho = Matrix4.CreateOrthographicOffCenter(0, width, height, 0, -1f, 1f);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            DrawSolidQuad(0, 0, width, height,
                new Vector3(0f, 0f, 0f), 0.55f, ortho);

            // ---- panel chrome (matches inventory) -----------------------
            CraftingScreen.GetPanelRect(width, height,
                out int panelX, out int panelY, out int panelW, out int panelH);
            DrawSolidQuad(panelX, panelY, panelW, panelH,
                new Vector3(0.16f, 0.16f, 0.18f), 0.95f, ortho);
            var border = new Vector3(0.78f, 0.82f, 0.88f);
            int pb = CraftingScreen.SlotBorderPx(width, height);
            DrawSolidQuad(panelX, panelY, panelW, pb, border, 1f, ortho);
            DrawSolidQuad(panelX, panelY + panelH - pb, panelW, pb, border, 1f, ortho);
            DrawSolidQuad(panelX, panelY, pb, panelH, border, 1f, ortho);
            DrawSolidQuad(panelX + panelW - pb, panelY, pb, panelH, border, 1f, ortho);

            // ---- title --------------------------------------------------
            DrawString(CraftingScreen.Title, /*scale*/CraftingScreen.TitleScale(width, height),
                /*centerX*/width / 2,
                /*topY*/CraftingScreen.TitleY(width, height),
                new Vector4(1f, 1f, 1f, 1f), ortho);

            // ---- slot wells (all 55 slots) ------------------------------
            var wellFill   = new Vector3(0.35f, 0.35f, 0.35f);
            var wellEdgeLo = new Vector3(0.10f, 0.10f, 0.10f);
            var wellEdgeHi = new Vector3(0.55f, 0.55f, 0.55f);
            for (int i = 0; i < CraftingScreen.TotalSlots; i++)
            {
                CraftingScreen.GetSlotRect(i, width, height,
                    out int sx, out int sy, out int sw, out int sh);
                DrawSlotWell(sx, sy, sw, sh, width, height, wellFill, wellEdgeLo, wellEdgeHi, ortho);
            }

            // ---- arrow (3×3 grid → output) ------------------------------
            // Simple white wedge (a vertical bar plus a triangular head)
            // — keeps the panel readable without needing a dedicated
            // sprite sheet. UiScale.S so it stays proportional at every
            // window size.
            CraftingScreen.GetArrowCenter(width, height, out int acx, out int acy);
            int arrowBarLen = UiScale.S(28, width, height);
            int arrowBarTh  = UiScale.S(6, width, height);
            int arrowHead   = UiScale.S(14, width, height);
            var arrowCol    = new Vector3(0.92f, 0.94f, 0.98f);
            // Shaft.
            DrawSolidQuad(acx - arrowBarLen / 2, acy - arrowBarTh / 2,
                arrowBarLen, arrowBarTh, arrowCol, 1f, ortho);
            // Head — stack of horizontal slices to fake a triangle without
            // needing a triangle primitive (DrawSolidQuad is the only
            // non-textured shape we have here).
            for (int i = 0; i < arrowHead; i++)
            {
                int barW = arrowHead - i;
                if (barW <= 0) break;
                DrawSolidQuad(acx + arrowBarLen / 2 + i, acy - barW,
                    1, barW * 2, arrowCol, 1f, ortho);
            }

            // ---- icons in grid + output --------------------------------
            var inv = Input?.Inventory;
            int iconPad = (CraftingScreen.SlotPx(width, height) - CraftingScreen.IconPx(width, height)) / 2;

            for (int i = 0; i < CraftingScreen.GridSlotCount; i++)
            {
                var stack = _craftingGrid[i];
                if (stack.IsEmpty) continue;
                CraftingScreen.GetSlotRect(i, width, height, out int sx, out int sy, out _, out _);
                DrawSlotIcon(stack.Type, sx + iconPad, sy + iconPad, width, height, ortho);
            }
            if (!_craftingOutput.IsEmpty)
            {
                CraftingScreen.GetSlotRect(CraftingScreen.OutputSlot, width, height,
                    out int ox, out int oy, out _, out _);
                DrawSlotIcon(_craftingOutput.Type, ox + iconPad, oy + iconPad, width, height, ortho);
            }
            // Player inventory + hotbar — same iteration pattern as the
            // survival inventory body, just remapped through
            // InventoryIndexFor since slot space here starts at 10.
            if (inv != null)
            {
                for (int i = CraftingScreen.InvMainStart; i < CraftingScreen.TotalSlots; i++)
                {
                    int invIdx = CraftingScreen.InventoryIndexFor(i);
                    if (invIdx < 0 || invIdx >= Inventory.TotalSlots) continue;
                    var stack = inv.Slots[invIdx];
                    if (stack.IsEmpty) continue;
                    CraftingScreen.GetSlotRect(i, width, height, out int sx, out int sy, out _, out _);
                    DrawSlotIcon(stack.Type, sx + iconPad, sy + iconPad, width, height, ortho);
                }
                GL.Disable(EnableCap.CullFace);
            }

            // ---- stack counts on every visible non-empty stack ---------
            for (int i = 0; i < CraftingScreen.GridSlotCount; i++)
            {
                var stack = _craftingGrid[i];
                if (stack.IsEmpty || stack.Count <= 1) continue;
                CraftingScreen.GetSlotRect(i, width, height,
                    out int sx, out int sy, out int sw, out int sh);
                DrawStackCount(stack.Count, sx, sy, sw, sh, ortho);
            }
            if (!_craftingOutput.IsEmpty && _craftingOutput.Count > 1)
            {
                CraftingScreen.GetSlotRect(CraftingScreen.OutputSlot, width, height,
                    out int ox, out int oy, out int ow, out int oh);
                DrawStackCount(_craftingOutput.Count, ox, oy, ow, oh, ortho);
            }
            if (inv != null)
            {
                for (int i = CraftingScreen.InvMainStart; i < CraftingScreen.TotalSlots; i++)
                {
                    int invIdx = CraftingScreen.InventoryIndexFor(i);
                    if (invIdx < 0 || invIdx >= Inventory.TotalSlots) continue;
                    var stack = inv.Slots[invIdx];
                    if (stack.IsEmpty || stack.Count <= 1) continue;
                    CraftingScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawStackCount(stack.Count, sx, sy, sw, sh, ortho);
                }
                // Durability bars (tools with damage) on every slot the
                // player owns. Grid + output are mostly material stacks so
                // the bar is a no-op there for typical recipes; cheap to
                // include for completeness.
                for (int i = 0; i < CraftingScreen.GridSlotCount; i++)
                {
                    var stack = _craftingGrid[i];
                    if (stack.IsEmpty) continue;
                    CraftingScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawDurabilityBar(stack, sx, sy, sw, sh, width, height, ortho);
                }
                for (int i = CraftingScreen.InvMainStart; i < CraftingScreen.TotalSlots; i++)
                {
                    int invIdx = CraftingScreen.InventoryIndexFor(i);
                    if (invIdx < 0 || invIdx >= Inventory.TotalSlots) continue;
                    var stack = inv.Slots[invIdx];
                    if (stack.IsEmpty) continue;
                    CraftingScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawDurabilityBar(stack, sx, sy, sw, sh, width, height, ortho);
                }
            }

            // ---- cursor stack (follows the mouse, drawn last) ----------
            if (inv != null && !inv.Cursor.IsEmpty && Input != null)
            {
                int cx = Input.MenuMouseX;
                int cy = Input.MenuMouseY;
                int iconSize = CraftingScreen.IconPx(width, height);
                int ix = cx - iconSize / 2;
                int iy = cy - iconSize / 2;

                if (BlockData.IsCubeShape(inv.Cursor.Type))
                {
                    RenderBlockIcon3D(inv.Cursor.Type, ix, iy, iconSize, iconSize, ortho);
                    GL.Disable(EnableCap.CullFace);
                }
                else
                {
                    DrawFlatSpriteIcon(inv.Cursor.Type, ix, iy, iconSize, iconSize, ortho);
                }
                int cursorFrame = CraftingScreen.SlotPx(width, height);
                int cursorFrameX = cx - cursorFrame / 2;
                int cursorFrameY = cy - cursorFrame / 2;
                if (inv.Cursor.Count > 1)
                {
                    DrawStackCount(inv.Cursor.Count, cursorFrameX, cursorFrameY,
                        cursorFrame, cursorFrame, ortho);
                }
                DrawDurabilityBar(inv.Cursor, cursorFrameX, cursorFrameY,
                    cursorFrame, cursorFrame, width, height, ortho);
            }

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // Survival inventory body — the original 4×9 main grid + hotbar
        // row, every slot a real ItemStack from Inventory.Slots[].
        private void RenderSurvivalInventoryBody(int width, int height, Matrix4 ortho)
        {
            // ---- slot wells ---------------------------------------------
            var wellFill   = new Vector3(0.35f, 0.35f, 0.35f);
            var wellEdgeLo = new Vector3(0.10f, 0.10f, 0.10f);
            var wellEdgeHi = new Vector3(0.55f, 0.55f, 0.55f);
            for (int i = 0; i < InventoryScreen.TotalSlots; i++)
            {
                InventoryScreen.GetSlotRect(i, width, height,
                    out int sx, out int sy, out int sw, out int sh);
                DrawSlotWell(sx, sy, sw, sh, width, height, wellFill, wellEdgeLo, wellEdgeHi, ortho);
            }

            // ---- block icons (every non-empty slot) --------------------
            var inv = Input?.Inventory;
            int iconPad = (InventoryScreen.SlotPx(width, height) - InventoryScreen.IconPx(width, height)) / 2;
            int hotbarBase = InventoryScreen.MainSlotCount;

            if (inv != null)
            {
                for (int i = 0; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty) continue;
                    InventoryScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out _, out _);
                    DrawSlotIcon(stack.Type, sx + iconPad, sy + iconPad, width, height, ortho);
                }
                GL.Disable(EnableCap.CullFace);
            }

            // ---- selected hotbar highlight -----------------------------
            int selected = Input != null ? Input.HotbarIndex : -1;
            if (selected >= 0 && selected < InventoryScreen.HotbarSlotCount)
            {
                DrawHotbarSelectionHighlight(hotbarBase + selected, width, height, ortho);
            }

            // ---- per-slot stack counts ---------------------------------
            if (inv != null)
            {
                for (int i = 0; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty || stack.Count <= 1) continue;
                    InventoryScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawStackCount(stack.Count, sx, sy, sw, sh, ortho);
                }

                // Durability bars on damaged tools — same slot iteration,
                // skipping the count-1 guard above (tools always have
                // Count=1 but still need the bar).
                for (int i = 0; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty) continue;
                    InventoryScreen.GetSlotRect(i, width, height,
                        out int sx, out int sy, out int sw, out int sh);
                    DrawDurabilityBar(stack, sx, sy, sw, sh, width, height, ortho);
                }
            }
        }

        // Creative inventory body — search bar + scrollable catalog where
        // the main grid would be in survival, plus the live hotbar row at
        // the bottom. Catalog clicks fill the cursor with a stack; hotbar
        // slots still play the standard left-click swap rules so the
        // player can pull a chosen catalog item onto a specific bar slot.
        private void RenderCreativeInventoryBody(int width, int height, Matrix4 ortho)
        {
            var inv = Input?.Inventory;
            string searchText = Input?.InventorySearchText ?? string.Empty;
            int scrollRows = Input?.InventoryScrollRows ?? 0;

            // ---- search bar ---------------------------------------------
            InventoryScreen.GetSearchBarRect(width, height,
                out int sbx, out int sby, out int sbw, out int sbh);
            DrawSolidQuad(sbx, sby, sbw, sbh,
                new Vector3(0.10f, 0.10f, 0.12f), 1f, ortho);
            // Inset border: dark inner ring so the bar reads like a text field.
            var sbBorder = new Vector3(0.55f, 0.60f, 0.66f);
            DrawSolidQuad(sbx, sby, sbw, 1, sbBorder, 1f, ortho);
            DrawSolidQuad(sbx, sby + sbh - 1, sbw, 1, sbBorder, 1f, ortho);
            DrawSolidQuad(sbx, sby, 1, sbh, sbBorder, 1f, ortho);
            DrawSolidQuad(sbx + sbw - 1, sby, 1, sbh, sbBorder, 1f, ortho);

            int sbScale = InventoryScreen.TitleScale(width, height);
            int sbGlyphW = HotbarTextures.GlyphCellW * sbScale;
            int sbGlyphH = HotbarTextures.GlyphCellH * sbScale;
            int sbTextY = sby + (sbh - sbGlyphH) / 2;
            int sbTextPad = UiScale.S(6, width, height);

            string display = string.IsNullOrEmpty(searchText) ? "Search..." : searchText.ToUpperInvariant();
            var sbTint = string.IsNullOrEmpty(searchText)
                ? new Vector4(0.55f, 0.58f, 0.64f, 1f)   // placeholder grey
                : new Vector4(1f, 1f, 1f, 1f);
            DrawDigits(display, sbx + sbTextPad, sbTextY, sbGlyphW, sbGlyphH, sbTint, ortho);

            // Caret blink — visible while there's any text and ~0.5 s on/off.
            // Keyed off _timeOfDay so it ticks with the world clock without
            // needing a new field. (Inventory is open for short windows;
            // any cadence reads as "alive".)
            bool caretOn = ((int)(_timeOfDay * 200f) & 1) == 0;
            if (caretOn)
            {
                int caretX = sbx + sbTextPad + (string.IsNullOrEmpty(searchText) ? 0 : searchText.Length * sbGlyphW);
                DrawSolidQuad(caretX, sbTextY, UiScale.S(2, width, height), sbGlyphH, new Vector3(1f, 1f, 1f), 1f, ortho);
            }

            // ---- catalog grid -------------------------------------------
            InventoryScreen.GetCatalogRect(width, height,
                out int cx, out int cy, out int cw, out int ch);

            // Catalog area background — same well palette as a slot, but
            // one big rect so the grid floats inside it. The 2-px gutter
            // around the well grows with UiScale so it stays a visible
            // outline at fullscreen.
            int catalogGutter = UiScale.S(2, width, height);
            DrawSolidQuad(cx - catalogGutter, cy - catalogGutter,
                cw + catalogGutter * 2, ch + catalogGutter * 2,
                new Vector3(0.10f, 0.10f, 0.12f), 1f, ortho);
            DrawSolidQuad(cx, cy, cw, ch, new Vector3(0.20f, 0.20f, 0.22f), 1f, ortho);

            var filtered = CreativeCatalog.Filter(searchText);
            int totalRows = (filtered.Count + InventoryScreen.Cols - 1) / InventoryScreen.Cols;
            int maxScroll = System.Math.Max(0, totalRows - InventoryScreen.CatalogRows);
            if (scrollRows > maxScroll) scrollRows = maxScroll;
            if (scrollRows < 0) scrollRows = 0;

            // Push the clamped scroll back into Input so the click code
            // sees a consistent value next frame.
            if (Input != null) Input.InventoryScrollRows = scrollRows;

            int iconPad = (InventoryScreen.SlotPx(width, height) - InventoryScreen.IconPx(width, height)) / 2;
            var catalogWellFill   = new Vector3(0.30f, 0.30f, 0.32f);
            var catalogEdgeLo     = new Vector3(0.08f, 0.08f, 0.10f);
            var catalogEdgeHi     = new Vector3(0.50f, 0.50f, 0.55f);

            int hoverTile = -1;
            int mxh = Input?.MenuMouseX ?? -1;
            int myh = Input?.MenuMouseY ?? -1;
            if (mxh >= 0)
            {
                hoverTile = InventoryScreen.HitTestCatalogTile(width, height, mxh, myh);
            }

            for (int row = 0; row < InventoryScreen.CatalogRows; row++)
            {
                for (int col = 0; col < InventoryScreen.Cols; col++)
                {
                    int tx = cx + col * InventoryScreen.SlotPx(width, height);
                    int ty = cy + row * InventoryScreen.SlotPx(width, height);
                    int tile = row * InventoryScreen.Cols + col;
                    int absolute = (scrollRows + row) * InventoryScreen.Cols + col;

                    bool inRange = absolute < filtered.Count;
                    var fill = inRange ? catalogWellFill : new Vector3(0.18f, 0.18f, 0.20f);
                    DrawSlotWell(tx, ty, InventoryScreen.SlotPx(width, height), InventoryScreen.SlotPx(width, height),
                        width, height, fill, catalogEdgeLo, catalogEdgeHi, ortho);

                    // Hover ring on the live tile under the mouse.
                    if (inRange && tile == hoverTile)
                    {
                        var hi = new Vector3(1f, 1f, 1f);
                        int b = InventoryScreen.SlotBorderPx(width, height);
                        DrawSolidQuad(tx, ty, InventoryScreen.SlotPx(width, height), b, hi, 1f, ortho);
                        DrawSolidQuad(tx, ty + InventoryScreen.SlotPx(width, height) - b, InventoryScreen.SlotPx(width, height), b, hi, 1f, ortho);
                        DrawSolidQuad(tx, ty, b, InventoryScreen.SlotPx(width, height), hi, 1f, ortho);
                        DrawSolidQuad(tx + InventoryScreen.SlotPx(width, height) - b, ty, b, InventoryScreen.SlotPx(width, height), hi, 1f, ortho);
                    }
                }
            }

            // Catalog icons (skip empty trailing tiles).
            for (int row = 0; row < InventoryScreen.CatalogRows; row++)
            {
                for (int col = 0; col < InventoryScreen.Cols; col++)
                {
                    int absolute = (scrollRows + row) * InventoryScreen.Cols + col;
                    if (absolute >= filtered.Count) continue;
                    int tx = cx + col * InventoryScreen.SlotPx(width, height);
                    int ty = cy + row * InventoryScreen.SlotPx(width, height);
                    DrawSlotIcon(filtered[absolute], tx + iconPad, ty + iconPad, width, height, ortho);
                }
            }
            // RenderBlockIcon3D toggles cull state.
            GL.Disable(EnableCap.CullFace);

            // ---- scrollbar ----------------------------------------------
            // Thin track on the right edge of the catalog, with a thumb
            // sized to the visible window. Purely decorative — the wheel
            // drives the actual scroll — but a useful visual cue when the
            // catalog overflows.
            if (totalRows > InventoryScreen.CatalogRows)
            {
                int trackW = UiScale.S(4, width, height);
                int trackInset = UiScale.S(2, width, height);
                int trackX = cx + cw - trackW - trackInset;
                int trackY = cy + trackInset;
                int trackH = ch - trackInset * 2;
                DrawSolidQuad(trackX, trackY, trackW, trackH,
                    new Vector3(0.12f, 0.12f, 0.14f), 1f, ortho);

                int thumbH = System.Math.Max(UiScale.S(8, width, height),
                    trackH * InventoryScreen.CatalogRows / totalRows);
                int thumbY = trackY +
                    (maxScroll == 0 ? 0 : (trackH - thumbH) * scrollRows / maxScroll);
                DrawSolidQuad(trackX, thumbY, trackW, thumbH,
                    new Vector3(0.62f, 0.66f, 0.72f), 1f, ortho);
            }

            // ---- hotbar wells + icons -----------------------------------
            // Always visible so the player can see (and click into) their
            // current loadout while picking from the catalog.
            var wellFill   = new Vector3(0.35f, 0.35f, 0.35f);
            var wellEdgeLo = new Vector3(0.10f, 0.10f, 0.10f);
            var wellEdgeHi = new Vector3(0.55f, 0.55f, 0.55f);
            int hotbarBase = InventoryScreen.MainSlotCount;
            for (int i = hotbarBase; i < InventoryScreen.TotalSlots; i++)
            {
                InventoryScreen.GetSlotRect(i, width, height, /*creative*/true,
                    out int hsx, out int hsy, out int hsw, out int hsh);
                DrawSlotWell(hsx, hsy, hsw, hsh, width, height, wellFill, wellEdgeLo, wellEdgeHi, ortho);
            }
            if (inv != null)
            {
                for (int i = hotbarBase; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty) continue;
                    InventoryScreen.GetSlotRect(i, width, height, /*creative*/true,
                        out int hsx, out int hsy, out _, out _);
                    DrawSlotIcon(stack.Type, hsx + iconPad, hsy + iconPad, width, height, ortho);
                }
                GL.Disable(EnableCap.CullFace);
            }

            int selected = Input != null ? Input.HotbarIndex : -1;
            if (selected >= 0 && selected < InventoryScreen.HotbarSlotCount)
            {
                DrawHotbarSelectionHighlight(hotbarBase + selected, width, height, ortho, /*creative*/true);
            }

            if (inv != null)
            {
                for (int i = hotbarBase; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty || stack.Count <= 1) continue;
                    InventoryScreen.GetSlotRect(i, width, height, /*creative*/true,
                        out int hsx, out int hsy, out int hsw, out int hsh);
                    DrawStackCount(stack.Count, hsx, hsy, hsw, hsh, ortho);
                }

                // Durability bars on damaged hotbar tools (creative pulls
                // pristine tools from the catalog so this is mostly for
                // the creative-survival hand-off cases — still cheap).
                for (int i = hotbarBase; i < InventoryScreen.TotalSlots; i++)
                {
                    var stack = inv.Slots[i];
                    if (stack.IsEmpty) continue;
                    InventoryScreen.GetSlotRect(i, width, height, /*creative*/true,
                        out int hsx, out int hsy, out int hsw, out int hsh);
                    DrawDurabilityBar(stack, hsx, hsy, hsw, hsh, width, height, ortho);
                }
            }

            // ---- catalog hover tooltip ----------------------------------
            // Show the friendly name of the catalog tile under the cursor
            // along the bottom of the panel — quick way to confirm what the
            // player's about to pick without re-reading the search text.
            if (hoverTile >= 0)
            {
                int absolute = (scrollRows + hoverTile / InventoryScreen.Cols) * InventoryScreen.Cols
                             + hoverTile % InventoryScreen.Cols;
                if (absolute < filtered.Count)
                {
                    string label = CreativeCatalog.FriendlyName(filtered[absolute]);
                    InventoryScreen.GetPanelRect(width, height, /*creative*/true,
                        out int ppx, out int ppy, out int ppw, out int pph);
                    int tipScale = InventoryScreen.TitleScale(width, height);
                    DrawString(label, /*scale*/tipScale,
                        /*centerX*/ppx + ppw / 2,
                        /*topY*/ppy + pph - HotbarTextures.GlyphCellH * tipScale - UiScale.S(4, width, height),
                        new Vector4(1f, 1f, 1f, 1f), ortho);
                }
            }
        }

        // Slot well + chiseled border helper (extracted so survival and
        // creative bodies share the exact same look). The viewport size is
        // threaded in so the border thickness scales with UiScale alongside
        // the slot's own dimensions.
        private void DrawSlotWell(int x, int y, int w, int h, int viewW, int viewH,
            Vector3 fill, Vector3 edgeLo, Vector3 edgeHi, Matrix4 ortho)
        {
            int b = InventoryScreen.SlotBorderPx(viewW, viewH);
            DrawSolidQuad(x, y, w, h, fill, 1f, ortho);
            DrawSolidQuad(x, y, w, b, edgeLo, 1f, ortho);                 // top
            DrawSolidQuad(x, y, b, h, edgeLo, 1f, ortho);                 // left
            DrawSolidQuad(x, y + h - b, w, b, edgeHi, 1f, ortho);         // bottom
            DrawSolidQuad(x + w - b, y, b, h, edgeHi, 1f, ortho);         // right
        }

        // Block icon helper — picks 3-face cube vs flat sprite based on
        // the block shape, same logic the survival path used inline. Takes
        // viewport size so the icon dimension matches the active UiScale.
        private void DrawSlotIcon(BlockType type, int xp, int yp, int viewW, int viewH, Matrix4 ortho)
        {
            int icon = InventoryScreen.IconPx(viewW, viewH);
            if (BlockData.IsCubeShape(type))
            {
                RenderBlockIcon3D(type, xp, yp, icon, icon, ortho);
            }
            else
            {
                DrawFlatSpriteIcon(type, xp, yp, icon, icon, ortho);
            }
        }

        // Selection highlight on a hotbar slot — same sprite the in-game
        // bar uses, drawn slightly oversize so it reads as a frame around
        // the slot.
        private void DrawHotbarSelectionHighlight(int slotIndex, int width, int height, Matrix4 ortho)
            => DrawHotbarSelectionHighlight(slotIndex, width, height, ortho, /*creative*/false);

        private void DrawHotbarSelectionHighlight(int slotIndex, int width, int height, Matrix4 ortho, bool creative)
        {
            _spriteShader.Use();
            _spriteShader.SetInt("uSprite", 0);
            _spriteShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            _spriteShader.SetVector2("uUvOffset", new Vector2(0f, 0f));
            _spriteShader.SetVector2("uUvScale", new Vector2(1f, 1f));
            GL.BindTexture(TextureTarget.Texture2D, _hotbarHighlightTexture);

            InventoryScreen.GetSlotRect(slotIndex, width, height, creative,
                out int sx, out int sy, out int sw, out int sh);
            int hSize = UiScale.S(HotbarTextures.HighlightSize * 2, width, height);
            int hx = sx + (sw - hSize) / 2;
            int hy = sy + (sh - hSize) / 2;
            DrawSpriteQuad(hx, hy, hSize, hSize, ortho);
        }

        // Draw a flat block-tile sprite to a HUD pixel rect. Used for
        // cross-sprite items (torch, flowers, tall grass) — those tiles
        // are an "X"-shape so a 3-face cube icon would look wrong; the
        // flat sprite reads exactly like the in-world geometry. Same V-flip
        // as the legacy hotbar path: the block atlas was authored with v=0
        // at the bottom of each tile, but the screen ortho grows y down,
        // so we map aPos.y=0 → vUV.y=1 to render top-up.
        private void DrawFlatSpriteIcon(BlockType type, int x, int y, int w, int h, Matrix4 ortho)
        {
            _spriteArrayShader.Use();
            _spriteArrayShader.SetInt("uAtlas", 0);
            _spriteArrayShader.SetVector4("uTint", new Vector4(1f, 1f, 1f, 1f));
            _spriteArrayShader.SetVector2("uUvOffset", new Vector2(0f, 1f));
            _spriteArrayShader.SetVector2("uUvScale",  new Vector2(1f, -1f));
            int layer = BlockData.GetTileIndex(type, /*side*/2);
            _spriteArrayShader.SetFloat("uLayer", layer);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);
            DrawSpriteQuadFor(_spriteArrayShader, x, y, w, h, ortho);
        }

        // Durability bar overlay for a tool stack — Alpha-style: a thin
        // 2-px (UI-scaled) horizontal bar pinned to the bottom of the
        // slot rect, colour fading from green (full durability) through
        // yellow to red (about to snap). The bar is hidden on undamaged
        // tools so a freshly-crafted pickaxe doesn't show a green stripe
        // taking up icon real estate. Slots that aren't tools are no-ops.
        private void DrawDurabilityBar(in ItemStack stack, int slotX, int slotY,
            int slotW, int slotH, int viewW, int viewH, Matrix4 ortho)
        {
            if (stack.IsEmpty) return;
            if (!BlockData.IsTool(stack.Type)) return;
            int max = ToolData.MaxDurability(stack.Type);
            if (max <= 0) return;
            int used = stack.Durability;
            if (used <= 0) return; // pristine — hide the bar
            if (used > max) used = max;

            // Bar geometry: full slot width minus a 2px inset on each
            // side, pinned to the bottom with another 2px inset so it
            // sits inside the slot border. Height = 2 UI-scaled px,
            // matching the hotbar wells' chiselled border thickness.
            int inset = UiScale.S(2, viewW, viewH);
            int barH  = UiScale.S(2, viewW, viewH);
            int barX  = slotX + inset;
            int barY  = slotY + slotH - inset - barH;
            int barW  = slotW - inset * 2;
            if (barW <= 0 || barH <= 0) return;

            // Background (dark grey, full width) gives the foreground
            // colour something to read against on light icons.
            DrawSolidQuad(barX, barY, barW, barH, new Vector3(0f, 0f, 0f), 1f, ortho);

            // Foreground length scales with remaining durability.
            float remaining = (max - used) / (float)max; // 1 = pristine, 0 = broken
            int fgW = (int)(barW * remaining);
            if (fgW <= 0) return;

            // Colour ramp: green at full → yellow at half → red at empty.
            // Hue interpolation is overkill for a 2px bar, so do a 2-leg
            // RGB lerp through (255,255,0) at 0.5.
            Vector3 colour;
            if (remaining > 0.5f)
            {
                float t = (remaining - 0.5f) * 2f; // 0..1 from yellow to green
                colour = new Vector3(1f - t, 1f, 0f);
            }
            else
            {
                float t = remaining * 2f; // 0..1 from red to yellow
                colour = new Vector3(1f, t, 0f);
            }
            DrawSolidQuad(barX, barY, fgW, barH, colour, 1f, ortho);
        }

        // Solid-colour quad at (x,y) sized (w,h) in pixels via the overlay
        // shader. Caller is responsible for blend / depth / cull state.
        private void DrawSolidQuad(int x, int y, int w, int h,
            Vector3 color, float alpha, Matrix4 ortho)
        {
            var model = Matrix4.CreateScale(w, h, 1f) * Matrix4.CreateTranslation(x, y, 0f);
            _overlayShader.Use();
            _overlayShader.SetMatrix4("uMVP", model * ortho);
            _overlayShader.SetVector3("uColor", color);
            _overlayShader.SetFloat("uAlpha", alpha);
            _unitQuadMesh.Draw();
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
            _breakCubeMesh?.Dispose();
            _shader?.Dispose();
            _overlayShader?.Dispose();
            _spriteShader?.Dispose();
            _spriteArrayShader?.Dispose();
            _crackShader?.Dispose();
            _multiFaceCubeShader?.Dispose();
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
            if (_crackTexture != 0)
            {
                GL.DeleteTexture(_crackTexture);
                _crackTexture = 0;
            }
        }
    }
}
