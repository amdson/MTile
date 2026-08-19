using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Aiming and visibility helpers for enemy action states. Pure functions over
// (position, target, terrain) — no state, no mutation, safe to call from a
// precondition scan where evaluation order isn't guaranteed.
//
// These started life as statics hanging off EnemyRailShotAction, which meant
// EnemyLashAction had to reach into an unrelated sibling action to aim. Any new
// action that needs to point at something belongs here instead.
internal static class EnemyAim
{
    // Unit direction toward `to` (a delta, not an absolute position), falling
    // back to flat-forward when the target is effectively on top of us — a zero
    // vector would leave both the telegraph and the attack pointing nowhere.
    public static Vector2 AimAt(Vector2 to, int facing)
        => to.LengthSquared() > 1e-4f ? Vector2.Normalize(to) : new Vector2(facing == 0 ? 1 : facing, 0f);

    // March along the firing line sampling solid cells. Starts at `skip` — the
    // muzzle offset — so a shooter never blocks itself on the platform it is
    // standing on, and stops one step short of the target so a body standing
    // half-inside a cell doesn't read as cover. Step is 6px against a 16px tile,
    // so nothing thinner than half a tile can slip between samples.
    //
    // Deliberately checks LIVE terrain every call: shoot the wall down and the
    // sightline opens, which is the same "terrain is the weapon" rule the player
    // plays by.
    public static bool HasLineOfSight(Vector2 from, Vector2 to, ChunkMap chunks, float skip)
    {
        if (chunks == null) return true;         // no world to occlude with
        var delta = to - from;
        float dist = delta.Length();
        if (dist <= skip) return true;
        var dir = delta / dist;

        const float Step = 6f;
        for (float d = skip; d < dist - Step; d += Step)
        {
            var p = from + dir * d;
            if (TileQuery.IsSolidAt(chunks, p.X, p.Y)) return false;
        }
        return true;
    }
}
