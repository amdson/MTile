using System.Collections.Generic;
using MTile;

namespace MTile.Tests;

// Fixture helper for the gait clips these tests build by hand.
//
// Contacts are authored SPANS now, but a locomotion fixture is naturally written as "at this
// keyframe, this foot is planted" — and, more importantly, the tests that use this predate the
// span model and must keep meaning what they meant, or they prove nothing about the change.
// So this reproduces exactly the interval the old per-keyframe labeling implied: each maximal
// run of consecutive keys naming the same point becomes one span, ending at the key AFTER the
// run (unwrapped past 1 when a looping clip's run crosses the seam), else at the clip's end.
internal static class ContactFixture
{
    // `plants[i]` is the point planted at `doc.Keyframes[i]`, or null for none.
    public static void ApplyPlants(AnimationDocument doc, IReadOnlyList<string> plants,
                                   ContactSource source = ContactSource.SelfPlant)
    {
        var ks = doc.Keyframes;
        int n = ks.Count;
        // A closing key duplicating the first (walk-shaped, t≈1) is the seam, not a key of its own.
        bool loop = doc.Loop && n >= 2;
        int ring = loop && System.MathF.Abs((ks[^1].Time - ks[0].Time) - 1f) < 1e-3f ? n - 1 : n;

        var spans = new List<ContactSpan>();
        for (int i = 0; i < ring; i++)
        {
            string p = plants[i];
            if (p == null) continue;
            int prev = loop ? (i - 1 + ring) % ring : i - 1;
            if (i > 0 || loop)
                if (prev >= 0 && plants[prev] == p) continue;   // not a run start

            int j = i, len = 1;
            while (len < ring)
            {
                int next = loop ? (j + 1) % ring : j + 1;
                if (next >= ring && !loop) break;
                if (loop && next == i) break;
                if (plants[next] != p) break;
                j = next; len++;
            }
            float start = ks[i].Time;
            float end;
            if (!loop && j == ring - 1) end = 1f;
            else
            {
                int after = loop ? (j + 1) % ring : j + 1;
                end = ks[after].Time;
                while (end <= start) end += 1f;
            }
            spans.Add(new ContactSpan { Point = p, Start = start, End = end, Source = source });
        }
        doc.Contacts = spans.Count == 0 ? null : spans;
    }
}
