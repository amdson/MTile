using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Sustained beam: Shift + LMB press edge, held. BeamAction charges ~21 frames, then
// locks the aim from the cursor at the instant it lights and fires for ~33 frames
// (a 420px hitscan lance, HitTargets.All, magenta). Releasing LMB early cancels it, and
// any L/R/Space input drops the beam — so the maneuver owns locomotion and stands still.
//
// The other ranged moves are not live in the kit right now: EnergyBallAction and
// GrenadeAction were removed (2026-09-01) and LobbedAreaAction is unregistered in
// PlayerCharacter (the lobbed ball is thrown by BlockGrabAction), so there is nothing for
// a bot to press for them. This is the only ranged maneuver that can work.
public sealed class BeamManeuver : BotManeuver
{
    // Shorter than this a slash is the better idea (and the beam's muzzle is the body
    // centre, so point-blank it works but wastes the 54-frame commitment).
    public float MinRange = 70f;
    // BeamAction's reach is 420px; stay inside it with margin for the energy falloff.
    public float MaxRange = 380f;
    // Frames to hold LMB: charge (21) + firing (33) + a little slack. The action ends
    // itself at MaxFiringTime; the extra frames just keep LMB down until then.
    public int   HoldFrames = 58;
    // The aim locks ~21 frames in and the bot sees the target ReactionFrames stale; lead
    // the cursor by this many frames of target velocity.
    public int   LeadFrames = 12;
    public float LeadClamp  = 40f;   // never aim more than this far off the perceived target
    // BeamAction (passive 45) currently LOSES every Shift+LMB press to GrabAction (passive
    // 48) unless the cursor is on terrain (then BlockGrabAction wins) — so against a body
    // in open air the press just starts a whiffing grab. Off by default so registering
    // this maneuver in DefaultManeuvers can't make the bot whiff grabs; flip to true (or
    // delete) once the game lets the beam fire. Verified: with GrabAction's passive
    // lowered to 44 locally, this maneuver fires BeamAction at frame ~22 and lands damage.
    public bool  Enabled = false;
    public int   CooldownMin = 60;
    public int   CooldownMax = 100;

    private int _cooldown = 60;
    private int _t;
    public override int Cooldown => _cooldown;
    public override bool OwnsLocomotion => true;

    // Applicable only at beam range with a clear line (stone chokes the beam fast), and
    // only while grounded: BeamAction slows you, and airborne the charge is spent falling.
    // 0.8 so an in-range slash (1) still wins when close.
    public override float Score(BotView v)
    {
        if (!Enabled) return 0f;
        float d = v.Dist;
        if (d < MinRange || d > MaxRange) return 0f;
        if (!v.Grounded) return 0f;
        if (!v.LineOfSight()) return 0f;
        // The beam is a straight lance that locks aim late; a target sprinting across the
        // line is a worse bet.
        if (MathF.Abs(v.TargetVel.X) > 200f) return 0.4f;
        return 0.8f;
    }

    public override void Begin(BotView v)
    {
        _t = 0;
        _cooldown = v.Rng.Next(CooldownMin, CooldownMax + 1);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        // Shift must be down on the press frame and LMB goes down the same frame (the
        // action's pre-condition wants Shift && LMB && !prev LMB). Keep both held.
        input.Shift     = true;
        input.LeftClick = true;
        input.Left = input.Right = input.Space = false;

        // The cursor is the aim: the beam direction is body→cursor sampled when it lights
        // (frame ~21), so keep tracking the target with a clamped lead until then.
        Vector2 lead = v.TargetVel * (LeadFrames * Simulation.FixedDt);
        if (lead.Length() > LeadClamp) lead = Vector2.Normalize(lead) * LeadClamp;
        input.MouseWorldPosition = v.TargetPos + lead;

        return ++_t < HoldFrames;
    }
}
