using Microsoft.Xna.Framework.Input;

namespace MTile;

// Dev tool: pause the live sim and advance it one fixed step at a time, for inspecting
// movement/animation/corrector state frame by frame. OFFLINE ONLY — Game1 skips it in a
// netplay session, because pausing one peer stalls the other into its rollback cap and
// then desyncs the pair.
//
//   F6 / Pause      toggle pause
//   F7 / .          advance ONE sim frame (hold to repeat)
//   Shift+F7 / >    advance BurstSteps frames
//
// This is distinct from GameRecorder's Ctrl+P scrubbing: that replays a *recorded* take
// with the sim frozen, so it cannot show you a frame that has not happened yet. Here the
// sim is live — the keyboard/mouse are polled as normal on each stepped frame, so you can
// hold Right, tap F7, and watch one frame of running resolve.
//
// The stepper only decides HOW MANY steps run; it never touches sim state, so a paused
// game is bit-identical to the same game unpaused (modulo the input you feed it).
public sealed class FrameStepper
{
    // Frames advanced by one Shift+step. Roughly an eyeblink of sim time (1/6 s) —
    // enough to cross a short state transition without losing your place.
    private const int BurstSteps = 10;

    // Seconds a step key must be held before it starts auto-repeating, then the interval
    // between repeats. Tap-to-single-step has to stay reliable, so the delay is well
    // clear of any realistic tap.
    private const float RepeatDelay    = 0.35f;
    private const float RepeatInterval = 0.06f;

    private KeyboardState _prev;
    private float _heldFor;        // seconds the step key has been continuously down
    private float _repeatAccum;    // seconds banked since the last auto-repeat fired
    private int   _pending;        // steps owed to the sim, drained by TryConsumeStep

    public bool Paused { get; private set; }

    // Sim frames advanced since the last pause — the "how far did I step" counter shown
    // in the HUD. Reset on every pause so it reads as an offset from where you stopped.
    public int SteppedFrames { get; private set; }

    // Call once per Update with the live keyboard and the REAL frame delta (not sim time —
    // key repeat has to keep working while the sim is frozen).
    public void HandleInput(KeyboardState keys, float realDt)
    {
        bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);

        if (Pressed(keys, Keys.F6) || Pressed(keys, Keys.Pause))
            SetPaused(!Paused);

        bool stepDown = keys.IsKeyDown(Keys.F7) || keys.IsKeyDown(Keys.OemPeriod);
        bool stepHit  = stepDown && !(_prev.IsKeyDown(Keys.F7) || _prev.IsKeyDown(Keys.OemPeriod));

        if (stepHit)
        {
            // Stepping implies pausing: tapping step while the game runs is always a
            // request to stop and look at a frame, never to add one to a live 60 Hz.
            if (!Paused) SetPaused(true);
            _pending += shift ? BurstSteps : 1;
            _heldFor = 0f;
            _repeatAccum = 0f;
        }
        else if (stepDown && Paused)
        {
            // TODO(human): hold-to-repeat. _heldFor is seconds the key has been down and
            // _repeatAccum is seconds banked since the last repeat fired; advance both by
            // realDt and add to _pending when a repeat is due.
        }
        else
        {
            _heldFor = 0f;
            _repeatAccum = 0f;
        }

        _prev = keys;
    }

    private void SetPaused(bool paused)
    {
        Paused = paused;
        _pending = 0;
        SteppedFrames = 0;
        _heldFor = 0f;
        _repeatAccum = 0f;
    }

    // Drain one owed step. Game1 calls this in a while-loop in place of the accumulator
    // loop while paused, so a burst runs its frames inside a single rendered frame.
    public bool TryConsumeStep()
    {
        if (_pending <= 0) return false;
        _pending--;
        SteppedFrames++;
        return true;
    }

    // HUD line while paused; null when running (nothing to draw).
    public string HudLine(int simFrame)
        => Paused
            ? $"[PAUSED]  frame {simFrame}  (+{SteppedFrames})   F7/. step  Shift +{BurstSteps}  F6 resume"
            : null;

    private bool Pressed(KeyboardState keys, Keys k) => keys.IsKeyDown(k) && !_prev.IsKeyDown(k);
}
