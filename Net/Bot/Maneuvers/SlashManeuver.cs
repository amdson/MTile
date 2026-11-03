using System;

namespace MTile.Net;

// Ground/air slash: a 1-frame LMB tap with the cursor on the target. The press edge +
// next frame's release is a Click; SlashLikeAction picks the variant (GroundSlash1-3,
// AirSlash1/2, CrouchSlash, DownAirSlash…) from movement state and combo flags.
public sealed class SlashManeuver : BotManeuver
{
    // Horizontal gap (body centre to body centre) inside which a swing connects.
    // A ground slash reaches ~47px from the body centre (SlashLikeAction: 1.5 × ArcRadius).
    public float Reach         = 42f;
    // Slashes are a flat arc: only swing when the target is inside this vertical band.
    public float VerticalReach = 30f;
    public int   CooldownMin   = 14;
    public int   CooldownMax   = 30;

    private int _cooldown = 20;
    public override int Cooldown => _cooldown;

    public override float Score(BotView v)
        => MathF.Abs(v.Dx) <= Reach && MathF.Abs(v.Dy) <= VerticalReach ? 1f : 0f;

    public override void Begin(BotView v) => _cooldown = v.Rng.Next(CooldownMin, CooldownMax + 1);

    public override bool Step(BotView v, ref PlayerInput input)
    {
        input.LeftClick = true;   // this frame only; next frame's release is the Click
        // A slash aimed behind the body is clamped to vertical, so make sure we're
        // facing the target on the press frame.
        if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        return false;
    }
}
