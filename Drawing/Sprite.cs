using System;
using Microsoft.Xna.Framework;

namespace MTile;

// "Thing that draws itself." Callers sync Position (and optionally Rotation/Scale)
// to whatever they're attached to (a PhysicsBody, a fixed world point, …) then call
// Update + Draw each frame. Layer ordering is the caller's responsibility — draw
// order in Game1 determines depth.
public class Sprite
{
    public Vector2 Position;
    public float   Rotation;
    public float   Scale   = 1f;
    public Color   Tint    = Color.White;
    public bool    Visible = true;
    public Pose    Pose;

    public virtual void Update(float dt) { }

    public virtual void Draw(DrawContext ctx)
    {
        if (!Visible || Pose == null) return;
        Pose.Draw(ctx, new SpriteTransform(Position, Rotation, Scale), Tint);
    }
}

// Frame-array animation. Each frame is a fully-specified Pose; the player just
// advances a clock and snaps to the matching frame. No tweening — keeps the
// chunky pixel feel and matches what a hand-drawn sprite sheet would do.
public sealed class SpriteAnimation
{
    public readonly Pose[] Frames;
    public readonly float  FrameDuration;
    public readonly bool   Loop;
    public float Duration => Frames.Length * FrameDuration;

    public SpriteAnimation(Pose[] frames, float frameDuration, bool loop = true)
    {
        Frames        = frames;
        FrameDuration = frameDuration;
        Loop          = loop;
    }

    public Pose SampleAt(float time)
    {
        if (Frames.Length == 0) return null;
        int i = (int)(time / FrameDuration);
        if (Loop) i = ((i % Frames.Length) + Frames.Length) % Frames.Length;
        else      i = Math.Clamp(i, 0, Frames.Length - 1);
        return Frames[i];
    }
}

// A Sprite that swaps Pose over time, driven by a SpriteAnimation. Play() restarts.
// OnComplete fires once when a non-looping animation reaches its last frame —
// used by transient effects (slash flash, hit reaction) to clean themselves up.
public class AnimatedSprite : Sprite
{
    public SpriteAnimation Animation;
    public float           Time;
    public Action          OnComplete;
    private bool _completed;

    public void Play(SpriteAnimation anim)
    {
        Animation  = anim;
        Time       = 0f;
        _completed = false;
        if (anim != null) Pose = anim.SampleAt(0f);
    }

    public bool IsFinished => Animation != null && !Animation.Loop && Time >= Animation.Duration;

    public override void Update(float dt)
    {
        if (Animation == null) return;
        Time += dt;
        if (!Animation.Loop && Time >= Animation.Duration)
        {
            Time = Animation.Duration;
            if (!_completed)
            {
                _completed = true;
                OnComplete?.Invoke();
            }
        }
        Pose = Animation.SampleAt(Time);
    }
}
