using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class Camera
{
    public Vector2 Position;
    public float Zoom = 1.55f;
    public float Buffer = 150f;
    // Per-second rate the camera lerps vertically toward the target when not
    // pinned to a dead-zone edge. Without this, a jump apex snaps the camera
    // up — but on landing the player drops back inside the (now-shifted) dead
    // zone, so the camera stays high. The relax pulls Y back down toward the
    // target each frame; slow enough that jumps still feel punchy, fast enough
    // that the screen recenters between platforms.
    public float VerticalRelaxRate = 10f;

    // Screen shake (Plans/HIT_FEEL_PLAN.md phase 4). Trauma-style: Shake() ADDS to a
    // 0..1 trauma pool rather than setting an absolute magnitude, so overlapping hits
    // compound instead of one replacing another; squared falloff (trauma²) makes it
    // read snappy at high trauma and settle quickly rather than lingering at low
    // amplitude. Render-only — decayed here (TrackTarget already runs once per render
    // frame with dt) and applied as an offset in GetTransform; never touches sim state.
    private const float ShakeDecayPerSecond = 3.5f;
    private const float MaxShakePx          = 10f;
    private static readonly Random _shakeRng = new();
    private float   _shakeTrauma;
    private Vector2 _shakeOffset;

    public void Shake(float trauma) => _shakeTrauma = MathF.Min(1f, _shakeTrauma + trauma);

    // Moves the camera only when target crosses inside the buffer boundary,
    // then bleeds Y toward the target so vertical excursions self-recover.
    public void TrackTarget(Vector2 target, Vector2 screenCenter, float dt)
    {
        if (_shakeTrauma > 0f && dt > 0f)
        {
            _shakeTrauma = MathF.Max(0f, _shakeTrauma - ShakeDecayPerSecond * dt);
            float mag = _shakeTrauma * _shakeTrauma * MaxShakePx;
            _shakeOffset = new Vector2(
                ((float)_shakeRng.NextDouble() * 2f - 1f) * mag,
                ((float)_shakeRng.NextDouble() * 2f - 1f) * mag);
        }
        else _shakeOffset = Vector2.Zero;

        float halfW = screenCenter.X / Zoom;
        float halfH = screenCenter.Y / Zoom;
        // Buffer is world-units, but the visible half-extents shrink with zoom; cap the
        // margin so the dead zone can't invert (halfExtent < Buffer would pin the camera
        // OFF-center — vertically it parked above the player at high zoom, horizontally
        // it jittered between the two crossed clamps). At the cap the zone is a point:
        // the camera locks to the target, the right behavior for a tight zoom.
        float bufX = MathF.Min(Buffer, halfW);
        float bufY = MathF.Min(Buffer, halfH);

        if (target.X < Position.X - halfW + bufX)
            Position.X = target.X + halfW - bufX;
        else if (target.X > Position.X + halfW - bufX)
            Position.X = target.X - halfW + bufX;

        if (target.Y < Position.Y - halfH + bufY)
            Position.Y = target.Y + halfH - bufY;
        else if (target.Y > Position.Y + halfH - bufY)
            Position.Y = target.Y - halfH + bufY;

        if (dt > 0f && VerticalRelaxRate > 0f)
        {
            float k = MathF.Min(1f, VerticalRelaxRate * dt);
            Position.Y += (target.Y - Position.Y) * k;
        }
    }

    // Unshaken — this is what ScreenToWorld inverts to recover MouseWorldPosition,
    // which feeds PlayerInput and therefore the sim. Screen shake is non-deterministic
    // render jitter (Random, no snapshot); if it leaked into here it would leak into
    // sim-affecting aim input — a determinism hazard, not just a visual one.
    public Matrix GetTransform(Vector2 screenCenter) =>
        Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
        * Matrix.CreateScale(Zoom, Zoom, 1f)
        * Matrix.CreateTranslation(screenCenter.X, screenCenter.Y, 0f);

    // Shake included — use this one for drawing (SpriteBatch.Begin, primitive passes).
    // Never invert this for input; see GetTransform's note above.
    public Matrix GetDrawTransform(Vector2 screenCenter) =>
        Matrix.CreateTranslation(-Position.X - _shakeOffset.X, -Position.Y - _shakeOffset.Y, 0f)
        * Matrix.CreateScale(Zoom, Zoom, 1f)
        * Matrix.CreateTranslation(screenCenter.X, screenCenter.Y, 0f);

    public Vector2 ScreenToWorld(Vector2 screenPos, Vector2 screenCenter) =>
        Vector2.Transform(screenPos, Matrix.Invert(GetTransform(screenCenter)));
}
