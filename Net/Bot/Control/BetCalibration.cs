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
    // 2026-11-04, vs Rampart / Citadel / Riposte / Bulldog / Brawler on the three league arenas.
    public static readonly IReadOnlyDictionary<string, (float Dealt, float Taken)> Field = new Dictionary<string, (float, float)>
    {
        ["AntiAirJumpSlashManeuver"]  = (0.180f, 0.119f),   // n=2231
        ["AntiAirSidestepManeuver"]   = (0.175f, 0.080f),   // n=1183
        ["AntiAirStabManeuver"]       = (0.122f, 0.046f),   // n=343
        ["BulldogBreaker"]            = (0.035f, 0.114f),   // n=1133
        ["BulldogCrossUp"]            = (0.085f, 0.233f),   // n=387
        ["BulldogGrab"]               = (0.062f, 0.143f),   // n=128
        ["BulldogGuard"]              = (0.030f, 0.090f),   // n=350
        ["BulldogLowPoke"]            = (0.242f, 0.099f),   // n=265
        ["BulldogPunish"]             = (0.192f, 0.164f),   // n=656
        ["BulldogStab"]               = (0.138f, 0.160f),   // n=237
        ["BulldogString"]             = (0.594f, 0.202f),   // n=2276
        ["ChaseStance"]               = (0.197f, 0.119f),   // n=9765
        ["CornerEscapeManeuver"]      = (0.093f, 0.028f),   // n=73
        ["CounterSlashManeuver"]      = (0.096f, 0.167f),   // n=89
        ["DashManeuver"]              = (0.130f, 0.082f),   // n=1348
        ["DownAirManeuver"]           = (0.083f, 0.157f),   // n=1881
        ["GuardManeuver"]             = (0.023f, 0.077f),   // n=554
        ["Idle"]                      = (0.087f, 0.086f),   // n=618
        ["RangerBlockThrowManeuver"]  = (0.329f, 0.057f),   // n=847
        ["RangerCrystalWaveManeuver"] = (0.009f, 0.293f),   // n=339
        ["StepBackManeuver"]          = (0.146f, 0.062f),   // n=391
        ["StepOutStance"]             = (0.001f, 0.085f),   // n=530
        ["WallUpManeuver"]            = (0.136f, 0.136f),   // n=50
        ["WhiffPunishManeuver"]       = (0.127f, 0.200f),   // n=766
    };
}
