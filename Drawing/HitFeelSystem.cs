using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Render-only hit feedback: screen shake, a directional knockback streak, and a
// contact-point flash (Plans/HIT_FEEL_PLAN.md phases 4-6). Reads sim state, writes
// nothing back to it — same contract as GameAudio/CosmeticUpdateSystem.
//
// Trigger + dedupe: CombatState.LastHitFrame is already the rollback-safe "the stamp
// IS the identity" signal GameAudio.HitConnect uses (Audio/GameAudio.cs). Rather than
// re-deriving a staleness window here, this just remembers the last LastHitFrame it
// has already reacted to per tracked player and fires again only when that stamp
// advances — the same edge-detect shape CosmeticUpdateSystem uses for the landing
// puff, just keyed on a frame-equality check instead of a bool.
public sealed class HitFeelSystem
{
    private readonly ParticleSystem _particles;
    private readonly Camera _camera;
    // Players are keyed by player index (stable, small, non-recycled). Entities are
    // keyed by EntityId since indices into Simulation.Entities aren't stable frame
    // to frame (despawns reshuffle the list) — EntityId is the sim's actual stable
    // identity for exactly this reason (see Entity.Id's doc comment).
    private readonly Dictionary<int, int> _lastHandledFrame = new();
    private readonly Dictionary<EntityId, int> _lastHandledGeneration = new();

    public HitFeelSystem(ParticleSystem particles, Camera camera)
    {
        _particles = particles;
        _camera    = camera;
    }

    public void Collect(Simulation sim)
    {
        Player(sim.Player, 0);
        var secondaries = sim.SecondaryPlayers;
        for (int i = 0; i < secondaries.Count; i++) Player(secondaries[i].Player, i + 1);

        var entities = sim.Entities;
        for (int i = 0; i < entities.Count; i++) TrackedEntity(entities[i]);
    }

    private void Player(PlayerCharacter p, int index)
    {
        var c = p?.Combat;
        if (c == null || c.LastHitFrame <= 0) return;

        _lastHandledFrame.TryGetValue(index, out int last);
        if (c.LastHitFrame == last) return;
        _lastHandledFrame[index] = c.LastHitFrame;

        // Same normalization GameAudio.HitConnect scales gain/pitch by, so a hit's
        // sound and its screen-space feel agree on what "big" means.
        float t   = MathHelper.Min(c.LastHitImpulse / 900f, 1f);
        var   pos = p.Body.Position;

        React(pos, c.LastHitDir, t);
    }

    // Balloons/balls/future combat targets (Entities/Entity.cs) — anything hit
    // through the generic IHittable path that isn't a PlayerCharacter. Same stamp
    // contract as Player() above, just keyed on a per-hit counter instead of a
    // frame number (a bare Entity has no frame clock of its own).
    private void TrackedEntity(Entity e)
    {
        if (e == null || e.HitGeneration <= 0) return;

        _lastHandledGeneration.TryGetValue(e.Id, out int last);
        if (e.HitGeneration == last) return;
        _lastHandledGeneration[e.Id] = e.HitGeneration;

        float t = MathHelper.Min(e.LastHitImpulse / 900f, 1f);
        React(e.Body.Position, e.LastHitDir, t);
    }

    private void React(Vector2 pos, Vector2 dir, float t)
    {
        _camera.Shake(0.12f + 0.5f * t);

        if (dir.LengthSquared() > 1e-4f)
        {
            Effects.KnockbackCue(_particles, pos, dir, t);
            Effects.HitSpark(_particles, pos, dir, count: 3 + (int)(5f * t));
        }
    }
}
