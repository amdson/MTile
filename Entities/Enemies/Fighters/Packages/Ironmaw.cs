using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Ironmaw: a fast, durable closer built around the one strike nobody can read. Every
// windup is fixed and every brain can see a tell, but Contact has no windup and no tell,
// never roots the body, and a landed hit cancels whatever the victim was winding up and
// staggers it. So the whole kit is legs, health and one heavy touch: close, overlap, and
// keep overlapping — a shooter inside its own MinRange cannot shoot, and every swing it
// starts is cancelled by the next tick. The jump is spent on dodges and terrain only: a
// ball is aimed at the true position on the fire frame, so the brain leaves the ground on
// that frame (never early, or the shot is aimed at it mid-air); a rail line is frozen at
// windup start, so it leaves late in the windup. Ledges and walls get a hop when the legs
// stall; a rationed probe keeps it from following a kiter off the edge of the world.
public sealed class Ironmaw : IFighterPackage
{
    public string Name   => "Ironmaw";
    public string Author => "fable-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 6f,
        Strength       = 1.4f,
        Density        = 1.7f,
        ReactionFrames = 3,
        GroundPower    = 355f,
        JumpImpulse    = 450f,
        EnergyReserve  = 3f,
        EnergyRegen    = 0.6f,
        TargetMemory   = true,
        Sides          = 7,
        Color          = new Color(170, 60, 40),
        Actions        = { Touch() },
        EngageRange    = 12f,
        AlertRange     = 900f,
        Brain          = s => new IronmawBrain(s),
    };

    // The touch. Low knockback on purpose: a victim that stays inside the box eats the
    // next tick. Trigger band tight to the overlap distance so a tick is not spent on a
    // target the box cannot reach during its 0.1 s window.
    private static ActionSpec Touch()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage = 2.4f; a.HalfWidth = 14f; a.MaxRange = 28f;
        a.Knockback = new Vector2(60f, -40f);
        return a;
    }

    // Scratch: I0 = mode (0 hunt, 1 back-step), I1 = dodge flags (1 jumped for the current
    // ball, 2 for the current bolt), F0 = seconds in mode, F1 = seconds stalled against
    // terrain, F2 = frames until the next edge probe, F3 = last probe: signed distance to
    // the drop ahead (sign = probe direction; 0 = none bought).
    private sealed class IronmawBrain : FighterController
    {
        private const float RangedWindup = 0.60f;   // fixed per kind (FighterCompiler)
        private const float EdgeStop     = 6f;      // a drop in the very next cell
        private readonly float _engage, _touchMax;

        public IronmawBrain(FighterSpec s)
        {
            _engage   = s.EngageRange;
            _touchMax = s.Actions[0].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self   = s.Self;
            var coarse = s.TargetCoarse();
            var input  = new EnemyInput { AimWorld = self.Position + new Vector2(self.Facing, 0f) };
            if (!coarse.Known) return input;

            float cd = (coarse.Position - self.Position).Length();
            // Ration the exact read: every third frame in the fight, every other while a
            // shot is being wound up (the dodge needs the fire frame), sparse when far or
            // blind (nothing to read behind a wall).
            EnemyTarget prev = s.TargetStale(out int prevAge);
            bool shotLive = prev.Known && prevAge <= 6 && prev.TellProgress >= 0f && prev.TellProgress < 1f
                            && prev.Tell is ActionKind.Ranged or ActionKind.RailShot;
            int cadence = !s.Visible || cd > 400f ? 6 : shotLive ? 2 : 3;
            EnemyTarget t; int age;
            if (s.Frame % cadence == 0 && self.Energy - s.TargetCost >= 0f) t = s.Target(out age);
            else t = s.TargetStale(out age);
            bool exact = t.Known && age <= 8;
            if (!exact) { t = coarse; age = 0; }

            var pos    = t.Position + t.Velocity * (age * s.Dt);
            var to     = pos - self.Position;
            float dist = to.Length();
            float dx   = to.X >= 0f ? 1f : -1f;
            input.AimWorld = pos;

            bool grounded  = MathF.Abs(self.Velocity.Y) < 30f;
            bool staggered = self.Staggered;

            // ── Tells ──────────────────────────────────────────────────────────
            bool live   = exact && s.Visible && t.TellProgress >= 0f;
            bool ball   = live && t.Tell == ActionKind.Ranged;
            bool bolt   = live && t.Tell == ActionKind.RailShot;
            bool swing  = live && t.TellProgress < 1f && t.Tell is ActionKind.Melee or ActionKind.Lash;
            if (!ball) m.I1 &= ~1;
            if (!bolt) m.I1 &= ~2;

            bool wantJump = false;
            bool canJump  = grounded && !staggered;
            // Ball: aimed at where we ARE on the fire frame, so leave the ground on that
            // frame — seen `age` frames late — and only with enough flight left to clear.
            if (ball && (m.I1 & 1) == 0 && dist > 95f && canJump)
            {
                float remain = (1f - t.TellProgress) * RangedWindup - age * s.Dt;
                if (remain <= 0.034f) { wantJump = true; m.I1 |= 1; }
            }
            // Bolt: the line was frozen at windup start; be off it when it fires.
            if (bolt && (m.I1 & 2) == 0 && dist > 40f && canJump && t.TellProgress >= 0.82f)
            { wantJump = true; m.I1 |= 2; }

            // ── Back-step ──────────────────────────────────────────────────────
            // A swing we can see coming while the touch is cooling: step out of it. With
            // the touch ready we press in instead — it lands first and cancels the swing.
            bool touchCooling = self.CurrentAction == ActionKind.Contact;
            if (m.I0 == 1)
            {
                m.F0 += s.Dt;
                if (m.F0 < 0.25f)
                {
                    input.MoveDir.X = -dx;
                    input.AimWorld  = self.Position - to;
                    return input;
                }
                m.I0 = 0; m.F0 = 0f;
            }
            else if (swing && touchCooling && t.TellProgress >= 0.35f && dist < 50f && !staggered)
            {
                m.I0 = 1; m.F0 = 0f;
                input.MoveDir.X = -dx;
                input.AimWorld  = self.Position - to;
                return input;
            }

            // ── Close ──────────────────────────────────────────────────────────
            // Do not chase a target that has fallen well below the ground we stand on —
            // that is the edge of the world, and following it is a draw at best.
            bool fallen   = to.Y > 60f && grounded;
            bool wantMove = dist > _engage && !fallen;

            // Edge guard: a probe ahead every 20 frames while moving; never step into a
            // drop in the next cell (the arena floor simply ends).
            if (wantMove)
            {
                if (--m.F2 <= 0f)
                {
                    if (self.Energy - s.ProbeCost >= 0.3f)
                    {
                        var p = s.Probe(dx >= 0f ? 1 : -1);
                        if (p.Known) m.F3 = dx * p.DropAhead;
                    }
                    m.F2 = 20f;
                }
                bool edge = m.F3 != 0f && MathF.Sign(m.F3) == MathF.Sign(dx) && MathF.Abs(m.F3) <= EdgeStop;
                if (edge && grounded) wantMove = false;
            }
            if (wantMove) input.MoveDir.X = dx;

            // Stalled against a step or a wall (not a knockback): hop. Target above us on
            // a ledge: hop.
            bool stalled = wantMove && grounded && !staggered && MathF.Abs(self.Velocity.X) < 10f && s.Frame > 15;
            m.F1 = stalled ? m.F1 + s.Dt : 0f;
            bool climb = to.Y < -14f && MathF.Abs(to.X) < 70f && canJump;
            if (m.F1 > 0.22f || climb) { wantJump = true; m.F1 = 0f; }
            input.Jump = wantJump;

            // ── Strike ─────────────────────────────────────────────────────────
            input.WantAttack      = true;
            input.RequestedAction = dist <= _touchMax ? 0 : null;
            return input;
        }
    }
}
