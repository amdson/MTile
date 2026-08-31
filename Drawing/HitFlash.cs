using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Render-only "flash white when hit". Reusable by anything that has a hit stamp:
// give the tracker an id and a stamp each frame, ask it for a 0..1 whiteness at draw
// time, and feed that to whichever draw path the target uses.
//
// Trigger + dedupe: the sim already stamps every hit — CombatState.LastHitFrame for
// players, Entity.HitGeneration for entities — and both are snapshotted, so a rollback
// replay reproduces the same stamp rather than minting a fresh one. Watching for a
// stamp to ADVANCE is therefore edge-detection that survives rollback, the same trick
// HitFeelSystem and GameAudio.HitConnect use. Nothing here writes back to the sim.
//
// Why a stamp and not "health went down": a hit absorbed by a guard, or one that
// only knocks back, would never register — and the stamp costs nothing extra, since
// the sim was already recording it for audio.
public sealed class HitFlashTracker
{
    // How long one flash lasts, and how white it goes at the instant of the hit
    // (1 = pure white). Short and strong reads as impact; long and weak reads as a
    // status effect.
    public float Seconds = 0.10f;
    public float Peak    = 0.9f;

    // Entries live on past their flash so a stamp that stops advancing can't re-fire,
    // and are dropped once they are this stale — a target that no longer exists simply
    // stops being stamped.
    private const float PruneAfter = 5f;

    private struct Entry { public int Stamp; public float Left; }

    private readonly Dictionary<EntityId, Entry> _flashes = new();
    private readonly List<EntityId> _keys = new();

    // Note a target's current hit stamp. A fresh flash starts only when the stamp has
    // advanced since last seen, so this is safe (and meant) to call every frame.
    public void Stamp(EntityId id, int stamp)
    {
        if (stamp <= 0) return;
        if (_flashes.TryGetValue(id, out var e) && e.Stamp == stamp) return;
        _flashes[id] = new Entry { Stamp = stamp, Left = Seconds };
    }

    // Decay every live flash. Real (rendered-frame) dt, not sim dt — the flash is a
    // render effect and should look the same whatever the sim is doing.
    public void Tick(float dt)
    {
        if (_flashes.Count == 0) return;
        _keys.Clear();
        foreach (var k in _flashes.Keys) _keys.Add(k);
        foreach (var k in _keys)
        {
            var e = _flashes[k];
            e.Left -= dt;
            if (e.Left < -PruneAfter) _flashes.Remove(k);
            else _flashes[k] = e;
        }
    }

    // 0 = untouched, 1 = pure white. Fades linearly over Seconds.
    public float Intensity(EntityId id)
    {
        if (Seconds <= 0f || !_flashes.TryGetValue(id, out var e) || e.Left <= 0f) return 0f;
        return Peak * (e.Left / Seconds);
    }

    // Lerp a colour toward white by `flash`, leaving alpha alone — a hit flash
    // brightens a silhouette, it doesn't fade one in. Capped at the alpha so the result
    // stays valid premultiplied colour (RGB never exceeds A), which is what every
    // SpriteBatch path in the game expects.
    public static Color Whiten(Color c, float flash)
    {
        if (flash <= 0f) return c;
        float f = MathHelper.Clamp(flash, 0f, 1f);
        return new Color(Channel(c.R, c.A, f), Channel(c.G, c.A, f), Channel(c.B, c.A, f), c.A);
    }

    private static byte Channel(byte v, byte cap, float f)
        => v >= cap ? v : (byte)(v + (cap - v) * f);
}

// The wiring: reads this frame's hit stamps off the sim and answers "how white is that
// thing right now?" for the draw pass. Players and entities go through one table
// because both are IHittables with an EntityId — the same key the combat dedupe uses.
public sealed class HitFlashSystem
{
    private readonly HitFlashTracker _tracker = new();

    public HitFlashTracker Tuning => _tracker;

    // Once per rendered frame, before drawing.
    public void Collect(Simulation sim, float dt)
    {
        _tracker.Tick(dt);
        if (sim == null) return;

        Player(sim.Player);
        var secondaries = sim.SecondaryPlayers;
        for (int i = 0; i < secondaries.Count; i++) Player(secondaries[i].Player);

        foreach (var e in sim.Entities) _tracker.Stamp(e.Id, e.HitGeneration);
    }

    private void Player(PlayerCharacter p)
    {
        if (p?.Combat != null) _tracker.Stamp(p.Id, p.Combat.LastHitFrame);
    }

    public float Intensity(EntityId id)  => _tracker.Intensity(id);
    public float Intensity(IHittable h)  => h == null ? 0f : _tracker.Intensity(h.Id);
}
