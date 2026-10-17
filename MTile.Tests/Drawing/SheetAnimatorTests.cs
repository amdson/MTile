using MTile;

namespace MTile.Tests;

public class SheetAnimatorTests
{
    // 10 frames, 100 ms each; tags carve them up the way an enemy sheet would.
    private static SpriteSheetData Sheet(params (string name, int from, int to)[] tags)
    {
        var frames = string.Join(",", Enumerable.Range(0, 10).Select(i =>
            $$"""{ "frame": { "x": {{i * 8}}, "y": 0, "w": 8, "h": 8 }, "duration": 100 }"""));
        var tagJson = string.Join(",", tags.Select(t =>
            $$"""{ "name": "{{t.name}}", "from": {{t.from}}, "to": {{t.to}} }"""));
        return SpriteSheetData.Parse($$"""
            { "frames": [{{frames}}], "meta": { "image": "e.png", "frameTags": [{{tagJson}}] } }
            """);
    }

    private static VisualState Acting(string action, VisualPhase phase, float phaseP, float actionP = 0f)
        => new() { Action = action, Phase = phase, PhaseProgress = phaseP, ActionProgress = actionP, Facing = 1 };

    [Fact]
    public void MostSpecificTagWins_ActionPhase_ThenAction_ThenPhase_ThenIdle()
    {
        var full = new SheetAnimator(Sheet(("idle", 0, 1), ("windup", 2, 3), ("Lunge", 4, 5), ("Lunge_windup", 6, 7)));
        full.Apply(Acting("LungeAction", VisualPhase.Windup, 0f));
        Assert.Equal("Lunge_windup", full.Tag);   // "Action" suffix dropped

        var noPhaseTag = new SheetAnimator(Sheet(("idle", 0, 1), ("windup", 2, 3), ("Lunge", 4, 5)));
        noPhaseTag.Apply(Acting("LungeAction", VisualPhase.Windup, 0f));
        Assert.Equal("Lunge", noPhaseTag.Tag);

        var generic = new SheetAnimator(Sheet(("idle", 0, 1), ("windup", 2, 3)));
        generic.Apply(Acting("LungeAction", VisualPhase.Windup, 0f));
        Assert.Equal("windup", generic.Tag);
        generic.Apply(Acting("LungeAction", VisualPhase.Active, 0f));   // no "attack" tag
        Assert.Equal("idle", generic.Tag);
    }

    [Fact]
    public void IdleStates_PreferHurt_ThenMove_ThenIdle()
    {
        var a = new SheetAnimator(Sheet(("idle", 0, 1), ("move", 2, 3), ("hurt", 4, 5)));
        a.Apply(new VisualState { Moving = true, Hurt = true });
        Assert.Equal("hurt", a.Tag);
        a.Apply(new VisualState { Moving = true });
        Assert.Equal("move", a.Tag);
        a.Apply(new VisualState());
        Assert.Equal("idle", a.Tag);
    }

    [Fact]
    public void PhaseTags_FollowGameplayProgress_NotTheClock()
    {
        var a = new SheetAnimator(Sheet(("windup", 2, 5)));
        a.Apply(Acting("Lunge", VisualPhase.Windup, 0.6f));
        a.Advance(10f);                       // the clock must not matter
        Assert.Equal(4, a.Frame);             // 60% of 4 frames → third frame of the tag

        var whole = new SheetAnimator(Sheet(("Lunge", 0, 9)));
        whole.Apply(Acting("Lunge", VisualPhase.Recovery, 0.1f, actionP: 0.95f));
        Assert.Equal(9, whole.Frame);         // an action tag spans the whole action
    }

    [Fact]
    public void LoopTags_RunOnTheirOwnClock_AndRestartOnChange()
    {
        var a = new SheetAnimator(Sheet(("idle", 0, 2), ("move", 3, 4)));
        a.Apply(new VisualState());
        a.Advance(0.25f);
        Assert.Equal(2, a.Frame);
        a.Advance(0.1f);
        Assert.Equal(0, a.Frame);             // wrapped
        a.Apply(new VisualState { Moving = true });
        Assert.Equal(3, a.Frame);             // fresh tag starts at its first frame
    }

    [Fact]
    public void UntaggedSheet_LoopsEveryFrame()
    {
        var a = new SheetAnimator(Sheet());
        a.Apply(Acting("Lunge", VisualPhase.Windup, 0.9f));
        Assert.Null(a.Tag);
        a.Advance(0.55f);
        Assert.Equal(5, a.Frame);
    }
}

public class EntityArtCatalogTests
{
    private static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) d = d.Parent;
        return Path.Combine(d!.FullName, "Assets", "Sprites");
    }

    // Every entry names a real EntityKind, a known renderer, and a sheet that parses.
    [Fact]
    public void ShippedCatalog_ResolvesEveryEntry()
    {
        var catalog = EntityArtSystem.ParseCatalog(File.ReadAllText(Path.Combine(Dir(), "entities.json")));
        foreach (var (kind, def) in catalog)
        {
            Assert.True(Enum.TryParse<EntityKind>(kind, ignoreCase: true, out _), $"'{kind}' is not an EntityKind");
            Assert.Equal("sheet", def.Type?.ToLowerInvariant());
            Assert.True(def.PixelSize > 0f, $"{kind}: PixelSize");
            var sheet = SpriteSheetData.Parse(File.ReadAllText(Path.Combine(Dir(), def.Sheet + ".json")));
            Assert.True(File.Exists(Path.Combine(Dir(), sheet.Image)), $"{kind}: missing {sheet.Image}");
        }
    }
}
