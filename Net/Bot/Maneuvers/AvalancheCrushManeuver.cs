using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Offense by avalanche, the damage half: aim the wave at a target standing in a roofed
// pocket — ideally against a wall — so the front lifts them into the roof, where growth
// has nowhere to push them and crushes.
//
// Why a roof, not just a wall: the MassBall has no hitbox. Its sprouts only HURT a body
// they can't shove out of the way — PhysicsWorld.CrushOverlappingSprouts fires when the
// contact solver runs out of iterations against surfaces that disagree, breaks the
// sprouts the body is wedged against and bills each one as SproutCrushDamage (0.5 HP,
// with stun). A wave that drives someone into an open-topped wall does NOT crush: the
// pile banks up the face and carries them up it like an elevator (measured: 0 damage in
// 35 of 36 wall-only runs — the same elevator AvalancheRide climbs cliffs with). Put a
// roof over that wall and the elevator runs them into it: all 27 wall-backed pocket
// runs with 3–6 open rows crushed (0.5–1.5 HP), and 12/12 under a free-standing roof at
// the chosen angle/lead (other angle/lead pairs were spottier there — the target can
// slide out sideways — hence the lower RoofUtility).
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
public sealed class AvalancheCrushManeuver : BotManeuver
{
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

    // 0.5–1.5 HP of crush is one to three slashes' worth, and above Eruption's best
    // (1.3 + 0.5 confined): in a pocket this measured strictly better.
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
        if (!v.Grounded || !v.Target.IsGrounded) return 0f;
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
