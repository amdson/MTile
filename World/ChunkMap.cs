using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class ChunkMap : IEnumerable<Chunk>
{
    private readonly Dictionary<Point, Chunk> _dict = new();

    public Chunk this[Point pos]
    {
        get => _dict[pos];
        set => _dict[pos] = value;
    }

    public bool TryGet(Point pos, out Chunk chunk) => _dict.TryGetValue(pos, out chunk);

    public IEnumerator<Chunk> GetEnumerator() => _dict.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
