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

    public EnemyTarget(EntityId id, Vector2 position, Vector2 velocity, float health, int team, bool isPlayer)
    {
        Id       = id;
        Position = position;
        Velocity = velocity;
        Health   = health;
        Team     = team;
        IsPlayer = isPlayer;
    }

    public static EnemyTarget Of(PlayerCharacter p) =>
        new(p.Id, p.Body.Position, p.Body.Velocity, p.Health, p.Team, isPlayer: true);

    public static EnemyTarget Of(Entity e) =>
        new(e.Id, e.Body.Position, e.Body.Velocity, e.Health, e.Team, isPlayer: false);
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
