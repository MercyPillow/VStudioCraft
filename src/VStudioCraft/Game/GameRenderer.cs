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
out vec2 vUV;
out vec3 vNormal;
flat out int vLayer;
uniform mat4 uProjection;
uniform mat4 uView;
void main()
{
    gl_Position = uProjection * uView * vec4(aPos, 1.0);
    vUV = aUV;
    vNormal = aNormal;
    vLayer = int(aLayer);
}
";

        private const string FragmentSrc = @"#version 330 core
in vec2 vUV;
in vec3 vNormal;
flat in int vLayer;
out vec4 FragColor;
uniform sampler2DArray uAtlas;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uAmbient;
void main()
{
    // Greedy quads emit UVs that span the merged area (e.g. 0..w, 0..h); we
    // tile within each array layer by fract()ing. GL_REPEAT on the sampler
    // gives the same result but fract avoids any driver quirks at integer seams.
    vec2 tileUV = fract(vUV);
    vec4 tex = texture(uAtlas, vec3(tileUV, float(vLayer)));
    float diff = max(dot(normalize(vNormal), normalize(uSunDir)), 0.0);
    vec3 light = vec3(uAmbient) + uSunColor * diff * (1.0 - uAmbient);
    FragColor = vec4(tex.rgb * light, tex.a);
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
void main() { FragColor = vec4(uColor, 1.0); }
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
        private OverlayMesh _crosshairMesh;
        private OverlayMesh _wireCubeMesh;
        private int _atlasTexture;
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
            _crosshairMesh = BuildCrosshairMesh();
            _wireCubeMesh = BuildWireCubeMesh();
            _atlasTexture = BlockTextures.CreateAtlas();
            _initialized = true;
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
            };
            WorldSaveFormat.Save(path, header, _world);
        }

        public void UpdatePlayer(float dt, Vector3 wishHorizVel, bool wantJump)
        {
            if (_world == null) return;
            Player.Update(dt, wishHorizVel, wantJump, _world);
            SyncCameraToPlayer();
        }

        private void SyncCameraToPlayer()
        {
            Camera.Position = Player.Position + new Vector3(0f, Player.EyeHeight, 0f);
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

            if (r.IndexCount == 0)
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
            mesh.Upload(r.Verts, r.VertFloatCount, r.Indices, r.IndexCount);
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
            if (BlockData.IsSolid(_world.GetBlock(px, py, pz))) return false;
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

            GL.ClearColor(sky.X, sky.Y, sky.Z, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var proj = Camera.GetProjection(width, height);
            var view = Camera.GetView();
            var vp = view * proj;
            _frustum.UpdateFromViewProj(ref vp);

            _shader.Use();
            _shader.SetMatrix4("uProjection", proj);
            _shader.SetMatrix4("uView", view);
            _shader.SetVector3("uSunDir", sun);
            _shader.SetVector3("uSunColor", sunColor);
            _shader.SetFloat("uAmbient", ambient);
            _shader.SetInt("uAtlas", 0);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

            foreach (var kv in _chunkMeshes)
            {
                int cx = kv.Key.x, cz = kv.Key.z;
                float minX = cx * Chunk.SizeX;
                float minZ = cz * Chunk.SizeZ;
                if (!_frustum.Intersects(minX, 0, minZ, minX + Chunk.SizeX, Chunk.SizeY, minZ + Chunk.SizeZ))
                    continue;
                kv.Value.Draw();
            }

            GL.BindTexture(TextureTarget.Texture2DArray, 0);

            RenderSelectionOutline(width, height);
            RenderCrosshair(width, height);
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

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            _crosshairMesh.Draw();
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
        }

        public void Dispose()
        {
            _jobs?.Dispose();
            _jobs = null;
            foreach (var m in _chunkMeshes.Values) m.Dispose();
            _chunkMeshes.Clear();
            _crosshairMesh?.Dispose();
            _wireCubeMesh?.Dispose();
            _shader?.Dispose();
            _overlayShader?.Dispose();
            if (_atlasTexture != 0)
            {
                GL.DeleteTexture(_atlasTexture);
                _atlasTexture = 0;
            }
        }
    }
}
