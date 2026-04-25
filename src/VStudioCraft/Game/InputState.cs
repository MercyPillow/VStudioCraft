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

        // 9-slot hotbar. The render thread reads HotbarIndex + HotbarSlots
        // every frame to draw the bar; the UI thread writes them in response
        // to number-key presses. Both fields are atomic single-word writes
        // on x86/x64 (the slot array reference is fixed at construction —
        // only its contents are mutated, and only by the UI thread), so no
        // lock is needed for the cross-thread reads.
        public int HotbarIndex;
        public readonly BlockType[] HotbarSlots = new BlockType[]
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
        };

        // Block currently held — what TryPlace places, what the status text
        // shows. Slot index is clamped on read so an out-of-range index never
        // crashes (in practice it's always 0..8).
        public BlockType SelectedBlock
        {
            get
            {
                int i = HotbarIndex;
                if (i < 0 || i >= HotbarSlots.Length) i = 0;
                return HotbarSlots[i];
            }
        }

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
        }
    }
}
