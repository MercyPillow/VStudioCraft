using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Tier 6 — Multi-frame animated background, used by the title
    // screen to play a looping day/night GIF instead of a flat sky-
    // blue fill. Decodes once at startup via WPF's BitmapDecoder
    // (handles GIF frame iteration + frame composition correctly,
    // unlike the bare System.Drawing path which can return raw
    // sub-rect frames or all-transparent layers depending on the
    // disposal method). Each composed frame is uploaded as a layer
    // of a Texture2DArray; the renderer samples the array via the
    // existing sprite-array shader so no new shader path is needed.
    internal sealed class AnimatedBackground : IDisposable
    {
        public int Texture { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameCount { get; private set; }
        public bool Loaded { get; private set; }
        // Diagnostic — non-empty when load failed; surfaced by the
        // title screen as a small subtitle so we can see WHY the GIF
        // isn't rendering when it doesn't.
        public string DiagStatus { get; private set; } = "(not attempted)";

        // Per-frame delays in seconds. Defaults to 0.10 s when the
        // GIF has no explicit metadata — matches the canonical
        // browser default for delay=0 frames.
        private float[] _frameDelays;
        private float _totalDurationSec;

        // Title-screen day/night clips are typically authored at slow
        // browser-friendly cadences (~50-200 ms per frame). 1× wall
        // clock takes ~1-2 minutes for a full cycle which feels
        // glacial behind a menu; 4× felt distractingly fast in
        // practice; 3× is the sweet spot — visibly animated, not so
        // quick the player notices.
        private const float SpeedMultiplier = 3.0f;

        // Wall-clock anchor (seconds since first call). The first
        // CurrentLayer poll captures this so frame 0 lines up with
        // the moment the title screen first appears, not with app start.
        private double _startSeconds = -1.0;
        private static readonly System.Diagnostics.Stopwatch _wallClock = System.Diagnostics.Stopwatch.StartNew();

        // Try to load the GIF from a manifest resource. Returns false
        // on any error so the renderer can fall back to its solid
        // sky-blue clear without crashing the title screen.
        public bool TryLoadFromManifestResource(string resourceName)
        {
            // Try every accessible assembly's manifest, not just
            // GetExecutingAssembly — when AnimatedBackground.cs is
            // linked into Standalone via the globbed <Compile>, the
            // executing assembly IS Standalone, but the resource
            // could in principle live in a different assembly (VSIX
            // host, third-party). Also fall back to a side-by-side
            // file in the EXE's directory so a pure-content shipping
            // mode works without rebuild.
            try
            {
                var asms = new System.Collections.Generic.List<Assembly>();
                asms.Add(Assembly.GetExecutingAssembly());
                var entry = Assembly.GetEntryAssembly();
                if (entry != null && !asms.Contains(entry)) asms.Add(entry);
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a != null && !asms.Contains(a)) asms.Add(a);
                }

                int asmIdx = 0;
                foreach (var asm in asms)
                {
                    asmIdx++;
                    string[] names;
                    try { names = asm.GetManifestResourceNames(); }
                    catch { continue; }
                    foreach (var n in names)
                    {
                        if (string.Equals(n, resourceName, StringComparison.Ordinal)
                            || n.IndexOf("Day_Night", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                using (var s = asm.GetManifestResourceStream(n))
                                {
                                    if (s == null) { DiagStatus = $"stream null: {n} (asm{asmIdx})"; continue; }
                                    if (LoadFromStream(s)) { DiagStatus = $"ok {n}"; return true; }
                                }
                            }
                            catch (Exception ex)
                            {
                                DiagStatus = $"decode threw: {ex.GetType().Name} {ex.Message}";
                            }
                        }
                    }
                }

                // Side-by-side file fallback.
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                    string sideBy = System.IO.Path.Combine(baseDir, "Assets", "Day_Night.gif");
                    if (System.IO.File.Exists(sideBy))
                    {
                        using (var fs = System.IO.File.OpenRead(sideBy))
                        {
                            if (LoadFromStream(fs)) { DiagStatus = "ok side-by-side"; return true; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagStatus = $"sxs threw: {ex.GetType().Name} {ex.Message}";
                }

                if (string.IsNullOrEmpty(DiagStatus) || DiagStatus == "(not attempted)")
                    DiagStatus = "no resource matched 'Day_Night'";
                return false;
            }
            catch (Exception ex)
            {
                DiagStatus = $"outer threw: {ex.GetType().Name} {ex.Message}";
                return false;
            }
        }

        private bool LoadFromStream(Stream stream)
        {
            try { return LoadFromStreamCore(stream); }
            catch (Exception ex)
            {
                DiagStatus = $"core threw: {ex.GetType().Name} {ex.Message}";
                return false;
            }
        }

        private bool LoadFromStreamCore(Stream stream)
        {
            // Copy to a memory stream the decoder can keep open. WPF's
            // GIF decoder lazy-evaluates frame pixels, so we need the
            // backing stream alive for the whole CopyPixels loop.
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            // OnLoad caches every frame eagerly so the source stream
            // can be closed after the decoder constructor returns.
            // PreservePixelFormat keeps the alpha channel intact so a
            // transparent crossfade frame doesn't get baked to opaque.
            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            int n = decoder.Frames.Count;
            if (n <= 0) return false;

            // Use the first frame's pixel size as the canvas. Every
            // frame is converted to BGRA8 + reblitted onto an
            // accumulating canvas to handle GIF's "additive frames"
            // disposal model.
            var firstFrame = decoder.Frames[0];
            Width = firstFrame.PixelWidth;
            Height = firstFrame.PixelHeight;

            // Cap GPU layers. A 565-frame 640×360 GIF would want
            // ~520 MB of array-texture VRAM — past the GL_MAX_ARRAY_
            // TEXTURE_LAYERS cap on most GPUs (commonly 256, sometimes
            // 2048) and gratuitous in any case. Skip frames evenly
            // until we're at or below MaxLayers; every kept frame
            // accumulates the delays of the frames it absorbed so
            // total clip duration stays the same.
            const int MaxLayers = 64;
            int srcN = n;
            int keepN = System.Math.Min(srcN, MaxLayers);
            FrameCount = keepN;
            _frameDelays = new float[keepN];

            // Accumulating canvas — pixels are inherited from the
            // previous frame and overwritten by each new frame's
            // non-transparent area. This is the simplest disposal
            // model that gets day/night-cycle GIFs (single-shot
            // composed cycle, no per-frame restore) rendering
            // correctly. More elaborate disposal handling would
            // need to consult /grctlext/Disposal per frame.
            var canvas = new byte[Width * Height * 4];
            // Init canvas to transparent — first frame paints over.
            for (int i = 0; i < canvas.Length; i++) canvas[i] = 0;

            Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, Texture);
            GL.TexImage3D(TextureTarget.Texture2DArray, 0,
                PixelInternalFormat.Rgba, Width, Height, keepN, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            // Walk every source frame so disposal/composition stays
            // correct (we have to apply each delta to the canvas even
            // for frames we won't ultimately keep), but only Upload
            // the keepN-many evenly-spaced ones to GPU layers.
            _totalDurationSec = 0f;
            int kept = 0;
            float pendingDelay = 0f;
            for (int i = 0; i < srcN; i++)
            {
                var frame = decoder.Frames[i];
                pendingDelay += ReadFrameDelaySeconds(frame);
                CompositeFrameOntoCanvas(frame, canvas);

                // Decide whether to keep this source frame as a GPU
                // layer. Even-spacing rule: keep frame i iff its
                // index lands on the kept-out grid.
                bool shouldKeep = (long)(i + 1) * keepN / srcN > kept;
                if (shouldKeep && kept < keepN)
                {
                    _frameDelays[kept] = System.Math.Max(0.01f, pendingDelay);
                    _totalDurationSec += _frameDelays[kept];
                    UploadLayerFlipped(canvas, kept);
                    kept++;
                    pendingDelay = 0f;
                }
            }
            // Roll any leftover delay into the last kept frame so the
            // clip's total duration is preserved exactly.
            if (pendingDelay > 0f && kept > 0)
            {
                _frameDelays[kept - 1] += pendingDelay;
                _totalDurationSec += pendingDelay;
            }

            // Re-bind the texture so the TexParameter calls below
            // actually operate on it. UploadLayerFlipped unbinds at
            // the end of each call (defensive housekeeping); without
            // this re-bind the params silently no-op and the texture
            // stays at the default NEAREST_MIPMAP_LINEAR min filter,
            // which without mipmaps renders BLACK on most drivers.
            GL.BindTexture(TextureTarget.Texture2DArray, Texture);
            if (_totalDurationSec < 0.01f) _totalDurationSec = 0.01f;

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            Loaded = true;
            return true;
        }

        // Read the GIF's per-frame delay metadata. Path is the WPF
        // standard "/grctlext/Delay" — value is in 1/100 s. Falls
        // back to 0.10 s for frames missing the metadata.
        private static float ReadFrameDelaySeconds(BitmapFrame frame)
        {
            float seconds = 0.10f;
            try
            {
                if (frame.Metadata is BitmapMetadata md)
                {
                    var v = md.GetQuery("/grctlext/Delay");
                    if (v is ushort us)  seconds = us / 100f;
                    else if (v is short ss)   seconds = ss / 100f;
                    else if (v is int ii)     seconds = ii / 100f;
                    else if (v is uint ui)    seconds = ui / 100f;
                }
            }
            catch { }
            // Apply the title-screen speedup AFTER reading the
            // authored value so the cycle plays at SpeedMultiplier ×
            // the GIF's natural cadence. Floor at 0.005 s so a
            // mis-authored zero-delay frame doesn't divide-by-zero
            // the wrap-around math.
            seconds /= SpeedMultiplier;
            if (seconds < 0.005f) seconds = 0.005f;
            return seconds;
        }

        // Convert the WPF frame to BGRA8 and blit any non-fully-
        // transparent pixels onto the accumulating canvas. This
        // handles GIF's additive-frame disposal model correctly
        // for the common case (day/night-cycle clip, no per-frame
        // restore).
        private void CompositeFrameOntoCanvas(BitmapFrame frame, byte[] canvas)
        {
            // Convert to BGRA32 so the byte layout is predictable.
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = converted.PixelWidth;
            int fh = converted.PixelHeight;
            int stride = fw * 4;
            var src = new byte[stride * fh];
            converted.CopyPixels(src, stride, 0);

            // For day/night cycles the source is typically full-frame
            // (each frame replaces the canvas entirely). We still
            // composite per-pixel via alpha so transparent regions
            // inherit from the prior canvas — cheap, ~ms for a 256px
            // ten-frame clip.
            int copyW = System.Math.Min(fw, Width);
            int copyH = System.Math.Min(fh, Height);
            for (int y = 0; y < copyH; y++)
            {
                int srcRow = y * stride;
                int dstRow = y * Width * 4;
                for (int x = 0; x < copyW; x++)
                {
                    byte sb = src[srcRow + x * 4 + 0];
                    byte sg = src[srcRow + x * 4 + 1];
                    byte sr = src[srcRow + x * 4 + 2];
                    byte sa = src[srcRow + x * 4 + 3];
                    if (sa == 0) continue; // inherit canvas
                    canvas[dstRow + x * 4 + 0] = sb;
                    canvas[dstRow + x * 4 + 1] = sg;
                    canvas[dstRow + x * 4 + 2] = sr;
                    canvas[dstRow + x * 4 + 3] = sa;
                }
            }
        }

        // Upload the BGRA canvas as one layer of the Texture2DArray,
        // converting to RGBA + flipping vertically so the OpenGL
        // bottom-up convention matches without an extra shader hack.
        private void UploadLayerFlipped(byte[] bgra, int layer)
        {
            var rgbaFlipped = new byte[Width * Height * 4];
            int stride = Width * 4;
            for (int y = 0; y < Height; y++)
            {
                int srcRow = y * stride;
                int dstY = Height - 1 - y;
                int dstRow = dstY * stride;
                for (int x = 0; x < Width; x++)
                {
                    byte b = bgra[srcRow + x * 4 + 0];
                    byte g = bgra[srcRow + x * 4 + 1];
                    byte r = bgra[srcRow + x * 4 + 2];
                    byte a = bgra[srcRow + x * 4 + 3];
                    rgbaFlipped[dstRow + x * 4 + 0] = r;
                    rgbaFlipped[dstRow + x * 4 + 1] = g;
                    rgbaFlipped[dstRow + x * 4 + 2] = b;
                    rgbaFlipped[dstRow + x * 4 + 3] = a;
                }
            }
            GL.BindTexture(TextureTarget.Texture2DArray, Texture);
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0,
                0, 0, layer,
                Width, Height, 1,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgbaFlipped);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
        }

        // Pick the layer index for the current wall-clock time. First
        // call captures the start anchor so frame 0 plays at the
        // moment the title screen first appears (vs. at app start —
        // which would mean the user always lands on a random mid-
        // animation frame).
        public int CurrentLayer
        {
            get
            {
                if (!Loaded) return 0;
                double now = _wallClock.Elapsed.TotalSeconds;
                if (_startSeconds < 0.0) _startSeconds = now;
                float t = (float)((now - _startSeconds) % _totalDurationSec);
                float acc = 0f;
                for (int i = 0; i < FrameCount; i++)
                {
                    acc += _frameDelays[i];
                    if (t < acc) return i;
                }
                return FrameCount - 1;
            }
        }

        public void Dispose()
        {
            if (Texture != 0)
            {
                GL.DeleteTexture(Texture);
                Texture = 0;
            }
            Loaded = false;
        }
    }
}
