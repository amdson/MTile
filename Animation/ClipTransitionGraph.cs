using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// TRANSITION GRAPH BETWEEN TWO CLIPS — the motion-graph edge search (Kovar, Gleicher &
// Pighin 2002) restricted to one ordered pair of clips: at which phase of `From` can playback
// switch to which phase of `To` with the least visual discontinuity?
//
// Both clips are sampled at N uniform phases. Cost[i, j] is the distance between a WINDOW of
// samples around From's phase i and the same-length window around To's phase j — the two
// clips should agree not just at the switch point but for a few samples on either side of it,
// which is what makes joint velocities and foot swing direction count. Windows wrap on a
// looping clip and clamp on a non-looping one.
//
// The per-sample distance has three terms, each dimensionless so their weights compare:
//   angles   — summed squared wrapped local-rotation difference over every bone (radians);
//   feet     — squared difference of each support point's root-relative position and of its
//              per-sample velocity, both divided by the larger clip's maximum foot reach so
//              the term is in units of "fractions of a leg";
//   contacts — a flat penalty per support point whose stance state (inside a contact span or
//              not) differs; applied only when BOTH clips carry spans, so an unannotated clip
//              is not penalised for its silence.
// Support points are the rig's NamedPoints with Role == Options.FootRole ("support").
//
// Transitions are the LOCAL MINIMA of the cost surface (8-neighbourhood, wrapping per clip)
// at or below Options.MaxCost, sorted cheapest first. BestEntry(fromPhase) answers the runtime
// question directly: the cheapest To-phase for a switch at that From-phase.
//
// Render-only and self-contained: a pure function of (From, To, rig, options) with no
// animator state, so it can be built offline, cached per clip pair, or used ad hoc from a
// tool. Nothing here reads terrain or the solver.
public sealed class TransitionOptions
{
    public int    Samples            = 32;     // phases per clip
    public int    Window             = 2;      // samples each side of the switch point
    public float  AngleWeight        = 1f;
    public float  FootPositionWeight = 4f;
    public float  FootVelocityWeight = 2f;
    public float  ContactMismatch    = 1f;     // per support point whose stance state differs
    public float  MaxCost            = float.PositiveInfinity;   // keep local minima at or below this
    public string FootRole           = "support";
    // Clamp the comparison windows at both clips' ends even if the documents say Loop —
    // for a search that does not trust the authored seam (ClipLoopCut).
    public bool   TreatAsNonLooping  = false;
    // Sample phases i/(Samples−1) over [0, 1] INCLUSIVE instead of i/Samples over [0, 1):
    // the last sample is the clip's end pose, so a jump from the very end (the authored
    // seam) is a scored pair like any other. Only meaningful with TreatAsNonLooping.
    public bool   IncludeEndPhase    = false;
}

public readonly struct ClipTransition
{
    public readonly float FromPhase;   // leave From here …
    public readonly float ToPhase;     // … and enter To here
    public readonly float Cost;
    public ClipTransition(float fromPhase, float toPhase, float cost)
    { FromPhase = fromPhase; ToPhase = toPhase; Cost = cost; }
    public override string ToString() => $"{FromPhase:0.000} -> {ToPhase:0.000} ({Cost:0.000})";
}

public sealed class ClipTransitionGraph
{
    public readonly AnimationDocument From, To;
    public readonly int Samples;
    public readonly bool IncludesEnd;                // sample phases are i/(Samples−1)
    public readonly float[,] Cost;                  // [fromIndex, toIndex]
    public readonly ClipTransition[] Transitions;   // local minima ≤ MaxCost, cheapest first

    private ClipTransitionGraph(AnimationDocument from, AnimationDocument to, int samples, bool includesEnd,
                                float[,] cost, ClipTransition[] transitions)
    { From = from; To = to; Samples = samples; IncludesEnd = includesEnd; Cost = cost; Transitions = transitions; }

    public float PhaseOf(int index) => IncludesEnd ? index / (float)(Samples - 1) : index / (float)Samples;
    public int   IndexOf(float phase)
    {
        if (IncludesEnd) return Math.Clamp((int)MathF.Round(phase * (Samples - 1)), 0, Samples - 1);
        float p = phase - MathF.Floor(phase);
        return (int)MathF.Round(p * Samples) % Samples;
    }

    // Cost of switching at the sample nearest each phase.
    public float CostAt(float fromPhase, float toPhase) => Cost[IndexOf(fromPhase), IndexOf(toPhase)];

    // The cheapest entry into To for a switch at `fromPhase` (nearest sample).
    public ClipTransition BestEntry(float fromPhase)
    {
        int i = IndexOf(fromPhase), bestJ = 0;
        float best = float.MaxValue;
        for (int j = 0; j < Samples; j++)
            if (Cost[i, j] < best) { best = Cost[i, j]; bestJ = j; }
        return new ClipTransition(PhaseOf(i), PhaseOf(bestJ), best);
    }

    // The cheapest switch anywhere, or null when no transition met MaxCost.
    public ClipTransition? Best => Transitions.Length > 0 ? Transitions[0] : null;

    public static ClipTransitionGraph Build(AnimationDocument from, AnimationDocument to, Skeleton rig,
                                            TransitionOptions options = null)
    {
        if (from == null) throw new ArgumentNullException(nameof(from));
        if (to == null) throw new ArgumentNullException(nameof(to));
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        var o = options ?? new TransitionOptions();
        if (o.Samples < 2) throw new ArgumentException("Samples must be ≥ 2", nameof(options));
        int n = o.Samples, w = Math.Max(0, o.Window);

        var feet = SupportBones(rig, o.FootRole);
        var a = ClipSamples.Take(from, rig, feet, n, o.TreatAsNonLooping, o.IncludeEndPhase);
        var b = ClipSamples.Take(to, rig, feet, n, o.TreatAsNonLooping, o.IncludeEndPhase);

        // One reach normaliser for both clips so a foot-position gap means the same thing
        // whichever side it is measured on.
        float reach = MathF.Max(a.MaxReach, b.MaxReach);
        float invReach = reach > 1e-6f ? 1f / reach : 0f;
        bool contacts = a.HasContacts && b.HasContacts;

        // Per-sample-pair distance, cached: the window sum below reuses each pair up to
        // 2w+1 times.
        var pair = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                pair[i, j] = Distance(a, i, b, j, invReach, contacts, o);

        var cost = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float sum = 0f;
                for (int k = -w; k <= w; k++)
                    sum += pair[a.Neighbour(i, k), b.Neighbour(j, k)];
                cost[i, j] = sum / (2 * w + 1);
            }

        // Local minima over the 8-neighbourhood, each axis wrapping only if its clip loops.
        var found = new List<ClipTransition>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float c = cost[i, j];
                if (c > o.MaxCost) continue;
                bool min = true;
                for (int di = -1; di <= 1 && min; di++)
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int ii = a.Neighbour(i, di), jj = b.Neighbour(j, dj);
                        if (ii == i && jj == j) continue;             // clamped onto itself
                        if (cost[ii, jj] < c) { min = false; break; }
                    }
                if (min) found.Add(new ClipTransition(a.PhaseOf(i), b.PhaseOf(j), c));
            }
        found.Sort((x, y) => x.Cost.CompareTo(y.Cost));
        return new ClipTransitionGraph(from, to, n, o.IncludeEndPhase, cost, found.ToArray());
    }

    // ---- the metric ----------------------------------------------------------

    private static float Distance(ClipSamples a, int i, ClipSamples b, int j,
                                  float invReach, bool contacts, TransitionOptions o)
    {
        float d = 0f;
        if (o.AngleWeight > 0f)
        {
            float s = 0f;
            var ta = a.Angles[i]; var tb = b.Angles[j];
            for (int k = 0; k < ta.Length; k++)
            {
                float e = MathHelper.WrapAngle(ta[k] - tb[k]);
                s += e * e;
            }
            d += o.AngleWeight * s;
        }
        int feet = a.FootPos[i].Length;
        if (feet > 0 && invReach > 0f)
        {
            float sp = 0f, sv = 0f;
            for (int f = 0; f < feet; f++)
            {
                Vector2 dp = (a.FootPos[i][f] - b.FootPos[j][f]) * invReach;
                Vector2 dv = (a.FootVel[i][f] - b.FootVel[j][f]) * invReach;
                sp += dp.LengthSquared();
                sv += dv.LengthSquared();
            }
            d += o.FootPositionWeight * sp + o.FootVelocityWeight * sv;
        }
        if (contacts && o.ContactMismatch > 0f)
            for (int f = 0; f < feet; f++)
                if (a.Planted[i][f] != b.Planted[j][f]) d += o.ContactMismatch;
        return d;
    }

    private static int[] SupportBones(Skeleton rig, string role)
    {
        var bones = new List<int>();
        if (rig.Points != null)
            foreach (var p in rig.Points)
            {
                if (!string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase)) continue;
                int b = rig.IndexOf(p.Bone);
                if (b >= 0 && !bones.Contains(b)) bones.Add(b);
            }
        return bones.ToArray();
    }

    // ---- per-clip sampled features -------------------------------------------

    private sealed class ClipSamples
    {
        public bool       Loop;
        public int        N;
        public bool       IncludesEnd;
        public float PhaseOf(int i) => IncludesEnd ? i / (float)(N - 1) : i / (float)N;
        public float[][]  Angles;    // [sample][bone]
        public Vector2[][] FootPos;  // [sample][foot], root-relative rig units, com-subtracted
        public Vector2[][] FootVel;  // [sample][foot], per-sample finite difference
        public bool[][]   Planted;   // [sample][foot], inside any contact span
        public bool       HasContacts;
        public float      MaxReach;

        // Sample index k steps from i: wrapped on a loop, clamped otherwise.
        public int Neighbour(int i, int k)
        {
            int j = i + k;
            if (Loop) { j %= N; if (j < 0) j += N; return j; }
            return Math.Clamp(j, 0, N - 1);
        }

        public static ClipSamples Take(AnimationDocument doc, Skeleton rig, int[] feet, int n,
                                       bool forceNonLooping = false, bool includeEnd = false)
        {
            var ks = doc.Keyframes;
            if (ks == null || ks.Count == 0) throw new ArgumentException($"clip '{doc.Name}' has no keyframes");
            var s = new ClipSamples
            {
                Loop = doc.Loop && !forceNonLooping && !includeEnd, N = n, IncludesEnd = includeEnd,
                Angles = new float[n][], FootPos = new Vector2[n][], FootVel = new Vector2[n][],
                Planted = new bool[n][], HasContacts = doc.Contacts is { Count: > 0 },
            };

            // Which spans pin which support bone — resolved once.
            var spansOf = new List<ContactSpan>[feet.Length];
            for (int f = 0; f < feet.Length; f++) spansOf[f] = new List<ContactSpan>();
            if (s.HasContacts)
                foreach (var c in doc.Contacts)
                {
                    if (!EndpointResolver.TryResolvePoint(rig, doc, c.Point, out var rp)) continue;
                    for (int f = 0; f < feet.Length; f++) if (feet[f] == rp.Bone) spansOf[f].Add(c);
                }

            var a = rig.CreatePose(); var b = rig.CreatePose(); var c2 = rig.CreatePose();
            var d = rig.CreatePose(); var dst = rig.CreatePose();
            for (int i = 0; i < n; i++)
            {
                float phase = s.PhaseOf(i);
                AnimationSampler.SampleSmooth(doc, phase, a, b, c2, d, dst);
                var ang = new float[rig.Count];
                for (int k = 0; k < rig.Count; k++) ang[k] = dst.Local[k].Rotation;
                s.Angles[i] = ang;

                var world = dst.ComputeWorld(Affine2.Identity);
                bool haveCom = BodyPath.TrySampleAnchor(doc, phase, out var com, out _);
                var pos = new Vector2[feet.Length];
                var pl = new bool[feet.Length];
                for (int f = 0; f < feet.Length; f++)
                {
                    Vector2 tip = world[feet[f]].Translation;
                    if (haveCom) tip -= com;
                    pos[f] = tip;
                    s.MaxReach = MathF.Max(s.MaxReach, tip.Length());
                    foreach (var span in spansOf[f]) if (span.Covers(phase, out _)) { pl[f] = true; break; }
                }
                s.FootPos[i] = pos; s.Planted[i] = pl;
            }

            // Central-difference velocity per sample (one-sided at a non-loop's ends).
            for (int i = 0; i < n; i++)
            {
                int prev = s.Neighbour(i, -1), next = s.Neighbour(i, 1);
                float steps = s.Loop ? 2f : MathF.Max(1, next - prev);
                var v = new Vector2[feet.Length];
                for (int f = 0; f < feet.Length; f++)
                    v[f] = (s.FootPos[next][f] - s.FootPos[prev][f]) / steps;
                s.FootVel[i] = v;
            }
            return s;
        }
    }
}
