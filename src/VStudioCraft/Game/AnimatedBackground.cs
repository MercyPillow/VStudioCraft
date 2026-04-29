using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Tier 6 — Multi-frame animated background for the title screen.
    // Decodes GIF frames once on a BACKGROUND THREAD so app start
    // isn't halted while the WPF GIF decoder reads ~500 frames; the
    // GPU upload happens on the next render frame after decode
    // completes (PollAndUpload is called per-frame from Render()).
    //
    // Adds a disk cache: the first run decodes the GIF, composites
    // every frame, runs the per-frame skip + speed-multiplier passes,
    // and writes the resulting raw RGBA byte arrays to a cache file.
    // Subsequent runs read the cache (~tens of MB of memcpy from
    // disk) and skip the GIF decode entirely — what was a multi-
    // second halt becomes a near-instant per-frame poll.
    internal sealed class AnimatedBackground : IDisposable
    {
        public int Texture { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameCount { get; private set; }
        // Static placeholder texture (1-layer Texture2DArray with the
        // GIF's first frame). Loaded synchronously on the render
        // thread so the title screen has a meaningful background
        // immediately, even before the async GIF decode finishes.
        // Same target type as Texture (sampler2DArray) so the shader
        // path can swap between them with just a Bind + uLayer change.
        public int PlaceholderTexture { get; private set; }
        public int PlaceholderWidth { get; private set; }
        public int PlaceholderHeight { get; private set; }
        public bool PlaceholderLoaded => PlaceholderTexture != 0;
        // Volatile so the render thread sees Loaded=true the moment
        // the upload poll runs. Set last by UploadPayloadToGpu so a
        // concurrent reader that sees Loaded=true is guaranteed to
        // also see Texture / Width / Height / FrameCount populated.
        public bool Loaded => _loaded;
        private volatile bool _loaded;
        public string DiagStatus { get; private set; } = "(not attempted)";

        // Per-frame delays in seconds, post-SpeedMultiplier.
        private float[] _frameDelays;
        private float _totalDurationSec;

        // 1× takes ~1-2 minutes for a typical day/night clip — tuned
        // by the user; bumping this divides each delay by it so the
        // total clip duration shortens proportionally.
        private const float SpeedMultiplier = 1.1f;

        // Wall-clock anchor (seconds since first call). The first
        // CurrentLayer poll captures this so frame 0 plays at the
        // moment the title screen first appears.
        private double _startSeconds = -1.0;
        private static readonly System.Diagnostics.Stopwatch _wallClock = System.Diagnostics.Stopwatch.StartNew();

        // Background-thread → render-thread handoff. Background thread
        // assigns once when decode completes; render thread reads,
        // uploads to GPU, clears. No lock needed: single-writer +
        // single-reader on a reference-typed field is atomic on any
        // sane CLR target.
        private DecodedPayload _pendingUpload;
        private const int CacheMagic = unchecked((int)0xBACECAFE);
        private const int CacheVersion = 1;

        // CPU-side decoded payload. Produced on the background thread,
        // consumed on the render thread. Reference-only field copy
        // makes the handoff thread-safe without locks.
        private sealed class DecodedPayload
        {
            public int Width;
            public int Height;
            public int FrameCount;
            public float[] Delays;        // length == FrameCount
            public byte[][] FramesRgba;   // [FrameCount][Width*Height*4], already vertically flipped
            public float TotalDurationSec;
        }

        // Kick off decode on the thread pool. The GIF byte stream is
        // read fully here (cheap, 7-8 MB), then the heavy decode +
        // composite loop runs off-thread. Once the payload's ready
        // we stash it for the render thread to pick up.
        //
        // cacheFilePath is the location on disk where we'll cache the
        // raw decoded RGBA bytes for next run. Pass null to disable.
        public void BeginAsyncLoadFromManifest(string resourceName, string cacheFilePath)
        {
            byte[] resourceBytes = ReadManifestResource(resourceName);
            if (resourceBytes == null)
            {
                DiagStatus = "no resource matched 'Day_Night'";
                return;
            }

            // Spawn the decode on the thread pool. Long-running but
            // doesn't block; if the user dismisses the title before
            // decode finishes, the upload poll just never sees a
            // payload (harmless — no GL resources allocated yet).
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DecodedPayload payload = null;

                    // Try the disk cache first. If it matches our
                    // current source-bytes length and version, use it
                    // (multi-second decode → ~hundreds of ms file read).
                    if (!string.IsNullOrEmpty(cacheFilePath))
                    {
                        payload = TryReadCache(cacheFilePath, resourceBytes.Length);
                    }

                    // Cache miss → full decode.
                    if (payload == null)
                    {
                        using (var ms = new MemoryStream(resourceBytes))
                        {
                            payload = DecodeGifToPayload(ms);
                        }
                        if (payload != null && !string.IsNullOrEmpty(cacheFilePath))
                        {
                            try { WriteCache(cacheFilePath, payload, resourceBytes.Length); }
                            catch { /* cache write is best-effort */ }
                        }
                    }

                    if (payload != null)
                    {
                        DiagStatus = "ok async";
                        _pendingUpload = payload;
                    }
                    else
                    {
                        DiagStatus = "decode produced no payload";
                    }
                }
                catch (Exception ex)
                {
                    DiagStatus = $"async threw: {ex.GetType().Name} {ex.Message}";
                }
            });
        }

        // Synchronously decode the static placeholder PNG (the GIF's
        // first frame, pre-extracted at asset prep) and upload it as
        // a 1-layer Texture2DArray so the same _backgroundShader
        // sampler path works for both placeholder and animated
        // textures (just bind a different texture + sample uLayer=0).
        // Called once on the render thread during InitializeGraphics
        // — fast (~ms) because it's a single uncompressed frame.
        public void LoadPlaceholderFromManifest(string resourceName)
        {
            try
            {
                byte[] bytes = ReadManifestResource(resourceName);
                if (bytes == null) return;
                using (var ms = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(ms,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return;
                    var frame = decoder.Frames[0];
                    int w = frame.PixelWidth;
                    int h = frame.PixelHeight;
                    var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    int stride = w * 4;
                    var src = new byte[stride * h];
                    converted.CopyPixels(src, stride, 0);

                    // BGRA top-down → RGBA bottom-up, same convention
                    // as the animated frames so the same UV transform
                    // in the shader works for both.
                    var rgba = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                    {
                        int srcRow = y * stride;
                        int dstY = h - 1 - y;
                        int dstRow = dstY * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte b = src[srcRow + x * 4 + 0];
                            byte g = src[srcRow + x * 4 + 1];
                            byte r = src[srcRow + x * 4 + 2];
                            byte a = src[srcRow + x * 4 + 3];
                            rgba[dstRow + x * 4 + 0] = r;
                            rgba[dstRow + x * 4 + 1] = g;
                            rgba[dstRow + x * 4 + 2] = b;
                            rgba[dstRow + x * 4 + 3] = a;
                        }
                    }

                    PlaceholderTexture = GL.GenTexture();
                    PlaceholderWidth = w;
                    PlaceholderHeight = h;
                    GL.BindTexture(TextureTarget.Texture2DArray, PlaceholderTexture);
                    GL.TexImage3D(TextureTarget.Texture2DArray, 0,
                        PixelInternalFormat.Rgba, w, h, 1, 0,
                        OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                    GL.TexSubImage3D(TextureTarget.Texture2DArray, 0,
                        0, 0, 0, w, h, 1,
                        OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                    // Set parameters with the texture still bound —
                    // same fix as the animated path (default min
                    // filter would render black without mipmaps).
                    GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                    GL.BindTexture(TextureTarget.Texture2DArray, 0);
                }
            }
            catch
            {
                // Placeholder failure is non-fatal — the title falls
                // back to the sky-blue clear like before.
            }
        }

        // Render-thread poll. If the background decoder has produced
        // a payload, claim it + upload to GPU. Cheap when nothing's
        // pending (single field read + null check).
        public void PollAndUpload()
        {
            if (_loaded) return;
            var p = _pendingUpload;
            if (p == null) return;
            _pendingUpload = null;
            UploadPayloadToGpu(p);
        }

        // ---- background-thread-safe decode --------------------------

        private static DecodedPayload DecodeGifToPayload(Stream stream)
        {
            // Copy to a memory stream the decoder can keep open. WPF's
            // GIF decoder lazy-evaluates frame pixels, so we need the
            // backing stream alive for the whole CopyPixels loop.
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            int n = decoder.Frames.Count;
            if (n <= 0) return null;

            var firstFrame = decoder.Frames[0];
            int w = firstFrame.PixelWidth;
            int h = firstFrame.PixelHeight;

            // Cap GPU layers. ~520 MB array-texture VRAM for a 565-
            // frame 640×360 GIF is past the GL_MAX_ARRAY_TEXTURE_LAYERS
            // cap on most GPUs and gratuitous in any case. Skip frames
            // evenly until we're at or below MaxLayers.
            const int MaxLayers = 128;
            int srcN = n;
            int keepN = Math.Min(srcN, MaxLayers);

            var canvas = new byte[w * h * 4];
            for (int i = 0; i < canvas.Length; i++) canvas[i] = 0;

            var payload = new DecodedPayload
            {
                Width = w,
                Height = h,
                FrameCount = keepN,
                Delays = new float[keepN],
                FramesRgba = new byte[keepN][],
                TotalDurationSec = 0f,
            };

            int kept = 0;
            float pendingDelay = 0f;
            for (int i = 0; i < srcN; i++)
            {
                var frame = decoder.Frames[i];
                pendingDelay += ReadFrameDelaySeconds(frame);
                CompositeFrameOntoCanvas(frame, canvas, w, h);

                bool shouldKeep = (long)(i + 1) * keepN / srcN > kept;
                if (shouldKeep && kept < keepN)
                {
                    payload.Delays[kept] = Math.Max(0.01f, pendingDelay);
                    payload.TotalDurationSec += payload.Delays[kept];
                    payload.FramesRgba[kept] = FlipBgraToRgba(canvas, w, h);
                    kept++;
                    pendingDelay = 0f;
                }
            }
            if (pendingDelay > 0f && kept > 0)
            {
                payload.Delays[kept - 1] += pendingDelay;
                payload.TotalDurationSec += pendingDelay;
            }
            if (payload.TotalDurationSec < 0.01f) payload.TotalDurationSec = 0.01f;
            return payload;
        }

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
            seconds /= SpeedMultiplier;
            if (seconds < 0.005f) seconds = 0.005f;
            return seconds;
        }

        private static void CompositeFrameOntoCanvas(BitmapFrame frame, byte[] canvas, int canvasW, int canvasH)
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = converted.PixelWidth;
            int fh = converted.PixelHeight;
            int stride = fw * 4;
            var src = new byte[stride * fh];
            converted.CopyPixels(src, stride, 0);
            int copyW = Math.Min(fw, canvasW);
            int copyH = Math.Min(fh, canvasH);
            for (int y = 0; y < copyH; y++)
            {
                int srcRow = y * stride;
                int dstRow = y * canvasW * 4;
                for (int x = 0; x < copyW; x++)
                {
                    byte sa = src[srcRow + x * 4 + 3];
                    if (sa == 0) continue;
                    canvas[dstRow + x * 4 + 0] = src[srcRow + x * 4 + 0];
                    canvas[dstRow + x * 4 + 1] = src[srcRow + x * 4 + 1];
                    canvas[dstRow + x * 4 + 2] = src[srcRow + x * 4 + 2];
                    canvas[dstRow + x * 4 + 3] = sa;
                }
            }
        }

        // BGRA-top-down → RGBA-bottom-up. Returns a new buffer; the
        // caller stashes it in the payload's per-frame array. Allocating
        // a fresh buffer per kept frame is unavoidable since we want
        // each frame independent (the GIF's accumulating canvas would
        // otherwise mutate every prior frame as well).
        private static byte[] FlipBgraToRgba(byte[] bgra, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * stride;
                int dstY = h - 1 - y;
                int dstRow = dstY * stride;
                for (int x = 0; x < w; x++)
                {
                    byte b = bgra[srcRow + x * 4 + 0];
                    byte g = bgra[srcRow + x * 4 + 1];
                    byte r = bgra[srcRow + x * 4 + 2];
                    byte a = bgra[srcRow + x * 4 + 3];
                    dst[dstRow + x * 4 + 0] = r;
                    dst[dstRow + x * 4 + 1] = g;
                    dst[dstRow + x * 4 + 2] = b;
                    dst[dstRow + x * 4 + 3] = a;
                }
            }
            return dst;
        }

        // ---- render-thread GPU upload --------------------------------

        private void UploadPayloadToGpu(DecodedPayload p)
        {
            Width = p.Width;
            Height = p.Height;
            FrameCount = p.FrameCount;
            _frameDelays = p.Delays;
            _totalDurationSec = p.TotalDurationSec;

            Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, Texture);
            GL.TexImage3D(TextureTarget.Texture2DArray, 0,
                PixelInternalFormat.Rgba, Width, Height, FrameCount, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            for (int i = 0; i < FrameCount; i++)
            {
                GL.TexSubImage3D(TextureTarget.Texture2DArray, 0,
                    0, 0, i, Width, Height, 1,
                    OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p.FramesRgba[i]);
            }

            // Set parameters with the texture still bound — without
            // mipmaps the default NEAREST_MIPMAP_LINEAR min filter
            // returns black. Linear + ClampToEdge gives a clean
            // fullscreen sample.
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            _loaded = true;
        }

        // ---- disk cache ---------------------------------------------

        // Layout:
        //   int  magic
        //   int  version
        //   int  sourceLength    (bytes of the original GIF resource)
        //   int  width
        //   int  height
        //   int  frameCount
        //   float totalDurationSec
        //   float[frameCount] delays
        //   byte[frameCount][width*height*4] frame RGBA (vertically flipped)
        // Magic + version + sourceLength let us invalidate stale
        // caches when the GIF asset changes (different size in bytes
        // → cache regenerated).
        private static DecodedPayload TryReadCache(string path, int expectedSourceLength)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadInt32() != CacheMagic) return null;
                    if (br.ReadInt32() != CacheVersion) return null;
                    if (br.ReadInt32() != expectedSourceLength) return null;
                    int w = br.ReadInt32();
                    int h = br.ReadInt32();
                    int fc = br.ReadInt32();
                    if (w <= 0 || h <= 0 || fc <= 0 || fc > 256) return null;
                    float totalDur = br.ReadSingle();
                    var delays = new float[fc];
                    for (int i = 0; i < fc; i++) delays[i] = br.ReadSingle();
                    var frames = new byte[fc][];
                    int frameLen = w * h * 4;
                    for (int i = 0; i < fc; i++)
                    {
                        frames[i] = br.ReadBytes(frameLen);
                        if (frames[i].Length != frameLen) return null;
                    }
                    return new DecodedPayload
                    {
                        Width = w,
                        Height = h,
                        FrameCount = fc,
                        Delays = delays,
                        FramesRgba = frames,
                        TotalDurationSec = totalDur,
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(string path, DecodedPayload p, int sourceLength)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch { }
            // Write to a temp file + rename so a partial write
            // (process crash mid-write) doesn't corrupt the cache.
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(CacheMagic);
                bw.Write(CacheVersion);
                bw.Write(sourceLength);
                bw.Write(p.Width);
                bw.Write(p.Height);
                bw.Write(p.FrameCount);
                bw.Write(p.TotalDurationSec);
                for (int i = 0; i < p.FrameCount; i++) bw.Write(p.Delays[i]);
                for (int i = 0; i < p.FrameCount; i++) bw.Write(p.FramesRgba[i]);
            }
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // ---- manifest resource read ---------------------------------

        private static byte[] ReadManifestResource(string resourceName)
        {
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
                foreach (var asm in asms)
                {
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
                                    if (s == null) continue;
                                    using (var ms = new MemoryStream())
                                    {
                                        s.CopyTo(ms);
                                        return ms.ToArray();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                // Side-by-side file fallback.
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                    string sideBy = Path.Combine(baseDir, "Assets", "Day_Night.gif");
                    if (File.Exists(sideBy)) return File.ReadAllBytes(sideBy);
                }
                catch { }
            }
            catch { }
            return null;
        }

        // ---- per-frame layer pick -----------------------------------

        public int CurrentLayer
        {
            get
            {
                if (!_loaded) return 0;
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
            if (PlaceholderTexture != 0)
            {
                GL.DeleteTexture(PlaceholderTexture);
                PlaceholderTexture = 0;
            }
            _loaded = false;
        }
    }
}
