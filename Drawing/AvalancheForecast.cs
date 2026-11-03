using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Render-only look-ahead for avalanches in flight: the cells a live MassBall's wave
// will end up claiming, so ChunkRenderer can shade the whole fill zone the moment the
// ball leaves the hand instead of revealing it one cascade step at a time.
//
// It is the real sim, not a model of it: Snapshot, step forward with every player's
// input frozen at its current value (held buttons stay held, so no new press or release
// edge starts anything), collect every cell tagged with a tracked wave, Restore. The
// rollback snapshot is exact, so nothing about the live match changes — but the steps
// DO raise the sim's cosmetic events, so the caller's PresentationEventLog is muted for
// the duration (an un-muted forecast would present future events now and then dedupe
// the real ones away when they happen).
//
// Offline only: a RollbackSession owns its sim's timeline, so Game1 doesn't run this
// while networked.
//
// Cost: it is HorizonFrames-ish real sim steps in one rendered frame (~12–15 ms on a duel
// stage, Release), so it runs once per launch and again only when reality leaves the
// forecast — a cell gets claimed that wasn't predicted (someone built in the path, a
// player walked into the cascade) — at most once per RefreshFrames.
public sealed class AvalancheForecast
{
    // Step cap. The ball's own backstop is 2.5 s (MassBall.LifeSeconds); mass normally
    // runs out well before that, and TailFrames covers the cascade after it dies.
    public const int HorizonFrames = 170;
    public const int TailFrames    = 30;
    // Minimum gap between divergence re-forecasts (a new ball forecasts at once).
    public const int RefreshFrames = 12;
    // Keep shading this long after the last ball died — the cascade and the schedule
    // lag are still promoting cells — then drop whatever never filled.
    public const int LingerFrames  = 90;

    public readonly struct Claim
    {
        public readonly TileType Type;
        public readonly bool     Crystal;
        public Claim(TileType type, bool crystal) { Type = type; Crystal = crystal; }
    }

    // The forecast fill zone: cell → what will fill it.
    public IReadOnlyDictionary<(int gtx, int gty), Claim> Cells => _cells;

    private readonly Dictionary<(int gtx, int gty), Claim> _cells = new();
    private readonly Dictionary<EntityId, Claim> _waves = new();
    private readonly HashSet<EntityId> _known = new();
    private int _lastForecastFrame = int.MinValue / 2;
    private int _lastBallFrame     = int.MinValue / 2;

    // Once per rendered frame, after the sim stepped. `p1` is the second player's last
    // input (null when there is no second player).
    public void Update(Simulation sim, PlayerInput p0, PlayerInput? p1, PresentationEventLog log)
    {
        _waves.Clear();
        bool fresh = false;
        foreach (var e in sim.Entities)
        {
            if (e is not MassBall b || b.Health <= 0f) continue;
            _waves[b.Id] = new Claim(b.BlockType, b.Crystal);
            if (_known.Add(b.Id)) fresh = true;
        }

        int now = sim.Frame;
        if (_waves.Count == 0)
        {
            if (now - _lastBallFrame > LingerFrames) { _cells.Clear(); _known.Clear(); }
            return;
        }
        _lastBallFrame = now;
        if (!fresh && (now - _lastForecastFrame < RefreshFrames || !Diverged(sim.Chunks))) return;

        _lastForecastFrame = now;
        _cells.Clear();
        bool wasMuted = log != null && log.Muted;
        if (log != null) log.Muted = true;
        try { Predict(sim, _waves, p0, p1, _cells); }
        finally { if (log != null) log.Muted = wasMuted; }
    }

    // A real claim the forecast didn't predict.
    private bool Diverged(ChunkMap chunks)
    {
        foreach (var (cell, wave) in chunks.Waves.CellEntries)
            if (_waves.ContainsKey(wave) && !_cells.ContainsKey(cell)) return true;
        foreach (var (cell, c) in chunks.Crystals.Entries)
            if (_waves.ContainsKey(c.Wave) && !_cells.ContainsKey(cell)) return true;
        return false;
    }

    // The look-ahead itself, separate so tests can check it against what really happens.
    public static void Predict(Simulation sim, IReadOnlyDictionary<EntityId, Claim> waves,
                               PlayerInput p0, PlayerInput? p1,
                               Dictionary<(int gtx, int gty), Claim> into)
    {
        var snap = sim.Snapshot();
        try
        {
            Collect(sim.Chunks, waves, into);
            int deadAt = -1;
            for (int f = 0; f < HorizonFrames; f++)
            {
                if (p1.HasValue && sim.SecondaryPlayers.Count > 0) sim.Step(p0, p1.Value);
                else                                               sim.Step(p0);
                Collect(sim.Chunks, waves, into);

                if (deadAt < 0 && !AnyAlive(sim, waves)) deadAt = f;
                if (deadAt >= 0 && f - deadAt >= TailFrames) break;
            }
        }
        finally { sim.Restore(snap); }
    }

    // Union over the whole look-ahead, not just its last frame: crystals vanish once
    // they've grown, and a cell can be claimed and then broken again mid-cascade.
    private static void Collect(ChunkMap chunks, IReadOnlyDictionary<EntityId, Claim> waves,
                                Dictionary<(int gtx, int gty), Claim> into)
    {
        foreach (var (cell, wave) in chunks.Waves.CellEntries)
            if (waves.TryGetValue(wave, out var claim)) into[cell] = claim;
        foreach (var (cell, c) in chunks.Crystals.Entries)
            if (waves.TryGetValue(c.Wave, out var claim)) into[cell] = claim;
    }

    private static bool AnyAlive(Simulation sim, IReadOnlyDictionary<EntityId, Claim> waves)
    {
        foreach (var e in sim.Entities)
            if (e is MassBall b && b.Health > 0f && waves.ContainsKey(b.Id)) return true;
        return false;
    }
}
