using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Spacing: step away from the target for ~15-25 frames right after the bot has attacked,
// or when the target is crowding the bot while its attacks are on cooldown. Without this
// the default locomotion walks to EngageRange and stands face-hugging the target between
// swings.
//
// Two utilities. Crowded / just-maneuvered: just under an available slash (1), so it
// fills cooldown gaps. Hit-and-run: right after the bot's own attack press, while the
// target is close and NOT helpless, it beats every melee option — staying in to swing
// again is what a target that swings back punishes (in full-brain fights against a
// click-spamming P1, nearly all damage the bot took landed in its own recovery inside
// P1's reach). A target in hitstun/stun can't punish, so then the combo continues.
//
// Input: a held Left/Right away from the target (the cursor stays on the target, so the
// bot keeps aiming at it while retreating). If a wall stops the retreat, it hops (Space)
// so a cornered bot still opens distance in the air.
public sealed class RampartBackoff : BotManeuver
{
    private readonly RampartSense _s;
    public RampartBackoff(RampartSense s) => _s = s;

    // Closer than this (centre to centre, horizontally) counts as crowded.
    public float CrowdDist   = 20f;
    // Backoff is also wanted this long after the previous maneuver began, while still close.
    public int   AfterAttack = 26;
    public float CloseDist   = 44f;
    public int   MinFrames   = 15;
    public int   MaxFrames   = 25;
    public float Utility     = 0.8f;
    public int   RunAfterAttack = 12;    // frames after an attack press that hit-and-run applies
    public float RunDist        = 56f;   // the target's plausible melee reach plus a step
    public float RunUtility     = 1.6f;

    private int _frames, _len;
    private int _blocked;          // consecutive frames pushing away with no progress
    private int _hop;              // >0 while Space is held for a hop

    public override int Cooldown => 10;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        float adx = MathF.Abs(v.Dx);
        if (MathF.Abs(v.Dy) > 30f) return 0f;                 // not on the same level: nothing to space against
        if (adx < RunDist && v.FramesSinceAttack < RunAfterAttack && !_s.TargetHelpless) return RunUtility;
        if (adx < CrowdDist) return Utility;                   // face-hugging
        if (adx < CloseDist && v.FramesSinceManeuver < AfterAttack) return Utility;
        return 0f;
    }

    public override void Begin(BotView v)
    {
        _len     = v.Rng.Next(MinFrames, MaxFrames + 1);
        _frames  = 0;
        _blocked = 0;
        _hop     = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        BotInput.Move(ref input, -v.Toward);

        // Cornered: a wall right behind us and no horizontal progress ⇒ hop. (Checking the
        // wall matters: a target shoving into us also zeroes our speed.)
        bool wallBehind = v.Solid(v.Me + new Vector2(-v.Toward * (Chunk.TileSize * 1.5f), 0f));
        if (wallBehind && MathF.Abs(v.MyVel.X) < 8f && _frames > 2) _blocked++; else _blocked = 0;
        if (_hop > 0) { input.Space = true; _hop--; }
        else if (_blocked >= 4 && v.Grounded) { input.Space = true; _hop = 9; _blocked = 0; }

        return ++_frames < _len;
    }
}
