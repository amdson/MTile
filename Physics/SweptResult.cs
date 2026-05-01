using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct SweptResult
{
    public static readonly SweptResult NoHit = new(false, 1f, Vector2.Zero);

    public bool Hit { get; }
    public float T { get; }       // time of first contact in [0, 1]
    public Vector2 Normal { get; } // surface normal at contact, pointing from B toward A

    public SweptResult(bool hit, float t, Vector2 normal)
    {
        Hit = hit;
        T = t;
        Normal = normal;
    }
}
