using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Brain output. The controller examines the world (player position, own
// physics state, terrain — whatever it needs) and emits a small bag of
// intents per frame. Movement and action states consume this bag without
// re-reading the world for tactical decisions, mirroring how PlayerCharacter
// movement consumes PlayerInput rather than polling hardware.
//
// Fields are deliberately abstract w.r.t. the world: MoveDir is "the way I
// want to go", Jump is "leave the ground if you can", AimWorld is "where I'm
// pointing." A flee brain and a chase brain emit values from the same shape —
// the states don't care which one wrote them.
public struct EnemyInput
{
    // Requested 2D move direction. Ground states (EnemyChaseState) read .X
    // for left/right intent; surface-anchored states (EnemyClingMoveState)
    // read the full vector and pick the best available direction. Brains
    // typically emit a unit-ish vector but states are tolerant of any
    // magnitude (Chase signs it; Cling normalizes).
    public Vector2 MoveDir;

    // Tactical "jump now if you can." Triggers the simple drift-style jump
    // (EnemyJumpState) — fixed upward impulse + horizontal drift toward facing.
    // The movement state still gates on physical feasibility (|Vy| small
    // enough to be grounded-ish); the brain only decides whether jumping
    // makes sense, not whether it's possible.
    public bool Jump;

    // Directional leap velocity. Nonzero = "launch the body with this exact
    // vector now." Triggers EnemyLeapState, which applies the vector once on
    // entry and then lets gravity + collision shape a ballistic arc (no
    // mid-air control). Use this to chain pillar-hops via EnemyClingMoveState:
    // the cling state re-engages as soon as the leap lands near a tile.
    //
    // Separate from Jump because the two semantics differ — Jump is "lift +
    // drift" with continuous horizontal control, JumpVelocity is "one-shot
    // ballistic trajectory you committed to."
    public Vector2 JumpVelocity;

    // Where the enemy is pointing. Drives facing (sign of AimWorld.X - body.X)
    // when no action is committed, and is read by ranged actions for aim.
    // Decoupled from MoveDir so a flee brain can move away while still facing
    // the player.
    public Vector2 AimWorld;

    // Global attack permission. When false, action selection is suppressed —
    // useful for "panic flee" or scripted timeouts. Defaults to true so
    // controllers that don't care don't have to set it.
    public bool WantAttack;
}

// Swappable brain. One Decide call per Update produces the per-frame
// EnemyInput. Implementations should be stateless (or carry only
// configuration data) — per-entity mutable state would need its own
// snapshot/restore path, which we don't have. Configuration-only state is
// fine because it's reconstructed identically on Rehydrate.
//
// Single instances are shared across entities and across the blueprint
// registry; nothing about Decide mutates `this`.
public abstract class EnemyController
{
    public abstract EnemyInput Decide(in EnemyContext ctx);

    // Default brain — chase the player, jump when they're meaningfully above,
    // attack whenever an action's preconditions otherwise pass. Used by
    // BruteEnemy and as the EnemyBlueprint default.
    public static readonly EnemyController Default = new ChasePlayerController();
}

// Walk toward the player until inside EngageRange, then stand still and let
// the action FSM take over. Jump when the player is at least
// JumpHeightThreshold above (Y-down → negative). All tactical knobs live
// here rather than scattered across movement states.
//
// Aim points at the player's body, which is what makes ranged actions
// targeted correctly and what derives `Facing` inside EnemyEntity.
public sealed class ChasePlayerController : EnemyController
{
    // Stop walking once within this distance — the action FSM's range gates
    // take over from here. Match this to your closest-range action's outer
    // edge (Brute's Lunge starts at 36; 56 leaves a sliver where the brute
    // can stand and lunge without overshooting).
    public float EngageRange { get; init; } = 56f;

    // Player must be at least this much higher to trigger a jump (Y-down →
    // negative). Tactical only; physical feasibility (|Vy| small) is
    // re-checked by EnemyJumpState.
    public float JumpHeightThreshold { get; init; } = -20f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var toPlayer = ctx.ToPlayer;

        // Move horizontally toward the player when outside engage range, else
        // stand still (so actions read as planted swings rather than slides).
        // Y is always 0 — this brain doesn't know about surface anchoring;
        // pair a clinging enemy with MoveTowardPlayerController instead.
        Vector2 move = Vector2.Zero;
        if (toPlayer.LengthSquared() > EngageRange * EngageRange)
            move.X = toPlayer.X >= 0f ? 1f : -1f;

        bool jump = toPlayer.Y < JumpHeightThreshold;

        return new EnemyInput
        {
            MoveDir    = move,
            Jump       = jump,
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = true,
        };
    }
}

// 2D approach brain — emits a unit vector pointing straight at the player,
// regardless of terrain. Suitable for surface-anchored enemies (paired with
// EnemyClingMoveState): the brain says "go toward the player," the state
// translates that into the best available surface direction. On flat ground
// with the player at the same altitude this collapses to the same horizontal
// chase as ChasePlayerController.
public sealed class MoveTowardPlayerController : EnemyController
{
    // Stop emitting motion intent once within this distance. Mirrors
    // ChasePlayerController.EngageRange so the action FSM has room to fire.
    public float EngageRange { get; init; } = 56f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var to = ctx.ToPlayer;
        Vector2 move = Vector2.Zero;
        if (to.LengthSquared() > EngageRange * EngageRange)
        {
            move = to;
            move.Normalize();
        }

        return new EnemyInput
        {
            MoveDir    = move,
            // Jump is meaningless for a clinger — the cling state ignores it
            // and gravity is off — but harmless to leave false.
            Jump       = false,
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = true,
        };
    }
}
