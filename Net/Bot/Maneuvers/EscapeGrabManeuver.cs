using System;

namespace MTile.Net;

// Struggle out of a grab. While the bot is held (GrabbedActive, movement state
// GrabbedState) every normal attack is gated off; the one attack left is GrabbedSlash,
// which needs a Click intent and erodes the grabber's GrabStrength (3 => breaks on the
// 3rd connecting hit). So: mash LMB with the cursor on the grabber, alternating down/up
// so each down-edge + release is a fresh Click. GrabbedSlash's own 9-frame end-lag
// throttles it; the mash just keeps a click always ready.
public sealed class EscapeGrabManeuver : BotManeuver
{
    public override int Cooldown => 0;

    // Chase/backoff locomotion would fight the grab; the maneuver only leans toward the
    // grabber (to face them — the struggle arc is short and one-sided).
    public override bool OwnsLocomotion => true;

    // Dominates everything: nothing else can be done while held.
    public override float Score(BotView v) => v.Self.Combat.GrabbedActive ? 10f : 0f;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (!v.Self.Combat.GrabbedActive) return false;
        // Press on even frames; the odd-frame release completes the Click.
        input.LeftClick = (v.Frame & 1) == 0;
        BotInput.Move(ref input, v.Toward);
        return true;
    }
}
