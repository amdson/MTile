using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Convex polygon defined by local-space vertices centered at the origin.
// Pass a position and rotation to GetVertices() to get world-space coordinates.
public class Polygon
{
    private readonly Vector2[] _vertices;

    public int VertexCount => _vertices.Length;

    public Polygon(Vector2[] vertices)
    {
        if (vertices.Length < 3)
            throw new ArgumentException("A polygon requires at least 3 vertices.", nameof(vertices));
        _vertices = (Vector2[])vertices.Clone();
    }

    public static Polygon CreateRectangle(float width, float height)
    {
        float hw = width * 0.5f, hh = height * 0.5f;
        return new Polygon(new[]
        {
            new Vector2(-hw, -hh),
            new Vector2( hw, -hh),
            new Vector2( hw,  hh),
            new Vector2(-hw,  hh),
        });
    }

    // sides >= 3; starts with a vertex at the top (useful for e.g. hexagons, triangles)
    public static Polygon CreateRegular(float radius, int sides)
    {
        if (sides < 3) throw new ArgumentException("sides must be >= 3", nameof(sides));
        var verts = new Vector2[sides];
        float step = MathHelper.TwoPi / sides;
        for (int i = 0; i < sides; i++)
        {
            float angle = step * i - MathHelper.PiOver2;
            verts[i] = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }
        return new Polygon(verts);
    }

    // Returns world-space vertices after applying position + rotation.
    public Vector2[] GetVertices(Vector2 position, float rotation = 0f)
    {
        var result = new Vector2[_vertices.Length];
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        for (int i = 0; i < _vertices.Length; i++)
        {
            var v = _vertices[i];
            result[i] = new Vector2(
                v.X * cos - v.Y * sin + position.X,
                v.X * sin + v.Y * cos + position.Y);
        }
        return result;
    }

    // Edge normals for SAT — called with already-transformed world vertices.
    internal Vector2[] GetAxes(Vector2[] worldVertices)
    {
        var axes = new Vector2[worldVertices.Length];
        for (int i = 0; i < worldVertices.Length; i++)
        {
            var edge = worldVertices[(i + 1) % worldVertices.Length] - worldVertices[i];
            axes[i] = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
        }
        return axes;
    }

    // Scalar projection of all vertices onto axis, returning the covered [min, max] interval.
    internal static (float Min, float Max) Project(Vector2[] vertices, Vector2 axis)
    {
        float min = Vector2.Dot(vertices[0], axis);
        float max = min;
        for (int i = 1; i < vertices.Length; i++)
        {
            float d = Vector2.Dot(vertices[i], axis);
            if (d < min) min = d;
            else if (d > max) max = d;
        }
        return (min, max);
    }

    // Axis-aligned bounding box in world space (integer pixels).
    public Rectangle GetBounds(Vector2 position, float rotation = 0f)
    {
        var verts = GetVertices(position, rotation);
        float minX = verts[0].X, maxX = verts[0].X;
        float minY = verts[0].Y, maxY = verts[0].Y;
        for (int i = 1; i < verts.Length; i++)
        {
            if (verts[i].X < minX) minX = verts[i].X;
            if (verts[i].X > maxX) maxX = verts[i].X;
            if (verts[i].Y < minY) minY = verts[i].Y;
            if (verts[i].Y > maxY) maxY = verts[i].Y;
        }

        int left = (int)MathF.Floor(minX);
        int top = (int)MathF.Floor(minY);
        int right = (int)MathF.Ceiling(maxX);
        int bottom = (int)MathF.Ceiling(maxY);

        return new Rectangle(left, top, right - left, bottom - top);
    }
}
