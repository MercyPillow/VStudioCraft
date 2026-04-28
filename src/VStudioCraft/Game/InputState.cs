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
        // Tier 5 #27 — Middle-click pick-block. One-shot press flag
        // queued by the host on MMB-down; the renderer consumes it on
        // the next frame and runs TryPickBlock to copy the looked-at
        // block into / select on the hotbar.
        public bool PickBlockPressed;

        // True while the left mouse button is held with mouse-look captured.
        // Drives survival-mode block-break progress: the renderer accumulates
        // dt/hardness each frame this is set against a stable target. The
        // one-shot BreakPressed above still fires on click (used in creative
        // for instant break); survival ignores it and uses BreakHeld instead.
        public bool BreakHeld;

        // Tier 4 #17 — True while the right mouse button is held with
        // mouse-look captured. Drives bow-charge accumulation: while
        // the held item is Bow and PlaceHeld is set, the renderer
        // builds up draw fraction over ArrowProjectile.MaxDrawSeconds.
        // On the falling edge (PlaceHeld → false with prior charge)
        // an arrow fires from the camera at scaled muzzle velocity.
        // Other RMB consumers (placement, food eat, doors, etc.) keep
        // routing through the existing one-shot PlacePressed; the
        // hold path is bow-only.
        public bool PlaceHeld;

        // Player-owned 45-slot inventory + cursor. Hotbar lives at slot
        // indices 36..44 (matching InventoryScreen). The render thread reads
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
        // Shift state captured at the moment the click was queued. The
        // renderer drains the click on its own frame so we have to snapshot
        // the modifier state at queue time — querying Control.ModifierKeys
        // from the render thread would race with the UI thread.
        public bool InventoryClickShift;

        // RMB drag-deposit queue. While the player holds RMB and drags
        // the cursor across modal slots (inventory or crafting), the
        // host paints each newly-entered slot with one item from the
        // cursor stack. Multiple slot crossings can happen between
        // render frames (mouse moves several times faster than the
        // 60Hz drain), so we accumulate slot indices in a queue
        // rather than the single-shot InventoryClickButton field.
        // Slot index meaning is panel-dependent: when the inventory
        // is open these are Inventory.Slots indices (0..44 — survival
        // path) or hotbar indices (creative path); when crafting is
        // open these are CraftingScreen slot indices (0..54). The
        // renderer reads its own _is*Open flag at drain time to pick
        // the right interpretation, so the queue itself is opaque.
        private readonly Queue<int> _dragDeposits = new Queue<int>();

        // Q-drop one-shots. Q with no modifier drops 1 from the selected
        // hotbar slot; Shift+Q drops the whole stack. Both are consumed by
        // the renderer between frames and reset to false.
        public bool DropOnePressed;
        public bool DropStackPressed;

        // Creative-mode catalog state. Both fields are written by the host
        // (UI thread) and read by the renderer (render thread) — same
        // single-int / reference-assignment model as the existing
        // InventoryClick* fields, no lock needed for the brief tear window.
        //
        // SearchText: current contents of the search bar. The host appends
        //   typed characters in GlOnKeyPress and prunes the last on
        //   Backspace. Renderer reads this each frame to filter the catalog.
        // ScrollRows: how many catalog rows are scrolled past the top of
        //   the visible window. Mouse wheel events while the creative
        //   inventory is open mutate this; clamping to the filtered list
        //   length happens on the render side because the host doesn't
        //   know how many rows the current filter produced.
        public string InventorySearchText = string.Empty;
        public int InventoryScrollRows;

        // Tier 6 #47 — Generic text-entry pipeline. Each modal that
        // needs a text field declares an enum value here; the host's
        // KeyPress / Backspace path branches on FocusedField and
        // appends to / pops from the matching buffer. Persists across
        // frames so a user can type, click another field, and resume
        // typing without losing their previous input. Renderer reads
        // the field-of-interest + active focus to decide caret visibility.
        public enum TextField
        {
            None,
            InventorySearch,
            WorldName,
            WorldSeed,
            ServerAddress,
            ServerUsername,
        }

        public TextField FocusedField;
        public string WorldNameText = string.Empty;
        public string WorldSeedText = string.Empty;
        public string ServerAddressText = "localhost:25565";
        public string ServerUsernameText = "Player";
        public string MultiplayerErrorText = string.Empty;
        public int WorldSelectScroll;

        // Append a printable character to the focused text field, capped
        // at maxLen. Routes via FocusedField so call sites don't have to
        // switch on it. No-op if no field is focused.
        public void AppendChar(char c, int maxLen)
        {
            switch (FocusedField)
            {
                case TextField.InventorySearch:
                    if (InventorySearchText.Length < maxLen) InventorySearchText += c;
                    break;
                case TextField.WorldName:
                    if (WorldNameText.Length < maxLen) WorldNameText += c;
                    break;
                case TextField.WorldSeed:
                    if (WorldSeedText.Length < maxLen) WorldSeedText += c;
                    break;
                case TextField.ServerAddress:
                    if (ServerAddressText.Length < maxLen) ServerAddressText += c;
                    break;
                case TextField.ServerUsername:
                    if (ServerUsernameText.Length < maxLen) ServerUsernameText += c;
                    break;
            }
        }

        // Pop one character from the end of the focused text field.
        // No-op if no field is focused or the field is already empty.
        public void Backspace()
        {
            switch (FocusedField)
            {
                case TextField.InventorySearch:
                    if (InventorySearchText.Length > 0)
                        InventorySearchText = InventorySearchText.Substring(0, InventorySearchText.Length - 1);
                    break;
                case TextField.WorldName:
                    if (WorldNameText.Length > 0)
                        WorldNameText = WorldNameText.Substring(0, WorldNameText.Length - 1);
                    break;
                case TextField.WorldSeed:
                    if (WorldSeedText.Length > 0)
                        WorldSeedText = WorldSeedText.Substring(0, WorldSeedText.Length - 1);
                    break;
                case TextField.ServerAddress:
                    if (ServerAddressText.Length > 0)
                        ServerAddressText = ServerAddressText.Substring(0, ServerAddressText.Length - 1);
                    break;
                case TextField.ServerUsername:
                    if (ServerUsernameText.Length > 0)
                        ServerUsernameText = ServerUsernameText.Substring(0, ServerUsernameText.Length - 1);
                    break;
            }
        }

        // Tier 5 #30 — F3 debug overlay toggle. Persists across pause /
        // inventory / etc. so you can flip it on, open inventory to read
        // your inventory + coords side by side, then flip it off.
        public bool DebugOverlayVisible;

        // Tier 5 #31 — Hotbar-change label timer. The renderer reads the
        // currently-held block name above the hotbar; without a timer the
        // label sat there permanently and just polluted the bottom of the
        // screen. With this it fades out HotbarLabelHoldSeconds after the
        // last selection change, fading over the last HotbarLabelFadeSeconds.
        // Mutated on the render thread (CountDown each frame + the host's
        // OnHotbarMaybeChanged poll), but a brief tear from a parallel
        // numeric-key press just resets the timer to the hold duration —
        // worst case is one extra blink, never a crash.
        public float HotbarLabelTimer;
        public const float HotbarLabelHoldSeconds = 2.0f;
        public const float HotbarLabelFadeSeconds = 0.5f;
        private int _lastHotbarIndex = -1;

        // Re-arm the label timer if HotbarIndex changed since the last
        // poll. Called once per frame by the render thread; -1 sentinel
        // on first call ensures the very first frame doesn't spuriously
        // show the label (the next frame's poll matches and stays quiet
        // until a real key/scroll event flips the index).
        public void OnHotbarMaybeChanged()
        {
            if (HotbarIndex != _lastHotbarIndex)
            {
                if (_lastHotbarIndex >= 0) HotbarLabelTimer = HotbarLabelHoldSeconds;
                _lastHotbarIndex = HotbarIndex;
            }
        }

        public InputState()
        {
            // Starter loadout — nine canonical Alpha blocks, full stacks.
            // Survival players can clear these by tossing them; creative
            // ignores stack counts entirely.
            Inventory.FillHotbar(new[]
            {
                BlockType.Grass,
                BlockType.Dirt,
                BlockType.Stone,
                BlockType.Sand,
                BlockType.Torch,
                BlockType.Dandelion,
                BlockType.Rose,
                BlockType.Cobblestone,
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

        // Append a slot index to the drag-deposit queue. Called by the
        // host on MouseMove when the RMB-drag state crosses into a new
        // unpainted slot. Lock-protected because the render thread
        // drains it (single-thread reader + single-thread writer is
        // still racy on the underlying array head/tail pointers).
        public void EnqueueDragDeposit(int slotIndex)
        {
            lock (_lock) _dragDeposits.Enqueue(slotIndex);
        }

        // Drain the drag-deposit queue, returning the pending slot
        // indices in arrival order. Safe to call when the queue is
        // empty (returns an empty array). Called by the render thread
        // once per frame after the single-shot click drain.
        public int[] DrainDragDeposits()
        {
            lock (_lock)
            {
                if (_dragDeposits.Count == 0) return System.Array.Empty<int>();
                var arr = _dragDeposits.ToArray();
                _dragDeposits.Clear();
                return arr;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _down.Clear();
                _mouseDx = _mouseDy = 0;
                _dragDeposits.Clear();
            }
            MouseLookActive = false;
            BreakPressed = PlacePressed = false;
            PickBlockPressed = false;
            BreakHeld = false;
            PlaceHeld = false;
            InventoryClickButton = 0;
            InventoryClickShift = false;
            DropOnePressed = false;
            DropStackPressed = false;
        }

        // Reset the creative catalog UI state. Called when the inventory
        // closes so the next open starts with a fresh, unfiltered, top-of-
        // catalog view.
        public void ResetInventorySearch()
        {
            InventorySearchText = string.Empty;
            InventoryScrollRows = 0;
        }
    }
}
