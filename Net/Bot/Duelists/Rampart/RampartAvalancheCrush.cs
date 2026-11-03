using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Offense by avalanche, the damage half: aim the wave at a target standing in a roofed
// pocket — ideally against a wall — so the front lifts them into the roof, where growth
// has nowhere to push them and crushes.
//
// Why a roof, not just a wall: the MassBall has no hitbox. Its sprouts only HURT a body
// they can't shove out of the way. Two channels: PhysicsWorld.CrushOverlappingSprouts
// (the contact solver runs out of iterations, breaks the wedged sprouts, 0.5 HP each), and
// the wall pin (todo #2, PhysicsBody.WavePinned → PlayerCharacter.WavePinDamage, a flat
// 1.5 HP): a growing WAVE sprout driving the body into an opposing static surface. The
// pin is NOT billed to a TerrainCarried rider (riding is off-limits this pass), and an
// AFK target in front of an open-topped wall is caught and carried up the face like an
// elevator — so an open wall alone still scores nothing for this maneuver. Under a roof
// the elevator runs the target into it: the roof crush stuns them out of the ride, and
// the stunned body is then pinned against the pocket's wall. Since walls stop the ball
// (todo #4) and sprouts grow at 0.04 s/cell, the wall-backed pocket layouts in
// BotManeuverAvalancheCrushTests crush 5/5 (0.6–2.35 HP; four of the five include a 1.5 HP pin).
// The free-standing roof (no wall) crushes 0/2 at the new sprout speed (it went red with
// the 0.04 s avalanche dial, before the wall stop, and the stop did not bring it back):
// the target slides out sideways and is carried up the roof's edge. RoofUtility is kept
// for now; the free-roof case needs a retune or a rework of the angle/lead.
//
// What it adds over EruptionManeuver: Eruption's floor-skim ridge only grows ~1–2 rows,
// so it crushes under a 3-row ceiling and misses at 5–6 (0 of 18 runs at clearance 5–6
// in BotManeuverAvalancheCrushTests' comparison); this wave climbs, so it reaches.
//
// Input plan:
//   Launch — AvalancheLauncher with the release point LeadTiles in front of the target
//            on the floor line, rising at AngleDeg, so the ball passes through the
//            target's body and the front reaches them already climbing. The release
//            point is ahead of us, so we're not in the wave's path. If the target walks
//            off before release, the charge is let go in the floor (no eruption).
//   Watch  — stand still until the target takes damage or WatchBudget runs out.
public sealed class RampartAvalancheCrush : BotManeuver
{
    private readonly RampartSense _s;
    public RampartAvalancheCrush(RampartSense s) => _s = s;

    public float MinRange      = 5f * Chunk.TileSize;
    public float MaxRange      = 14f * Chunk.TileSize;
    public float VerticalReach = 2f * Chunk.TileSize;
    public int   WallReach     = 3;      // tiles behind the target that count as "wall-backed"
    // Open rows over the target (BotTerrainSense.Clearance) that make a crush pocket.
    // ≤ 2 nothing stands there; above 6 the wave runs out of lift before the roof.
    public int   MinClearance  = 3, MaxClearance = 6;
    // Swept 15/30° × lead 3/5/6 over pocket and free-roof layouts: 30° × 3 tiles was the
    // only pair that crushed in every pocket run.
    public float AngleDeg      = 30f;
    public float LeadTiles     = 3f;     // release this far in front of the target
    public int   ChargeFrames  = 50;
    public float MinBuild      = 50f;
    public int   WatchBudget   = 90;

    // 0.6–2.35 HP of crush in a pocket (one to four-plus slashes' worth), above Eruption's
    // best (1.3 + 0.5 confined): in a pocket this measured strictly better.
    public float PocketUtility = 1.85f;  // roof + wall behind the target
    public float RoofUtility   = 1.0f;   // roof only: spottier
    // Abort the charge if the target drifts this far from where we aimed.
    public float DriftTolerance = 2f * Chunk.TileSize;
    public override int Cooldown => _failed ? 180 : 150;
    public override bool OwnsLocomotion => true;

    private enum Phase { Launch, Watch }
    private Phase _phase;
    private int   _t, _dir;
    private bool  _failed;
    private float _hp0;
    private Vector2 _aimedAt;
    private readonly AvalancheLauncher _launch = new();

    public bool Fired;
    public int  FiredFrame = -1;

    public override float Score(BotView v)
    {
        if (!v.Grounded || !_s.TargetGrounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinRange || adx > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.LineOfSight()) return 0f;
        if (v.Self.Abilities.Meters.Build < MinBuild) return 0f;
        int clear = BotTerrainSense.Clearance(v, v.TargetPos, cap: MaxClearance + 2);
        if (clear < MinClearance || clear > MaxClearance) return 0f;
        if (!PlanOrigin(v, v.Toward, out var o)) return 0f;
        if (!AvalancheLauncher.FindChargePoint(v, o, AvalancheRideManeuver.WaveDir(v.Toward, AngleDeg),
                                               _launch.RunUp, out _)) return 0f;
        return WallBehind(v, v.Toward) ? PocketUtility : RoofUtility;
    }

    public override void Begin(BotView v)
    {
        _dir = v.Toward; _t = 0; _failed = false; Fired = false; FiredFrame = -1;
        _hp0 = v.TargetHealth;
        _aimedAt = v.TargetPos;
        _phase = Phase.Launch;
        if (!PlanOrigin(v, _dir, out var o)
            || !_launch.Plan(v, o, AvalancheRideManeuver.WaveDir(_dir, AngleDeg), ChargeFrames)) _failed = true;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (_failed) return false;
        _t++;
        if (_phase == Phase.Launch)
        {
            // Target walked out of the pocket before release: bank the charge instead
            // of erupting at an empty corner.
            if (!_launch.Pulling && Vector2.Distance(v.TargetPos, _aimedAt) > DriftTolerance)
            { _launch.Cancel(ref input); return Fail(); }
            var s = _launch.Step(v, ref input);
            if (s == AvalancheLauncher.Status.Failed) return Fail();
            if (s == AvalancheLauncher.Status.Fired) { Fired = true; FiredFrame = v.Frame; _phase = Phase.Watch; _t = 0; }
            return true;
        }
        // Watch: done once the target's been crushed, or the wave's spent.
        if (v.TargetHealth < _hp0) return false;
        return _t <= WatchBudget;
    }

    private bool Fail() { _failed = true; return false; }

    // The release point: LeadTiles short of the target, in the air row above our floor.
    private bool PlanOrigin(BotView v, int dir, out Vector2 origin)
    {
        var feet = BotView.Cell(AvalancheRideManeuver.FeetPoint(v));
        var tc = BotView.Cell(v.TargetPos);
        int x = tc.X - dir * (int)MathF.Round(LeadTiles);
        origin = BotView.CellCenter(new Point(x, feet.Y));
        // Must be between us and the target, at least two tiles in front of us so the
        // wave doesn't start under our own feet, and on floor.
        if ((x - feet.X) * dir < 2) return false;
        return v.Solid(x, feet.Y + 1) && !v.Solid(x, feet.Y);
    }

    // A wall on the target's far side within WallReach tiles, at body height.
    private bool WallBehind(BotView v, int dir)
    {
        var tc = BotView.Cell(v.TargetPos);
        for (int i = 1; i <= WallReach; i++)
            if (v.Solid(tc.X + i * dir, tc.Y) && v.Solid(tc.X + i * dir, tc.Y - 1)) return true;
        return false;
    }
}
