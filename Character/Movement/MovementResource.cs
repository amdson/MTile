using System;

namespace MTile;

// The resource layer (Plans/MOVEMENT_RESOURCE_MODEL.md §3.2): what the body
// can act with. Each resource is available iff its channel probes pass
// (EnvironmentContext) and nothing else; a solver channel's mask is
// (Available & state.Uses) for the resource it belongs to.
[Flags]
public enum MovementResource : byte
{
    None    = 0,
    Legs    = 1,   // the Legs probe: LegServo, Drive, Tuck, floor Redirect
    Hands   = 2,   // HandCorner / Walls / Grip: corner Redirect, ContactPush, CornerPlant, Grip*
    Air     = 4,   // not Legs: the state's own air control (a baseline, not a solver channel)
    Support = 8,   // the Support probe: the gravity hold (a baseline, not a solver channel)
}

public static class MovementResources
{
    // What the environment offers this frame, from the probes alone.
    public static MovementResource Available(EnvironmentContext ctx)
    {
        var r = MovementResource.None;
        if (ctx.Legs) r |= MovementResource.Legs;
        else          r |= MovementResource.Air;
        if (ctx.Support(out _)) r |= MovementResource.Support;
        ctx.Walls(out bool l, out bool rt);
        if (ctx.HandCorner || l || rt) r |= MovementResource.Hands;
        return r;
    }

    // The one channel → resource table. Redirect is the plant-and-deflect
    // disc on whatever the body plants against: the floor (Legs) or a corner
    // in hand reach (Hands) — the same actuator on two anchors.
    public static MovementResource Of(CorrectionChannel c) => c switch
    {
        CorrectionChannel.LegServo or CorrectionChannel.Drive or CorrectionChannel.Tuck
            => MovementResource.Legs,
        CorrectionChannel.Redirect
            => MovementResource.Legs | MovementResource.Hands,
        CorrectionChannel.ContactPushLeft or CorrectionChannel.ContactPushRight
            or CorrectionChannel.CornerPlant or CorrectionChannel.CornerAssist
            or CorrectionChannel.GripLift or CorrectionChannel.GripPull
            or CorrectionChannel.GripHold or CorrectionChannel.GripBrace
            => MovementResource.Hands,
        CorrectionChannel.AirLateral or CorrectionChannel.AirLateralBack or CorrectionChannel.AirVertical
            => MovementResource.Air,
        _ => MovementResource.None,
    };

    public static bool Has(this MovementResource set, MovementResource r) => (set & r) != 0;
}
