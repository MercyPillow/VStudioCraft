using System;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Draws the pieces of the sky that sit "behind" the voxel world: stars,
    // sun and moon billboards, and the scrolling cloud plane. GameRenderer
    // calls Draw() sandwiched between the depth-clear and the world pass for
    // celestial bodies; clouds draw after the world so they can occlude
    // everything below y=108 without z-fighting.
    internal sealed class SkyRenderer : IDisposable
    {
        // Celestial-body radius in world units. Anything beyond the fog end
        // would be hidden, so we render sun/moon/stars with depth disabled
        // and just place them at a comfortable radius for nice projection.
        private const float SkyRadius = 100f;
        private const float SunSize = 18f;   // ~10° across on the sky dome
        private const float MoonSize = 14f;
        // 8 phases packed horizontally into a single moon strip
        // texture (full → waning → new → waxing → full). Driven by
        // the renderer's _lunarDay counter, which exposes
        // LunarPhase = _lunarDay & 7.
        private const int MoonPhaseFrames = 8;

        // Height of the cloud plane, matching Alpha exactly.
        private const float CloudY = 108f;
        // How far out from the camera the cloud plane extends. Should be a
        // bit larger than the fog end so the plane fills the whole visible
        // ring of sky; fog then fades its edges.
        private const float CloudHalfExtent = 160f;
        // World units covered by one cloud-texture repeat. 64 blocks ≈ four
        // chunks — keeps clouds fairly large without looking samey.
        private const float CloudTileWorld = 64f;
        // Horizontal drift in blocks/sec. Alpha clouds move at ~0.05 blocks
        // per tick = 1 bl/s; we double it so test worlds show motion quickly.
        private const float CloudWindX = 2.0f;

        // Number of stars scattered on the celestial sphere. Alpha's night
        // sky is densely peppered; 500 at 3-px size feels about right.
        private const int StarCount = 500;

        private const string BillboardVS = @"#version 330 core
layout(location = 0) in vec3 aCorner; // [-0.5,0.5]^2 quad corner, z=0
uniform mat4 uProjection;
uniform mat4 uView;
uniform vec3 uWorldPos;   // billboard centre (world space)
uniform float uSize;      // world-space edge length
out vec2 vUV;
void main()
{
    // Pull right/up basis vectors out of the view matrix so the quad always
    // faces the camera, regardless of how the camera yaws or pitches.
    vec3 right = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 up    = vec3(uView[0][1], uView[1][1], uView[2][1]);
    vec3 worldP = uWorldPos + right * (aCorner.x * uSize) + up * (aCorner.y * uSize);
    gl_Position = uProjection * uView * vec4(worldP, 1.0);
    vUV = aCorner.xy + 0.5;
}
";

        private const string BillboardFS = @"#version 330 core
in vec2 vUV;
out vec4 FragColor;
uniform sampler2D uTex;
uniform vec4 uTint;
// UV transform — selects a sub-region of the source texture so a
// single billboard quad can sample one frame of an N-frame strip
// (used by the moon-phase 8-frame texture). Default is (0,0)
// offset + (1,1) scale for the sun's whole-texture sample.
uniform vec2 uUVOffset;
uniform vec2 uUVScale;
void main()
{
    vec2 sampleUV = uUVOffset + vUV * uUVScale;
    vec4 t = texture(uTex, sampleUV);
    if (t.a < 0.01) discard;
    FragColor = vec4(t.rgb * uTint.rgb, t.a * uTint.a);
}
";

        private const string StarVS = @"#version 330 core
layout(location = 0) in vec3 aDir;      // unit vector on celestial sphere
layout(location = 1) in float aBright;  // per-star brightness in [0.4, 1.0]
uniform mat4 uProjection;
uniform mat4 uView;
uniform vec3 uCamPos;
uniform mat3 uSkyRot;   // rotates sky-local directions into world space
uniform float uPointSize;
out float vBright;
void main()
{
    vec3 dirWorld = uSkyRot * aDir;
    vec3 worldP = uCamPos + dirWorld * 100.0; // distance doesn't matter since points face camera
    gl_Position = uProjection * uView * vec4(worldP, 1.0);
    gl_PointSize = uPointSize;
    vBright = aBright;
}
";

        // Round-point fragment: fall off to transparent at the circle edge so
        // stars look like discs instead of hard squares.
        private const string StarFS = @"#version 330 core
in float vBright;
out vec4 FragColor;
uniform float uAlpha;
void main()
{
    vec2 uv = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(uv, uv);
    if (r2 > 1.0) discard;
    float disc = 1.0 - r2;
    FragColor = vec4(vec3(1.0) * vBright, disc * uAlpha * vBright);
}
";

        private const string CloudVS = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uProjection;
uniform mat4 uView;
uniform vec2 uUvOffset;
uniform float uUvScale;
out vec2 vUV;
out float vViewDist;
void main()
{
    vec4 viewP = uView * vec4(aPos, 1.0);
    gl_Position = uProjection * viewP;
    // UV follows absolute world XZ, not the moving camera-centred mesh, so
    // clouds drift past the player rather than sticking to them.
    vUV = aPos.xz * uUvScale + uUvOffset;
    vViewDist = length(viewP.xyz);
}
";

        private const string CloudFS = @"#version 330 core
in vec2 vUV;
in float vViewDist;
out vec4 FragColor;
uniform sampler2D uClouds;
uniform vec3 uTint;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
void main()
{
    vec4 c = texture(uClouds, vUV);
    if (c.a < 0.02) discard;
    vec3 rgb = c.rgb * uTint;
    float fog = clamp((vViewDist - uFogStart) / max(uFogEnd - uFogStart, 0.0001), 0.0, 1.0);
    rgb = mix(rgb, uFogColor, fog);
    FragColor = vec4(rgb, c.a);
}
";

        private Shader _billboardShader;
        private Shader _starShader;
        private Shader _cloudShader;
        private int _sunTex;
        private int _moonTex;
        private int _cloudTex;

        // Unit-quad centred at origin ([-0.5,0.5]^2) for billboarding.
        private OverlayMesh _quad;
        // Bag of stars: packed (dir.xyz, brightness) × StarCount, one vertex
        // per star, drawn with GL_POINTS.
        private int _starVao;
        private int _starVbo;
        // Cloud plane: a recentered quad around origin, translated to the
        // camera at draw time so we never run out of coverage at the edges.
        private OverlayMesh _cloudMesh;

        // Cloud wind accumulator (world units of drift).
        private float _cloudOffsetX;

        public void Initialize()
        {
            _billboardShader = new Shader(BillboardVS, BillboardFS);
            _starShader = new Shader(StarVS, StarFS);
            _cloudShader = new Shader(CloudVS, CloudFS);
            _sunTex = SkyTextures.CreateSunTexture();
            _moonTex = SkyTextures.CreateMoonPhasesTexture(MoonPhaseFrames);
            _cloudTex = SkyTextures.CreateCloudTexture();
            _quad = BuildCentredQuad();
            BuildStars();
            _cloudMesh = BuildCloudPlane();
        }

        private static OverlayMesh BuildCentredQuad()
        {
            const float h = 0.5f;
            float[] v =
            {
                -h, -h, 0,  h, -h, 0,  h, h, 0,
                -h, -h, 0,  h,  h, 0, -h, h, 0,
            };
            var m = new OverlayMesh { Primitive = PrimitiveType.Triangles };
            m.Upload(v);
            return m;
        }

        // Cloud plane at local y=0. It will be translated in world space so
        // y=CloudY when drawn; centred on the camera in XZ so we always have
        // coverage. Size = 2*CloudHalfExtent per side. 32×32 grid = 1024
        // quads → smooth fog gradient across the plane.
        private static OverlayMesh BuildCloudPlane()
        {
            const int Grid = 32;
            float step = (CloudHalfExtent * 2f) / Grid;
            float[] v = new float[Grid * Grid * 6 * 3];
            int o = 0;
            for (int iz = 0; iz < Grid; iz++)
            for (int ix = 0; ix < Grid; ix++)
            {
                float x0 = -CloudHalfExtent + ix * step;
                float x1 = x0 + step;
                float z0 = -CloudHalfExtent + iz * step;
                float z1 = z0 + step;
                // Two triangles, wound so normal points up (cull-backed).
                v[o++] = x0; v[o++] = 0; v[o++] = z0;
                v[o++] = x1; v[o++] = 0; v[o++] = z0;
                v[o++] = x1; v[o++] = 0; v[o++] = z1;
                v[o++] = x0; v[o++] = 0; v[o++] = z0;
                v[o++] = x1; v[o++] = 0; v[o++] = z1;
                v[o++] = x0; v[o++] = 0; v[o++] = z1;
            }
            var m = new OverlayMesh { Primitive = PrimitiveType.Triangles };
            m.Upload(v);
            return m;
        }

        private void BuildStars()
        {
            // Spread stars over the full celestial sphere with a slight bias
            // away from the poles so they don't cluster. Brightness jitter
            // means the sky has a few bright stars and many faint ones.
            var rng = new Random(0x57A25);
            float[] data = new float[StarCount * 4];
            int o = 0;
            for (int i = 0; i < StarCount; i++)
            {
                // Uniform unit vector via rejection sampling.
                double x, y, z, len2;
                do
                {
                    x = rng.NextDouble() * 2 - 1;
                    y = rng.NextDouble() * 2 - 1;
                    z = rng.NextDouble() * 2 - 1;
                    len2 = x * x + y * y + z * z;
                } while (len2 > 1.0 || len2 < 1e-4);
                double inv = 1.0 / Math.Sqrt(len2);
                data[o++] = (float)(x * inv);
                data[o++] = (float)(y * inv);
                data[o++] = (float)(z * inv);
                // Brightness skewed toward mid (`u^2` gives more faint stars).
                float u = (float)rng.NextDouble();
                data[o++] = 0.4f + 0.6f * u * u;
            }

            _starVao = GL.GenVertexArray();
            _starVbo = GL.GenBuffer();
            GL.BindVertexArray(_starVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _starVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
        }

        // Advance the cloud scroll offset. Game renderer's AdvanceTime
        // drives us every frame.
        public void Advance(float dt)
        {
            _cloudOffsetX += CloudWindX * dt;
        }

        // Render the sun, moon and stars at the "back" of the scene. Caller
        // must have cleared colour + depth already. Leaves GL state in a
        // restored configuration so the world pass can run unmodified.
        public void RenderCelestial(Matrix4 projection, Matrix4 view, Vector3 camPos,
            float sunAngleRad, Vector3 sunDir, Vector3 antiSunDir,
            int moonPhase = 0)
        {
            // No depth: sun/moon/stars are "at infinity" and the world pass
            // will overdraw anything in front of them.
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // ---- Stars ----
            // Alpha-fade stars to zero whenever the sun is above horizon.
            // At pure midnight (sun.Y < -0.2) stars are fully on.
            float starAlpha = 0f;
            if (sunDir.Y < 0f)
            {
                starAlpha = Math.Min(1f, (-sunDir.Y) / 0.2f);
            }
            if (starAlpha > 0.01f)
            {
                // Sky rotates with the sun angle about the Z axis, so stars
                // rotate with it (night sky drifts across the dome).
                var skyRot = Matrix3.CreateRotationZ(sunAngleRad);
                _starShader.Use();
                _starShader.SetMatrix4("uProjection", projection);
                _starShader.SetMatrix4("uView", view);
                _starShader.SetVector3("uCamPos", camPos);
                _starShader.SetMatrix3("uSkyRot", skyRot);
                _starShader.SetFloat("uPointSize", 2.5f);
                _starShader.SetFloat("uAlpha", starAlpha);
                GL.Enable(EnableCap.ProgramPointSize);
                GL.BindVertexArray(_starVao);
                GL.DrawArrays(PrimitiveType.Points, 0, StarCount);
                GL.BindVertexArray(0);
                GL.Disable(EnableCap.ProgramPointSize);
            }

            // ---- Sun ----
            float sunAlpha = Math.Max(0f, Math.Min(1f, (sunDir.Y + 0.15f) / 0.3f));
            if (sunAlpha > 0.01f)
            {
                DrawBillboard(projection, view, camPos + sunDir * SkyRadius, SunSize,
                    _sunTex, new Vector4(1f, 1f, 1f, sunAlpha));
            }

            // ---- Moon ---- 8-phase strip texture; sample column =
            // moonPhase out of MoonPhaseFrames. uvScale.x = 1/N picks
            // a single frame's worth of texture width.
            float moonAlpha = Math.Max(0f, Math.Min(1f, (antiSunDir.Y + 0.15f) / 0.3f));
            if (moonAlpha > 0.01f)
            {
                int phase = ((moonPhase % MoonPhaseFrames) + MoonPhaseFrames) % MoonPhaseFrames;
                float uOffset = phase / (float)MoonPhaseFrames;
                float uScale  = 1f / MoonPhaseFrames;
                DrawBillboard(projection, view, camPos + antiSunDir * SkyRadius, MoonSize,
                    _moonTex, new Vector4(1f, 1f, 1f, moonAlpha),
                    new Vector2(uOffset, 0f), new Vector2(uScale, 1f));
            }

            // Leave GL state how the world pass expects it.
            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
        }

        // Render the cloud plane. Call this AFTER the opaque + transparent
        // world passes so clouds occlude everything they sit in front of
        // without writing to the depth buffer (same rule as water).
        public void RenderClouds(Matrix4 projection, Matrix4 view, Vector3 camPos,
            Vector3 skyColor, float fogStart, float fogEnd, float sunY)
        {
            // Skip entirely when the camera is already above CloudY + a bit:
            // the plane is one-sided (culled) and would show no back face.
            // (We could flip culling, but Alpha clouds are functionally one-
            // sided too — you see them from below, not above.)
            if (camPos.Y > CloudY + 4f) return;

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);   // clouds don't occlude depth; water etc. already drew
            GL.Enable(EnableCap.DepthTest);
            // Leave face cull on — the cloud mesh is wound so its top-face
            // normal points +Y and the player views from below.

            // Tint clouds toward dusk/night colour based on sun altitude so
            // they glow orange at sunset and turn grey at night.
            float t = Math.Max(0f, Math.Min(1f, sunY * 2f + 0.5f));
            var dayTint = new Vector3(1f, 1f, 1f);
            var nightTint = new Vector3(0.35f, 0.38f, 0.45f);
            Vector3 tint = nightTint + (dayTint - nightTint) * t;

            // Centre the cloud plane on the camera's XZ so it always covers
            // the visible area, and raise it to CloudY.
            var model = Matrix4.CreateTranslation((float)Math.Floor(camPos.X), CloudY, (float)Math.Floor(camPos.Z));

            _cloudShader.Use();
            _cloudShader.SetMatrix4("uProjection", projection);
            _cloudShader.SetMatrix4("uView", model * view); // model merged into view so aPos is still absolute world pre-translate
            _cloudShader.SetVector2("uUvOffset",
                new Vector2(_cloudOffsetX / CloudTileWorld + (float)Math.Floor(camPos.X) / CloudTileWorld,
                             (float)Math.Floor(camPos.Z) / CloudTileWorld));
            _cloudShader.SetFloat("uUvScale", 1f / CloudTileWorld);
            _cloudShader.SetVector3("uTint", tint);
            _cloudShader.SetVector3("uFogColor", skyColor);
            _cloudShader.SetFloat("uFogStart", fogStart);
            _cloudShader.SetFloat("uFogEnd", fogEnd);
            _cloudShader.SetInt("uClouds", 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _cloudTex);

            _cloudMesh.Draw();

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        private void DrawBillboard(Matrix4 projection, Matrix4 view, Vector3 worldPos,
            float size, int texture, Vector4 tint)
        {
            DrawBillboard(projection, view, worldPos, size, texture, tint,
                Vector2.Zero, Vector2.One);
        }

        // Variant with explicit UV transform — used for the moon
        // strip texture so each phase samples one of the 8 sub-frames.
        private void DrawBillboard(Matrix4 projection, Matrix4 view, Vector3 worldPos,
            float size, int texture, Vector4 tint, Vector2 uvOffset, Vector2 uvScale)
        {
            _billboardShader.Use();
            _billboardShader.SetMatrix4("uProjection", projection);
            _billboardShader.SetMatrix4("uView", view);
            _billboardShader.SetVector3("uWorldPos", worldPos);
            _billboardShader.SetFloat("uSize", size);
            _billboardShader.SetVector4("uTint", tint);
            _billboardShader.SetVector2("uUVOffset", uvOffset);
            _billboardShader.SetVector2("uUVScale",  uvScale);
            _billboardShader.SetInt("uTex", 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            _quad.Draw();
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Dispose()
        {
            _billboardShader?.Dispose();
            _starShader?.Dispose();
            _cloudShader?.Dispose();
            _quad?.Dispose();
            _cloudMesh?.Dispose();
            if (_sunTex != 0)   { GL.DeleteTexture(_sunTex);   _sunTex = 0; }
            if (_moonTex != 0)  { GL.DeleteTexture(_moonTex);  _moonTex = 0; }
            if (_cloudTex != 0) { GL.DeleteTexture(_cloudTex); _cloudTex = 0; }
            if (_starVbo != 0)  { GL.DeleteBuffer(_starVbo);   _starVbo = 0; }
            if (_starVao != 0)  { GL.DeleteVertexArray(_starVao); _starVao = 0; }
        }
    }
}
