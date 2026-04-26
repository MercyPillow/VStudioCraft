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

        // Raised when the player clicks "Save" in the pause menu. Hosts (the
        // standalone window or the VS editor pane) hook this to drive the
        // save-as dialog or persist to a known path. If no subscriber and a
        // path is already known, we silently save to it.
        public event Action SaveRequested;
        // Raised when the player clicks "Quit" in the pause menu. Hosts hook
        // this to close the window / editor. If no subscriber, we close the
        // owning WPF Window as a fallback.
        public event Action QuitRequested;

        // Mouse-wheel ticks accumulate here on the UI thread; we cycle the
        // hotbar one slot per 120-unit notch (Windows convention). UI-thread
        // only, no synchronisation needed.
        private int _wheelAccum;

        public GameHostControl()
        {
            InitializeComponent();

            // Eagerly decode the embedded Alpha terrain.png on the WPF
            // UI thread (this constructor) so its byte buffer is cached
            // before any render-thread atlas rebuild touches it. WPF
            // imaging has thread-affinity quirks that can silently break
            // a decode initiated from the GL render thread; prewarming
            // here means the toggle flips later just read the cached
            // BGRA bytes and never touch BitmapDecoder again.
            VStudioCraft.Game.BlockTextures.PrewarmEmbeddedTerrain();

            _gl = new GLControl(new GraphicsMode(32, 24, 0, 0), 3, 3, GraphicsContextFlags.Default)
            {
                Dock = DockStyle.Fill
            };
            _gl.Load += GlOnLoad;
            _gl.Paint += GlOnPaint;
            _gl.Resize += GlOnResize;
            _gl.KeyDown += GlOnKeyDown;
            _gl.KeyUp += GlOnKeyUp;
            _gl.KeyPress += GlOnKeyPress;
            _gl.MouseDown += GlOnMouseDown;
            _gl.MouseUp += GlOnMouseUp;
            _gl.MouseMove += GlOnMouseMove;
            _gl.MouseWheel += GlOnMouseWheel;
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
                    // Either pause menu OR inventory halts world ticks +
                    // swallows queued clicks. Single-flag check so a future
                    // modal (chat, options) just ORs into IsWorldHalted.
                    bool paused = _renderer.IsWorldHalted;
                    if (!paused)
                    {
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
                    }
                    else
                    {
                        // Drop any clicks queued from before the pause so they
                        // don't fire the moment we resume.
                        _input.BreakPressed = false;
                        _input.PlacePressed = false;
                    }

                    // Drain queued inventory-screen clicks while it's open.
                    // Host MouseDown writes (button, x, y) into _input;
                    // renderer applies the slot/cursor swap here so all
                    // Inventory mutations stay on the render thread.
                    if (_renderer.IsInventoryOpen && _input.InventoryClickButton != 0)
                    {
                        var (pw0, ph0) = GetPhysicalSize();
                        _renderer.HandleInventoryClick(
                            _input.InventoryClickButton,
                            _input.InventoryClickX,
                            _input.InventoryClickY,
                            pw0, ph0,
                            _input.InventoryClickShift);
                        _input.InventoryClickButton = 0;
                        _input.InventoryClickShift = false;
                    }

                    // Q-drop one-shots. Three sources by context:
                    //   • Pause menu open: discard the press.
                    //   • Inventory open: drop from cursor / hovered slot.
                    //   • Otherwise: drop from the selected hotbar slot.
                    // Drops thrown from the inventory still spawn into the
                    // world immediately — TickDrops keeps running below
                    // even with the inventory open, so the player sees the
                    // toss arc rather than discovering a new pile when
                    // they close the panel.
                    if (_renderer.IsPaused)
                    {
                        _input.DropOnePressed = false;
                        _input.DropStackPressed = false;
                    }
                    else if (_input.DropStackPressed || _input.DropOnePressed)
                    {
                        bool whole = _input.DropStackPressed;
                        if (_renderer.IsInventoryOpen)
                        {
                            var (pwQ, phQ) = GetPhysicalSize();
                            _renderer.DropFromInventoryHover(whole, pwQ, phQ);
                        }
                        else
                        {
                            _renderer.DropFromHotbar(whole);
                        }
                        _input.DropStackPressed = false;
                        _input.DropOnePressed = false;
                        changed = true;
                    }
                    if (changed)
                    {
                        var cb = Modified;
                        if (cb != null) Dispatcher.BeginInvoke(cb);
                    }

                    // Streaming + mesh uploads keep running while paused so any
                    // chunks already in flight finish their handoff (cheap,
                    // no world-state mutation). World updates — day cycle,
                    // fluid ticks, player movement, survival timers — all
                    // gate on !paused.
                    //
                    // Drops are special: when the inventory is the only
                    // reason the world is halted (i.e. !IsPaused but
                    // IsInventoryOpen), we still tick them so a Q / GUI
                    // toss thrown from the inventory flies away in real
                    // time instead of teleporting onto the floor the
                    // moment the panel closes. The pause menu still
                    // freezes drops fully (it's a true pause).
                    _renderer.UpdateStreaming();
                    _renderer.ProcessDirtyChunks(3);
                    if (!paused)
                    {
                        _renderer.AdvanceTime(dt);
                        UpdatePlayer(dt);
                        _renderer.TickDrops(dt);
                    }
                    else if (!_renderer.IsPaused && _renderer.IsInventoryOpen)
                    {
                        _renderer.TickDrops(dt);
                    }

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
            // Sel/hotbar hint dropped from this strip — the in-game hotbar
            // HUD now shows the held block (and the bar itself documents the
            // 1-9 number-key mapping visually). The status strip stays focused
            // on debug data the HUD doesn't surface: perf timings, mode, GPU.
            StatusText.Text =
                $"{name}  |  FPS {_fps}  |  game {_gameMs:F2} / render {_renderMs:F2} / swap {_swapMs:F2} ms  " +
                $"|  Mode: {mode}{hpBadge}  " +
                $"(LMB/RMB break/place, WASD+Space move, Shift sprint, wheel/1-9 hotbar, F3 toggle mode, Esc pause)  " +
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

            float speed = _input.IsDown(Keys.ShiftKey) ? Player.SprintSpeed : Player.WalkSpeed;
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

            // Creative inventory open → search bar has focus. Only Esc /
            // Backspace / digit-hotbar shortcuts get game treatment; every
            // other printable key flows through KeyPress into the search
            // text. The early-return swallows E so typing "earth" doesn't
            // close the panel after the first letter.
            bool creativeInventoryTyping = _renderer != null
                && _renderer.IsInventoryOpen
                && _renderer.GameMode == GameMode.Creative;
            if (creativeInventoryTyping)
            {
                switch (e.KeyCode)
                {
                    case Keys.Escape:
                        ToggleInventory();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        return;
                    case Keys.Back:
                        if (_input.InventorySearchText.Length > 0)
                        {
                            _input.InventorySearchText =
                                _input.InventorySearchText.Substring(0, _input.InventorySearchText.Length - 1);
                            _input.InventoryScrollRows = 0;
                        }
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        return;
                    case Keys.PageUp:
                        _input.InventoryScrollRows = Math.Max(0, _input.InventoryScrollRows - 4);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        return;
                    case Keys.PageDown:
                        _input.InventoryScrollRows += 4;   // renderer clamps
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        return;
                    // Fall through for everything else — KeyPress (below)
                    // collects printable characters into the search bar.
                }
                e.Handled = true;
                return;
            }

            switch (e.KeyCode)
            {
                // Number keys just flip the render-thread-visible hotbar
                // index; the status strip no longer shows the held block,
                // so there's nothing to refresh on the UI thread.
                case Keys.D1: _input.HotbarIndex = 0; break;
                case Keys.D2: _input.HotbarIndex = 1; break;
                case Keys.D3: _input.HotbarIndex = 2; break;
                case Keys.D4: _input.HotbarIndex = 3; break;
                case Keys.D5: _input.HotbarIndex = 4; break;
                case Keys.D6: _input.HotbarIndex = 5; break;
                case Keys.D7: _input.HotbarIndex = 6; break;
                case Keys.D8: _input.HotbarIndex = 7; break;
                case Keys.D9: _input.HotbarIndex = 8; break;
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
                case Keys.Q:
                    // Q drops one item; Shift+Q drops the whole stack.
                    // Two contexts:
                    //   • Inventory closed: source is the selected hotbar
                    //     slot.
                    //   • Inventory open: source is the cursor stack if
                    //     non-empty, otherwise the slot under the mouse
                    //     pointer (or the hovered hotbar slot in creative).
                    //
                    // The render thread drains the one-shot in RenderLoop;
                    // it picks the right source based on IsInventoryOpen.
                    // The pause menu still gates Q out (player is in a
                    // modal that owns the keyboard).
                    if (_renderer != null && !_renderer.IsPaused)
                    {
                        bool shiftHeld = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                        if (shiftHeld) _input.DropStackPressed = true;
                        else           _input.DropOnePressed = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.E:
                    // Open / close inventory. Esc also closes it (handled
                    // below) so the player has the same dismiss key as
                    // every other modal.
                    //
                    // SuppressKeyPress (not just Handled) is REQUIRED here:
                    // KeyDown flips IsInventoryOpen to true, but WinForms
                    // still fires a KeyPress for the same physical 'e'
                    // unless we suppress it explicitly. Without this, the
                    // creative-mode search bar would receive an 'e' the
                    // moment the panel opens — pre-typing the very key
                    // that opened it.
                    ToggleInventory();
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Escape:
                    // Modal precedence: inventory > options > pause. Esc
                    // pops the topmost modal so the player isn't trapped
                    // (e.g. Esc inside Options returns to the pause menu,
                    // a second Esc returns to the game).
                    if (_renderer != null && _renderer.IsInventoryOpen) ToggleInventory();
                    else if (_renderer != null && _renderer.IsOptionsOpen) _renderer.IsOptionsOpen = false;
                    else TogglePause();
                    e.SuppressKeyPress = true;
                    break;
            }
            e.Handled = true;
        }

        // KeyPress fires only for printable characters (post-IME, post-
        // shift mapping). Used by the creative inventory's search bar:
        // every printable key the player types while the catalog is open
        // appends to InventorySearchText. KeyDown handles the structural
        // keys (Esc / Backspace / PageUp/Down) above.
        private void GlOnKeyPress(object sender, System.Windows.Forms.KeyPressEventArgs e)
        {
            if (_renderer == null) return;
            if (!_renderer.IsInventoryOpen) return;
            if (_renderer.GameMode != GameMode.Creative) return;

            char c = e.KeyChar;
            // Ignore control chars (Backspace, Enter, etc.) — KeyDown
            // handled the ones we care about. Printable ASCII + space pass
            // through; the bitmap font is upper-case-only, so we keep the
            // raw char and the renderer normalises to upper at draw time.
            if (c < 32 || c == 127) return;
            // Cap search length so a runaway keystroke can't blow the bar.
            if (_input.InventorySearchText.Length >= 32) return;

            _input.InventorySearchText += c;
            _input.InventoryScrollRows = 0; // any edit resets scroll to top
            e.Handled = true;
        }

        private void TogglePause()
        {
            if (_renderer == null) return;
            if (_renderer.IsPaused)
            {
                _renderer.IsPaused = false;
                // Closing the pause layer also drops any sub-menu (Options)
                // that was open on top, so a future re-pause starts on the
                // top-level pause menu rather than mid-Options.
                _renderer.IsOptionsOpen = false;
                // Resume the look-capture so the player drops straight back
                // into the game without an extra click.
                CaptureMouseLook();
            }
            else
            {
                _renderer.IsPaused = true;
                ReleaseMouseLook();
                // Drop held movement keys — otherwise the player would stay
                // walking the moment they unpause if they had W down when
                // they hit Esc.
                _input.Clear();
            }
        }

        // Open / close the inventory screen. Same lifecycle as TogglePause:
        // releases the mouse-look on open (cursor becomes visible so the
        // player can browse slots) and re-captures it on close. Held keys
        // are cleared on open so a W that was down at the moment of opening
        // doesn't make the player walk through the screen.
        private void ToggleInventory()
        {
            if (_renderer == null) return;
            if (_renderer.IsInventoryOpen)
            {
                _renderer.IsInventoryOpen = false;
                // Clear creative-mode catalog state so the next open starts
                // fresh — otherwise the search text persists across sessions
                // and the panel would re-open mid-filter.
                _input.ResetInventorySearch();
                CaptureMouseLook();
                return;
            }
            // Don't stack modals — if the pause menu is up, ignore E. The
            // player can press Esc first and then E.
            if (_renderer.IsPaused) return;
            _renderer.IsInventoryOpen = true;
            _input.ResetInventorySearch();
            ReleaseMouseLook();
            _input.Clear();
        }

        private void GlOnMouseWheel(object sender, MouseEventArgs e)
        {
            // Creative inventory open: wheel scrolls the catalog instead
            // of cycling the hotbar. One row per notch matches the catalog
            // grid's vertical step. Renderer clamps the value to the
            // filtered list length each frame so we don't have to know it
            // here.
            if (_renderer != null && _renderer.IsInventoryOpen
                && _renderer.GameMode == GameMode.Creative)
            {
                _wheelAccum += e.Delta;
                while (_wheelAccum >= 120)
                {
                    _wheelAccum -= 120;
                    _input.InventoryScrollRows = Math.Max(0, _input.InventoryScrollRows - 1);
                }
                while (_wheelAccum <= -120)
                {
                    _wheelAccum += 120;
                    _input.InventoryScrollRows += 1;
                }
                return;
            }

            // Cycling the hotbar while a modal is up would be confusing; the
            // bar isn't even visually focal then. Number keys still work for
            // direct selection if the user wants it for some reason.
            if (_renderer != null && _renderer.IsWorldHalted) return;

            _wheelAccum += e.Delta;
            int slotCount = Inventory.HotbarCount;
            if (slotCount <= 0) return;

            // 120 units per notch is the Windows convention. Scroll up
            // (positive Delta) moves the selection LEFT, scroll down moves
            // RIGHT — matches Minecraft's hotbar feel.
            while (_wheelAccum >= 120)
            {
                _wheelAccum -= 120;
                int idx = _input.HotbarIndex - 1;
                if (idx < 0) idx = slotCount - 1;
                _input.HotbarIndex = idx;
            }
            while (_wheelAccum <= -120)
            {
                _wheelAccum += 120;
                int idx = _input.HotbarIndex + 1;
                if (idx >= slotCount) idx = 0;
                _input.HotbarIndex = idx;
            }
        }

        private void GlOnKeyUp(object sender, KeyEventArgs e)
        {
            _input.KeyUp(e.KeyCode);
            e.Handled = true;
        }

        private void GlOnMouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();

            // Inventory open: route the click into InputState as a
            // one-shot. The render thread reads (button, x, y) once per
            // frame in RenderLoop and dispatches to the renderer's
            // HandleInventoryClick — keeps every Inventory mutation on
            // the render thread without needing a lock around Slots[].
            if (_renderer != null && _renderer.IsInventoryOpen)
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    var (px, py) = ToPhysicalCoord(e.X, e.Y);
                    _input.InventoryClickX = px;
                    _input.InventoryClickY = py;
                    // Snapshot the shift modifier at click time. The render
                    // thread drains the click on its own frame, so reading
                    // ModifierKeys there would race with the UI thread.
                    _input.InventoryClickShift =
                        (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                    _input.InventoryClickButton = e.Button == MouseButtons.Left ? 1 : 2;
                }
                return;
            }

            // Paused: clicks hit-test the pause menu (or the options menu
            // if it's layered on top). We swallow them either way so the
            // click never re-captures the cursor or fires a place / break
            // action under the menu.
            if (_renderer != null && _renderer.IsPaused)
            {
                if (e.Button == MouseButtons.Left)
                {
                    var (px, py) = ToPhysicalCoord(e.X, e.Y);
                    var (pw, ph) = GetPhysicalSize();
                    if (_renderer.IsOptionsOpen)
                    {
                        bool isSurvival = _renderer.GameMode == VStudioCraft.Game.GameMode.Survival;
                        var oact = OptionsMenu.HitTest(pw, ph, px, py,
                            _renderer.HungerEnabled, isSurvival,
                            VStudioCraft.Game.Settings.UseRealTextures);
                        HandleOptionsMenuAction(oact);
                    }
                    else
                    {
                        var act = PauseMenu.HitTest(pw, ph, px, py);
                        HandlePauseMenuAction(act);
                    }
                }
                return;
            }

            if (!_mouseCaptured)
            {
                // First click swallows the action and captures the cursor, like most FPS games.
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    CaptureMouseLook();
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                _input.BreakPressed = true;
                _input.BreakHeld = true;
            }
            else if (e.Button == MouseButtons.Right) _input.PlacePressed = true;
        }

        private void HandlePauseMenuAction(PauseMenu.ActionId act)
        {
            switch (act)
            {
                case PauseMenu.ActionId.BackToGame:
                    TogglePause();
                    break;
                case PauseMenu.ActionId.Options:
                    // Layer the Options sub-menu on top of the pause menu.
                    // _isPaused stays true so the world remains halted; the
                    // renderer draws OptionsMenu over the dim wash + pause
                    // buttons (the buttons become unreachable until BACK).
                    _renderer.IsOptionsOpen = true;
                    break;
                case PauseMenu.ActionId.Save:
                    RaiseSaveRequested();
                    break;
                case PauseMenu.ActionId.Quit:
                    RaiseQuitRequested();
                    break;
                case PauseMenu.ActionId.None:
                    break;
            }
        }

        // Click handling for the Options sub-menu. Toggles flip the matching
        // GameRenderer property in place; BACK pops the layer. Section
        // headings and disabled rows already short-circuit inside
        // OptionsMenu.HitTest, so we only ever see actionable IDs here.
        private void HandleOptionsMenuAction(OptionsMenu.ActionId act)
        {
            if (_renderer == null) return;
            switch (act)
            {
                case OptionsMenu.ActionId.Back:
                    _renderer.IsOptionsOpen = false;
                    break;
                case OptionsMenu.ActionId.ToggleHunger:
                    _renderer.HungerEnabled = !_renderer.HungerEnabled;
                    break;
                case OptionsMenu.ActionId.ToggleRealTextures:
                    // Persist to HKCU first so the next world load picks
                    // up the new value at startup; then queue an atlas
                    // rebuild on the GL thread (deletes the old texture
                    // and uploads a fresh one from the new source). The
                    // queue is drained at the top of RenderLoop, so the
                    // swap happens between the click and the next frame
                    // without a racy mid-draw GL state change.
                    VStudioCraft.Game.Settings.UseRealTextures =
                        !VStudioCraft.Game.Settings.UseRealTextures;
                    // Make sure the BGRA decode has happened on the WPF
                    // UI thread before we queue the GL-thread rebuild —
                    // even if the constructor's prewarm got skipped or
                    // the cache was somehow cleared, this guarantees
                    // the toggle either populates the cache here or
                    // marks it failed *now*, on the thread where WPF
                    // imaging is happy. The label re-reads the status
                    // immediately so the user sees the truth.
                    VStudioCraft.Game.BlockTextures.PrewarmEmbeddedTerrain();
                    var rendererRef = _renderer;
                    _renderQueue.Enqueue(() => rendererRef.RebuildBlockAtlas());
                    break;
                case OptionsMenu.ActionId.None:
                    break;
            }
        }

        private void RaiseSaveRequested()
        {
            var cb = SaveRequested;
            if (cb != null)
            {
                Dispatcher.BeginInvoke(cb);
            }
            else if (!string.IsNullOrEmpty(_worldPath))
            {
                // Fallback: silently save to the path we already know.
                SaveToFile(_worldPath);
            }
            // Otherwise do nothing — no path, no host hook.
        }

        private void RaiseQuitRequested()
        {
            var cb = QuitRequested;
            if (cb != null)
            {
                Dispatcher.BeginInvoke(cb);
                return;
            }
            // Fallback: close the owning WPF window. Works for the standalone
            // shell and most VSIX docking hosts (which respond to Window.Close
            // by closing the editor frame).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var win = System.Windows.Window.GetWindow(this);
                win?.Close();
            }));
        }

        // Convert a WinForms client-pixel mouse coordinate to the physical
        // pixel space used by the renderer. WPF/WinForms can hand us logical
        // pixels under per-monitor DPI; the renderer always works in physical
        // pixels (GetClientRect via win32). This keeps hover / hit-tests in
        // sync with the rendered button rects regardless of DPI scaling.
        private (int x, int y) ToPhysicalCoord(int logX, int logY)
        {
            var (pw, ph) = GetPhysicalSize();
            int cw = _gl?.ClientSize.Width ?? pw;
            int ch = _gl?.ClientSize.Height ?? ph;
            if (cw <= 0 || ch <= 0) return (logX, logY);
            return ((int)((long)logX * pw / cw), (int)((long)logY * ph / ch));
        }

        private void GlOnMouseUp(object sender, MouseEventArgs e)
        {
            // BreakHeld stays true between LMB-down and LMB-up; the renderer
            // resets break-progress as soon as it sees this clear. Right
            // button doesn't have a parallel hold state — placement is a
            // one-shot fired by BreakPressed-equivalent on click.
            if (e.Button == MouseButtons.Left) _input.BreakHeld = false;
        }

        private void GlOnMouseMove(object sender, MouseEventArgs e)
        {
            // While any modal is up, track the cursor so the renderer can
            // highlight the button / slot under it. Mouse-look stays
            // released. (Inventory has no hover state today, but plumbing
            // the coords through means it's free when slot-picking lands.)
            if (_renderer != null && _renderer.IsWorldHalted)
            {
                var (px, py) = ToPhysicalCoord(e.X, e.Y);
                _input.MenuMouseX = px;
                _input.MenuMouseY = py;
                return;
            }

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
