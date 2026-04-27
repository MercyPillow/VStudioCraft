using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // Modal RMB-drag state. While `_rmbDragActive` is true, MouseMove
        // hit-tests the slot under the cursor and (if not already painted
        // for this drag) paints it into `_rmbDragPainted` and enqueues a
        // drag-deposit on InputState for the render thread to apply. Each
        // slot only deposits once per drag — once painted, re-entering it
        // is a no-op so the player can wiggle without dumping extras.
        // Cleared on RMB MouseUp and on every modal close path (Esc / E /
        // pause / focus-lost) so a stale drag never carries between
        // sessions of an open modal.
        private bool _rmbDragActive;
        private readonly HashSet<int> _rmbDragPainted = new HashSet<int>();

        // Options-menu slider drag latch. Set on a slider mouse-down,
        // consumed by MouseMove to keep updating the value while the
        // user drags, cleared on MouseUp or any options-menu dismiss.
        // ActionId.None means "no drag in progress".
        private OptionsMenu.ActionId _optionsSliderDrag = OptionsMenu.ActionId.None;

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
            _gl.LostFocus += (_, __) =>
            {
                ReleaseMouseLook();
                _input.Clear();
                // Mouse events stop arriving when focus drops — abort
                // any in-flight RMB drag so it doesn't paint stale
                // slots when focus returns.
                _rmbDragActive = false;
                _rmbDragPainted.Clear();
            };
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

            // Bring up the audio engine + procedural SFX bank alongside
            // graphics. Both are idempotent and defensive — if OpenAL isn't
            // present on this host (no openal32.dll) the engine falls into
            // a silent state and SfxBank.Play* calls become no-ops, so the
            // game still runs without sound.
            VStudioCraft.Game.SfxBank.Initialize();

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
                            // RMB dispatch: interact-first (CraftingTable opens
                            // its panel etc.), then fall through to placement
                            // if no interaction consumed the click. The
                            // interact path returns true on consume so it can
                            // short-circuit the placement attempt and avoid
                            // double-dispatching the right-click.
                            if (_renderer.TryInteract())
                            {
                                changed = true;
                                // TryInteract may have opened a modal screen
                                // (e.g. crafting / furnace) on the render
                                // thread — mouse-look toggling has to happen
                                // on the UI thread, so dispatch the release
                                // once we observe the flag transition. The
                                // matching CaptureMouseLook on close runs
                                // from the UI Esc handler in CloseCrafting /
                                // CloseFurnace.
                                if ((_renderer.IsCraftingOpen || _renderer.IsFurnaceOpen || _renderer.IsChestOpen) && _mouseCaptured)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        ReleaseMouseLook();
                                        _input.Clear();
                                    }));
                                }
                            }
                            else
                            {
                                changed |= _renderer.TryPlace(_input.SelectedBlock);
                            }
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

                    // Same drain pattern for the crafting screen — clicks
                    // are queued by the host MouseDown handler into the
                    // same _input field as the inventory queue (only one
                    // modal can be open at a time), and the renderer's
                    // crafting handler applies them on this thread.
                    if (_renderer.IsCraftingOpen && _input.InventoryClickButton != 0)
                    {
                        var (pw0, ph0) = GetPhysicalSize();
                        _renderer.HandleCraftingClick(
                            _input.InventoryClickButton,
                            _input.InventoryClickX,
                            _input.InventoryClickY,
                            pw0, ph0,
                            _input.InventoryClickShift);
                        _input.InventoryClickButton = 0;
                        _input.InventoryClickShift = false;
                    }

                    // Same drain pattern for the furnace screen.
                    if (_renderer.IsFurnaceOpen && _input.InventoryClickButton != 0)
                    {
                        var (pw0, ph0) = GetPhysicalSize();
                        _renderer.HandleFurnaceClick(
                            _input.InventoryClickButton,
                            _input.InventoryClickX,
                            _input.InventoryClickY,
                            pw0, ph0,
                            _input.InventoryClickShift);
                        _input.InventoryClickButton = 0;
                        _input.InventoryClickShift = false;
                    }

                    // Same drain pattern for the chest screen.
                    if (_renderer.IsChestOpen && _input.InventoryClickButton != 0)
                    {
                        var (pw0, ph0) = GetPhysicalSize();
                        _renderer.HandleChestClick(
                            _input.InventoryClickButton,
                            _input.InventoryClickX,
                            _input.InventoryClickY,
                            pw0, ph0,
                            _input.InventoryClickShift);
                        _input.InventoryClickButton = 0;
                        _input.InventoryClickShift = false;
                    }

                    // Drain RMB drag-deposit queue — the host paints one
                    // slot per MouseMove crossing, and we apply them all
                    // here in arrival order. The renderer's HandleDrag-
                    // Deposit methods deposit one item from the cursor
                    // into the slot if it's empty or same-type, and skip
                    // it (no swap) if it holds a foreign type. Doing this
                    // after the single-shot click drain means the initial
                    // RMB-down is applied before any subsequent paints.
                    var deposits = _input.DrainDragDeposits();
                    if (deposits.Length > 0)
                    {
                        if (_renderer.IsCraftingOpen)
                        {
                            for (int i = 0; i < deposits.Length; i++)
                                _renderer.HandleCraftingDragDeposit(deposits[i]);
                        }
                        else if (_renderer.IsFurnaceOpen)
                        {
                            for (int i = 0; i < deposits.Length; i++)
                                _renderer.HandleFurnaceDragDeposit(deposits[i]);
                        }
                        else if (_renderer.IsChestOpen)
                        {
                            for (int i = 0; i < deposits.Length; i++)
                                _renderer.HandleChestDragDeposit(deposits[i]);
                        }
                        else if (_renderer.IsInventoryOpen)
                        {
                            for (int i = 0; i < deposits.Length; i++)
                                _renderer.HandleInventoryDragDeposit(deposits[i]);
                        }
                        // If both flags are false (modal closed mid-frame),
                        // the deposits are silently dropped — they were
                        // tied to the now-defunct modal session anyway.
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
                        _renderer.TickPigs(dt);
                        _renderer.TickHostiles(dt);
                        _renderer.TickFurnacesIfDue(dt);
                    }
                    else if (!_renderer.IsPaused)
                    {
                        // Non-pause modal (inventory / crafting / furnace
                        // screen) — keep drops physics-ticking so a Q-toss
                        // still flies, and keep furnaces smelting so a
                        // player parked at the furnace screen sees real-
                        // time progress (Alpha behaviour).
                        _renderer.TickDrops(dt);
                        _renderer.TickFurnacesIfDue(dt);
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
                // Tear down the AL device alongside the GL context — the
                // tool window may be closed and reopened (or the game loop
                // restarted), and AudioEngine.Initialize() is idempotent
                // so it'll bring a fresh context back up next launch.
                try { VStudioCraft.Game.AudioEngine.Shutdown(); } catch { }
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
                    if (_renderer != null && _renderer.IsCraftingOpen) CloseCrafting();
                    else if (_renderer != null && _renderer.IsFurnaceOpen) CloseFurnace();
                    else if (_renderer != null && _renderer.IsChestOpen) CloseChest();
                    else if (_renderer != null && _renderer.IsInventoryOpen) ToggleInventory();
                    else if (_renderer != null && _renderer.IsOptionsOpen)
                    {
                        _renderer.IsOptionsOpen = false;
                        _optionsSliderDrag = OptionsMenu.ActionId.None;
                    }
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
                _optionsSliderDrag = OptionsMenu.ActionId.None;
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
                // Drop any in-flight RMB drag (see CloseCrafting comment).
                _rmbDragActive = false;
                _rmbDragPainted.Clear();
                CaptureMouseLook();
                return;
            }
            // Don't stack modals — if the pause menu is up, ignore E. The
            // player can press Esc first and then E.
            if (_renderer.IsPaused) return;
            // Crafting screen open: E dismisses it the same way it
            // dismisses the inventory (the player's instinct is "E
            // closes the panel I'm in"). Mirror Esc's path so the grid
            // contents flush back into the inventory and mouse-look
            // re-captures.
            if (_renderer.IsCraftingOpen)
            {
                CloseCrafting();
                return;
            }
            if (_renderer.IsFurnaceOpen)
            {
                CloseFurnace();
                return;
            }
            if (_renderer.IsChestOpen)
            {
                CloseChest();
                return;
            }
            _renderer.IsInventoryOpen = true;
            _input.ResetInventorySearch();
            ReleaseMouseLook();
            _input.Clear();
        }

        // Close the crafting screen. Opening is handled directly by
        // TryInteract on the render thread (RMB on a CraftingTable cell);
        // this is the symmetric close path, called from Esc and (later)
        // from the ToggleInventory key if the player hits E to dismiss
        // crafting the same way they dismiss the inventory panel.
        private void CloseCrafting()
        {
            if (_renderer == null) return;
            _renderer.CloseCrafting();
            // Drop any in-flight RMB drag — a drag started inside the
            // crafting panel shouldn't carry over to the next modal.
            _rmbDragActive = false;
            _rmbDragPainted.Clear();
            CaptureMouseLook();
        }

        // Close the furnace screen. Mirrors CloseCrafting — the renderer
        // handles cursor flush; we drop the RMB drag state and re-capture
        // mouse-look. Furnace slot contents stay in the tile entity
        // (Alpha behaviour: closing the screen doesn't dump the contents).
        private void CloseFurnace()
        {
            if (_renderer == null) return;
            _renderer.CloseFurnace();
            _rmbDragActive = false;
            _rmbDragPainted.Clear();
            CaptureMouseLook();
        }

        // Close the chest screen. Same lifecycle as CloseFurnace —
        // chest slot contents stay on the entity, only the cursor is
        // flushed back into the player inventory by the renderer.
        private void CloseChest()
        {
            if (_renderer == null) return;
            _renderer.CloseChest();
            _rmbDragActive = false;
            _rmbDragPainted.Clear();
            CaptureMouseLook();
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

            // Inventory or crafting open: route the click into InputState
            // as a one-shot. The render thread reads (button, x, y) once
            // per frame in RenderLoop and dispatches to the renderer's
            // HandleInventoryClick / HandleCraftingClick — keeps every
            // Inventory + grid mutation on the render thread without
            // needing a lock around Slots[]. Both modal screens share the
            // same _input click slot since only one can be open at a time
            // (the renderer's drain branches in RenderLoop pick the right
            // handler based on the active modal).
            if (_renderer != null && (_renderer.IsInventoryOpen || _renderer.IsCraftingOpen || _renderer.IsFurnaceOpen || _renderer.IsChestOpen))
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
                    // Audible feedback for every modal click — hit or miss.
                    // Misses still produce a meaningful action (cursor-stack
                    // toss-out, RMB single-deposit on empty space) so the
                    // click feedback is correct regardless of slot hit-test.
                    VStudioCraft.Game.SfxBank.PlayClick();

                    // Start an RMB drag. The initial click is dispatched
                    // via InventoryClickButton above (full RMB rules — may
                    // swap on a foreign-type slot). MouseMove will paint
                    // subsequent slots one item at a time. Pre-paint the
                    // initial slot so a wiggle back to it after a move
                    // doesn't double-deposit.
                    if (e.Button == MouseButtons.Right && !_input.InventoryClickShift)
                    {
                        _rmbDragActive = true;
                        _rmbDragPainted.Clear();
                        var (pw, ph) = GetPhysicalSize();
                        int slot = HitTestActiveModalSlot(px, py, pw, ph);
                        if (slot >= 0) _rmbDragPainted.Add(slot);
                    }
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
                        float masterVol = VStudioCraft.Game.AudioEngine.MasterGain;
                        float musicVol  = VStudioCraft.Game.AudioEngine.MusicGain;
                        var hit = OptionsMenu.HitTestEx(pw, ph, px, py,
                            _renderer.HungerEnabled, isSurvival,
                            VStudioCraft.Game.Settings.UseRealTextures,
                            masterVol, musicVol);
                        HandleOptionsMenuAction(hit.Id, hit.SliderValue);
                        // Latch slider drag: while LMB is held over a
                        // slider, mouse-moves should keep updating the
                        // value. The MouseMove handler reads this latch
                        // and re-fires the action with the new X.
                        if (hit.Id == OptionsMenu.ActionId.SetMasterVolume ||
                            hit.Id == OptionsMenu.ActionId.SetMusicVolume)
                        {
                            _optionsSliderDrag = hit.Id;
                        }
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
            // Audible feedback for any actionable hit. None = click missed
            // every button (clicked dead space inside the menu) — staying
            // silent there matches the visual feedback (no button highlight).
            if (act != PauseMenu.ActionId.None) VStudioCraft.Game.SfxBank.PlayClick();
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
        private void HandleOptionsMenuAction(OptionsMenu.ActionId act, float sliderValue)
        {
            if (_renderer == null) return;
            // Same rule as the pause menu: only chirp on actionable hits so
            // dead-space clicks (section headings, padding) stay silent.
            // Sliders are explicitly excluded — a dragging slider would
            // emit a stream of clicks that masks every SFX it's mixing.
            if (act != OptionsMenu.ActionId.None
                && act != OptionsMenu.ActionId.SetMasterVolume
                && act != OptionsMenu.ActionId.SetMusicVolume)
            {
                VStudioCraft.Game.SfxBank.PlayClick();
            }
            switch (act)
            {
                case OptionsMenu.ActionId.Back:
                    _renderer.IsOptionsOpen = false;
                    _optionsSliderDrag = OptionsMenu.ActionId.None;
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
                case OptionsMenu.ActionId.SetMasterVolume:
                    // Apply immediately so subsequent SFX play at the new
                    // gain (the next test-click on the slider itself is a
                    // no-op for click feedback, but block-break / steps /
                    // pickup all multiply through MasterGain). Persist to
                    // HKCU so the value survives a restart.
                    VStudioCraft.Game.AudioEngine.MasterGain = sliderValue;
                    VStudioCraft.Game.Settings.MasterVolume  = sliderValue;
                    break;
                case OptionsMenu.ActionId.SetMusicVolume:
                    VStudioCraft.Game.AudioEngine.MusicGain = sliderValue;
                    VStudioCraft.Game.Settings.MusicVolume  = sliderValue;
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
            if (e.Button == MouseButtons.Left)
            {
                _input.BreakHeld = false;
                // Releasing LMB ends any in-progress slider drag. The
                // value already in AudioEngine + Settings is the final
                // value (last MouseMove wrote it).
                _optionsSliderDrag = OptionsMenu.ActionId.None;
            }
            // RMB-up ends the modal drag-deposit (if any). Painted-slot
            // set is cleared so the next RMB-press starts with a fresh
            // canvas. Any deposits already enqueued for the render
            // thread stay queued — they'll drain on the next frame.
            if (e.Button == MouseButtons.Right)
            {
                _rmbDragActive = false;
                _rmbDragPainted.Clear();
            }
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

                // RMB-drag spread: while RMB is held over the modal,
                // each new slot the cursor enters gets one item from
                // the cursor stack. The render thread does the actual
                // deposit + cap-checks; the host just dedupes by slot
                // so the queue doesn't grow on every pixel of motion
                // within the same slot.
                if (_rmbDragActive
                    && _renderer != null
                    && (_renderer.IsInventoryOpen || _renderer.IsCraftingOpen || _renderer.IsFurnaceOpen || _renderer.IsChestOpen))
                {
                    var (pw, ph) = GetPhysicalSize();
                    int slot = HitTestActiveModalSlot(px, py, pw, ph);
                    if (slot >= 0 && _rmbDragPainted.Add(slot))
                    {
                        _input.EnqueueDragDeposit(slot);
                    }
                }

                // Options-menu slider drag: re-evaluate the slider hit
                // using the current cursor X. We re-fire HitTestEx on
                // the row matching the latched action so the user can
                // drag *off* the row vertically and still keep updating
                // (clamped to the row's X range internally) — matches
                // the convention every desktop slider follows.
                if (_optionsSliderDrag != OptionsMenu.ActionId.None
                    && _renderer != null && _renderer.IsOptionsOpen)
                {
                    var (pw, ph) = GetPhysicalSize();
                    bool isSurvival = _renderer.GameMode == VStudioCraft.Game.GameMode.Survival;
                    float masterVol = VStudioCraft.Game.AudioEngine.MasterGain;
                    float musicVol  = VStudioCraft.Game.AudioEngine.MusicGain;
                    var rows = OptionsMenu.BuildRows(pw, ph,
                        _renderer.HungerEnabled, isSurvival,
                        VStudioCraft.Game.Settings.UseRealTextures,
                        masterVol, musicVol);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var r = rows[i];
                        if (!r.IsSlider || r.Id != _optionsSliderDrag) continue;
                        // Same edge-inset logic as HitTestEx so click and
                        // drag agree at the row borders.
                        int border = VStudioCraft.Game.UiScale.S(2, pw, ph);
                        int trackX = r.X + border;
                        int trackW = r.W - 2 * border;
                        if (trackW < 1) trackW = 1;
                        float t = (px - trackX) / (float)trackW;
                        if (t < 0f) t = 0f;
                        else if (t > 1f) t = 1f;
                        HandleOptionsMenuAction(_optionsSliderDrag, t);
                        break;
                    }
                }
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

        // Hit-test the slot under (mx,my) in whichever modal is active.
        // Returns -1 if no slot, the slot index in the active panel's
        // index space otherwise:
        //   • Crafting open  → 0..54 (CraftingScreen.HitTest)
        //   • Inventory open + survival → 0..44 (InventoryScreen.HitTest)
        //   • Inventory open + creative → hotbar slot index (HitTestHotbar)
        // Creative catalog tiles are intentionally excluded — drag-spread
        // doesn't make sense on the read-only catalog.
        private int HitTestActiveModalSlot(int mx, int my, int pw, int ph)
        {
            if (_renderer == null) return -1;
            if (_renderer.IsCraftingOpen)
            {
                return CraftingScreen.HitTest(pw, ph, mx, my);
            }
            if (_renderer.IsFurnaceOpen)
            {
                return FurnaceScreen.HitTest(pw, ph, mx, my);
            }
            if (_renderer.IsChestOpen)
            {
                return ChestScreen.HitTest(pw, ph, mx, my);
            }
            if (_renderer.IsInventoryOpen)
            {
                if (_renderer.GameMode == VStudioCraft.Game.GameMode.Creative)
                    return InventoryScreen.HitTestHotbar(pw, ph, mx, my, /*creative*/true);
                return InventoryScreen.HitTest(pw, ph, mx, my);
            }
            return -1;
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
