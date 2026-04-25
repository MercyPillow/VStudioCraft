using System.Collections.Generic;
using System.Windows.Forms;

namespace VStudioCraft.Game
{
    // Shared between the UI thread (writers: key/mouse events) and the GL render
    // thread (reader: each frame's per-axis movement + camera look delta). All
    // mutations go through locks so a render-thread read is never torn mid-update.
    internal sealed class InputState
    {
        private readonly HashSet<Keys> _down = new HashSet<Keys>();
        private readonly object _lock = new object();

        public bool MouseLookActive;
        private float _mouseDx;
        private float _mouseDy;

        public bool BreakPressed;   // one-shot, consumed by renderer
        public bool PlacePressed;   // one-shot, consumed by renderer

        // True while the left mouse button is held with mouse-look captured.
        // Drives survival-mode block-break progress: the renderer accumulates
        // dt/hardness each frame this is set against a stable target. The
        // one-shot BreakPressed above still fires on click (used in creative
        // for instant break); survival ignores it and uses BreakHeld instead.
        public bool BreakHeld;

        // Player-owned 36-slot inventory + cursor. Hotbar lives at slot
        // indices 27..35 (matching InventoryScreen). The render thread reads
        // Slots[i] every frame to draw the bar / inventory; the UI thread
        // writes them in response to picks/drops/clicks. Reads + writes of
        // an ItemStack (two int-sized fields) aren't atomic on x86/x64, so
        // we accept that an ItemStack readback could in principle tear.
        // In practice the only mutator is the render thread itself (the UI
        // thread enqueues click events via the InventoryClick* fields below
        // and the render thread applies them between Render and the next
        // hotbar read), so the cross-thread reader path is read-only and
        // the write path is single-threaded.
        public readonly Inventory Inventory = new Inventory();
        public int HotbarIndex;

        // Pending one-shot click against the inventory UI:
        //   0 = none, 1 = left button, 2 = right button.
        // X / Y are physical-pixel coordinates — same space as MenuMouseX/Y
        // and the InventoryScreen rectangles. Set by the host's MouseDown
        // when the inventory is open, cleared by the renderer once consumed.
        public int InventoryClickButton;
        public int InventoryClickX;
        public int InventoryClickY;

        public InputState()
        {
            // Starter loadout — same nine blocks the bar shipped with before
            // the inventory landed, at full stacks. Survival players can clear
            // these by tossing them; creative ignores stack counts entirely.
            Inventory.FillHotbar(new[]
            {
                BlockType.Grass,
                BlockType.Dirt,
                BlockType.Stone,
                BlockType.Sand,
                BlockType.Torch,
                BlockType.Dandelion,
                BlockType.Rose,
                BlockType.TallGrass,
                BlockType.Planks,
            }, ItemStack.MaxCount);
        }

        // Last known mouse position over the GLControl in physical pixels,
        // tracked while the game is paused so the renderer can highlight the
        // pause-menu button under the cursor. Single-int writes are atomic on
        // x86/x64; the renderer reads each frame for hover and the worst case
        // (a frame with a torn coord) just shows highlight 1 frame stale.
        public int MenuMouseX;
        public int MenuMouseY;

        // Block currently held — what TryPlace places, what the status text
        // shows. Empty hotbar slot returns Air so existing call sites (which
        // already short-circuit Air to "nothing") still work.
        public BlockType SelectedBlock => Inventory.GetHotbar(HotbarIndex).Type;

        public void KeyDown(Keys k) { lock (_lock) _down.Add(k); }
        public void KeyUp(Keys k) { lock (_lock) _down.Remove(k); }
        public bool IsDown(Keys k) { lock (_lock) return _down.Contains(k); }

        public void AddMouseDelta(float dx, float dy)
        {
            lock (_lock) { _mouseDx += dx; _mouseDy += dy; }
        }

        public void ConsumeMouseDelta(out float dx, out float dy)
        {
            lock (_lock) { dx = _mouseDx; dy = _mouseDy; _mouseDx = 0; _mouseDy = 0; }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _down.Clear();
                _mouseDx = _mouseDy = 0;
            }
            MouseLookActive = false;
            BreakPressed = PlacePressed = false;
            BreakHeld = false;
            InventoryClickButton = 0;
        }
    }
}
