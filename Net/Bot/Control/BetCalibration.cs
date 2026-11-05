using System.Collections.Generic;

namespace MTile.Net;

// Data-calibrated bet priors: what each arm REALISED, mean HP dealt and taken per record,
// under the hand-written policy against the duelist field (shadow ledger, 180 bots x 60 s;
// MTile.Tests/Sim/ZzzLearningEffect.CalibrationDump regenerates it). Used in place of the
// authored magnitudes (BotController.Calibration) so the online learner starts from the
// truth about each arm instead of paying, per bucket and per bout, to discover that a
// hand-authored 0.6 HP grab really lands 0.06. The gating and horizon of each bet stay
// authored; only the (dealt, taken) prior comes from here. Values are per record (an
// option's return until the next decision in its category), not per hit.
public static class BetCalibration
{
    // 2026-11-05 (after the combat frame-data pass: damage spread, safe-on-hit heavies,
    // launches, true strings, 12f opener buffer, air 42, fall 0.7; the string-read guard),
    // vs Rampart / Citadel / Riposte / Bulldog / Brawler on the three league arenas.
    // Arms the hand policy never took in this dump keep their 2026-11-04 row.
    public static readonly IReadOnlyDictionary<string, (float Dealt, float Taken)> Field = new Dictionary<string, (float, float)>
    {
        ["AntiAirJumpSlashManeuver"]    = (0.305f, 0.162f),   // n=2154
        ["AntiAirSidestepManeuver"]     = (0.175f, 0.080f),   // n=1183, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["AntiAirStabManeuver"]         = (0.275f, 0.114f),   // n=327
        ["BreachManeuver"]              = (0.100f, 0.027f),   // n=40
        ["BulldogBreaker"]              = (0.150f, 0.157f),   // n=278
        ["BulldogCrossUp"]              = (0.085f, 0.233f),   // n=387, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["BulldogGrab"]                 = (0.062f, 0.143f),   // n=128, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["BulldogGuard"]                = (0.301f, 0.174f),   // n=48
        ["BulldogLowPoke"]              = (0.193f, 0.052f),   // n=87
        ["BulldogPunish"]               = (0.257f, 0.287f),   // n=793
        ["BulldogStab"]                 = (0.270f, 0.143f),   // n=90
        ["BulldogString"]               = (0.539f, 0.152f),   // n=3919
        ["ChaseStance"]                 = (0.235f, 0.113f),   // n=9669
        ["CornerEscapeManeuver"]        = (0.093f, 0.028f),   // n=73, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["CounterSlashManeuver"]        = (0.096f, 0.167f),   // n=89, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["DashManeuver"]                = (0.219f, 0.146f),   // n=1304
        ["DownAirManeuver"]             = (0.250f, 0.202f),   // n=1230
        ["GuardManeuver"]               = (0.225f, 0.234f),   // n=962
        ["Idle"]                        = (0.201f, 0.268f),   // n=1201
        ["PressStance"]                 = (0.233f, 0.139f),   // n=1502
        ["RangerBlockThrowManeuver"]    = (0.363f, 0.103f),   // n=790
        ["RangerCrystalWaveManeuver"]   = (0.009f, 0.293f),   // n=339, carried from 2026-11-04 (not recorded in the 2026-11-05 dump)
        ["StepBackManeuver"]            = (0.302f, 0.094f),   // n=815
        ["StepOutStance"]               = (0.096f, 0.128f),   // n=87
        ["WallUpManeuver"]              = (0.234f, 0.278f),   // n=43
        ["WhiffPunishManeuver"]         = (0.133f, 0.240f),   // n=567
    };
}
