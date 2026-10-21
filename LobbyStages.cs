namespace MTile;

// The curated stage list the browser lobby offers (MTile.Web/Pages/Index.razor). Plain
// data on purpose: the lobby renders before Game1 exists, and touching Stages there would
// run its static constructor — which builds the depths map — behind the menu. Names must
// be registered Stages and Coop must match the Stage's own flag;
// MTile.Tests/LobbyStageCatalogTests.cs checks both. First entry is the lobby default.
public sealed record LobbyStage(string Name, string Label, bool Coop);

public static class LobbyStages
{
    public static readonly LobbyStage[] All =
    {
        new("prism",    "Prism",    false),
        new("depths",   "Depths",   true),
        new("duel_hall",  "Duel Hall",  false),
        new("duel_plain", "Duel Plain", false),
        new("arena",    "Arena",    false),
        new("gauntlet", "Gauntlet", false),
        new("training", "Training", false),
    };

    public static LobbyStage Find(string name)
    {
        foreach (var s in All)
            if (string.Equals(s.Name, name, System.StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
