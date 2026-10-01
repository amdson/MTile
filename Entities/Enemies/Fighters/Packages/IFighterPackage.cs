using System;
using System.Collections.Generic;
using System.Reflection;

namespace MTile;

// A fighter package: one spec plus the brain it ships with, authored as a single C#
// file under Entities/Enemies/Fighters/Packages/ (Plans/FIGHTER_PACKAGE_GUIDE.md). The
// league discovers packages by reflection, so a package touches no shared file — it
// is one class with a parameterless constructor, nothing to register anywhere.
//
// Contract:
//   * Name is unique across packages (the league keys on it) and Author is free text.
//   * Spec() returns a FRESH FighterSpec each call (the arena mutates Kind/Team on it).
//     Its Brain must build a FighterController subclass defined in the same file: the
//     only view of the world a brain gets is FighterSenses, and all its memory lives in
//     the BrainScratch passed by ref. No statics, no System.Random, no wall clock.
//   * The spec must compile under FighterCosts.DefaultBudget (FighterCompiler.Compile
//     with zero violations) — FighterPackageTests enforces it for every package found.
public interface IFighterPackage
{
    string Name   { get; }
    string Author { get; }
    FighterSpec Spec();
}

public static class FighterPackages
{
    // Every IFighterPackage with a public parameterless constructor in the library
    // assembly, ordered by Name so the league's spawn order (and therefore every
    // tie-break) is the same on every machine.
    public static IReadOnlyList<IFighterPackage> Discover()
    {
        var list = new List<IFighterPackage>();
        foreach (var t in typeof(IFighterPackage).Assembly.GetTypes())
        {
            if (t.IsAbstract || t.IsInterface || !typeof(IFighterPackage).IsAssignableFrom(t)) continue;
            if (t.GetConstructor(Type.EmptyTypes) == null) continue;
            list.Add((IFighterPackage)Activator.CreateInstance(t));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    // The package called `name` (case-insensitive), or null. Tool-time lookup (league,
    // --record-fight, loading a fight file) — reflection, so never per frame.
    public static IFighterPackage Find(string name)
    {
        foreach (var p in Discover())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    // Which package's brain a spec carries, by the controller type its factory builds
    // (timing wrappers looked through), or null for a stock brain. This is how a fight
    // file names a package brain — a delegate cannot be serialized, a package name can.
    public static string BrainOwner(FighterSpec spec)
    {
        var ctrl = TimedController.Unwrap(spec?.Brain?.Invoke(spec));
        if (ctrl == null || ctrl is FighterCloserBrain or FighterKiterBrain or FighterHoverDiveBrain) return null;
        var t = ctrl.GetType();
        foreach (var p in Discover())
        {
            var ps = p.Spec();
            if (TimedController.Unwrap(ps.Brain?.Invoke(ps))?.GetType() == t) return p.Name;
        }
        return null;
    }
}
