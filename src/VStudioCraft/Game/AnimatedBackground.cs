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

        // Per-frame delays in seconds. Defaults to 0.10 s when the
        // GIF has no explicit metadata — matches the canonical
        // browser default for delay=0 frames.
        private float[] _frameDelays;
        private float _totalDurationSec;

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
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (s != null) return LoadFromStream(s);
                }
                // Manifest scan fallback for builds where the
                // resource ended up under a different prefix.
                var asm = Assembly.GetExecutingAssembly();
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (n.IndexOf("Day_Night", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using (var s2 = asm.GetManifestResourceStream(n))
                        {
                            if (s2 != null) return LoadFromStream(s2);
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool LoadFromStream(Stream stream)
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
            // disposal model (later frames may only carry the changed
            // sub-rect, with the rest of the canvas inherited from
            // the previous frame).
            var firstFrame = decoder.Frames[0];
            Width = firstFrame.PixelWidth;
            Height = firstFrame.PixelHeight;
            FrameCount = n;
            _frameDelays = new float[n];

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
                PixelInternalFormat.Rgba, Width, Height, n, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            _totalDurationSec = 0f;
            for (int i = 0; i < n; i++)
            {
                var frame = decoder.Frames[i];
                _frameDelays[i] = ReadFrameDelaySeconds(frame);
                _totalDurationSec += _frameDelays[i];

                CompositeFrameOntoCanvas(frame, canvas);

                // Upload the canvas (post-composite) as this frame's
                // layer. Vertical flip (PNG/GIF top-down → GL bottom-
                // up) is done by walking rows in reverse below.
                UploadLayerFlipped(canvas, i);
            }
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
            try
            {
                if (frame.Metadata is BitmapMetadata md)
                {
                    var v = md.GetQuery("/grctlext/Delay");
                    if (v is ushort us)  return System.Math.Max(0.01f, us / 100f);
                    if (v is short ss)   return System.Math.Max(0.01f, ss / 100f);
                    if (v is int ii)     return System.Math.Max(0.01f, ii / 100f);
                    if (v is uint ui)    return System.Math.Max(0.01f, ui / 100f);
                }
            }
            catch { }
            return 0.10f;
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
