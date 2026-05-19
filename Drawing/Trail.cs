using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// A fading ribbon built from recent positions. Push() one sample per frame from
// wherever you want the trail's tip to be; Tick(dt) ages all samples and drops
// any past Lifetime; Draw() connects consecutive samples with line segments
// whose color and width lerp from "newest" toward "oldest."
//
// Backing storage is a fixed-size ring buffer — no per-frame allocation. Owned
// by whoever wants the trail (an action, the cursor, a projectile); independent
// of the global ParticleSystem so it can run in either the world-space or
// screen-space SpriteBatch pass.
public sealed class Trail
{
    private readonly Vector2[] _pos;
    private readonly float[]   _age;
    private int _head;     // index where the NEXT Push will write
    private int _count;

    public int   Capacity { get; }
    public float Lifetime { get; }
    public int   Count    => _count;

    public Trail(int capacity, float lifetime)
    {
        if (capacity < 2)   capacity = 2;
        if (lifetime <= 0f) lifetime = 0.01f;
        Capacity = capacity;
        Lifetime = lifetime;
        _pos = new Vector2[capacity];
        _age = new float[capacity];
    }

    public void Push(Vector2 p)
    {
        _pos[_head] = p;
        _age[_head] = 0f;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    public void Tick(float dt)
    {
        for (int i = 0; i < _count; i++) _age[i] += dt;
        // Drop samples that have aged past Lifetime. Because Push writes in order,
        // the oldest live sample sits at `(_head - _count) mod Capacity`.
        while (_count > 0)
        {
            int oldest = (_head - _count + Capacity) % Capacity;
            if (_age[oldest] >= Lifetime) _count--;
            else break;
        }
    }

    public void Clear() => _count = 0;

    // Connects consecutive samples newest → oldest with line segments. Each
    // segment's color/width is sampled from the *older* endpoint's normalized
    // age, so the head end (just-Push()-ed) reads as `startColor`/`startWidth`.
    public void Draw(SpriteBatch sb, Texture2D pixel,
                     Color startColor, Color endColor,
                     float startWidth, float endWidth)
    {
        if (_count < 2) return;
        int last = _count - 1;
        for (int i = 0; i < last; i++)
        {
            int newer = (_head - 1 - i + Capacity) % Capacity;
            int older = (_head - 2 - i + Capacity) % Capacity;
            float u   = MathHelper.Clamp(_age[older] / Lifetime, 0f, 1f);
            var col   = Color.Lerp(startColor, endColor, u);
            float w   = MathHelper.Lerp(startWidth, endWidth, u);
            var a = _pos[newer]; var b = _pos[older];
            var edge = b - a;
            float len = edge.Length();
            if (len < 1e-4f) continue;
            float angle = MathF.Atan2(edge.Y, edge.X);
            sb.Draw(pixel, a, null, col, angle, Vector2.Zero,
                new Vector2(len, w), SpriteEffects.None, 0f);
        }
    }
}
