using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Anti-air: stab UP at an opponent who is coming down on us (the jump-over + DownAirSlash
// pogo that humans use against the bot).
//
// Input: the shared StabGesture (hold LMB 14f with the cursor swiped out along `dir`, release
// = Stab intent). StabAction.Enter only zeroes a swipe X component that points AWAY from
// facing, so a straight-up or up-and-forward swipe survives from the ground (no ground clamp
// on direction). The stab then has ~10.8f startup, 7 active frames, reach 12*2.3*1.75 = 48 px
// from the body (plus the target's radius ~12), steering up to ~31 deg toward the cursor.
//
// Timing: Begin -> release is 14f, release -> active is ~11f, so the tip is out ~25..32f
// (~0.42..0.53 s) after Begin. We therefore predict the target at LeadFrames = 28 with ballistic
// motion (pos + v t + 0.5 g t^2, g = 600) and start only when that point sits inside the stab's
// reach (up to MaxUp above, within ReachX sideways). While holding, the aim is re-sampled every
// frame at the predicted point for the remaining lead, so the swipe tracks the descent.
// Not applicable to grounded targets or ones already below us.
public sealed class AntiAirStabManeuver : BotManeuver
{
    public int   LeadFrames = 28;
    public float Gravity    = 600f;
    public float ReachX     = 34f;    // horizontal slack at the predicted point
    public float MinUp      = 14f;    // predicted point must be at least this far above centre
    public float MaxUp      = 60f;    // ... and no further than tip 48 + target radius
    public float SeeRange   = 200f;   // only consider targets within this span

    private int _i;
    public override int Cooldown => 45;
    public override bool OwnsLocomotion => true;   // stay planted: no chase/hop while the stab is held
    public override MoveClaim Move => MoveClaim.Still;   // holds the stab (LMB) and writes no movement keys; no chase/hop while it is held

    private Vector2 Predict(BotView v, int frames)
    {
        float t = frames / 60f;
        return v.TargetPos + v.TargetVel * t + new Vector2(0f, 0.5f * Gravity * t * t);
    }

    public override float Score(BotView v)
    {
        if (v.TargetGrounded) return 0f;
        if (v.Dy >= 0f || MathF.Abs(v.Dx) > SeeRange || -v.Dy > SeeRange) return 0f;
        var p = Predict(v, LeadFrames) - v.Me;
        if (MathF.Abs(p.X) > ReachX || -p.Y < MinUp || -p.Y > MaxUp) return 0f;
        return 1.4f;
    }

    // Bet (M4): stab straight up, 0.75 + launch ~60% (0.55); a stab that beats the descent
    // avoids ~0.08 (0.63, after the jump slash); 14 f hold + 36 f stab ~ 50 f.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.55f, -0.08f, 50);
    }

    public override void Begin(BotView v) => _i = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        int remaining = Math.Max(LeadFrames - _i, 1);
        var d = Predict(v, remaining) - v.Me;
        // Stay mostly upward: the tip steers at most ~31 deg off the captured swipe.
        if (d.Y > -10f) d.Y = -10f;
        var dir = Vector2.Normalize(d);
        bool done = StabGesture.Drive(v, ref input, _i, dir);
        _i++;
        return !done;
    }
}
