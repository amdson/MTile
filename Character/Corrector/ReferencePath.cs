using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Runtime half of the reference-clip system (BALLISTIC_CORRECTOR_PLAN §1):
// retarget an authored HermiteClip at state Enter; the state then FOLLOWS it as
// a geometric path (PathTracker.BuildClipPolyline + FollowPolyline — LedgePull,
// Dropdown). There is no clock: arc-length progress is the tracker's bead.
//
// The retarget is the affine two-point map —
// the clip's Entry anchor → the body's position at Enter, its Gate anchor → the
// measured gate point — with independent x/y scaling (a left-facing move falls
// out of a negative x scale, a descent out of a negative y scale).
public readonly struct ReferenceFrame
{
    public readonly Vector2 ClipEntry;   // clip-space point that binds to Entry
    public readonly Vector2 Entry;       // its world position
    public readonly Vector2 Scale;       // world units per clip unit, componentwise

    // Anchored form: the clip carries its own authoring box, so key positions are
    // free (a key may sit before the entry or past the gate) and are read in the
    // pixels they were authored in.
    public ReferenceFrame(HermiteClipDocument clip, Vector2 entry, Vector2 gate)
    {
        Vector2 span = clip.Gate - clip.Entry;
        Vector2 want = gate - entry;
        ClipEntry = clip.Entry;
        Entry     = entry;
        // A degenerate clip axis (anchors sharing an x or y — e.g. a pure-vertical
        // pull) declares no extent to normalize by, so that axis passes through at
        // the authored pixel size rather than collapsing to zero.
        Scale = new Vector2(
            MathF.Abs(span.X) < 1e-4f ? 1f : want.X / span.X,
            MathF.Abs(span.Y) < 1e-4f ? 1f : want.Y / span.Y);
    }

    // Legacy normalized form: clip (0,0) → entry, clip (1,-1) → gate. Identical to
    // the anchored form on a clip carrying the default anchors.
    public ReferenceFrame(Vector2 entry, Vector2 gate)
    {
        ClipEntry = Vector2.Zero;
        Entry     = entry;
        Scale     = new Vector2(gate.X - entry.X, entry.Y - gate.Y);
    }

    public Vector2 Map(Vector2 p)
    {
        Vector2 d = p - ClipEntry;
        return Entry + new Vector2(d.X * Scale.X, d.Y * Scale.Y);
    }

    public Vector2 MapTangent(Vector2 t) => new(t.X * Scale.X, t.Y * Scale.Y);

    // Inverse of Map. The mapping is diagonal, so it inverts componentwise; a degenerate axis
    // (scale 0) reports the clip-space origin on that axis rather than dividing by zero. The
    // editors use this to turn a dragged world point back into the pixels the arc is authored
    // in — drag in the frame you see, store in the frame the file keeps.
    public Vector2 Unmap(Vector2 world)
    {
        Vector2 d = world - Entry;
        return ClipEntry + new Vector2(
            MathF.Abs(Scale.X) < 1e-6f ? 0f : d.X / Scale.X,
            MathF.Abs(Scale.Y) < 1e-6f ? 0f : d.Y / Scale.Y);
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
    public const string Parkour   = "parkour";
    public const string Mantle    = "mantle";
    public const string ArcJump   = "arcjump";
    private static readonly string[] Names = { LedgePull, Dropdown, Parkour, Mantle, ArcJump };

    // Authoring-box sizes (px) for the baked clips. Readability only — the retarget
    // normalizes by the clip's own anchor span, so changing these changes nothing
    // about how the arc plays.
    public const float RefLedgeW = 26f, RefLedgeH = 40f;
    public const float RefDropW  = 20f, RefDropH  = 44f;

    private static readonly Dictionary<string, HermiteClipDocument> _clips = new()
    {
        [LedgePull] = MakeLedgePull(),
        [Dropdown]  = MakeDropdown(),
        [Parkour]   = MakeClimb(Parkour),
        [Mantle]    = MakeClimb(Mantle),
        [ArcJump]   = MakeClimb(ArcJump),
    };

    // The arcs that always exist (a baked default covers each, file or no file). Tooling
    // offers these alongside whatever else it finds in ReferenceClips/.
    public static IReadOnlyList<string> KnownNames => Names;

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

    // The climb family's arcs (ParkourState / MantleState / ArcJumpState), baked
    // verbatim from ReferenceClips/<name>.json so the headless sim and a build
    // without the files follow the same arc the editor authored (and the
    // animation's body path is mapped from — MTile.Probe mapcom).
    private static HermiteClipDocument MakeClimb(string name) => name switch
    {
        "parkour" => new HermiteClipDocument
        {
            Name = "parkour", Duration = 0.45f,
            EntryX = 0.0f, EntryY = 0.0f,
            GateX = 25.109129f, GateY = -16.339863f,
            Keys =
            {
                new HermiteClipKey { T = 0.0f, X = -2.4572186f, Y = 0.23007262f, TX = 13.754844f, TY = -81.96129f },
                new HermiteClipKey { T = 0.4914804f, X = 2.9656694f, Y = -20.858936f, TX = 22.294094f, TY = -22.896637f },
                new HermiteClipKey { T = 1.0f, X = 25.4104f, Y = -18.900671f, TX = 77.961296f, TY = -0.49032593f },
            },
        },
        "mantle" => new HermiteClipDocument
        {
            Name = "mantle", Duration = 0.45f,
            EntryX = 0.0f, EntryY = 0.0f,
            GateX = 18.0f, GateY = -16.0f,
            Keys =
            {
                new HermiteClipKey { T = 0.0f, X = 0.0f, Y = 0.0f, TX = 2.0f, TY = -40.0f },
                new HermiteClipKey { T = 0.5467f, X = 3.0f, Y = -18.0f, TX = 20.0f, TY = -6.0f },
                new HermiteClipKey { T = 1.0f, X = 18.0f, Y = -16.0f, TX = 24.0f, TY = 2.0f },
            },
        },
        "arcjump" => new HermiteClipDocument
        {
            Name = "arcjump", Duration = 0.5f,
            EntryX = 0.0f, EntryY = 0.0f,
            GateX = 34.0f, GateY = -32.0f,
            Keys =
            {
                new HermiteClipKey { T = 0.0f, X = 0.0f, Y = 0.0f, TX = 60.0f, TY = -70.0f },
                new HermiteClipKey { T = 0.6862f, X = 16.0f, Y = -40.0f, TX = 50.0f, TY = -6.0f },
                new HermiteClipKey { T = 1.0f, X = 34.0f, Y = -32.0f, TX = 45.0f, TY = 8.0f },
            },
        },
        _ => throw new ArgumentException(name),
    };

    // Best-guess pull-up: rise nearly straight up hugging the wall, then a late,
    // quick crest over the corner onto the top. The wall-hugging x-profile is
    // load-bearing, not just aesthetic: releasing Up mid-pull re-grabs the ledge
    // only while the body is still on the wall side of the corner, so the crest
    // must come late — the bespoke pull crossed in its final ~2 frames and
    // LedgeRegrabDriftTests pins that release window. Authored against a 26px
    // wide × 40px tall reference ledge, which is about what it retargets onto.
    private static HermiteClipDocument MakeLedgePull()
    {
        var doc = new HermiteClipDocument
        {
            Name = LedgePull,
            Duration = 0.45f,   // the pull's pace: arc length / Duration (LedgePullState)
            Keys =
            {
                new HermiteClipKey { X = 0.00f, Y =  0.00f, TX = 0.05f, TY = -1.60f },
                new HermiteClipKey { X = 0.06f, Y = -0.80f, TX = 0.15f, TY = -1.00f },
                new HermiteClipKey { X = 0.22f, Y = -1.08f, TX = 1.60f, TY =  0.00f },
                new HermiteClipKey { X = 1.00f, Y = -1.00f, TX = 1.60f, TY =  0.10f },
            },
        };
        doc.RederiveT();
        // Authored above in the old normalized frame, then rescaled into the pixel
        // box: T is chord-derived, and rescaling AFTER RederiveT keeps the exact
        // parametrization (and so the exact retargeted arc) the clip shipped with.
        doc.RescaleClipSpace(new Vector2(RefLedgeW, RefLedgeH));
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
            Duration = 0.45f,   // the slide's pace: arc length / Duration (DropdownState)
            Keys =
            {
                new HermiteClipKey { X = 0.00f, Y =  0.00f, TX = 1.60f, TY = -0.10f },
                new HermiteClipKey { X = 0.55f, Y = -0.35f, TX = 1.10f, TY = -1.00f },
                new HermiteClipKey { X = 1.00f, Y = -1.00f, TX = 0.55f, TY = -1.60f },
            },
        };
        doc.RederiveT();
        // Same rescale-after-RederiveT as the pull, but with a NEGATIVE y factor:
        // this move descends, so its gate belongs below its entry. The map
        // normalizes by the anchor span, so flipping clip-space y alongside the
        // gate leaves the retargeted arc identical — it just makes the editor
        // draw the slip-off going down instead of standing it on its head.
        doc.RescaleClipSpace(new Vector2(RefDropW, -RefDropH));
        return doc;
    }
}
