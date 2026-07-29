using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Physical actuator identity for a solver channel (ChannelDef.Id). The channel
// INDEX is a per-builder layout detail (BuildFold and BuildManeuver assign the
// same actuators to different slots); this enum is the stable name bookkeeping
// and tuning tools key on.
public enum CorrectionChannel : byte
{
    None,           // unstamped (hand-built test problems)
    LegServo,       // strong up-only push while a floor is in leg reach
    Drive,          // ground traction along held intent
    CornerAssist,   // weak lift near obstacle features (lips)
    Redirect,       // the Thales plant-and-deflect disc (velocity update)
    Tuck,           // down-only pull onto the floor (ducks, drop-to-hover)
    AirLateral,     // flight steering along intent (maneuver: forward, fade-capped)
    AirLateralBack, // maneuver-only: unrestricted damping back toward the line
    AirVertical,    // tiny two-sided vertical nudge in flight
}

// Per-step bookkeeping of what the corrector solve actually exerted — the
// applied tick-0 output broken down two ways:
//   BY CHANNEL: the force each actuator contributed (what the legs pushed,
//     what the redirect deflected), for tuning and for effects keyed on effort.
//   BY CONTACT: each clearance row's share of the applied correction plus the
//     source tile it came from — by Newton's third law the terrain pushed the
//     body with ContactForce[j], so the body pushed cell (CellX, CellY) with
//     −ContactForce[j]: the input block-breaking physics needs.
//
// Conventions: "force" is an acceleration (PhysicsBody has no mass, the
// codebase-wide rule); a VelocityUpdate channel's δv is reported as δv/dt so
// every entry shares units. At most one solve applies per player per step
// (ambient OR maneuver — never both), so a single record per scratch suffices.
//
// Lifecycle: cleared in CorrectorScratch.BeginFrame, written by the apply site
// right after its final solve. Pure per-frame derived data — never snapshot
// state; consumers must read it in the same step (or from render, which may
// not feed back into the sim).
public sealed class CorrectorLedger
{
    public struct ChannelEntry
    {
        public CorrectionChannel Channel;
        public Vector2 Force;   // applied tick-0 force (accel) this step
    }

    public struct ContactEntry
    {
        public int     CellX, CellY;  // source tile (gtx/gty) — valid iff HasCell
        public bool    HasCell;       // false: soft reference row (hover/progress), no tile
        public Vector2 Normal;        // the row's outward clearance normal
        public Vector2 Force;         // force ON THE BODY; the tile felt −Force
        public Vector2 Pos;           // body center at the row's predicted tick
    }

    public readonly ChannelEntry[] Channels = new ChannelEntry[CorrectionSolver.MaxChannels];
    public int ChannelCount;

    public readonly ContactEntry[] Contacts = new ContactEntry[ClearanceConstraintBuilder.MaxEvents];
    public int ContactCount;

    // Sum over channels — equals the correction the apply site fed into
    // Body.AppliedForce (TickDv[0]/dt).
    public Vector2 TotalForce;

    public void Clear()
    {
        ChannelCount = 0;
        ContactCount = 0;
        TotalForce   = Vector2.Zero;
    }

    // Record the APPLIED solve: p/z still hold the final pass, rowPush the
    // per-row attribution (uniform δv units — see CorrectionProblem.RowPush),
    // samples the coast the rows were built against.
    public void Record(CorrectionProblem p, Vector2[] z, Vector2[] rowPush,
                       CoastSample[] samples, float dt)
    {
        Clear();
        if (dt <= 0f) return;

        int H = p.H;
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var ch = p.Channels[c];
            var force = ch.Lever == LeverKind.VelocityUpdate ? z[c * H] / dt : z[c * H];
            Channels[ChannelCount++] = new ChannelEntry { Channel = ch.Id, Force = force };
            TotalForce += force;
        }

        for (int j = 0; j < p.RowCount; j++)
        {
            if (rowPush[j] == Vector2.Zero) continue;
            ref readonly var row = ref p.Rows[j];
            Contacts[ContactCount++] = new ContactEntry
            {
                CellX   = row.CellX,
                CellY   = row.CellY,
                HasCell = row.HasCell,
                Normal  = row.Normal,
                Force   = rowPush[j] / dt,
                Pos     = samples[row.Tick].Pos,
            };
        }
    }
}
