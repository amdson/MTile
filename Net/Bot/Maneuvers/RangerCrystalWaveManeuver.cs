using Microsoft.Xna.Framework;

namespace MTile.Net;

// Ranger's ranged pressure: the crystal wave (World/CrystalWave.cs) fired at any opponent
// standing off at mid range, whether it is camping, kiting or just walking in.
//
// Launch mechanics are RampartCrystalWave's (lift the cursor to the first air row, wait for
// the paint ball to settle, pull level, release fast), which lays the full ~20-tile run
// where the stock CrystalWaveManeuver's early release climbs off the floor after ~6 cells.
// This class only changes the *when*: CrystalWaveManeuver (the Brawler's, untouched) fires
// only at a retreating target, since walking in beats a 1 s warning; Ranger has no other
// ranged option, so it fires at a static target too.
//
// Numbers relied on:
//   - Game-side limits: CrystalWave.CooldownSeconds = 8 s per player (set at launch), and a
//     firing charge of BuildMeters.EruptMinToFire (40) banked + 20 spare. Those are the real
//     rate limit (~1 wave / 8 s); the bot-side Cooldown (45 f) only stops re-trying a
//     failed attempt.
//   - Reach: a charged wave is ~37 crystals and runs out a little past 14 tiles on a flat
//     floor; MinRange 6 tiles is just outside slash/stab reach, where the wave's ~1 s
//     warning can still be walked into by the melee follow-up. MaxRange 14.
//   - Damage: 0.5 per crystal, capped at CrystalWave.WaveDamageCap (1.5) per victim per wave.
//   - Score 1.2 (Bulldog's melee scores ~1-1.6, which wins once in reach).
public sealed class RangerCrystalWaveManeuver : BotManeuver
{
    private readonly RampartCrystalWave _wave;
    public float MinRange      = 6f * Chunk.TileSize;
    public float MaxRange      = 14f * Chunk.TileSize;
    public float VerticalReach = 2.5f * Chunk.TileSize;
    public float Utility       = 1.2f;

    public RangerCrystalWaveManeuver()
    {
        _wave = new RampartCrystalWave(null)
        {
            MinRange = MinRange, MaxRange = MaxRange, VerticalReach = VerticalReach, Utility = Utility,
            AbortRange = 4f * Chunk.TileSize,
        };
    }

    public override int  Cooldown       => 45;   // the real gate is the 8 s wave cooldown
    public override bool OwnsLocomotion => true;
    public override MoveClaim Move => MoveClaim.Still;   // RMB aim/launch only, no movement keys; the wave is aimed from a planted body

    // Test visibility.
    public bool Fired      => _wave.Fired;
    public int  FiredFrame => _wave.FiredFrame;

    public override float Score(BotView v)
    {
        _wave.MinRange = MinRange; _wave.MaxRange = MaxRange;
        _wave.VerticalReach = VerticalReach; _wave.Utility = Utility;
        return _wave.Score(v);
    }

    // Bet (M4): 0.5 per crystal, capped 1.5 per wave; the ~1 s warning is walked out of about half
    // the time (0.72); planted ~50 f charging at 6-14 tiles (0.2); charge + lift/skim + run ~ 120 f.
    // 0.52: last of the Score 1.2 ties.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.72f, 0.2f, 120);
    }

    public override void Begin(BotView v) => _wave.Begin(v);
    public override bool Step(BotView v, ref PlayerInput input) => _wave.Step(v, ref input);
    public override void Abort(BotView v, ref PlayerInput input) => _wave.Abort(v, ref input);
}
