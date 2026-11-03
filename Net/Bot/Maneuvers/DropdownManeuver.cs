using System;

namespace MTile.Net;

// Slide off a ledge when the target is on a lower level. Down held on the ground makes
// DropdownState (Down + ground + a drop edge the body overhangs) slide the body over the
// corner; it then falls and can LedgeGrab-chain. The default chase also walks off edges,
// but at walking pace with no regard for the drop; this commits to the edge and holds Down
// so the slide is the controlled one.
//
// Score: grounded, target clearly below (>= 3 tiles) and not far to the side.
public sealed class DropdownManeuver : BotManeuver
{
    public float MinDrop = 3f * Chunk.TileSize;
    public float MaxSide = 10f * Chunk.TileSize;
    public override int Cooldown => 60;

    private int _t, _airFrames;

    public override float Score(BotView v)
        => v.Grounded && v.Dy >= MinDrop && MathF.Abs(v.Dx) <= MaxSide ? 0.9f : 0f;

    public override void Begin(BotView v) { _t = 0; _airFrames = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        BotInput.Move(ref input, v.Toward);
        input.Down = true;
        _t++;
        if (!v.Grounded) _airFrames++;
        // Done a few frames after leaving the floor (the default air steering takes over),
        // or after 90 frames of failing to find an edge.
        return _airFrames < 4 && _t < 90;
    }
}
