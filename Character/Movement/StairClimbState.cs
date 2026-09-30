using System;
using Microsoft.Xna.Framework;

namespace MTile;

// STAIR CLIMB — a regular flight of one-riser steps, climbed as ONE fold state instead of a
// chain of vaults (2026-09-21).
//
// Why: on a 45° staircase the vault family re-entered every riser — a hop sized for the
// tread it was aimed at, delivered 14 frames later when the body was already over the
// NEXT tread — so the body rode 8–12 px above the corner line (bottom vertex on the lips)
// in a 190 px/s vertical sawtooth; the animation's stairs clip assumes ~19 px, and its
// phase fought the hops. Standing's fold already plans over the C-obstacle at the hover
// offset; on a staircase that surface IS the corner line. What kept Standing off the
// stairs was arbitration (the vaults outbid it at every riser) and FoldRiseCost pricing
// each riser against progress. This state is Standing's drive with the climb priced free
// (FoldProfile.Stairs) in the climb band, registered ahead of the vaults. Measured on the
// 10-step fixture, fold alone: 18 px above the corner line, a steady ~70 px/s glide up
// the 45° line at walk speed along it, no backslides.
//
// Scope: a flight is at least two consecutive floor rises, each one riser (the mantle
// band), at the same column spacing (the tread width, up to StairChecker.MaxTreadColumns). Anything
// irregular — a lone step, a two-high riser, a landing mid-flight — is not a flight and
// stays with the climb family. Descending is not this state's (a run of drops falls).
public class StairClimbState : MovementState
{
    private readonly int _dir;
    public StairClimbState(int dir) => _dir = dir;
    public int Dir => _dir;

    public override int ActivePriority  => MovementPriorities.StairClimbActive;
    public override int PassivePriority => MovementPriorities.StairClimbPassive;
    // Same capability gate as the climb family: a stunned body sliding into a flight must
    // not start climbing it (Stunned's Active 25 would otherwise lose to this 29).
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;
    public override AnimTag AnimationTag => AnimTag.Stairs;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cfg = MovementConfig.Current;
        if (!cfg.StairClimbEnabled || !cfg.CorrectorClimbEnabled) return false;
        if (ctx.Intent.HeldHorizontal != _dir) return false;
        if (!StandingState.IsStandingGround(ctx)) return false;
        // Bid no later than the vault would (its trigger distance), plus a tread so the
        // flight is claimed before the vault's own bid at the first riser.
        return FlightAheadWithinReach(ctx);
    }

    // Continuation asks the tiles, not the generic ground probe or the corridor: the stair
    // probe (StairChecker) reads the tread under the body and whether the column ahead
    // carries the next riser. That makes the state honest about terrain edited under it —
    // a deleted tread fails the probe (the climb drops), a deleted riser ends the flight
    // (Standing takes the landing). The generic ground probe's window is the rest band and
    // loses a body riding the corner line for a frame at every riser; the corridor scan has
    // no columns while the face is flush against a riser.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (ctx.Intent.CurrentHorizontal != _dir) return false;          // release cancels
        if (!StairChecker.TryFind(ctx.Body, ctx.Chunks, _dir, out var s)) return false;
        if (s.NextRiser) return true;
        // On the runway (not yet on a tread) the corridor's flight ahead carries the state
        // from its trigger distance up to the first riser. On a tread, only the next riser
        // does — a gap ahead ends the climb.
        return !s.OnFlight && FlightAheadWithinReach(ctx);
    }

    private bool FlightAheadWithinReach(EnvironmentContext ctx)
    {
        var corridor = ctx.GetCorridor(_dir);
        if (!TryFindFlight(corridor, out var first, out _)) return false;
        float dist = _dir * (first.Pos.X - ctx.Body.Bounds.Side(_dir));
        return dist <= MovementConfig.Current.CorrectorClimbTriggerDistance + Chunk.TileSize;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        abilities.Facing = _dir;
        // Supported like Standing, so the air jump re-arms here too (the grounded reset in
        // PlayerCharacter only covers Standing/Crouched) — same as the climb family's Enter.
        abilities.HasDoubleJumped = false;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        // Standing's baseline and Standing's fold, with the climb priced free.
        ctx.Body.AppliedForce = StandingState.FoldBaseline(ctx);
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.Stairs, startGrounded: true);
    }

    // A regular flight ahead: the first floor rise and the corner after it are both one
    // riser and the same number of columns apart. `spacing` is the tread width in columns.
    internal static bool TryFindFlight(Corridor c, out CorridorCorner first, out int spacing)
    {
        first = default; spacing = 0;
        var cfg = MovementConfig.Current;
        int i0 = -1;
        for (int i = 0; i < c.FloorCornerCount; i++)
            if (c.FloorCorners[i].Delta > 0f) { i0 = i; break; }
        if (i0 < 0 || i0 + 1 >= c.FloorCornerCount) return false;
        var a = c.FloorCorners[i0];
        var b = c.FloorCorners[i0 + 1];
        if (b.Delta <= 0f) return false;
        bool OneRiser(float d) => d >= cfg.MantleMinRise && d <= cfg.MantleMaxRise;
        if (!OneRiser(a.Delta) || !OneRiser(b.Delta)) return false;
        spacing = b.Column - a.Column;
        if (spacing < 1 || spacing > StairChecker.MaxTreadColumns) return false;
        first = a;
        return true;
    }
}
