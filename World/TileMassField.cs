using System.Collections.Generic;

namespace MTile;

// Per-cell accumulating "build mass", plus the spill cascade that turns it into
// sprouts. This is the deposition half of the mass-ball scheme, lifted out so it can
// run *live* (a held RMB dribbling mass in as the ball moves) instead of only as a
// one-shot at the end of a gesture.
//
// Why it lives on ChunkMap rather than on the action: a live painter has to carry
// partial mass across frames, which makes it sim state that must survive a rollback.
// As a sparse per-tile table next to TileDamage / TileImpactAccumulator it snapshots
// by value like they do, instead of needing the reference-type deep-copy treatment
// BlockEruptionAction's sample buffer gets.
//
// It's a bucket brigade. EVERY cell accumulates; crossing Threshold is what moves mass
// onward, one whole unit at a time:
//   Empty + supported — commit a sprout here and spill the excess.
//   Empty, no support — the request fails, so a full unit is passed to the neighbours and
//                       the remainder stays. Mass therefore *seeks* support: a stroke into
//                       open air pushes its mass toward whatever terrain is in cascade
//                       range, and dies out via MaxSpillDepth if there is none.
//   Sprouting / solid — occupied, so hand a full unit onward and keep the remainder.
//
// Note that solid cells forward mass rather than absorbing it, unlike the one-shot
// MassBallPlanner. A painter's ball spends most of its time sitting on the tile it just
// made, so discarding on solid means a held stroke places exactly one block and then
// silently eats everything after it. Forwarding is what makes a mound grow outward from
// the first tile — and it's why painting into a wall builds out from the wall face.
//
// The accumulate-then-release order is load-bearing, and getting it wrong is subtle. A
// live painter deposits ~0.2 mass per frame; splitting *that* four ways on arrival gives
// each neighbour ~0.05, which is then split again — so nothing downstream ever reaches
// Threshold and a held stroke places exactly one tile. Pooling at the occupied cell and
// forwarding a full unit keeps the quanta at 0.25 per hop, which do accumulate. (The
// one-shot MassBallPlanner never hit this because it deposits in big lumps.)
//
// Recursion is bounded twice over: MaxSpillDepth, and the fact that a hop only fires on a
// threshold crossing. Face order is fixed (N/E/S/W) so the cascade is deterministic.
public sealed class TileMassField
{
    // Mass needed to commit one tile — the mass→tiles exchange rate. Matches
    // MassBallPlanner.Threshold so a budget number means the same thing in both paths.
    public const float Threshold = 1f;

    private const float SpillShare    = 0.25f;
    private const float EpsAmount     = 0.001f;
    private const int   MaxSpillDepth = 8;

    // Slow bleed on partial mass. This is NOT a gameplay dial — it exists so a long
    // session can't grow the table without bound (which would inflate every rollback
    // snapshot). The half-life is ~1.4s, far slower than any paint stroke, so a stroke
    // never feels lossy.
    private const float Decay    = 0.5f;
    private const float PruneEps = 0.01f;

    // A cell's pooled partial mass plus its avalanche provenance. Wave is FIRST
    // CONTRIBUTION WINS: whichever wave opened the bucket owns everything it later
    // pools, commits, and spills — one deterministic owner per cell, no numeric
    // direction merging (AVALANCHE_RIDING_V2 Part 2). None = ordinary building.
    // The tag dies with the bucket (drained or pruned), so a later stroke re-tags.
    public struct MassBucket
    {
        public float    Amount;
        public EntityId Wave;
    }

    private readonly Dictionary<(int gtx, int gty), MassBucket> _mass = new();
    private readonly List<(int gtx, int gty)> _scratchPrune = new();

    public float MassAt(int gtx, int gty)
        => _mass.TryGetValue((gtx, gty), out var m) ? m.Amount : 0f;

    // Drop `amount` of mass at (gtx, gty). Returns the number of tiles committed by
    // this deposit and everything it cascaded into. `wave` tags the mass with its
    // avalanche (EntityId.None = untagged manual building).
    public int Deposit(ChunkMap chunks, int gtx, int gty, float amount, TileType type,
                       EntityId wave = default)
        => DepositAt(chunks, gtx, gty, amount, type, wave, 0);

    private int DepositAt(ChunkMap chunks, int gtx, int gty, float amount, TileType type,
                          EntityId wave, int depth)
    {
        if (amount < EpsAmount || depth > MaxSpillDepth) return 0;

        var key = (gtx, gty);
        _mass.TryGetValue(key, out var bucket);
        // First contribution owns the bucket; an empty/new bucket adopts this
        // deposit's wave. From here on `wave` IS the owner — commits and spills
        // below carry it, not the caller's tag.
        if (bucket.Amount >= EpsAmount) wave = bucket.Wave;
        float cur = bucket.Amount + amount;

        // Drain in whole units rather than handling one crossing per call. A painter
        // trickles in less than a unit per frame so this loops at most once, but a lump
        // deposit (a mass ball's per-frame leak, a block burst's injection) carries many
        // units and has to pay them all out — otherwise a 5-unit drop advances the
        // cascade by one unit and silently banks the rest.
        int committed = 0;
        while (cur >= Threshold)
        {
            var state = chunks.GetCellState(gtx, gty);
            bool free = state == TileState.Empty && !chunks.Graph.TryGet(gtx, gty, out _);
            if (free && chunks.TryRequestTile(gtx, gty, type, wave) != null)
            {
                // Committed here. Anything left over keeps flowing outward.
                cur -= Threshold;
                committed++;
                committed += Spill(chunks, gtx, gty, cur, type, wave, depth);
                cur = 0f;
                break;
            }

            // Occupied, or free but unsupported: hand one unit to the neighbours and
            // keep looping on what's left.
            cur -= Threshold;
            committed += Spill(chunks, gtx, gty, Threshold, type, wave, depth);
        }

        if (cur > 0f) _mass[key] = new MassBucket { Amount = cur, Wave = wave };
        else          _mass.Remove(key);
        return committed;
    }

    private int Spill(ChunkMap chunks, int gtx, int gty, float amount, TileType type,
                      EntityId wave, int depth)
    {
        if (amount < EpsAmount) return 0;
        float share = amount * SpillShare;
        int n = 0;
        n += DepositAt(chunks, gtx,     gty - 1, share, type, wave, depth + 1);
        n += DepositAt(chunks, gtx + 1, gty,     share, type, wave, depth + 1);
        n += DepositAt(chunks, gtx,     gty + 1, share, type, wave, depth + 1);
        n += DepositAt(chunks, gtx - 1, gty,     share, type, wave, depth + 1);
        return n;
    }

    // Exponential bleed + prune. Called once per frame from ChunkMap.TickSprouts.
    public void Tick(float dt)
    {
        if (_mass.Count == 0) return;
        float factor = System.MathF.Exp(-Decay * dt);
        _scratchPrune.Clear();
        foreach (var key in _mass.Keys) _scratchPrune.Add(key);
        foreach (var key in _scratchPrune)
        {
            var b = _mass[key];
            b.Amount *= factor;
            if (b.Amount < PruneEps) _mass.Remove(key);
            else                     _mass[key] = b;
        }
    }

    // Snapshot/restore (roadmap goal 6). Dict copy = deep copy (value-typed entries).
    // Live entries, without Capture()'s copy — Simulation.Checksum() folds these into
    // the terrain fingerprint every frame and must not allocate on the sim hot path.
    public IEnumerable<KeyValuePair<(int gtx, int gty), MassBucket>> Entries => _mass;

    public Dictionary<(int gtx, int gty), MassBucket> Capture() => new(_mass);

    public void Restore(Dictionary<(int gtx, int gty), MassBucket> s)
    {
        _mass.Clear();
        if (s == null) return;
        foreach (var kv in s) _mass[kv.Key] = kv.Value;
    }
}
