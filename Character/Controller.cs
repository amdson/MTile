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

        public void Update(Vector2 mouseWorldPosition)
        {
            _currentIndex = (_currentIndex + 1) % BufferSize;

            var keyboardState = Keyboard.GetState();
            var mouseState = Mouse.GetState();

            _inputBuffer[_currentIndex] = new PlayerInput
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
                Shift = keyboardState.IsKeyDown(Keys.LeftShift) || keyboardState.IsKeyDown(Keys.RightShift)
            };
        }
    }
}

