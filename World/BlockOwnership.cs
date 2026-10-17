using System;
using System.Collections.Generic;

namespace MTile;

// Sparse per-cell "who built this, and when" (Plans/BLOCK_OWNERSHIP_SPIKES.md §1). Any
// cell a player creates — paint, place, burst, eruption, a thrown clod's splat — is
// stamped with that player and the terrain frame it became visible terrain. Pre-existing
// terrain, enemy builds and editor writes are unowned. Breaking or emptying a cell
// clears its claim (ChunkMap does that on every path that empties a cell), and
// re-placing over a cell re-stamps it.
//
// The claim fades: strength s = 0.5^(age / HalfLife), computed on demand from the
// stamped frame, so nothing ticks per entry. Below MinStrength (~3.3 min) the claim is
// treated as gone, and Prune drops such entries on a fixed cadence so the table — and
// with it every rollback snapshot — stays small.
//
// Sim state: the block grab reads it (PullPointEntity scales an owned cell's glue by
// 1 - s for its owner), so it snapshots and restores with the other sparse per-tile
// tables and folds into Simulation.Checksum.
public sealed class BlockOwnership
{
    // Owner ids. 0 is "nobody"; players are 1-based so default(Claim) reads as unowned.
    // In netplay player 0 (host) is Faction.Player1 and the joiner Faction.Player2.
    public const byte None    = 0;
    public const byte Player1 = 1;
    public const byte Player2 = 2;

    // 60 s half-life at the fixed 60 fps step (Simulation.FixedDt).
    public const int   HalfLifeFrames = 60 * 60;
    public const float MinStrength    = 0.1f;
    // Frames between prune sweeps. Fixed, so peers prune on the same frames.
    public const int   PruneCadenceFrames = 60;

    public struct Claim
    {
        public byte Owner;
        public int  PlacedFrame;
    }

    private readonly Dictionary<(int gtx, int gty), Claim> _claims = new();
    private readonly List<(int gtx, int gty)> _scratch = new();

    // The owner id a placing faction stamps. Only players own blocks.
    public static byte OwnerFor(Faction f) => f switch
    {
        Faction.Player1 => Player1,
        Faction.Player2 => Player2,
        _               => None,
    };

    // s = 0.5^(age / HalfLife). Pure; age clamps at 0 so a stamp "from the future"
    // (never happens in-sim) reads as fresh rather than > 1.
    public static float StrengthAt(int ageFrames)
        => MathF.Pow(0.5f, Math.Max(0, ageFrames) / (float)HalfLifeFrames);

    public void Stamp(int gtx, int gty, byte owner, int frame)
    {
        if (owner == None) { _claims.Remove((gtx, gty)); return; }
        _claims[(gtx, gty)] = new Claim { Owner = owner, PlacedFrame = frame };
    }

    public void Clear(int gtx, int gty) => _claims.Remove((gtx, gty));

    // Owner and live strength of a cell's claim; false (owner None, s 0) when the cell
    // is unowned or its claim has faded below MinStrength.
    public bool TryGet(int gtx, int gty, int now, out byte owner, out float strength)
    {
        if (_claims.TryGetValue((gtx, gty), out var c))
        {
            float s = StrengthAt(now - c.PlacedFrame);
            if (s >= MinStrength) { owner = c.Owner; strength = s; return true; }
        }
        owner = None; strength = 0f;
        return false;
    }

    // Strength of `owner`'s claim on the cell — 0 when unowned, faded, or owned by
    // someone else. This is the grab's question: "is this mine, and how much?"
    public float StrengthFor(int gtx, int gty, byte owner, int now)
        => owner != None && TryGet(gtx, gty, now, out var o, out var s) && o == owner ? s : 0f;

    public int Count => _claims.Count;

    // Drop faded claims. Removal is decided by content alone, so the surviving set is
    // identical on every peer regardless of dictionary bucket order.
    public void Prune(int now)
    {
        if (_claims.Count == 0) return;
        _scratch.Clear();
        foreach (var kv in _claims)
            if (StrengthAt(now - kv.Value.PlacedFrame) < MinStrength) _scratch.Add(kv.Key);
        foreach (var k in _scratch) _claims.Remove(k);
    }

    // Live entries for the checksum fold and the render overlay (no copy).
    public IEnumerable<KeyValuePair<(int gtx, int gty), Claim>> Entries => _claims;

    // Snapshot/restore. Value-typed entries, so a dictionary copy is a deep copy.
    public Dictionary<(int gtx, int gty), Claim> Capture() => new(_claims);

    public void Restore(Dictionary<(int gtx, int gty), Claim> src)
    {
        _claims.Clear();
        if (src == null) return;
        foreach (var kv in src) _claims[kv.Key] = kv.Value;
    }
}
