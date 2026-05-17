using System.Collections.Generic;

namespace MTile;

// Frame-scoped registry of DEFENSIVE regions. Lifecycle each frame:
//   Clear() → every IHittable.PublishHurtboxes() → CombatSystem.Apply walks pairs
//   with HitboxWorld. Nothing persists across frames.
public class HurtboxWorld
{
    private readonly List<Hurtbox> _boxes = new();
    public IReadOnlyList<Hurtbox> All => _boxes;

    public void Clear() => _boxes.Clear();

    public void Publish(in Hurtbox hb) => _boxes.Add(hb);

    // Symmetric to HitboxWorld.Overlapping. `exclude` filters by faction so a
    // query for "what enemy hurtboxes are near me?" doesn't include my own.
    public IEnumerable<Hurtbox> Overlapping(BoundingBox region, Faction? exclude = null)
    {
        foreach (var hb in _boxes)
        {
            if (exclude.HasValue && hb.Owner == exclude.Value) continue;
            if (HitboxWorld.Overlaps(hb.Region, region)) yield return hb;
        }
    }
}
