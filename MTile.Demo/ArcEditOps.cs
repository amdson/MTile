using System;
using Microsoft.Xna.Framework;
using MTile;

namespace MTileDemo;

// ARC EDITING, in the arc's OWN space (the game pixels a HermiteClipDocument is authored in).
// Extracted so the two editors that shape arcs cannot drift apart:
//   • HermiteClipGame — the standalone `--ref <name>` window, editing a SHARED
//     ReferenceClips/<name>.json against a plain zoom/pan view;
//   • DemoGame's in-clip arc session, editing a CLIP-LOCAL arc drawn through the clip's scene
//     placement, so the curve sits where the mapped body would.
//
// Everything here takes and returns arc-space points; each host owns its own screen mapping.
internal static class ArcEditOps
{
    public const float HandleK   = 0.25f;   // fraction of the tangent drawn as a grab handle
    // Tangent magnitude bounds, as a fraction of the anchor span — a tangent of zero length
    // has no direction to drag, and a wild one swamps the curve.
    public const float TanMinFrac = 0.05f, TanMaxFrac = 8f;

    public static float SpanLen(HermiteClipDocument doc) => MathF.Max(doc.Span.Length(), 1e-3f);

    // The grab handle for key `i`'s tangent, in arc space. `side` is +1 for the outgoing tip,
    // −1 for the incoming one.
    public static Vector2 HandleTip(HermiteClipDocument doc, int i, int side)
    {
        var tan = doc.Keys[i].Tan;
        if (tan.LengthSquared() < 1e-6f) tan = Vector2.UnitX * SpanLen(doc);
        return doc.Keys[i].Pos + tan * (HandleK * side);
    }

    // Every key moves freely: the retarget anchors are separate points, so an arc may start
    // behind the entry or overshoot past the gate.
    public static void DragKey(HermiteClipDocument doc, int i, Vector2 p)
    {
        doc.Keys[i].Pos = p;
        doc.RederiveT();
    }

    // Moving an anchor re-frames the arc: it changes what the pixels MEAN (the retarget, and
    // the editor's scene mapping, normalize by the anchor span), not the authored key values.
    public static void DragAnchor(HermiteClipDocument doc, int which, Vector2 p)
    {
        if (which == 0) doc.Entry = p; else doc.Gate = p;
    }

    public static void DragHandle(HermiteClipDocument doc, int i, int side, Vector2 p)
    {
        Vector2 tan = (p - doc.Keys[i].Pos) * side / HandleK;
        float len = tan.Length();
        if (len < 1e-4f) return;
        float span = SpanLen(doc);
        tan *= MathHelper.Clamp(len, TanMinFrac * span, TanMaxFrac * span) / len;
        doc.Keys[i].Tan = tan;
    }

    // A new key sits ON the current curve at the point nearest `p` (position and tangent both
    // sampled), so adding one never pops the shape. False when it would land on an existing key.
    public static bool AddKeyNear(HermiteClipDocument doc, Vector2 p, out int index)
    {
        index = -1;
        const int Samples = 256;
        float bestT = -1f, bestD = float.MaxValue;
        for (int i = 0; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            float d = Vector2.DistanceSquared(doc.Eval(t), p);
            if (d < bestD) { bestD = d; bestT = t; }
        }
        float eps = 1e-3f * SpanLen(doc);
        foreach (var k in doc.Keys)
            if (Vector2.DistanceSquared(doc.Eval(bestT), k.Pos) < eps * eps) return false;

        int insert = 1;
        while (insert < doc.Keys.Count - 1 && doc.Keys[insert].T < bestT) insert++;
        doc.Keys.Insert(insert, new HermiteClipKey { Pos = doc.Eval(bestT), Tan = doc.EvalTangent(bestT) });
        doc.RederiveT();
        index = insert;
        return true;
    }

    // Interior keys only — the endpoints are the arc.
    public static bool DeleteKey(HermiteClipDocument doc, int i)
    {
        if (i <= 0 || i >= doc.Keys.Count - 1) return false;
        doc.Keys.RemoveAt(i);
        doc.RederiveT();
        return true;
    }

    public static void SetDuration(HermiteClipDocument doc, float d)
        => doc.Duration = MathF.Round(MathHelper.Clamp(d, 0.05f, 5f), 2);
}
