using System.Collections.Generic;
using System.Windows.Forms;

namespace VStudioCraft.Game
{
    internal sealed class InputState
    {
        private readonly HashSet<Keys> _down = new HashSet<Keys>();

        public bool MouseLookActive;
        public float MouseDx;
        public float MouseDy;

        public bool BreakPressed;   // one-shot, consumed by renderer
        public bool PlacePressed;   // one-shot, consumed by renderer
        public BlockType SelectedBlock = BlockType.Grass;

        public void KeyDown(Keys k) => _down.Add(k);
        public void KeyUp(Keys k) => _down.Remove(k);
        public bool IsDown(Keys k) => _down.Contains(k);

        public void Clear()
        {
            _down.Clear();
            MouseLookActive = false;
            MouseDx = MouseDy = 0;
            BreakPressed = PlacePressed = false;
        }

        public void ConsumeMouseDelta(out float dx, out float dy)
        {
            dx = MouseDx;
            dy = MouseDy;
            MouseDx = 0;
            MouseDy = 0;
        }
    }
}
