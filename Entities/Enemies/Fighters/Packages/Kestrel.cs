using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Kestrel: a light flyer with a slam and touch damage. The brain reads the ceiling
// (a rationed probe) and picks a hover height that always leaves room for a short dive,
// so the roofed corridor is not a dead zone like it is for the stock Flyer.
public sealed class Kestrel : IFighterPackage
{
    public string Name   => "Kestrel";
    public string Author => "sonnet-w1";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1.3f,
        Density        = 1.3f,
        ReactionFrames = 6,
        Thrust         = 1400f,
        EnergyReserve  = 3f,
        EnergyRegen    = 1.6f,
        Color          = new Color(200, 130, 60),
        Actions        = { Slam(), Contact() },
        EngageRange    = 26f,
        HoverHeight    = 40f,
        AlertRange     = 320f,
        Brain          = s => new KestrelBrain(s),
    };

    private static ActionSpec Slam()
    {
        var a = ActionSpec.Default(ActionKind.Slam);
        a.FallSpeedMin = 30f; a.MaxRange = 40f;
        return a;
    }

    private static ActionSpec Contact() => ActionSpec.Default(ActionKind.Contact);

    // Scratch: I0 = mode (0 station, 1 dive), I1 = frames to next probe,
    // F0 = seconds in mode, F1 = last headroom (tiles, 0 = unknown).
    private sealed class KestrelBrain : FighterController
    {
        private readonly float _engage, _hover, _alert;
        public KestrelBrain(FighterSpec s) { _engage = s.EngageRange; _hover = s.HoverHeight; _alert = s.AlertRange; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var pos  = self.Position;
            EnemyTarget t; int age;
            if (s.Frame % 3 == 0 && self.Energy - s.TargetCost >= 0.4f) t = s.Target(out age);
            else                                                         t = s.TargetStale(out age);
            if (!t.Known || age > 12) t = s.TargetCoarse();
            var to   = t.Position - pos;
            var input = new EnemyInput { AimWorld = t.Position };
            if (to.Length() > _alert) { m.I0 = 0; m.F0 = 0f; return input; }

            if (m.I1 <= 0)
            {
                if (self.Energy - s.ProbeCost >= 0.6f)
                {
                    var p = s.Probe(to.X >= 0f ? 1 : -1);
                    if (p.Known) m.F1 = p.Headroom;
                }
                m.I1 = 20;
            }
            else m.I1--;

            float hover = _hover;
            if (m.F1 > 0f && m.F1 < 4f)
                hover = MathF.Min(hover, MathF.Max(m.F1 * Chunk.TileSize - self.Radius - 3f, self.Radius + 10f));

            m.F0 += s.Dt;
            float dx = MathF.Abs(to.X);
            bool above = to.Y > 12f;
            if (m.I0 == 0 && above && dx < _engage && m.F0 >= 0.6f) { m.I0 = 1; m.F0 = 0f; }
            else if (m.I0 == 1 && (m.F0 >= 1.0f || !above)) { m.I0 = 0; m.F0 = 0f; }

            if (m.I0 == 1)
            {
                input.MoveDir = Vector2.Normalize(new Vector2(MathF.Sign(to.X) * MathF.Min(dx / 30f, 0.6f), 1f));
                input.WantAttack = true;
                return input;
            }

            // Touch damage on the way past when close.
            if (to.Length() < 34f) input.WantAttack = true;

            var station = t.Position + new Vector2(0f, -hover);
            var d = station - pos;
            if (d.LengthSquared() > 36f) input.MoveDir = Vector2.Normalize(d);
            return input;
        }
    }
}
