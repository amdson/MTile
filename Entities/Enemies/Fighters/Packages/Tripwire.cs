using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Tripwire: an exhibition bot by the coordinator, built after wave 4 to test whether the
// high-health contact rammers are a real meta or only the one nobody had answered. Not a
// competition entry: its author saw every report.
//
// Rammers walk straight at you and touch the moment the bodies will meet; none of them
// reads mines. So Tripwire never lets one arrive on its own terms:
//   * a charger far enough out gets a mine summoned in its path, timed to arm just before
//     it gets there — the blast hurts, knocks it back and staggers it;
//   * a charger that does arrive is dashed THROUGH, every dash frame untouchable, so its
//     touch whiffs into its long cooldown and Tripwire comes out behind it;
//   * anything slow (turrets, crawlers, a staggered rammer) gets a mine summoned on top
//     of it, and a ball when it is in range.
public sealed class Tripwire : IFighterPackage
{
    public string Name   => "Tripwire";
    public string Author => "coordinator-x";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 4.5f,
        Strength       = 1.0f,
        Density        = 1.0f,
        ReactionFrames = 4,
        GroundPower    = 260f,
        JumpImpulse    = 400f,
        EnergyReserve  = 4.5f,
        EnergyRegen    = 2.4f,
        Color          = new Color(200, 160, 60),
        Actions        = { Mine(), Dash(), Shot() },
        EngageRange    = 20f,
        AlertRange     = 2000f,
        Brain          = s => new WireBrain(s),
    };

    private static ActionSpec Mine()
    {
        var a = ActionSpec.Default(ActionKind.Mine);
        a.Damage = 4f; a.BlastRadius = 32f; a.ArmDelay = 0.4f; a.Fuse = 2.0f; a.MaxLive = 2;
        return a;
    }

    private static ActionSpec Dash()
    {
        // Long and slow, untouchable throughout: a touch stays live 0.45 s, so the dash
        // must outlast it, and end well past the body it crossed.
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed = 260f; a.Active = 0.45f; a.Intangible = 0.45f;
        return a;
    }

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.MinRange = 40f; a.MaxRange = 300f; a.Speed = 320f; a.Damage = 2.5f;
        return a;
    }

    private const int MineIdx = 0, DashIdx = 1, ShotIdx = 2;

    // Scratch: F0 = seconds since the last hop, F1 = seconds the target has been slow,
    // F2 = seconds stalled while trying to walk, I0 = frames to the next exact read,
    // I1 = 1 while the last edge probe found a drop behind us.
    private sealed class WireBrain : FighterController
    {
        private readonly float _mineCost, _dashCost, _shotCost, _mineRange, _mineArm, _mineWindup, _blast;
        private readonly float _dashSpeed, _dashTime, _shotMin, _shotMax, _shotSpeed;
        public WireBrain(FighterSpec s)
        {
            var m = s.Actions[MineIdx]; var d = s.Actions[DashIdx]; var r = s.Actions[ShotIdx];
            _mineCost = m.EnergyCost; _mineRange = m.MaxRange; _mineArm = m.ArmDelay; _blast = m.BlastRadius;
            _mineWindup = ActionSpec.Default(ActionKind.Mine).Windup;
            _dashCost = d.EnergyCost; _dashSpeed = d.Speed; _dashTime = d.Active;
            _shotCost = r.EnergyCost; _shotMin = r.MinRange; _shotMax = r.MaxRange; _shotSpeed = r.Speed;
        }

        // A charger closer than this (centre to centre, plus its closing lead) gets dashed through.
        private const float DashTrigger = 80f;
        private const float ChargeSpeed = 60f;
        // Assume anything can close this fast: a summon commits the body for its windup
        // plus recovery, and a standing rammer reaches top speed within that.
        private const float ThreatSpeed = 155f;
        private const float TouchGap    = 45f;
        private const float ThreatAccel = 170f;
        private static readonly float ShotWindup = ActionSpec.Default(ActionKind.Ranged).Windup;

        // Time to cover `dist` starting at `v0`, accelerating at ThreatAccel up to ThreatSpeed.
        private static float Arrive(float dist, float v0)
        {
            if (dist <= 0f) return 0f;
            // Moving away: it has to stop first (and drifts further off while it does).
            if (v0 < 0f) { float stop = -v0 / ThreatAccel; return stop + Arrive(dist + v0 * v0 / (2f * ThreatAccel), 0f); }
            v0 = MathF.Min(v0, ThreatSpeed);
            float tAcc = (ThreatSpeed - v0) / ThreatAccel;
            float dAcc = v0 * tAcc + 0.5f * ThreatAccel * tAcc * tAcc;
            if (dAcc >= dist) return (-v0 + MathF.Sqrt(v0 * v0 + 2f * ThreatAccel * dist)) / ThreatAccel;
            return tAcc + (dist - dAcc) / ThreatSpeed;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = (coarse.Position - self.Position).Length();

            EnemyTarget t; int age;
            if (m.I0 <= 0 && self.Energy >= s.TargetCost + 0.1f) { t = s.Target(out age); m.I0 = cd < 260f ? 4 : 6; }
            else { t = s.TargetStale(out age); m.I0--; }
            bool exact = t.Known && age <= 10;
            if (!exact) { t = coarse; age = 6; }

            var pos = t.Position + (exact ? t.Velocity * (age * s.Dt) : Vector2.Zero);
            var to = pos - self.Position;
            float d = to.Length();
            float sx = to.X >= 0f ? 1f : -1f;
            float closing = exact ? -t.Velocity.X * sx : 0f;       // its own speed toward us
            bool charger = exact && closing > ChargeSpeed;
            if (exact && t.Velocity.Length() < 40f) m.F1 += s.Dt; else if (exact) m.F1 = 0f;
            bool slow = m.F1 > 0.8f;
            bool idle = !self.ActionCommitted;
            bool grounded = MathF.Abs(self.Velocity.Y) < 1f;
            m.F0 += s.Dt;

            Span<MineView> mines = stackalloc MineView[8];
            int n = s.Mines(mines);
            int mine = 0; bool mineNearTarget = false;
            for (int i = 0; i < n; i++)
            {
                if (mines[i].Team != self.Team) continue;
                mine++;
                if (Vector2.Distance(mines[i].Position, pos) < 120f) mineNearTarget = true;
            }

            var input = new EnemyInput { AimWorld = pos, WantAttack = false };
            int? request = null;

            // Seconds before the opponent could touch us, accelerating from its current
            // closing speed: what every committed action must fit inside.
            float arrive = Arrive(d - TouchGap, closing);
            bool safeToSummon = arrive > _mineWindup + 0.3f;
            float flight = d / _shotSpeed;
            bool safeToShoot = arrive > ShotWindup + flight + 0.05f;

            // A wall or step inside the dash's reach stops the body mid-crossing: probe
            // before committing (only when a dash is about to be wanted).
            bool wallAhead(int dir)
            {
                if (self.Energy < s.ProbeCost + _dashCost) return true;
                var pr = s.Probe(dir);
                // The probe sees ProbeRangePx (66 px); anything it finds is inside the dash.
                return pr.Known && (pr.WallAhead < FighterSenses.ProbeRangePx || pr.DropAhead < FighterSenses.ProbeRangePx);
            }

            // ── 1. A charger about to arrive: dash through it. ──
            float lead = closing * (3f / 60f);                     // it closes during our 3-frame windup
            if (idle && charger && d < DashTrigger + lead && grounded && self.Energy >= _dashCost)
            {
                request = DashIdx;
                // Straight through, unless terrain would stop us inside it: then away.
                input.MoveDir.X = wallAhead((int)sx) ? -sx : sx;
            }
            // ── 2. A charger far enough out: a mine in its path, armed as it arrives. ──
            else if (idle && charger && safeToSummon && mine < 2 && !mineNearTarget && self.Energy >= _mineCost)
            {
                // It reaches a point `k` px in front of us after (d - k) / closing seconds;
                // the mine needs the summon windup plus its arming delay.
                float need = _mineWindup + _mineArm + 0.1f;
                float k = d - need * closing;
                if (k >= _blast + 26f && k <= _mineRange)
                {
                    request = MineIdx;
                    input.AimWorld = self.Position + new Vector2(sx * k, 0f);
                }
            }
            // ── 3. Something slow in reach: a mine on top of it. ──
            else if (idle && slow && !charger && safeToSummon && d <= _mineRange && d > _blast + 20f && mine < 2 && !mineNearTarget
                     && self.Energy >= _mineCost + 0.3f)
            {
                request = MineIdx;
                input.AimWorld = pos;
            }
            // ── 4. A ball when nothing better is on. ──
            else if (idle && safeToShoot && s.Visible && d >= _shotMin && d <= _shotMax
                     && self.Energy >= _shotCost + _dashCost)
            {
                request = ShotIdx;
            }

            // ── Movement: back off from a charger (unless the edge is behind us), close
            // on anything else to mine range. ──
            if (request == DashIdx) { }                           // MoveDir carries the dash direction
            else if (charger && d > DashTrigger)
            {
                bool edge = (m.I1 & 1) != 0;
                if (s.Frame % 20 == 0 && self.Energy >= s.ProbeCost + 0.5f)
                {
                    var pr = s.Probe(sx > 0f ? -1 : 1);
                    edge = pr.Known && pr.DropAhead < 60f;
                    m.I1 = edge ? 1 : 0;
                }
                input.MoveDir.X = edge ? 0f : -sx;
            }
            else if (!charger)
                input.MoveDir.X = d > 160f ? sx : d < 110f ? -sx : 0f;

            // Steps and ledges.
            // A hop drifts toward our facing (the target) and grounds the dash, so: only
            // after a real stall, and never with a charger in reach.
            if (grounded && MathF.Abs(self.Velocity.X) < 3f && input.MoveDir.X != 0f && idle) m.F2 += s.Dt; else m.F2 = 0f;
            bool chargerNear = charger && d < 220f;
            if (m.F2 > 0.25f && !chargerNear && MathF.Abs(to.X) > 40f && m.F0 > 0.4f)
            { input.Jump = true; m.F0 = 0f; m.F2 = 0f; }
            if (to.Y < -20f && MathF.Abs(to.X) < 70f && !charger && m.F0 > 0.4f) { input.Jump = true; m.F0 = 0f; }
            if (to.Y > 7f * Chunk.TileSize) { input.MoveDir.X = 0f; input.Jump = false; }

            if (request is int r) { input.WantAttack = true; input.RequestedAction = r; }
            return input;
        }
    }
}
