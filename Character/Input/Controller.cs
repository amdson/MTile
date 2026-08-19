using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace MTile
{
    public struct PlayerInput
    {
        public bool Left;
        public bool Right;
        public bool Up;
        public bool Down;
        public bool LeftClick;
        public bool RightClick;
        public Point MousePosition;
        public Vector2 MouseWorldPosition;
        public bool Space;
        public bool Shift;
        // F key: bound to GrenadeAction (Shift+RMB is reserved for LobbedAreaAction).
        // Polled by GrenadeAction's CheckPreConditions on press-edge.
        public bool F;
        // 'P' toggles the eruption planner mode. Captured as raw key state so the
        // edge-detect (toggle on press) happens deterministically inside the sim,
        // not by polling hardware mid-update.
        public bool P;
        // Block-picker number keys (1=Stone, 2=Dirt, 3=Sand, 4=Foam). Raw key state;
        // the sim interprets them into the active block type each Step. Part of input
        // so the selection is per-player and rollback-deterministic.
        public bool Num1;
        public bool Num2;
        public bool Num3;
        public bool Num4;
    }

    public class Controller
    {
        private const int BufferSize = 32;
        private readonly PlayerInput[] _inputBuffer = new PlayerInput[BufferSize];
        private int _currentIndex = 0;

        public PlayerInput Current => _inputBuffer[_currentIndex];

        public PlayerInput GetPrevious(int framesBack)
        {
            if (framesBack < 0 || framesBack >= BufferSize)
                framesBack = 0;

            int index = (_currentIndex - framesBack) % BufferSize;
            if (index < 0)
                index += BufferSize;

            return _inputBuffer[index];
        }

        // Used by headless simulation: advances the buffer with a scripted input instead of reading hardware.
        public void InjectInput(PlayerInput input)
        {
            _currentIndex = (_currentIndex + 1) % BufferSize;
            _inputBuffer[_currentIndex] = input;
        }

        // Snapshot hardware into a PlayerInput. Pure (no buffer mutation) so the
        // input-gather step can build the frame's input and hand it to the sim,
        // which is the only thing that advances the buffer (via InjectInput).
        // mouseWorldPosition is supplied by the caller because it depends on the
        // camera, which is a render-side concern.
        public static PlayerInput Poll(Vector2 mouseWorldPosition)
        {
            var keyboardState = Keyboard.GetState();
            var mouseState = Mouse.GetState();

            return new PlayerInput
            {
                Left  = keyboardState.IsKeyDown(Keys.Left)  || keyboardState.IsKeyDown(Keys.A),
                Right = keyboardState.IsKeyDown(Keys.Right) || keyboardState.IsKeyDown(Keys.D),
                Up    = keyboardState.IsKeyDown(Keys.Up)    || keyboardState.IsKeyDown(Keys.W),
                Down  = keyboardState.IsKeyDown(Keys.Down)  || keyboardState.IsKeyDown(Keys.S),
                LeftClick = mouseState.LeftButton == ButtonState.Pressed,
                RightClick = mouseState.RightButton == ButtonState.Pressed,
                MousePosition = mouseState.Position,
                MouseWorldPosition = mouseWorldPosition,
                Space = keyboardState.IsKeyDown(Keys.Space),
                Shift = keyboardState.IsKeyDown(Keys.LeftShift) || keyboardState.IsKeyDown(Keys.RightShift),
                F     = keyboardState.IsKeyDown(Keys.F),
                P     = keyboardState.IsKeyDown(Keys.P),
                Num1  = keyboardState.IsKeyDown(Keys.D1),
                Num2  = keyboardState.IsKeyDown(Keys.D2),
                Num3  = keyboardState.IsKeyDown(Keys.D3),
                Num4  = keyboardState.IsKeyDown(Keys.D4),
            };
        }

        public void Update(Vector2 mouseWorldPosition) => InjectInput(Poll(mouseWorldPosition));

        // ── Snapshot/restore (roadmap goal 4 §E) ────────────────────────────────
        // The controller's state is its 32-frame input ring + the write cursor. The
        // ring is PlayerInput (a struct), so the array copy is a deep copy.
        public ControllerState Capture()
        {
            var ring = new PlayerInput[BufferSize];
            System.Array.Copy(_inputBuffer, ring, BufferSize);
            return new ControllerState { Ring = ring, CurrentIndex = _currentIndex };
        }

        public void Restore(in ControllerState s)
        {
            if (s.Ring != null) System.Array.Copy(s.Ring, _inputBuffer, BufferSize);
            _currentIndex = s.CurrentIndex;
        }
    }

    // Flat snapshot of a Controller's input ring + write cursor (roadmap goal 4 §E).
    public struct ControllerState
    {
        public PlayerInput[] Ring;
        public int           CurrentIndex;
    }
}

