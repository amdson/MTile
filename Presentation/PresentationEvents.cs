using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The presentation-events seam: the render shell's edge-triggered channel out of the
// sim. See Plans/AUDIO_PLAN.md.
//
// The problem it solves: sim events fire from inside Step, and a rollback re-runs Step
// over every rolled-back frame (Net/RollbackSession.cs). A naive subscriber therefore
// sees the same tile break two, three, or ten times and sprays that many particle
// bursts. Audio would have the same bug, louder.
//
// The fix is identity, not suppression. Every event carries a SoundId-style key that is
// a pure function of sim state plus the sim frame it happened on. A replay re-emits the
// identical (frame, id) pair, so the log drops it. Nothing under Simulation/ or Net/
// needs to know a rollback is in progress — or that presentation exists at all.
//
// Level-triggered presentation (a wall-scrape loop, a dust plume that persists while
// sliding) does NOT belong here: it is re-derived from current state each frame and is
// idempotent for free.

public enum PresentationKind : byte
{
    TileBreak,
    TilePlace,
    PlayerRespawn,
    MassLand,      // a thrown/lobbed mass ball landed and erupted (A/B = entity id index/generation; payload = TileType | blocks << 8)
}

// Identity of a presentation event. Must be derivable from sim state alone — never a
// counter, never allocation order. A and B are kind-specific: tile coords for
// TileBreak, player index for PlayerRespawn.
public readonly struct PresentationId : IEquatable<PresentationId>
{
    public readonly PresentationKind Kind;
    public readonly int A;
    public readonly int B;

    public PresentationId(PresentationKind kind, int a = 0, int b = 0) { Kind = kind; A = a; B = b; }

    public bool Equals(PresentationId o) => Kind == o.Kind && A == o.A && B == o.B;
    public override bool Equals(object o) => o is PresentationId p && Equals(p);
    public override int GetHashCode() => ((int)Kind * 397 ^ A) * 397 ^ B;
}

public readonly struct PresentationEvent
{
    public readonly int             SimFrame;
    public readonly PresentationId  Id;
    public readonly Vector2         Position;
    public readonly int             Payload;   // kind-specific (TileBreak: TileType)

    public PresentationEvent(int simFrame, PresentationId id, Vector2 pos, int payload)
    { SimFrame = simFrame; Id = id; Position = pos; Payload = payload; }
}

// Collects sim-raised events during Step and hands the render shell the ones it has not
// already presented. Deduping happens at Emit, so Pending only ever holds fresh events.
public sealed class PresentationEventLog
{
    // Must exceed the rollback window (RollbackSession keeps 60 frames) or a
    // long rollback could re-present its oldest frames. Cheap enough to overshoot.
    private const int DedupWindowFrames = 120;

    private readonly List<PresentationEvent>         _pending = new();
    private readonly HashSet<(int, PresentationId)>  _seen    = new();
    private readonly Queue<(int, PresentationId)>    _order   = new();

    public IReadOnlyList<PresentationEvent> Pending => _pending;

    // Called from inside Step (via the sim's cosmetic events). Safe to call repeatedly
    // for the same (frame, id) — every call after the first is a no-op.
    public void Emit(int simFrame, PresentationId id, Vector2 pos, int payload = 0)
    {
        var key = (simFrame, id);
        if (!_seen.Add(key)) return;
        _order.Enqueue(key);
        _pending.Add(new PresentationEvent(simFrame, id, pos, payload));

        while (_order.Count > 0 && simFrame - _order.Peek().Item1 > DedupWindowFrames)
            _seen.Remove(_order.Dequeue());
    }

    // Drain once per RENDERED frame, after the session has finished stepping — not once
    // per Step. A rollback runs many Steps inside one rendered frame.
    public void Clear() => _pending.Clear();

    // Stage reload builds a fresh sim whose frame counter restarts at 0; without this
    // the stale keys would swallow the new timeline's first events.
    public void Reset()
    {
        _pending.Clear();
        _seen.Clear();
        _order.Clear();
    }
}
