using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MTile;
using Xunit;

namespace MTile.Tests;

// Guards the state → clip binding contract documented in Plans/ANIMATION_BINDING_MAP.md.
// Binding is by the clip JSON's `Type` field, with no aliases and no fallback — matched
// ORDINALLY against the ActionState CLASS NAME for overlays, and parsed as an `AnimClip`
// enum member for movement. A rename on either side silently drops the overlay (or throws
// at animator construction), and nothing else in the build would notice.
public class ClipBindingTests
{
    // Actions the animator deliberately treats as "no overlay" (CharacterAnimator.IsOverlayAction).
    private static readonly HashSet<string> NoOverlay = new(StringComparer.Ordinal)
        { "NullAction", "ReadyAction", "RecoveryAction" };

    // Every concrete ActionState in the assembly, by class name.
    private static List<string> ActionClassNames()
        => typeof(ActionState).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ActionState).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Where(n => !NoOverlay.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static List<AnimationDocument> Clips() => AnimationStore.LoadAll(StatesDir("biped"));

    // Movement side: an AnimClip with no authored file is a HARD THROW the first frame a
    // driver selects it (CharacterAnimator "No authored animation bound for clip 'X'") — so
    // adding an enum member without its file ships a crash, not a missing pose. Both rigs,
    // because the binder filters on the clip's Skeleton field.
    [Theory]
    [InlineData("biped")]
    [InlineData("biped_rabbit")]
    public void EveryAnimClip_HasAnAuthoredFile(string rig)
    {
        var types = new HashSet<string>(
            AnimationStore.LoadAll(StatesDir(rig)).Select(c => c.Type), StringComparer.OrdinalIgnoreCase);
        var missing = Enum.GetNames<AnimClip>().Where(n => !types.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"[{rig}] AnimClip values with no clip file: " + string.Join(", ", missing));
    }

    // The climb family split (2026-08-04): Parkour / Mantle / ArcJump / LedgePull are four
    // distinct maneuvers and must not collapse back onto one shared clip. They start as
    // identical copies, so this asserts the BINDING is distinct, not the poses.
    [Fact]
    public void GuidedLipManeuvers_EachBindADistinctClip()
    {
        var byType = AnimationStore.LoadAll(StatesDir("biped"))
            .ToDictionary(c => c.Type, c => c.Name, StringComparer.OrdinalIgnoreCase);
        var names = new[] { "Parkour", "Mantle", "ArcJump", "LedgePull" }
            .Select(t => byType.TryGetValue(t, out var n) ? n : null).ToList();

        Assert.DoesNotContain(null, names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryOverlayAction_HasAClipWithMatchingType()
    {
        var types = new HashSet<string>(Clips().Select(c => c.Type), StringComparer.Ordinal);
        var missing = ActionClassNames().Where(n => !types.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            "action states with no clip whose Type matches their class name: " + string.Join(", ", missing));
    }

    // The other direction: a clip typed like an action that no action class can produce is
    // dead weight (this is what caught `Misc` and the orphaned LobbedAreaAction clip).
    // Movement clips are exempt — their Type parses as an AnimClip instead.
    [Fact]
    public void EveryActionTypedClip_HasAnActionClass()
    {
        var classes = new HashSet<string>(ActionClassNames(), StringComparer.Ordinal);
        var known = new HashSet<string>(NoOverlay, StringComparer.Ordinal)
        {
            "ClimbHands",   // consumed by ParkourDriver as a movement overlay, not an action
        };
        var orphans = Clips()
            .Where(c => !Enum.TryParse<AnimClip>(c.Type, ignoreCase: true, out _))
            .Select(c => c.Type)
            .Where(t => t != null && !classes.Contains(t) && !known.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        Assert.True(orphans.Count == 0,
            "clips typed for an action class that does not exist (dead clips): " + string.Join(", ", orphans));
    }

    // The overlay-pacing contract (Plans/ANIMATION_BINDING_MAP.md "Known dead / broken
    // bindings"): an action either REPORTS progress, so its clip is remapped onto the real
    // activation, or it is open-ended and its clip must loop. An action that does neither
    // plays its clip on an unrelated clock and freezes on the last frame — the class of bug
    // that had Beam overrunning its 0.6s clip and Grab desyncing on an early throw.
    [Fact]
    public void EveryOverlayAction_ReportsProgress_OrHasALoopingClip()
    {
        var byType = Clips().ToDictionary(c => c.Type, c => c, StringComparer.Ordinal);
        var bad = new List<string>();

        foreach (var t in typeof(ActionState).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ActionState).IsAssignableFrom(t))
                     .Where(t => !NoOverlay.Contains(t.Name))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!byType.TryGetValue(t.Name, out var clip)) continue;   // covered by the test above

            // Does this class (or a base) override AnimationProgress?
            var m = t.GetMethod(nameof(ActionState.AnimationProgress),
                                BindingFlags.Public | BindingFlags.Instance);
            bool reports = m != null && m.DeclaringType != typeof(ActionState);

            if (!reports && !clip.Loop)
                bad.Add($"{t.Name} (clip '{clip.Name}', Duration {clip.Duration:0.##}s, Loop=false)");
        }

        Assert.True(bad.Count == 0,
            "actions that neither report AnimationProgress nor loop — their overlay plays on the "
            + "clip's own clock and holds the final pose: " + string.Join("; ", bad));
    }

    private static string StatesDir(string rig)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates", rig);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates/" + rig;
    }
}
