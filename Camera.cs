using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class Camera
{
    public Vector2 Position;
    public float Zoom = 1.25f;
    public float Buffer = 150f;
    // Per-second rate the camera lerps vertically toward the target when not
    // pinned to a dead-zone edge. Without this, a jump apex snaps the camera
    // up — but on landing the player drops back inside the (now-shifted) dead
    // zone, so the camera stays high. The relax pulls Y back down toward the
    // target each frame; slow enough that jumps still feel punchy, fast enough
    // that the screen recenters between platforms.
    public float VerticalRelaxRate = 10f;

    // Moves the camera only when target crosses inside the buffer boundary,
    // then bleeds Y toward the target so vertical excursions self-recover.
    public void TrackTarget(Vector2 target, Vector2 screenCenter, float dt)
    {
        float halfW = screenCenter.X / Zoom;
        float halfH = screenCenter.Y / Zoom;

        if (target.X < Position.X - halfW + Buffer)
            Position.X = target.X + halfW - Buffer;
        else if (target.X > Position.X + halfW - Buffer)
            Position.X = target.X - halfW + Buffer;

        if (target.Y < Position.Y - halfH + Buffer)
            Position.Y = target.Y + halfH - Buffer;
        else if (target.Y > Position.Y + halfH - Buffer)
            Position.Y = target.Y - halfH + Buffer;

        if (dt > 0f && VerticalRelaxRate > 0f)
        {
            float k = MathF.Min(1f, VerticalRelaxRate * dt);
            Position.Y += (target.Y - Position.Y) * k;
        }
    }

    public Matrix GetTransform(Vector2 screenCenter) =>
        Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
        * Matrix.CreateScale(Zoom, Zoom, 1f)
        * Matrix.CreateTranslation(screenCenter.X, screenCenter.Y, 0f);

    public Vector2 ScreenToWorld(Vector2 screenPos, Vector2 screenCenter) =>
        Vector2.Transform(screenPos, Matrix.Invert(GetTransform(screenCenter)));
}
