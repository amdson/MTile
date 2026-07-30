using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Runtime half of the reference-clip system (BALLISTIC_CORRECTOR_PLAN §1):
// retarget an authored HermiteClip at state Enter, then servo the body along it.
//
// PROTOTYPE SCOPE (movement_todo 7+8): the retarget is the affine endpoint map —
// clip (0,0) → the body's position at Enter, clip (1,-1) → the measured gate
// point — with independent x/y scaling (a left-facing move falls out of a
// negative x scale, a descent out of a negative y scale). The plan's fuller
// contract (entry tangent bound to the incoming velocity, arc-length progress)
// is deliberately not built yet: the two migrated moves enter at near-zero
// speed from known poses, so the authored tangents already are the entry
// velocities up to frame scale. Revisit when a fast-entry move migrates.
public readonly struct ReferenceFrame
{
    public readonly Vector2 Entry;   // world position of clip (0,0)
    public readonly Vector2 Scale;   // world units per normalized unit: (gate−entry).X per +1 x, (entry−gate).Y per −1 y

    public ReferenceFrame(Vector2 entry, Vector2 gate)
    {
        Entry = entry;
        Scale = new Vector2(gate.X - entry.X, entry.Y - gate.Y);
    }

    public Vector2 Map(Vector2 p)        => Entry + new Vector2(p.X * Scale.X, p.Y * Scale.Y);
    public Vector2 MapTangent(Vector2 t) => new(t.X * Scale.X, t.Y * Scale.Y);
}

public static class ReferencePath
{
    // Critically-damped PD servo toward the retargeted clip point at `progress`,
    // with velocity feedforward from the clip tangent and full gravity cancel
    // (the clip authors the descent; gravity must not double-apply on top of it).
    // Magnitude-capped so a blocked path stalls against terrain honestly instead
    // of grinding with unbounded force.
    public static Vector2 TrackForce(HermiteClipDocument clip, in ReferenceFrame frame,
        float progress, float duration, PhysicsBody body, Vector2 gravity, MovementConfig cfg)
    {
        duration = MathF.Max(duration, 1e-4f);
        Vector2 target    = frame.Map(clip.Eval(progress));
        Vector2 targetVel = frame.MapTangent(clip.EvalTangent(progress)) / duration;
        return TrackForce(target, targetVel, body, gravity, cfg);
    }

    // Explicit-target form: the servo core, for callers whose target has been
    // adjusted after clip evaluation (ReferenceCorrector's terrain-deformed arc).
    public static Vector2 TrackForce(Vector2 target, Vector2 targetVel,
        PhysicsBody body, Vector2 gravity, MovementConfig cfg)
    {
        Vector2 force = (target - body.Position) * cfg.GuidedSpringK
                      + (targetVel - body.Velocity) * cfg.GuidedDamping
                      - gravity;
        float len = force.Length();
        if (len > cfg.GuidedMaxForce) force *= cfg.GuidedMaxForce / len;
        return force;
    }
}

// Clip lookup for the states. Baked, code-authored defaults guarantee the game
// (and the headless test sim) always has every clip; ReferenceClips/<name>.json
// at the repo root overrides a default when present — that's the file the
// editor round-trips (dotnet run --project MTile.Demo -- --ref <name>).
//
// Determinism: the registry is a sim-affecting static, so it follows the
// MovementConfig contract exactly — written at startup (LoadOverrides) and by
// the desktop dev hot-reload watcher only, never mid-Step, and must match
// across rollback peers (both load the same files).
public static class ReferenceClipRegistry
{
    public const string LedgePull = "ledge_pull";
    public const string Dropdown  = "dropdown";
    private static readonly string[] Names = { LedgePull, Dropdown };

    private static readonly Dictionary<string, HermiteClipDocument> _clips = new()
    {
        [LedgePull] = MakeLedgePull(),
        [Dropdown]  = MakeDropdown(),
    };

    public static HermiteClipDocument Get(string name)
        => _clips.TryGetValue(name, out var clip) ? clip : null;

    public static void LoadOverrides(string dir = "ReferenceClips")
    {
        foreach (var name in Names)
        {
            try
            {
                using var stream = TitleContent.TryOpenRead(System.IO.Path.Combine(dir, name + ".json"));
                if (stream == null) continue;
                var doc = HermiteClipDocument.Load(stream);
                if (doc != null && doc.Keys.Count >= 2) _clips[name] = doc;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReferenceClips] {name}: {ex.Message}");
            }
        }
    }

    // Best-guess pull-up: rise nearly straight up hugging the wall, then a late,
    // quick crest over the corner onto the top. The wall-hugging x-profile is
    // load-bearing, not just aesthetic: releasing Up mid-pull re-grabs the ledge
    // only while the body is still on the wall side of the corner, so the crest
    // must come late — the bespoke pull crossed in its final ~2 frames and
    // LedgeRegrabDriftTests pins that release window. Normalized frame ≈ 26px
    // wide × 40px tall after retarget.
    private static HermiteClipDocument MakeLedgePull()
    {
        var doc = new HermiteClipDocument
        {
            Name = LedgePull,
            Keys =
            {
                new HermiteClipKey { X = 0.00f, Y =  0.00f, TX = 0.05f, TY = -1.60f },
                new HermiteClipKey { X = 0.06f, Y = -0.80f, TX = 0.15f, TY = -1.00f },
                new HermiteClipKey { X = 0.22f, Y = -1.08f, TX = 1.60f, TY =  0.00f },
                new HermiteClipKey { X = 1.00f, Y = -1.00f, TX = 1.60f, TY =  0.10f },
            },
        };
        doc.RederiveT();
        return doc;
    }

    // Best-guess slip-off: slide out fast and nearly level, nose over the corner
    // past the mid-point, mostly falling by the gate. Only the on-platform part
    // plays in practice (Dropdown exits to Falling the moment ground is lost),
    // so the early keys carry the feel; the tail sets the exit velocity.
    private static HermiteClipDocument MakeDropdown()
    {
        var doc = new HermiteClipDocument
        {
            Name = Dropdown,
            Keys =
            {
                new HermiteClipKey { X = 0.00f, Y =  0.00f, TX = 1.60f, TY = -0.10f },
                new HermiteClipKey { X = 0.55f, Y = -0.35f, TX = 1.10f, TY = -1.00f },
                new HermiteClipKey { X = 1.00f, Y = -1.00f, TX = 0.55f, TY = -1.60f },
            },
        };
        doc.RederiveT();
        return doc;
    }
}
