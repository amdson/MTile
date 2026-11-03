using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Pulse: the Circle gesture. LMB goes down, the cursor orbits the press point through
// more than InputParser.CircleAngleThreshold (270 deg) at radius >= MinCircleRadius
// (16px), then LMB comes up. PulseAction's ring (radius up to 5 body radii = 60px) is
// the crowd-clearing panic button: it is slow (12f startup, 24f end-lag), so it is only
// worth it when the target is hugging us or we have just been hit.
public sealed class RampartPulse : BotManeuver
{
    private readonly RampartSense _s;
    public RampartPulse(RampartSense s) => _s = s;

    // The ring's far edge is EndRadius = 5 x PlayerCharacter.Radius; inside this gap the
    // target is certain to be swept.
    public float Range           = 50f;
    public float VerticalRange   = 40f;
    // A hit this recent makes pulsing attractive (get the attacker off us).
    public int   RecentHitFrames = 90;

    // The orbit. Radius sits well above MinCircleRadius so quantisation never drops a
    // sample. 16 steps cover 93% of a turn (~335 deg, > 270 with margin). The press is
    // held 18 frames, longer than ClickMaxHoldSeconds (0.2 s = 12f at 60fps), which is
    // what separates a circle from a Click at the release edge.
    private const float OrbitRadius = 26f;
    private const int   SweepFrames = 16;
    private const float TotalSweep  = MathF.PI * 2f * 0.93f;
    private const int   HoldFrames  = SweepFrames + 2;

    private int     _t;
    private Vector2 _pressRel;   // press point relative to the body (the gesture is player-relative)
    private float   _dir = 1f;   // sweep direction; the parser accepts either sign

    public override int Cooldown => 90;
    public override bool OwnsLocomotion => true;   // stand still: the ring is centred on us

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        if (MathF.Abs(v.Dx) > Range || MathF.Abs(v.Dy) > VerticalRange) return 0f;
        bool hurt    = v.FramesSinceHit <= RecentHitFrames;
        bool hugging = MathF.Abs(v.Dx) <= 24f;
        // A slash scores 1; pulse only beats it when it is clearly the better answer.
        // The circle can't release before ClickMaxHoldSeconds (12 frames), so it is a
        // slow, punishable commitment — in brain fights it was the bot's single biggest
        // source of damage taken when it fired merely "recently hurt" at mid range.
        if (hurt && hugging) return 1.2f;
        if (hugging)         return 0.3f;   // fallback when everything else is on cooldown
        return 0f;
    }

    public override void Begin(BotView v)
    {
        _t = 0;
        _dir = v.Rng.Next(2) == 0 ? 1f : -1f;
        // Press point a little toward the target; any spot works since the sweep is
        // measured around it.
        _pressRel = new Vector2(v.Toward * 18f, 0f);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        // Cursor is body-relative (parser measures cursor minus body position), so a
        // shoved body can't distort the angle sum.
        Vector2 rel = _pressRel;
        if (_t >= 1)
        {
            float a = _dir * (Math.Min(_t, SweepFrames) - 1) * (TotalSweep / (SweepFrames - 1));
            rel += new Vector2(MathF.Cos(a), MathF.Sin(a)) * OrbitRadius;
        }
        input.MouseWorldPosition = v.Me + rel;

        // LMB down for frames 0..HoldFrames-1; the frame it is up is the release edge.
        input.LeftClick = _t < HoldFrames;
        _t++;
        return _t <= HoldFrames;
    }
}
