using System;
using Microsoft.Xna.Framework;

namespace MTile;

public enum ParticleKind : byte
{
    Square, // small filled rotated square (size = side length)
    Disc,   // DrawContext.Disc — visually a chunky square at particle sizes
    Line,   // velocity-aligned streak of length = size
}

public struct Particle
{
    public Vector2 Position, Velocity, Acceleration;
    public float   Life;        // remaining seconds
    public float   MaxLife;
    public Color   StartColor, EndColor;
    public float   StartSize, EndSize;
    public float   Rotation, AngularVelocity;
    public ParticleKind Kind;

    public readonly bool  Alive => Life > 0f;
    // Normalized age 0→1. Used to lerp color/size.
    public readonly float T     => MaxLife > 0f ? 1f - Life / MaxLife : 1f;
}

// Fixed-capacity particle pool. Spawn() either grabs a free slot or overwrites
// the oldest particle when the pool is full (visually equivalent to LRU). Update
// swap-removes dead particles so the live prefix stays compact.
public sealed class ParticleSystem
{
    private readonly Particle[] _pool;
    private int _count;
    private int _ring;  // wrap cursor used when the pool is saturated

    public int Count    => _count;
    public int Capacity => _pool.Length;

    public ParticleSystem(int capacity = 1024)
    {
        _pool = new Particle[capacity];
    }

    // Spawn returns a ref so the caller can fill fields in-place without
    // allocating a temporary Particle on the stack/heap.
    public ref Particle Spawn()
    {
        if (_count < _pool.Length)
        {
            int i = _count++;
            _pool[i] = default;
            return ref _pool[i];
        }
        int idx = _ring;
        _ring = (_ring + 1) % _pool.Length;
        _pool[idx] = default;
        return ref _pool[idx];
    }

    public void Update(float dt)
    {
        int n = _count;
        for (int i = 0; i < n; )
        {
            ref var p = ref _pool[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _pool[i] = _pool[--n];
                continue;
            }
            p.Velocity += p.Acceleration * dt;
            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularVelocity * dt;
            i++;
        }
        _count = n;
    }

    public void Draw(DrawContext ctx)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _pool[i];
            float t  = p.T;
            var col  = Color.Lerp(p.StartColor, p.EndColor, t);
            float sz = MathHelper.Lerp(p.StartSize, p.EndSize, t);
            switch (p.Kind)
            {
                case ParticleKind.Square:
                    ctx.RotatedRect(p.Position, new Vector2(sz, sz), p.Rotation, col);
                    break;
                case ParticleKind.Disc:
                    ctx.Disc(p.Position, sz * 0.5f, col);
                    break;
                case ParticleKind.Line:
                {
                    var dir = p.Velocity;
                    if (dir.LengthSquared() < 1e-3f) dir = new Vector2(1f, 0f);
                    else dir.Normalize();
                    var a = p.Position - dir * sz * 0.5f;
                    var b = p.Position + dir * sz * 0.5f;
                    ctx.Line(a, b, col, 1f);
                    break;
                }
            }
        }
    }

    public void Clear()
    {
        _count = 0;
        _ring  = 0;
    }
}
