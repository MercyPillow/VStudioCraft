using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using VStudioCraft.Game;
using Point = System.Drawing.Point;
using WfCursor = System.Windows.Forms.Cursor;

namespace VStudioCraft.UI
{
    public partial class GameHostControl : System.Windows.Controls.UserControl
    {
        private GLControl _gl;
        private GameRenderer _renderer;
        private readonly InputState _input = new InputState();
        private readonly Stopwatch _clock = new Stopwatch();

        // Render thread owns the GL context end-to-end so frame rate is decoupled
        // from WPF's 60 Hz CompositionTarget.Rendering cadence. UI thread only
        // handles input + menu actions; GL-touching actions are queued here.
        private Thread _renderThread;
        private volatile bool _shutdownRequested;
        private readonly ConcurrentQueue<Action> _renderQueue = new ConcurrentQueue<Action>();
        private readonly ManualResetEventSlim _contextDetached = new ManualResetEventSlim(false);
        private IntPtr _glHandle;   // cached HWND so GetPhysicalSize doesn't touch Control.Handle off-UI-thread

        // Rolling FPS + per-phase timings. Written on render thread, read on UI
        // thread when UpdateStatus runs via Dispatcher.BeginInvoke.
        private volatile int _fps;
        private double _gameMs;
        private double _renderMs;
        private double _swapMs;

        // Cached at GL init so we can tell at a glance whether the driver gave us
        // a hardware context (NVIDIA/AMD/Intel) or the GDI Generic software fallback.
        private string _glVersion = "init";
        private string _glRenderer = "init";
        private string _glVendor = "init";

        private bool _mouseCaptured;

        private string _pendingLoadPath;
        private int _pendingSeed;
        private bool _pendingIsLoad;
        private volatile bool _glReady;

        private string _worldPath;
        private bool _disposed;

        public event Action Modified;

        public GameHostControl()
        {
            InitializeComponent();

            _gl = new GLControl(new GraphicsMode(32, 24, 0, 0), 3, 3, GraphicsContextFlags.Default)
            {
                Dock = DockStyle.Fill
            };
            _gl.Load += GlOnLoad;
            _gl.Paint += GlOnPaint;
            _gl.Resize += GlOnResize;
            _gl.KeyDown += GlOnKeyDown;
            _gl.KeyUp += GlOnKeyUp;
            _gl.MouseDown += GlOnMouseDown;
            _gl.MouseUp += GlOnMouseUp;
            _gl.MouseMove += GlOnMouseMove;
            _gl.MouseEnter += (_, __) => _gl?.Focus();
            _gl.LostFocus += (_, __) => { ReleaseMouseLook(); _input.Clear(); };
            Host.Child = _gl;

            Loaded += (_, __) => _gl?.Focus();
        }

        public void LoadFromFile(string path)
        {
            _worldPath = path;
            if (!_glReady)
            {
                _pendingIsLoad = true;
                _pendingLoadPath = path;
                return;
            }
            _renderQueue.Enqueue(() =>
            {
                _renderer.LoadFromFile(path);
                Dispatcher.BeginInvoke(new Action(UpdateStatus));
            });
        }

        public void StartNewWorld(int seed)
        {
            if (!_glReady)
            {
                _pendingIsLoad = false;
                _pendingSeed = seed;
                return;
            }
            _renderQueue.Enqueue(() =>
            {
                _renderer.StartNewWorld(seed);
                Dispatcher.BeginInvoke(new Action(UpdateStatus));
            });
        }

        public void SaveToFile(string path)
        {
            _worldPath = path;
            if (!_glReady) return;
            _renderQueue.Enqueue(() =>
            {
                _renderer.SaveToFile(path);
                Dispatcher.BeginInvoke(new Action(UpdateStatus));
            });
        }

        // Return the HWND's client-area size in physical pixels. WinForms' Control.Width
        // can be logical pixels when the control is DPI-virtualised, which puts the GL
        // viewport out of lockstep with the actual framebuffer (off-centre HUD).
        // GetClientRect on a per-monitor DPI-aware process reports physical pixels.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        private (int w, int h) GetPhysicalSize()
        {
            if (_glHandle != IntPtr.Zero && GetClientRect(_glHandle, out var r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w > 0 && h > 0) return (w, h);
            }
            return (1, 1);
        }

        public void Shutdown()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseMouseLook();

            _shutdownRequested = true;
            try { _renderThread?.Join(2000); } catch { }
            _renderThread = null;
            _renderer = null;

            _gl?.Dispose();
            _gl = null;
        }

        private void GlOnLoad(object sender, EventArgs e)
        {
            _gl.MakeCurrent();
            _glHandle = _gl.Handle;

            _renderer = new GameRenderer();
            _renderer.Input = _input;
            _renderer.InitializeGraphics();

            _glVersion = GL.GetString(StringName.Version) ?? "unknown";
            _glRenderer = GL.GetString(StringName.Renderer) ?? "unknown";
            _glVendor = GL.GetString(StringName.Vendor) ?? "unknown";

            if (_pendingIsLoad && !string.IsNullOrEmpty(_pendingLoadPath))
            {
                _renderer.LoadFromFile(_pendingLoadPath);
            }
            else
            {
                int seed = _pendingSeed != 0 ? _pendingSeed : (int)(DateTime.Now.Ticks & 0x7FFFFFFF);
                _renderer.StartNewWorld(seed);
            }

            // Release GL context from the UI thread so the render thread can claim it.
            // VSync off so SwapBuffers returns as soon as the driver queues the flip;
            // the render thread then immediately starts the next frame.
            _gl.VSync = false;
            _gl.Context.MakeCurrent(null);

            _glReady = true;
            UpdateStatus();
            _clock.Start();

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "VStudioCraft GL render",
            };
            _renderThread.Start();
        }

        private void RenderLoop()
        {
            try
            {
                _gl.MakeCurrent();
            }
            catch (GraphicsContextException)
            {
                return;
            }

            try
            {
                long gameTicks = 0, renderTicks = 0, swapTicks = 0;
                int frames = 0;
                double windowStart = _clock.Elapsed.TotalSeconds;
                double lastSeconds = windowStart;
                double tickToMs = 1000.0 / Stopwatch.Frequency;

                while (!_shutdownRequested)
                {
                    // Drain UI-requested GL work (Load/Save/New worlds etc).
                    while (_renderQueue.TryDequeue(out var work))
                    {
                        try { work(); } catch { /* swallow; next frame still draws */ }
                    }

                    double now = _clock.Elapsed.TotalSeconds;
                    float dt = (float)Math.Min(0.1, now - lastSeconds);
                    lastSeconds = now;

                    long t0 = Stopwatch.GetTimestamp();

                    bool changed = false;
                    if (_input.BreakPressed)
                    {
                        changed |= _renderer.TryBreak();
                        _input.BreakPressed = false;
                    }
                    if (_input.PlacePressed)
                    {
                        changed |= _renderer.TryPlace(_input.SelectedBlock);
                        _input.PlacePressed = false;
                    }
                    if (changed)
                    {
                        var cb = Modified;
                        if (cb != null) Dispatcher.BeginInvoke(cb);
                    }

                    _renderer.UpdateStreaming();
                    _renderer.ProcessDirtyChunks(3);
                    _renderer.AdvanceTime(dt);
                    UpdatePlayer(dt);

                    long t1 = Stopwatch.GetTimestamp();

                    var (pw, ph) = GetPhysicalSize();
                    _renderer.Render(pw, ph);

                    long t2 = Stopwatch.GetTimestamp();

                    // Use the context directly: GLControl.SwapBuffers touches Control.Handle
                    // which would throw when invoked off the UI thread.
                    _gl.Context.SwapBuffers();

                    long t3 = Stopwatch.GetTimestamp();

                    gameTicks += t1 - t0;
                    renderTicks += t2 - t1;
                    swapTicks += t3 - t2;
                    frames++;

                    double windowElapsed = now - windowStart;
                    if (windowElapsed >= 0.5 && frames > 0)
                    {
                        int frozenFps = (int)Math.Round(frames / windowElapsed);
                        double g = gameTicks * tickToMs / frames;
                        double r = renderTicks * tickToMs / frames;
                        double s = swapTicks * tickToMs / frames;

                        _fps = frozenFps;
                        _gameMs = g;
                        _renderMs = r;
                        _swapMs = s;

                        gameTicks = renderTicks = swapTicks = 0;
                        frames = 0;
                        windowStart = now;

                        Dispatcher.BeginInvoke(new Action(UpdateStatus));
                    }
                }
            }
            catch (GraphicsContextException)
            {
                // Context was yanked out from under us (VS tab reparenting or shutdown race).
            }
            finally
            {
                try { _renderer?.Dispose(); } catch { }
                try { _gl?.Context?.MakeCurrent(null); } catch { }
                _contextDetached.Set();
            }
        }

        private void UpdateStatus()
        {
            var name = string.IsNullOrEmpty(_worldPath) ? "(untitled)" : System.IO.Path.GetFileName(_worldPath);
            // Cross-thread read: GameMode is a single-enum byte assignment (atomic
            // on x86/x64) and Health is a single int. Status text is purely
            // display, so a torn read at worst shows a one-frame stale value.
            var mode = _renderer?.GameMode ?? GameMode.Creative;
            int hp = _renderer?.Player.Health ?? 20;
            string hpBadge = mode == GameMode.Survival ? $"  |  HP {hp}/20" : "";
            StatusText.Text =
                $"{name}  |  FPS {_fps}  |  game {_gameMs:F2} / render {_renderMs:F2} / swap {_swapMs:F2} ms  " +
                $"|  Mode: {mode}{hpBadge}  |  Sel: {_input.SelectedBlock}  " +
                $"(1-9 hotbar, LMB/RMB break/place, WASD+Space+Ctrl move, F3 toggle mode, Esc uncapture)  " +
                $"|  GPU: {_glRenderer} [{_glVendor}]  |  GL {_glVersion}";
        }

        private void UpdatePlayer(float dt)
        {
            var cam = _renderer.Camera;
            float yaw = cam.Yaw;

            // Horizontal basis from yaw only (pitch doesn't tilt walking direction).
            var fwdH   = new Vector3((float)Math.Sin(yaw), 0f, -(float)Math.Cos(yaw));
            var rightH = new Vector3((float)Math.Cos(yaw), 0f,  (float)Math.Sin(yaw));

            var wish = Vector3.Zero;
            if (_input.IsDown(Keys.W)) wish += fwdH;
            if (_input.IsDown(Keys.S)) wish -= fwdH;
            if (_input.IsDown(Keys.D)) wish += rightH;
            if (_input.IsDown(Keys.A)) wish -= rightH;
            if (wish.LengthSquared > 0f) wish = Vector3.Normalize(wish);

            float speed = _input.IsDown(Keys.ControlKey) ? Player.SprintSpeed : Player.WalkSpeed;
            bool wantJump = _input.IsDown(Keys.Space);

            _renderer.UpdatePlayer(dt, wish * speed, wantJump);

            _input.ConsumeMouseDelta(out float mx, out float my);
            const float sensitivity = 0.0035f;
            cam.Yaw += mx * sensitivity;
            cam.Pitch -= my * sensitivity;
            cam.ClampPitch();
        }

        private void GlOnPaint(object sender, PaintEventArgs e)
        {
            // Rendering is driven by the render thread, not by WM_PAINT.
        }

        private void GlOnResize(object sender, EventArgs e)
        {
            // Render thread picks up the new client rect via GetPhysicalSize each frame;
            // GL.Viewport is set inside GameRenderer.Render before drawing. No UI-thread
            // GL work needed here.
        }

        private void GlOnKeyDown(object sender, KeyEventArgs e)
        {
            _input.KeyDown(e.KeyCode);

            switch (e.KeyCode)
            {
                case Keys.D1: _input.HotbarIndex = 0; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D2: _input.HotbarIndex = 1; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D3: _input.HotbarIndex = 2; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D4: _input.HotbarIndex = 3; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D5: _input.HotbarIndex = 4; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D6: _input.HotbarIndex = 5; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D7: _input.HotbarIndex = 6; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D8: _input.HotbarIndex = 7; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.D9: _input.HotbarIndex = 8; Dispatcher.BeginInvoke(new Action(UpdateStatus)); break;
                case Keys.F3:
                    if (_renderer != null)
                    {
                        // UI-thread write, render-thread read. Enum assignment is a
                        // single-byte store on x86/x64, so no lock needed.
                        _renderer.GameMode = _renderer.GameMode == GameMode.Creative
                            ? GameMode.Survival
                            : GameMode.Creative;
                        Dispatcher.BeginInvoke(new Action(UpdateStatus));
                    }
                    break;
                case Keys.Escape: ReleaseMouseLook(); break;
            }
            e.Handled = true;
        }

        private void GlOnKeyUp(object sender, KeyEventArgs e)
        {
            _input.KeyUp(e.KeyCode);
            e.Handled = true;
        }

        private void GlOnMouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();

            if (!_mouseCaptured)
            {
                // First click swallows the action and captures the cursor, like most FPS games.
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    CaptureMouseLook();
                }
                return;
            }

            if (e.Button == MouseButtons.Left)       _input.BreakPressed = true;
            else if (e.Button == MouseButtons.Right) _input.PlacePressed = true;
        }

        private void GlOnMouseUp(object sender, MouseEventArgs e)
        {
        }

        private void GlOnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseCaptured) return;

            var rect = _gl.RectangleToScreen(_gl.ClientRectangle);
            var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var screenPos = _gl.PointToScreen(e.Location);
            int dx = screenPos.X - center.X;
            int dy = screenPos.Y - center.Y;
            if (dx == 0 && dy == 0) return;

            _input.AddMouseDelta(dx, dy);
            // Snap back to center so we can always accumulate relative motion.
            WfCursor.Position = center;
            WfCursor.Clip = rect;
        }

        private void CaptureMouseLook()
        {
            if (_mouseCaptured || _gl == null || !_gl.IsHandleCreated) return;
            _mouseCaptured = true;
            _input.MouseLookActive = true;

            var rect = _gl.RectangleToScreen(_gl.ClientRectangle);
            WfCursor.Clip = rect;
            WfCursor.Position = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            WfCursor.Hide();
        }

        private void ReleaseMouseLook()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            _input.MouseLookActive = false;
            _input.ConsumeMouseDelta(out _, out _);
            WfCursor.Clip = System.Drawing.Rectangle.Empty;
            WfCursor.Show();
        }
    }
}
