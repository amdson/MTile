using Microsoft.Xna.Framework;

namespace MTile;

public class Camera
{
    public Vector2 Position;
    public float Zoom = 1f;
    public float Buffer = 150f;

    // Moves the camera only when target crosses inside the buffer boundary.
    public void TrackTarget(Vector2 target, Vector2 screenCenter)
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
    }

    public Matrix GetTransform(Vector2 screenCenter) =>
        Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
        * Matrix.CreateScale(Zoom, Zoom, 1f)
        * Matrix.CreateTranslation(screenCenter.X, screenCenter.Y, 0f);

    public Vector2 ScreenToWorld(Vector2 screenPos, Vector2 screenCenter) =>
        Vector2.Transform(screenPos, Matrix.Invert(GetTransform(screenCenter)));
}
