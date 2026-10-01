using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Targets and teams (Plans/FIGHTER_DESIGN_PLAN.md §5.3). An enemy no longer fights
// "the player" by construction: each frame it asks an ITargetSource for the nearest
// live candidate that is not on its team, and its brain and states read that target
// through EnemyContext.Target. With the player as the only opposing thing alive the
// answer is always the player, so every enemy written before teams behaves exactly
// as it did.

public static class Teams
{
    // The primary player. Secondary players mirror Factions.ForPlayerIndex: the first
    // (and every further) secondary is team 1, because the faction enum has two
    // player slots and a third player sharing P2's faction must share its team too.
    public const int Player  = 0;
    public const int Player2 = 1;
    // Every entity's default — the whole stock roster, props and helpers included.
    // Sharing one team is what keeps the gauntlet from turning on itself.
    public const int Enemies = 2;

    public static int ForPlayerIndex(int index) => index <= 0 ? Player : Player2;
}

// One candidate, flattened to what a brain reads. A value copy taken at the start of
// the enemy's Update — it does not track the body afterwards.
public readonly struct EnemyTarget
{
    // The target's World id — a player's is as real as an entity's (Simulation
    // registers players in the ECS too). None only on a target synthesized with no
    // id at all (a headless spawner whose player was never registered).
    public readonly EntityId Id;
    public readonly Vector2  Position;
    public readonly Vector2  Velocity;
    public readonly float    Health;
    public readonly int      Team;
    // True for a PlayerCharacter (primary or secondary). EnemyContext.Player is that
    // PlayerCharacter when this is set, null otherwise.
    public readonly bool     IsPlayer;
    // The target's body AABB this frame, for actions that must not build into it.
    public readonly BoundingBox Bounds;

    // ── The tell (FIGHTER_DESIGN_PLAN §16, "readable telegraphs") ───────────
    // What the target is visibly doing: the pool-action kind it is winding up or
    // swinging (Special for a bespoke action or a player), how far through the windup
    // it is (0..1; 1 ⇒ active; <0 ⇒ no action), the aim it locked, and its facing. A
    // brain that reads this can dodge; one that reads it late (reaction frames) cannot.
    public readonly ActionKind Tell;
    public readonly float      TellProgress;
    public readonly Vector2    TellAim;
    public readonly int        Facing;
    // False for the empty value — a history slot nothing has been written to yet.
    public readonly bool       Known;

    public EnemyTarget(EntityId id, Vector2 position, Vector2 velocity, float health, int team, bool isPlayer,
                       BoundingBox bounds = default,
                       ActionKind tell = ActionKind.Special, float tellProgress = -1f, Vector2 tellAim = default,
                       int facing = 1)
    {
        Id           = id;
        Position     = position;
        Velocity     = velocity;
        Health       = health;
        Team         = team;
        IsPlayer     = isPlayer;
        Bounds       = bounds;
        Tell         = tell;
        TellProgress = tellProgress;
        TellAim      = tellAim;
        Facing       = facing;
        Known        = true;
    }

    public static EnemyTarget Of(PlayerCharacter p) =>
        new(p.Id, p.Body.Position, p.Body.Velocity, p.Health, p.Team, isPlayer: true, p.Body.Bounds,
            facing: p.Facing == 0 ? 1 : p.Facing);

    public static EnemyTarget Of(Entity e) => e is EnemyEntity en
        ? new(e.Id, e.Body.Position, e.Body.Velocity, e.Health, e.Team, isPlayer: false, e.Body.Bounds,
              en.TellKind, en.TellProgress, en.TellAim, en.Facing)
        : new(e.Id, e.Body.Position, e.Body.Velocity, e.Health, e.Team, isPlayer: false, e.Body.Bounds);

    // The coarse view a fighter gets for free: position snapped to a grid, no velocity,
    // no tell. Health and team survive — a brain can always tell whether it is winning.
    public EnemyTarget Coarse(float quantPx)
    {
        float q = MathF.Max(quantPx, 1f);
        var p = new Vector2(MathF.Floor(Position.X / q) * q + q * 0.5f, MathF.Floor(Position.Y / q) * q + q * 0.5f);
        return new EnemyTarget(Id, p, Vector2.Zero, Health, Team, IsPlayer, default);
    }

    // The same target as last seen at `pos`, with nothing current about it — what a
    // hidden target looks like to a fighter that did not buy memory.
    public EnemyTarget Frozen(Vector2 pos)
        => new(Id, pos, Vector2.Zero, Health, Team, IsPlayer, default);
}

// "Who should `self` be fighting?" Implemented by Simulation. Must be a pure function
// of sim state plus `sticky` (the id `self` chose last frame, snapshotted on it), with
// a fixed candidate order, so a rollback replay picks the same target.
//
// Returns false when no candidate opposes `self` (everything alive is its teammate);
// the caller then falls back to the primary player.
public interface ITargetSource
{
    bool TryFindTarget(EnemyEntity self, EntityId sticky, out EnemyTarget target, out PlayerCharacter player);
}
