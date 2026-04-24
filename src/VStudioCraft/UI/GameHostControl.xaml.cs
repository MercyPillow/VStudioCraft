using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        private DispatcherTimer _tick;
        private readonly InputState _input = new InputState();
        private readonly Stopwatch _clock = new Stopwatch();
        private double _lastSeconds;

        // Rolling FPS sampled every ~0.5s so the number doesn't strobe frame-to-frame.
        private int _fpsFrameCount;
        private double _fpsWindowStart;
        private int _fps;

        private bool _mouseCaptured;

        private string _pendingLoadPath;
        private int _pendingSeed;
        private bool _pendingIsLoad;
        private bool _glReady;

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
            if (_glReady && TryMakeCurrent())
            {
                _renderer.LoadFromFile(path);
                UpdateStatus();
            }
            else
            {
                _pendingIsLoad = true;
                _pendingLoadPath = path;
            }
        }

        public void StartNewWorld(int seed)
        {
            if (_glReady && TryMakeCurrent())
            {
                _renderer.StartNewWorld(seed);
                UpdateStatus();
            }
            else
            {
                _pendingIsLoad = false;
                _pendingSeed = seed;
            }
        }

        public void SaveToFile(string path)
        {
            _worldPath = path;
            if (_glReady && TryMakeCurrent())
            {
                _renderer.SaveToFile(path);
                UpdateStatus();
            }
        }

        private bool TryMakeCurrent()
        {
            if (_gl == null || !_gl.IsHandleCreated) return false;
            try { _gl.MakeCurrent(); return true; }
            catch (OpenTK.Graphics.GraphicsContextException) { return false; }
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
            if (_gl != null && _gl.IsHandleCreated && GetClientRect(_gl.Handle, out var r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w > 0 && h > 0) return (w, h);
            }
            return (Math.Max(1, _gl?.Width ?? 1), Math.Max(1, _gl?.Height ?? 1));
        }

        public void Shutdown()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseMouseLook();
            _tick?.Stop();
            _tick = null;
            _renderer?.Dispose();
            _renderer = null;
            _gl?.Dispose();
            _gl = null;
        }

        private void GlOnLoad(object sender, EventArgs e)
        {
            _gl.MakeCurrent();
            _renderer = new GameRenderer();
            _renderer.InitializeGraphics();

            if (_pendingIsLoad && !string.IsNullOrEmpty(_pendingLoadPath))
            {
                _renderer.LoadFromFile(_pendingLoadPath);
            }
            else
            {
                int seed = _pendingSeed != 0 ? _pendingSeed : (int)(DateTime.Now.Ticks & 0x7FFFFFFF);
                _renderer.StartNewWorld(seed);
            }
            _glReady = true;

            UpdateStatus();

            _clock.Start();
            // 4 ms (~240 Hz ceiling) so the timer isn't itself the cap. Actual frame
            // rate will be limited by vsync / GPU / dispatcher pressure, not by us.
            _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(4) };
            _tick.Tick += OnTick;
            _tick.Start();
        }

        private void UpdateStatus()
        {
            var glVersion = _glReady ? (GL.GetString(StringName.Version) ?? "unknown") : "init";
            var name = string.IsNullOrEmpty(_worldPath) ? "(untitled)" : System.IO.Path.GetFileName(_worldPath);
            StatusText.Text = $"VStudioCraft  |  {name}  |  FPS {_fps}  |  Click to capture mouse, Esc to release  |  WASD walk, Space jump, Ctrl sprint  |  LMB break, RMB place  |  1/2/3/4 = Grass/Dirt/Stone/Sand  |  Selected: {_input.SelectedBlock}  |  OpenGL {glVersion}";
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_renderer == null || _gl == null) return;
            if (!_gl.IsHandleCreated) return;

            try { _gl.MakeCurrent(); }
            catch (OpenTK.Graphics.GraphicsContextException)
            {
                // Transient HWND / context state during VS tab dock/float/reparent.
                // Skip this frame; we'll retry on the next tick.
                return;
            }

            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, now - _lastSeconds);
            _lastSeconds = now;

            _fpsFrameCount++;
            double fpsElapsed = now - _fpsWindowStart;
            if (fpsElapsed >= 0.5)
            {
                _fps = (int)Math.Round(_fpsFrameCount / fpsElapsed);
                _fpsFrameCount = 0;
                _fpsWindowStart = now;
                UpdateStatus();
            }

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
            if (changed) Modified?.Invoke();

            _renderer.UpdateStreaming();
            _renderer.ProcessDirtyChunks(3);
            _renderer.AdvanceTime(dt);
            UpdatePlayer(dt);

            try
            {
                var (pw, ph) = GetPhysicalSize();
                _renderer.Render(pw, ph);
                _gl.SwapBuffers();
            }
            catch (OpenTK.Graphics.GraphicsContextException)
            {
            }
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
            // Rendering is driven by the tick timer, not by WM_PAINT. This avoids
            // races where VS reparents the host's HWND and our cached GL context's
            // HDC is momentarily invalid.
        }

        private void GlOnResize(object sender, EventArgs e)
        {
            if (_renderer == null || _gl == null || !_gl.IsHandleCreated) return;
            try
            {
                _gl.MakeCurrent();
                var (pw, ph) = GetPhysicalSize();
                _renderer.OnResize(pw, ph);
            }
            catch (OpenTK.Graphics.GraphicsContextException)
            {
            }
        }

        private void GlOnKeyDown(object sender, KeyEventArgs e)
        {
            _input.KeyDown(e.KeyCode);

            switch (e.KeyCode)
            {
                case Keys.D1: _input.SelectedBlock = BlockType.Grass; UpdateStatus(); break;
                case Keys.D2: _input.SelectedBlock = BlockType.Dirt;  UpdateStatus(); break;
                case Keys.D3: _input.SelectedBlock = BlockType.Stone; UpdateStatus(); break;
                case Keys.D4: _input.SelectedBlock = BlockType.Sand;  UpdateStatus(); break;
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

            _input.MouseDx += dx;
            _input.MouseDy += dy;
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
            _input.MouseDx = 0;
            _input.MouseDy = 0;
            WfCursor.Clip = System.Drawing.Rectangle.Empty;
            WfCursor.Show();
        }
    }
}
