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
    private readonly Dictionary<int, int> _lastHandledFrame = new();

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

        _camera.Shake(0.12f + 0.5f * t);

        if (c.LastHitDir.LengthSquared() > 1e-4f)
        {
            Effects.KnockbackCue(_particles, pos, c.LastHitDir, t);
            Effects.HitSpark(_particles, pos, c.LastHitDir, count: 3 + (int)(5f * t));
        }
    }
}
