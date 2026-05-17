using System.Collections.Generic;

namespace MTile;

// Frame-scoped registry of OFFENSIVE regions. Lifecycle each frame:
//   Clear() → publishers (action states, AI) call Publish() → CombatSystem.Apply
//   walks the list. Nothing persists across frames; an interrupted action simply
//   stops publishing and its hitbox is gone next frame.
public class HitboxWorld
{
    private readonly List<Hitbox> _boxes = new();
    public IReadOnlyList<Hitbox> All => _boxes;

    public void Clear() => _boxes.Clear();

    public void Publish(in Hitbox hb) => _boxes.Add(hb);

    // Used by consumers that want to query rather than wait for OnHit dispatch
    // (e.g. an entity reading "am I about to be hit?" for a flinch animation).
    // `exclude` filters by faction so an entity doesn't react to its own team's hits.
    public IEnumerable<Hitbox> Overlapping(BoundingBox region, Faction? exclude = null)
    {
        foreach (var hb in _boxes)
        {
            if (exclude.HasValue && hb.Owner == exclude.Value) continue;
            if (Overlaps(hb.Region, region)) yield return hb;
        }
    }

    public static bool Overlaps(BoundingBox a, BoundingBox b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
